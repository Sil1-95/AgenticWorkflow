using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M6 at the controller: new evidence the agent reports through the typed signal reroutes the task to the earliest invalid stage while the SAME native conversation
/// goes on with only the delta; the invalidated approvals, the implementation and the verification stay in the history and stop counting; the workspace is preserved as
/// provisional work and may not change until the rerouted stage is approved; and a failure after the reclassification is a recoverable continuation like any other.
/// </summary>
public sealed class ReclassificationRunnerTests
{
    private const string Evidence = "The checker rejects any call that is not routed through the audit log.";
    private const string Reason = "The approved strategy renames in place, which bypasses the audit log.";

    private static AgentWhatSignal What(string objective = "Rename GetMeal() to GetFood() everywhere it is public.")
        => new(objective, "GetFood() has the same behavior.", "src/ and tests/.", null, null, "The tests pass.", "Two readings are plausible.");

    private static AgentHowSignal How(string strategy = "Rename in place and keep a forwarding alias for one release.")
        => new(strategy, "Greeting owns the rename.", "Callers invoke GetFood(); GetMeal() forwards to it.", "The alias is marked obsolete.", "An alias keeps old callers working.", "Existing tests plus one for the alias.", "An alias and a hard rename differ in compatibility cost.");

    private static AgentReclassificationSignal Report(ReclassificationStage stage = ReclassificationStage.How, string evidence = Evidence)
        => new(evidence, stage, Reason);

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        public FakeSignals Signals { get; } = new();

        public FakeBaselineSink Baselines { get; } = new();

        public ImplementationState Baseline { get; set; } = null!;

        public DirectPathRunner Runner()
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options: null, evidence: null, new AgentConversationSupport(Conversations, Store), new AgentClarificationSupport(Signals, Baselines));

        public Task<DirectRunResult> RunAsync(int maxTurns = 4) => Runner().RunAsync(Fixture.TaskOne, H.Definition(maxTurns));

        public Task<DirectRunResult> ContinueAsync(int maxTurns = 4)
        {
            Baseline = Baselines.Saved.Last().Baseline;
            return Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, H.Definition(maxTurns), Baseline);
        }

        public async Task<DirectRunResult> RetryAsync(GateId baselineKey)
        {
            await new ContinuationRecoveryService(H.Service).ResumeAsync(Fixture.TaskOne);
            var baseline = Baselines.Saved.Last(s => s.Gate == baselineKey).Baseline;
            return await Runner().RecoverHumanInputAsync(Fixture.TaskOne, H.Definition(), baseline);
        }

        public WhatService Whats => new(H.Service);

        public HowService Hows => new(H.Service);

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);

        public IEnumerable<string> PromptsOf(AgentSessionId session) => H.Runtime.Sends.Where(s => s.Session == session).Select(s => s.Prompt);
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired, bool howRequired = true, bool whatRequired = false)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance, whatRequired: whatRequired, howRequired: howRequired);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    /// <summary>HOW v1 proposed in signal-call 1 and approved. The next stretch begins at signal-call 2.</summary>
    private static async Task<(Rig Rig, Gate Approved)> HowApprovedAsync(bool whatRequired = false)
    {
        var rig = await NewRigAsync(whatRequired: whatRequired);

        if (whatRequired)
        {
            rig.Signals.RaiseWhat(1, What());
            rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
            var whatProposed = await rig.RunAsync();
            await rig.Whats.ApproveAsync(Fixture.TaskOne, whatProposed.Task!.Task.PendingGate!.Id);

            rig.Signals.RaiseHow(2, How());
            rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
            var proposed = await rig.ContinueAsync();
            var gate = proposed.Task!.Task.PendingGate!;
            await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);
            return (rig, gate);
        }

        rig.Signals.RaiseHow(1, How());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var first = await rig.RunAsync();
        var v1 = first.Task!.Task.PendingGate!;
        await rig.Hows.ApproveAsync(Fixture.TaskOne, v1.Id);
        return (rig, v1);
    }

    /// <summary>Case A: HOW v1 approved; in the next turn the agent edits the workspace, then reports HOW-level evidence; the turn after proposes HOW v2.</summary>
    private static async Task<(Rig Rig, Gate V1, DirectRunResult Result)> MidTurnReclassifiedAsync()
    {
        var (rig, v1) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report());
        rig.Signals.RaiseHow(3, How("Hard rename routed through the audit log."));
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// provisional: alias added"));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.ContinueAsync();
        return (rig, v1, result);
    }

    // ---- case A: mid-turn -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mid_turn_report_invalidates_the_approved_HOW_and_ends_at_a_new_HOW_proposal_in_the_same_conversation()
    {
        var (rig, v1, result) = await MidTurnReclassifiedAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, result.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        var reclassification = Assert.Single(task.Reclassifications);
        Assert.Equal(ReclassificationStage.How, reclassification.Stage);
        Assert.Equal(Evidence, reclassification.Evidence);
        Assert.Equal(Reason, reclassification.Reason);
        Assert.Equal([v1.Id], reclassification.InvalidatedGates.ToArray());
        Assert.Equal(result.Session!.Value.Value, reclassification.Session);
        Assert.Equal(1, reclassification.Turn);   // the first turn of the resumed session's own numbering

        Assert.Equal(GateStatus.Invalidated, task.StatusOf(v1));
        var v2 = task.PendingGate!;
        Assert.Equal(GateType.How, v2.Type);
        Assert.NotEqual(v1.Id, v2.Id);
        Assert.Contains("audit log", v2.How!.Strategy, StringComparison.Ordinal);
        Assert.Equal(task.CurrentSemanticRevisionId, v2.SemanticRevisionId);
        Assert.Equal(1, rig.Events<ReclassificationRecorded>());
        Assert.Equal(1, task.SemanticRevisions.Count(r => r.Kind == SemanticChangeKind.AgentReclassification));

        // nothing was implemented, verified or completed; the run did not stop for a human in between
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Empty(rig.H.Verifier.Ran);
        Assert.False(task.MayImplement);
    }

    [Fact]
    public async Task The_workspace_is_preserved_as_provisional_work_neither_reverted_nor_absorbed()
    {
        var (rig, _, _) = await MidTurnReclassifiedAsync();

        var task = await rig.LoadAsync();
        var reclassification = Assert.Single(task.Reclassifications);
        var now = await rig.H.Workspace.CaptureAsync(@"C:ixture");

        Assert.True(reclassification.ProvisionalWork);
        Assert.True(task.HasProvisionalWork);
        Assert.Equal(now.Fingerprint, reclassification.Workspace);   // the exact state at acceptance is the baseline of the provisional work
        Assert.Contains("provisional: alias added", rig.H.Workspace.Files["src/Greeting.cs"], StringComparison.Ordinal);   // not reverted
        Assert.Empty(task.ImplementationRevisions);                    // not absorbed as an implementation
    }

    [Fact]
    public async Task The_same_conversation_continues_with_only_the_delta_and_no_transcript()
    {
        var (rig, _, result) = await MidTurnReclassifiedAsync();

        var prompts = rig.PromptsOf(result.Session!.Value).ToList();
        Assert.Equal(2, prompts.Count);                       // the approval delta, then the reclassification delta: one session
        Assert.Single(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);

        var delta = prompts[1];
        Assert.StartsWith("Your report of new evidence was recorded: the HOW is invalidated.", delta, StringComparison.Ordinal);
        Assert.Contains("The WHAT still stands.", delta, StringComparison.Ordinal);
        Assert.Contains("provisional work", delta, StringComparison.Ordinal);
        Assert.Contains(AgentHowSignal.ToolName, delta, StringComparison.Ordinal);
        Assert.Contains("Do not create, edit or delete any file", delta, StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", delta, StringComparison.Ordinal);   // the task is not restated
        Assert.DoesNotContain(Evidence, delta, StringComparison.Ordinal);                      // the agent knows what it reported
    }

    [Fact]
    public async Task The_workspace_baseline_is_kept_under_the_reclassification_before_it_is_recorded()
    {
        var (rig, _, _) = await MidTurnReclassifiedAsync();

        var task = await rig.LoadAsync();
        var reclassification = task.Reclassifications.Single();
        var saved = rig.Baselines.Saved.Single(s => s.Gate == new GateId(reclassification.Id.Value));

        Assert.Equal(rig.Baselines.Saved[1].Gate, saved.Gate);
    }

    [Fact]
    public async Task Edits_after_the_report_and_before_the_new_approval_are_a_stage_violation_and_are_not_recorded()
    {
        var (rig, _) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report());
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// before the report"));
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Other.cs"] = "// after the report, before approval"));

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.HowStageViolation, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(HowStageBlock.IsBlockedBy(task));
        Assert.Empty(task.ImplementationRevisions);
        Assert.Empty(rig.H.Verifier.Ran);
    }

    // ---- the agent cannot lower rigor; conflicts fail safely ------------------------------------------------------------------------

    [Fact]
    public async Task More_than_one_signal_in_a_turn_fails_safely_and_records_nothing()
    {
        var (rig, _) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report());
        rig.Signals.RaiseHow(2, How("Another."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var savedBefore = rig.Baselines.Saved.Count;

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("reclassification_conflict", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reclassifications);
        Assert.Equal(0, rig.Events<ReclassificationRecorded>());
        Assert.Equal(savedBefore, rig.Baselines.Saved.Count);

        var twice = await HowApprovedAsync();
        twice.Rig.Signals.RaiseReclassification(2, Report(), Report(ReclassificationStage.What));
        twice.Rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var second = await twice.Rig.ContinueAsync();

        Assert.StartsWith("reclassification_conflict", second.Reason, StringComparison.Ordinal);
        Assert.Equal(0, twice.Rig.Events<ReclassificationRecorded>());
    }

    [Fact]
    public async Task A_report_about_a_stage_that_is_already_being_worked_is_refused_and_records_nothing()
    {
        var rig = await NewRigAsync(howRequired: true);   // the HOW is required and was never approved: nothing to invalidate
        rig.Signals.RaiseReclassification(1, Report());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("reclassification_refused", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reclassifications);
        Assert.Empty(rig.Baselines.Saved);
    }

    [Fact]
    public async Task An_invalid_report_is_refused_before_anything_is_kept_or_recorded()
    {
        var (rig, _) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report(evidence: "   "));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var savedBefore = rig.Baselines.Saved.Count;

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("reclassification_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Equal(savedBefore, rig.Baselines.Saved.Count);
        Assert.Empty((await rig.LoadAsync()).Reclassifications);

        var (long_, _) = await HowApprovedAsync();
        long_.Signals.RaiseReclassification(2, Report(evidence: new string('x', Reclassification.MaxEvidenceLength + 1)));
        long_.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var tooLong = await long_.ContinueAsync();
        Assert.StartsWith("reclassification_invalid", tooLong.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecordable_baseline_means_the_reclassification_is_not_recorded()
    {
        var (rig, _) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.Baselines.Fail = new IOException("disk full");
        rig.Baseline = rig.Baselines.Saved.Last().Baseline;

        var result = await rig.Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, rig.H.Definition(), rig.Baseline);

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("reclassification_baseline_unavailable", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reclassifications);
    }

    // ---- case B: after an implementation was recorded --------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_after_an_implementation_was_recorded_makes_it_and_its_verification_historical_and_escalates_a_direct_task()
    {
        var rig = await NewRigAsync(howRequired: false);
        // turn 1: a partial rename, so an implementation is recorded and its verification fails; turn 2 reports the evidence; turn 3 proposes the HOW
        rig.Signals.RaiseReclassification(2, Report());
        rig.Signals.RaiseHow(3, How("Hard rename routed through the audit log."));
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += " // partial attempt"));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync(maxTurns: 4);

        Assert.Equal(DirectRunStatus.AwaitingHow, result.Status);
        var task = await rig.LoadAsync();
        var reclassification = Assert.Single(task.Reclassifications);
        Assert.Empty(reclassification.InvalidatedGates);   // an additive escalation: there was nothing approved to invalidate
        Assert.False(task.HowRequired);
        Assert.True(task.HowEscalated);                    // persisted by the event, not by configuration
        Assert.False(task.MayImplement);

        var implementation = Assert.Single(task.ImplementationRevisions);
        Assert.False(task.IsImplementationCurrent);
        Assert.True(task.IsVerificationStale);
        Assert.False(task.HasCurrentPassingVerification);
        Assert.Equal(AgenticWorkflow.Core.Verification.VerificationOutcome.Failed, VerificationOutcomeOf(task, implementation.Id));   // kept exactly as recorded
        Assert.Equal(1, rig.Events<ReclassificationRecorded>());
    }

    private static AgenticWorkflow.Core.Verification.VerificationOutcome VerificationOutcomeOf(WorkflowTask task, ImplementationRevisionId id)
        => task.LatestVerificationFor(id)!.Outcome;

    [Fact]
    public async Task After_the_new_approval_the_provisional_work_is_adapted_re_implemented_freshly_verified_and_the_task_completes()
    {
        var (rig, v1, _) = await MidTurnReclassifiedAsync();
        var v2 = (await rig.LoadAsync()).PendingGate!;
        await rig.Hows.ApproveAsync(Fixture.TaskOne, v2.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var done = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Completed, done.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        var implementation = Assert.Single(task.ImplementationRevisions);
        Assert.Equal(task.CurrentSemanticRevisionId, implementation.SemanticRevisionId);   // fresh, bound to the revision that includes the approval
        Assert.True(task.HasCurrentPassingVerification);
        Assert.Null(task.ActiveReclassification);
        Assert.False(task.HasProvisionalWork);

        // the history is intact: v1 is still there, still approved in the history and now invalidated
        Assert.Equal(GateStatus.Invalidated, task.StatusOf(v1));
        Assert.Equal(GateAction.Approve, task.DecisionFor(v1.Id)!.Action);
        Assert.Equal(GateStatus.Approved, task.StatusOf(v2));

        var prompt = rig.PromptsOf(done.Session!.Value).Single();
        Assert.StartsWith("The HOW proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains("Hard rename routed through the audit log.", prompt, StringComparison.Ordinal);
        Assert.Contains("assumptions that the new evidence invalidated", prompt, StringComparison.Ordinal);   // adapt, do not start over
        Assert.Contains("Adapt it", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_pass_can_never_complete_the_run_even_when_the_stage_is_approved_again_and_the_files_still_pass()
    {
        var (rig, _) = await HowApprovedAsync();
        // implemented and verified under HOW v1 ... but with acceptance required the controller stops at ACCEPT; use a direct completion check instead
        var task = await rig.LoadAsync();
        Assert.Empty(task.ImplementationRevisions);

        var (rig2, _, _) = await MidTurnReclassifiedAsync();
        var snapshot = await rig2.H.Service.LoadAsync(Fixture.TaskOne);
        var reclassified = snapshot.Task;

        var eligibility = DirectPathCompletion.EvaluateVerifiedImplementation(reclassified);

        Assert.False(eligibility.IsEligible);
        Assert.Contains(DirectCompletionReasons.RequiredStageNotApproved, eligibility.Reasons);
        await Assert.ThrowsAsync<CompletionNotEligibleException>(() => rig2.H.Service.CompleteVerifiedAsync(snapshot));
    }

    // ---- WHAT reclassification ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_WHAT_report_invalidates_the_WHAT_and_the_HOW_and_asks_for_the_WHAT_first_then_the_HOW_again()
    {
        var (rig, how) = await HowApprovedAsync(whatRequired: true);
        var what = (await rig.LoadAsync()).Gates.Single(g => g.Type == GateType.What);

        rig.Signals.RaiseReclassification(3, Report(ReclassificationStage.What, "The importer must also keep the audit log; the rename is not the whole intent."));
        rig.Signals.RaiseWhat(4, What("Rename GetMeal() to GetFood() and keep the audit log."));
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// provisional"));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var proposed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingWhat, proposed.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(GateStatus.Invalidated, task.StatusOf(what));
        Assert.Equal(GateStatus.Invalidated, task.StatusOf(how));
        Assert.Equal([what.Id, how.Id], task.Reclassifications.Single().InvalidatedGates.ToArray());
        Assert.False(task.WhatSatisfied);
        Assert.Equal(GateType.What, task.PendingGate!.Type);
        Assert.Contains("the WHAT is invalidated", rig.PromptsOf(proposed.Session!.Value).Last(), StringComparison.Ordinal);

        // once the new WHAT is approved the HOW is asked for again; nothing may be implemented in between
        await rig.Whats.ApproveAsync(Fixture.TaskOne, task.PendingGate!.Id);
        rig.Signals.RaiseHow(5, How("Rename and keep the audit log."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var howAgain = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, howAgain.Status);
        Assert.Contains("HOW approval is still required", rig.PromptsOf(howAgain.Session!.Value).First(), StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).ImplementationRevisions);
    }

    // ---- recovery ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_runtime_failure_after_the_report_blocks_a_recoverable_continuation_and_continue_retries_only_that_once()
    {
        var (rig, _) = await HowApprovedAsync();
        rig.Signals.RaiseReclassification(2, Report());
        rig.Signals.RaiseHow(3, How("Hard rename routed through the audit log."));   // the retry's turn is the next collection round (the failed turn never reached one)
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// provisional"));

        // the second turn of the stretch (the revised proposal) cannot be carried out: the runtime fails
        var waits = 0;
        rig.H.Runtime.OnWait = _ => ++waits == 2 ? throw new AgentRuntimeException("the connection was lost") : Task.CompletedTask;

        var failed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);

        var pending = RecoverableContinuation.Find(blocked)!;
        Assert.Equal(HumanInputKind.Reclassification, pending.Kind);
        Assert.Null(pending.Decision);
        Assert.Equal(new GateId(blocked.Reclassifications[0].Id.Value), pending.BaselineKey);
        Assert.Equal(1, rig.Events<ReclassificationRecorded>());

        // the retry: the same conversation is resumed, the agent is told the report was recorded (and that a previous attempt was interrupted)
        rig.H.Runtime.OnWait = null;
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var retried = await rig.RetryAsync(pending.BaselineKey);

        Assert.Equal(DirectRunStatus.AwaitingHow, retried.Status);
        var prompt = rig.PromptsOf(retried.Session!.Value).Single();
        Assert.Contains("interrupted by a runtime failure", prompt, StringComparison.Ordinal);
        Assert.Contains(AgentHowSignal.ToolName, prompt, StringComparison.Ordinal);
        Assert.Equal(1, rig.Events<ReclassificationRecorded>());   // never recorded twice

        // idempotent: the run is no longer blocked, so a second continue is refused and writes nothing
        var events = rig.H.Store.History(Fixture.TaskOne).Count;
        await Assert.ThrowsAsync<ContinuationRefusedException>(() => new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne));
        Assert.Equal(events, rig.H.Store.History(Fixture.TaskOne).Count);
    }

    [Fact]
    public async Task A_first_stretch_that_records_a_reclassification_and_then_fails_is_still_recoverable()
    {
        var rig = await NewRigAsync(howRequired: false);
        rig.Signals.RaiseReclassification(1, Report());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.FailSend = null;
        var sends = 0;
        rig.H.Runtime.OnWait = _ => ++sends == 2 ? throw new AgentRuntimeException("the connection was lost") : Task.CompletedTask;

        var failed = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(HumanInputKind.Reclassification, RecoverableContinuation.Find(blocked)!.Kind);
    }

    [Fact]
    public async Task An_unprocessed_reclassification_is_pending_work_until_a_proposal_gate_opens()
    {
        var (rig, _, _) = await MidTurnReclassifiedAsync();
        var awaiting = await rig.LoadAsync();

        Assert.Null(PendingHumanInput.Find(awaiting));            // a gate is open: the work is in a person's hands

        var (rig2, _) = await HowApprovedAsync();
        Assert.Equal(HumanInputKind.HowApproved, PendingHumanInput.Find(await rig2.LoadAsync())!.Kind);
    }

    // ---- fresh-session fallback ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_session_is_told_the_deterministic_history_including_what_the_agent_reported()
    {
        var (rig, _, _) = await MidTurnReclassifiedAsync();
        var v2 = (await rig.LoadAsync()).PendingGate!;
        await rig.Hows.ApproveAsync(Fixture.TaskOne, v2.Id);

        var entries = HumanInputHistory.Of(await rig.LoadAsync());
        Assert.Equal([HumanInputKind.HowApproved, HumanInputKind.Reclassification, HumanInputKind.HowApproved], entries.Select(e => e.Kind).ToArray());
        Assert.True(HumanInputHistory.TryRender(entries, out var history));
        Assert.True(history.IndexOf("You reported new evidence that invalidated the HOW", StringComparison.Ordinal) < history.LastIndexOf("Human approved the HOW", StringComparison.Ordinal));
        Assert.Contains(Evidence, history, StringComparison.Ordinal);

        rig.Store.Records.Clear();   // nothing recorded to resume
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.NoConversationRecorded), result.Continuation);
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains("Rename the public method", prompt, StringComparison.Ordinal);
        Assert.Contains(Evidence, prompt, StringComparison.Ordinal);
        Assert.Contains("Hard rename routed through the audit log.", prompt, StringComparison.Ordinal);
    }

    // ---- regression: the small task stays direct -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_small_task_that_never_reports_pays_nothing_beyond_the_one_line_of_guidance()
    {
        var rig = await NewRigAsync(howRequired: false);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.Empty(task.Gates);
        Assert.Empty(task.Reclassifications);
        Assert.Equal(0, rig.Events<ReclassificationRecorded>());
        Assert.Single(rig.H.Runtime.Sends);
        Assert.Contains(AgentReclassificationSignal.ToolName, rig.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
        Assert.Empty(rig.Baselines.Saved);
    }

    [Fact]
    public async Task A_runtime_without_the_signal_never_offers_it()
    {
        var rig = await NewRigAsync(howRequired: false);
        rig.Signals.Supported = false;
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.DoesNotContain(AgentReclassificationSignal.ToolName, rig.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
        Assert.Empty(rig.Signals.ReclassificationCalls);
    }
}
