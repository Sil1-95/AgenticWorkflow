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
/// M5C at the controller: a typed signal from the agent (or the human's requirement) puts a durable HOW gate in front of the person before anything is
/// implemented; approval or a request for changes is recorded once with a new semantic revision; the same native conversation is resumed; the WHAT always
/// comes first when both are required; and the stop-before-implementation invariant holds whatever the agent does.
/// </summary>
public sealed class HowStageTests
{
    private static AgentWhatSignal What()
        => new("Rename GetMeal() to GetFood() everywhere it is public.", "GetFood() has the same behavior.", "src/ and tests/.", null, null, "The tests pass.", "Two readings are plausible.");

    private static AgentHowSignal How(string strategy = "Rename in place and keep a forwarding alias for one release.", string rationale = "An alias and a hard rename have different compatibility costs.")
        => new(strategy, "Greeting owns the rename; callers move to GetFood().", "Callers invoke GetFood(); GetMeal() forwards to it.", "The alias is marked obsolete.", "An alias keeps old callers working but leaves a deprecated name.", "Existing tests plus one for the alias.", rationale);

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        public FakeSignals Signals { get; } = new();

        public FakeBaselineSink Baselines { get; } = new();

        public ImplementationState Baseline { get; set; } = null!;

        public DirectPathRunner Runner(DirectRunOptions? options = null)
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options, evidence: null, new AgentConversationSupport(Conversations, Store), new AgentClarificationSupport(Signals, Baselines));

        public Task<DirectRunResult> RunAsync(int maxTurns = 3) => Runner().RunAsync(Fixture.TaskOne, H.Definition(maxTurns));

        public Task<DirectRunResult> ContinueAsync() => Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline);

        public async Task<DirectRunResult> RetryAsync()
        {
            await new ContinuationRecoveryService(H.Service).ResumeAsync(Fixture.TaskOne);
            return await Runner().RecoverHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline);
        }

        public WhatService Whats => new(H.Service);

        public HowService Hows => new(H.Service);

        public AcceptGateService Gates => new(H.Service, H.Workspace);

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired, bool howRequired = false, bool whatRequired = false)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance, whatRequired: whatRequired, howRequired: howRequired);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    /// <summary>The agent reads, proposes the HOW in its first turn (changing nothing) and stops.</summary>
    private static async Task<(Rig Rig, DirectRunResult Proposed, Gate Gate)> ProposedAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired, bool required = true)
    {
        var rig = await NewRigAsync(acceptance, howRequired: required);
        rig.Signals.RaiseHow(1, How());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var proposed = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved.Count == 1 ? rig.Baselines.Saved[0].Baseline : null!;

        return (rig, proposed, proposed.Task!.Task.PendingGate!);
    }

    // ---- the signal ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_proposal_opens_a_durable_HOW_gate_bound_to_the_semantic_revision_and_records_nothing_as_implemented()
    {
        var (rig, proposed, gate) = await ProposedAsync(AcceptanceRequirement.HumanRequired);

        Assert.Equal(DirectRunStatus.AwaitingHow, proposed.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.How, gate.Type);
        Assert.Equal(How().Strategy, gate.How!.Strategy);
        Assert.Equal(1, gate.How.Turn);
        Assert.Equal(task.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Null(gate.What);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Single(task.Gates);   // no ACCEPT gate, even though acceptance is required
        Assert.Empty(rig.H.Verifier.Ran);
        Assert.Equal(1, rig.Events<GateOpened>());
    }

    [Fact]
    public async Task HOW_alone_is_required_without_implying_a_WHAT()
    {
        var (rig, _, _) = await ProposedAsync();
        var task = await rig.LoadAsync();

        Assert.True(task.HowRequired);
        Assert.False(task.WhatRequired);
        Assert.True(task.WhatSatisfied);       // no WHAT was asked for, so none stands in the way
        Assert.False(task.MayImplement);        // the HOW is still open
        Assert.Equal([SemanticChangeKind.Initial], task.SemanticRevisions.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public async Task The_baseline_is_kept_before_the_gate_exists_for_exactly_the_session_and_turn_that_finished()
    {
        var (rig, proposed, gate) = await ProposedAsync();

        var call = Assert.Single(rig.Signals.HowCalls);
        Assert.Equal(proposed.Session, call.Session);
        Assert.Equal(1, call.Turn.Value);

        var saved = Assert.Single(rig.Baselines.Saved);
        Assert.Equal(gate.Id, saved.Gate);
        Assert.Equal(0, saved.GatesAtSave);
        Assert.Contains(rig.H.Runtime.Stops, s => s == proposed.Session);
        Assert.Single(rig.H.Runtime.Sends);
    }

    [Fact]
    public async Task Two_proposals_an_invalid_one_and_an_unkeepable_baseline_record_nothing()
    {
        var two = await NewRigAsync(howRequired: true);
        two.Signals.RaiseHow(1, How("One."), How("Two."));
        two.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var conflict = await two.RunAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, conflict.Status);
        Assert.StartsWith("how_conflict", conflict.Reason, StringComparison.Ordinal);
        Assert.Empty((await two.LoadAsync()).Gates);

        foreach (var invalid in new[]
                 {
                     How(" "),
                     How(new string('x', HowProposal.MaxSectionLength + 1)),
                     How(rationale: new string('r', HowProposal.MaxRationaleLength + 1)),
                 })
        {
            var rig = await NewRigAsync(howRequired: true);
            rig.Signals.RaiseHow(1, invalid);
            rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

            var result = await rig.RunAsync();

            Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
            Assert.StartsWith("how_invalid", result.Reason, StringComparison.Ordinal);
            Assert.Empty((await rig.LoadAsync()).Gates);
            Assert.Empty(rig.Baselines.Saved);
        }

        var unkept = await NewRigAsync(howRequired: true);
        unkept.Baselines.Fail = new IOException("disk full");
        unkept.Signals.RaiseHow(1, How());
        unkept.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var failed = await unkept.RunAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        Assert.StartsWith("how_baseline_unavailable", failed.Reason, StringComparison.Ordinal);
        Assert.Empty((await unkept.LoadAsync()).Gates);
    }

    [Fact]
    public async Task Signals_of_different_kinds_in_one_turn_fail_safely_and_record_nothing()
    {
        var questionAndHow = await NewRigAsync(howRequired: true);
        questionAndHow.Signals.Raise(1, new AgentClarificationSignal("Which dessert?", "Not in the repo."));
        questionAndHow.Signals.RaiseHow(1, How());
        questionAndHow.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var whatAndHow = await NewRigAsync(whatRequired: true, howRequired: true);
        whatAndHow.Signals.RaiseWhat(1, What());
        whatAndHow.Signals.RaiseHow(1, How());
        whatAndHow.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        foreach (var rig in new[] { questionAndHow, whatAndHow })
        {
            var result = await rig.RunAsync();

            Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
            Assert.StartsWith("signal_conflict", result.Reason, StringComparison.Ordinal);
            Assert.Empty((await rig.LoadAsync()).Gates);
            Assert.Empty(rig.Baselines.Saved);
        }
    }

    // ---- the stage cannot be skipped ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_made_after_the_workspace_changed_is_a_HOW_stage_violation_and_is_not_recorded()
    {
        var rig = await NewRigAsync(howRequired: true);
        rig.Signals.RaiseHow(1, How());
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Notes.cs"] = "// started anyway"));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.HowStageViolation, result.Status);
        Assert.StartsWith("work_before_how_approval", result.Reason, StringComparison.Ordinal);
        var task = await rig.LoadAsync();
        Assert.Empty(task.Gates);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(HowStageBlock.IsBlockedBy(task));
        Assert.False(WhatStageBlock.IsBlockedBy(task));   // a distinct violation
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task A_required_stage_blocks_work_done_without_a_proposal_and_never_verifies_or_completes_it()
    {
        var rig = await NewRigAsync(howRequired: true);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.HowStageViolation, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task A_required_stage_reminds_an_agent_that_finished_without_proposing_and_a_stage_never_proposed_is_a_violation()
    {
        var reminded = await NewRigAsync(howRequired: true);
        reminded.Signals.RaiseHow(2, How());   // nothing in the first turn; proposed after the reminder
        reminded.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        reminded.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await reminded.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, result.Status);
        Assert.Equal(2, reminded.H.Runtime.Sends.Count);
        Assert.Contains(AgentHowSignal.ToolName, reminded.H.Runtime.Sends[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("Do not create, edit or delete", reminded.H.Runtime.Sends[1].Prompt, StringComparison.Ordinal);

        var never = await NewRigAsync(howRequired: true);
        never.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        never.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var violated = await never.RunAsync(maxTurns: 2);

        Assert.Equal(DirectRunStatus.HowStageViolation, violated.Status);
        Assert.StartsWith("how_not_proposed", violated.Reason, StringComparison.Ordinal);
        Assert.True(HowStageBlock.IsBlockedBy(await never.LoadAsync()));
    }

    [Fact]
    public async Task A_required_stage_is_rejected_up_front_when_the_runtime_cannot_receive_a_proposal()
    {
        var rig = await NewRigAsync(howRequired: true);
        rig.Signals.Supported = false;

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("how_unavailable", result.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.H.Runtime.Starts);
    }

    [Fact]
    public async Task The_instructions_say_must_when_required_a_narrow_may_otherwise_and_nothing_when_not_offered()
    {
        var required = await NewRigAsync(howRequired: true);
        required.Signals.RaiseHow(1, How());
        required.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        await required.RunAsync();

        var optional = await NewRigAsync();
        optional.H.Runtime.Enqueue(optional.H.Renames());
        await optional.RunAsync();

        var withheld = await NewRigAsync();
        withheld.Signals.Supported = false;
        withheld.H.Runtime.Enqueue(withheld.H.Renames());
        await withheld.RunAsync();

        var requiredPrompt = required.H.Runtime.Sends[0].Prompt;
        Assert.Contains("requires a human-approved HOW", requiredPrompt, StringComparison.Ordinal);
        Assert.Contains(AgentHowSignal.ToolName, requiredPrompt, StringComparison.Ordinal);

        var optionalPrompt = optional.H.Runtime.Sends[0].Prompt;
        Assert.Contains(AgentHowSignal.ToolName, optionalPrompt, StringComparison.Ordinal);
        Assert.Contains("materially different approaches", optionalPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not use it merely because the task is difficult", optionalPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(AgentHowSignal.ToolName, withheld.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_HOW_instruction_says_a_proposal_is_not_a_question_and_an_open_human_decision_is_clarified_first()
    {
        var required = await NewRigAsync(howRequired: true);
        required.Signals.RaiseHow(1, How());
        required.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        await required.RunAsync();

        var optional = await NewRigAsync();
        optional.H.Runtime.Enqueue(optional.H.Renames());
        await optional.RunAsync();

        foreach (var prompt in new[] { required.H.Runtime.Sends[0].Prompt, optional.H.Runtime.Sends[0].Prompt })
        {
            Assert.Contains("A HOW proposal is a contract a person approves, not a question", prompt, StringComparison.Ordinal);
            Assert.Contains($"call the {AgentClarificationSignal.ToolName} tool first, and propose the HOW once it is answered", prompt, StringComparison.Ordinal);
        }
    }

    // ---- deciding ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_records_one_decision_and_one_HOW_revision_returns_to_RUNNING_and_implements_nothing()
    {
        var (rig, _, gate) = await ProposedAsync();

        var outcome = await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);

        var task = outcome.Change.Task;
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanHowApproval, task.CurrentSemanticRevision.Kind);
        Assert.Equal(GateStatus.Approved, task.StatusOf(gate));
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.True(task.HowSatisfied);
        Assert.True(task.MayImplement);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task A_second_approval_an_unknown_gate_and_the_wrong_kind_of_gate_are_refused_and_record_nothing()
    {
        var (rig, _, gate) = await ProposedAsync();

        var unknown = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Hows.ApproveAsync(Fixture.TaskOne, new GateId(Guid.NewGuid())));
        Assert.Equal(GateRefusal.UnknownGate, unknown.Reason);

        await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);

        var again = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id));
        Assert.Equal(GateRefusal.AlreadyDecided, again.Reason);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());

        // a HOW gate is neither a WHAT nor answerable nor acceptable
        var (other, _, howGate) = await ProposedAsync();
        var asWhat = await Assert.ThrowsAsync<GateRefusedException>(() => other.Whats.ApproveAsync(Fixture.TaskOne, howGate.Id));
        Assert.Equal(GateRefusal.WrongGateKind, asWhat.Reason);
        var answered = await Assert.ThrowsAsync<GateRefusedException>(() => new ClarificationService(other.H.Service).AnswerAsync(Fixture.TaskOne, howGate.Id, "x"));
        Assert.Equal(GateRefusal.WrongGateKind, answered.Reason);
        var accepted = await Assert.ThrowsAsync<GateRefusedException>(() => other.Gates.AcceptAsync(Fixture.TaskOne, howGate.Id, "ws"));
        Assert.Equal(GateRefusal.WrongGateKind, accepted.Reason);
        Assert.Equal(0, other.Events<GateDecided>());

        // and an ACCEPT gate is not approvable as a HOW
        var accept = await NewRigAsync(AcceptanceRequirement.HumanRequired);
        accept.H.Runtime.Enqueue(accept.H.Renames());
        var awaiting = await accept.RunAsync();
        var wrong = await Assert.ThrowsAsync<GateRefusedException>(() => accept.Hows.ApproveAsync(Fixture.TaskOne, awaiting.Task!.Task.PendingGate!.Id));
        Assert.Equal(GateRefusal.WrongGateKind, wrong.Reason);
    }

    [Fact]
    public async Task Approval_resumes_the_same_conversation_with_the_approved_HOW_and_nothing_else_restated()
    {
        var (rig, proposed, gate) = await ProposedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;
        await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(AgentContinuationKind.ResumedNative, result.Continuation!.Kind);
        Assert.Equal(recorded, Assert.Single(rig.Conversations.Resumes).Conversation);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.NotEqual(proposed.Session, result.Session);

        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == result.Session!.Value).Prompt;
        Assert.StartsWith("The HOW proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains("Approved HOW:", prompt, StringComparison.Ordinal);
        Assert.Contains(How().Strategy, prompt, StringComparison.Ordinal);
        Assert.Contains(How().RisksAndTradeoffs, prompt, StringComparison.Ordinal);
        Assert.EndsWith("Continue from the current workspace using this implementation strategy.", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);   // the task is not restated
        Assert.DoesNotContain(AgentHowSignal.ToolName, prompt, StringComparison.Ordinal);      // nothing more to propose
    }

    [Fact]
    public async Task Requesting_changes_sends_the_feedback_into_the_same_conversation_and_the_stage_stays_unsatisfied()
    {
        var (rig, _, gate) = await ProposedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;

        var outcome = await rig.Gates.RequestChangesAsync(Fixture.TaskOne, gate.Id, "  Prefer a hard rename; no alias.  ");

        var task = outcome.Change.Task;
        Assert.Equal(GateStatus.ChangesRequested, task.StatusOf(gate));
        Assert.Equal(SemanticChangeKind.HumanHowChanges, task.CurrentSemanticRevision.Kind);
        Assert.Equal("Prefer a hard rename; no alias.", task.DecisionFor(gate.Id)!.Feedback);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.False(task.MayImplement);

        rig.Signals.RaiseHow(2, How("Hard rename, no alias."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, result.Status);   // revised, not implemented
        Assert.Equal(recorded, Assert.Single(rig.Conversations.Resumes).Conversation);
        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == result.Session!.Value).Prompt;
        Assert.StartsWith("The human requested changes to the HOW proposal:", prompt, StringComparison.Ordinal);
        Assert.Contains("Prefer a hard rename; no alias.", prompt, StringComparison.Ordinal);
        Assert.Contains("Revise the HOW strategy", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Do not implement yet.", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_round_of_changes_then_approval_keeps_v1_historical_v2_current_and_implements_only_after_approval()
    {
        var (rig, _, v1) = await ProposedAsync();
        await rig.Gates.RequestChangesAsync(Fixture.TaskOne, v1.Id, "Hard rename only.");

        rig.Signals.RaiseHow(2, How("Hard rename, no alias."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var second = await rig.ContinueAsync();
        Assert.Equal(DirectRunStatus.AwaitingHow, second.Status);

        var v2 = second.Task!.Task.PendingGate!;
        var atV2 = await rig.LoadAsync();
        Assert.NotEqual(v1.Id, v2.Id);
        Assert.False(atV2.IsCurrent(atV2.Gates[0]));
        Assert.True(atV2.IsCurrent(v2));
        Assert.Equal(How().Strategy, atV2.Gates[0].How!.Strategy);
        Assert.Equal("Hard rename, no alias.", v2.How!.Strategy);
        Assert.Empty(atV2.ImplementationRevisions);

        await Assert.ThrowsAsync<GateRefusedException>(() => rig.Hows.ApproveAsync(Fixture.TaskOne, v1.Id));
        await rig.Hows.ApproveAsync(Fixture.TaskOne, v2.Id);

        rig.Baseline = rig.Baselines.Saved.Last().Baseline;
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var done = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Completed, done.Status);
        var final = await rig.LoadAsync();
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanHowChanges, SemanticChangeKind.HumanHowApproval],
            final.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(final.CurrentSemanticRevisionId, final.CurrentImplementationRevision!.SemanticRevisionId);
        Assert.Equal(2, rig.Events<GateDecided>());
        Assert.Equal(2, rig.Events<GateOpened>());
    }

    // ---- WHAT before HOW ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_both_required_the_WHAT_comes_first_and_a_premature_HOW_is_not_recorded()
    {
        var rig = await NewRigAsync(whatRequired: true, howRequired: true);
        rig.Signals.RaiseHow(1, How());      // proposed before the WHAT: not recorded
        rig.Signals.RaiseWhat(2, What());    // proposed after the reminder
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingWhat, result.Status);
        var task = await rig.LoadAsync();
        var gate = Assert.Single(task.Gates);
        Assert.Equal(GateType.What, gate.Type);
        Assert.Null(task.LatestHowGate);
    }

    [Fact]
    public async Task After_the_WHAT_is_approved_the_HOW_is_still_required_so_the_agent_may_not_implement_yet()
    {
        var rig = await NewRigAsync(whatRequired: true, howRequired: true);
        rig.Signals.RaiseWhat(1, What());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var proposed = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved[0].Baseline;

        var approved = await rig.Whats.ApproveAsync(Fixture.TaskOne, proposed.Task!.Task.PendingGate!.Id);

        Assert.True(approved.Change.Task.WhatSatisfied);
        Assert.False(approved.Change.Task.HowSatisfied);
        Assert.False(approved.Change.Task.MayImplement);

        rig.Signals.RaiseHow(2, How());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var next = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, next.Status);
        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == next.Session!.Value).Prompt;
        Assert.Contains("The WHAT is approved. HOW approval is still required.", prompt, StringComparison.Ordinal);
        Assert.Contains("Produce the HOW proposal", prompt, StringComparison.Ordinal);
        Assert.Contains("do not implement yet.", prompt, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).ImplementationRevisions);
    }

    [Fact]
    public async Task Work_done_between_the_WHAT_approval_and_the_HOW_proposal_is_a_HOW_stage_violation()
    {
        var rig = await NewRigAsync(whatRequired: true, howRequired: true);
        rig.Signals.RaiseWhat(1, What());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var proposed = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved[0].Baseline;
        await rig.Whats.ApproveAsync(Fixture.TaskOne, proposed.Task!.Task.PendingGate!.Id);

        rig.H.Runtime.Enqueue(rig.H.Renames());   // implements instead of proposing the HOW
        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.HowStageViolation, result.Status);
        var task = await rig.LoadAsync();
        Assert.True(HowStageBlock.IsBlockedBy(task));
        Assert.Empty(task.ImplementationRevisions);
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task Both_stages_approved_in_order_the_work_is_implemented_and_completed_with_the_ordered_revisions()
    {
        var rig = await NewRigAsync(whatRequired: true, howRequired: true);
        rig.Signals.RaiseWhat(1, What());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var whatProposed = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved[0].Baseline;
        await rig.Whats.ApproveAsync(Fixture.TaskOne, whatProposed.Task!.Task.PendingGate!.Id);

        rig.Signals.RaiseHow(2, How());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var howProposed = await rig.ContinueAsync();
        rig.Baseline = rig.Baselines.Saved.Last().Baseline;
        Assert.Equal(DirectRunStatus.AwaitingHow, howProposed.Status);

        var howGate = howProposed.Task!.Task.PendingGate!;
        Assert.Equal(howProposed.Task.Task.CurrentSemanticRevisionId, howGate.SemanticRevisionId);   // bound to the revision that includes the approved WHAT

        await rig.Hows.ApproveAsync(Fixture.TaskOne, howGate.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var done = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Completed, done.Status);
        var final = await rig.LoadAsync();
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatApproval, SemanticChangeKind.HumanHowApproval],
            final.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.True(final.MayImplement);
        Assert.Equal(final.CurrentSemanticRevisionId, final.CurrentImplementationRevision!.SemanticRevisionId);
    }

    // ---- compatibility -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired, DirectRunStatus.Completed)]
    [InlineData(AcceptanceRequirement.HumanRequired, DirectRunStatus.AwaitingAcceptance)]
    public async Task After_approval_verified_work_ends_as_conditional_acceptance_says(AcceptanceRequirement acceptance, DirectRunStatus expected)
    {
        var (rig, _, gate) = await ProposedAsync(acceptance);
        await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(expected, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(acceptance == AcceptanceRequirement.HumanRequired, task.PendingGate is { Type: GateType.Accept });
        Assert.Equal(acceptance == AcceptanceRequirement.NotRequired, task.WorkflowState == WorkflowState.Completed);
    }

    [Fact]
    public async Task A_task_that_never_proposes_pays_nothing_no_gate_and_no_extra_turn()
    {
        var rig = await NewRigAsync();   // not required: the agent chooses, and does not
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Empty((await rig.LoadAsync()).Gates);
        Assert.Single(rig.H.Runtime.Sends);
    }

    [Fact]
    public async Task An_unrequired_HOW_does_not_block_a_task_that_proposes_it_anyway_and_the_agent_may_choose_it()
    {
        var (rig, proposed, _) = await ProposedAsync(required: false);

        Assert.Equal(DirectRunStatus.AwaitingHow, proposed.Status);
        Assert.False((await rig.LoadAsync()).HowRequired);
    }

    [Fact]
    public async Task A_CLARIFY_question_still_works_beside_the_HOW_capability()
    {
        var rig = await NewRigAsync(howRequired: true);
        rig.Signals.Raise(1, new AgentClarificationSignal("Which dessert?", "Not in the repo."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var asked = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingClarification, asked.Status);
        var task = await rig.LoadAsync();
        await new ClarificationService(rig.H.Service).AnswerAsync(Fixture.TaskOne, task.PendingGate!.Id, "Tiramisu.");
        var after = await rig.LoadAsync();
        Assert.False(after.MayImplement);   // answering a question does not satisfy a required HOW
    }

    // ---- history and the fresh fallback ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_conversation_that_cannot_be_resumed_falls_back_explicitly_with_the_ordered_HOW_history_and_no_summary()
    {
        var (rig, _, v1) = await ProposedAsync();
        await rig.Gates.RequestChangesAsync(Fixture.TaskOne, v1.Id, "Hard rename only.");
        rig.Signals.RaiseHow(2, How("Hard rename, no alias."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var second = await rig.ContinueAsync();
        await rig.Hows.ApproveAsync(Fixture.TaskOne, second.Task!.Task.PendingGate!.Id);

        var entries = HumanInputHistory.Of(await rig.LoadAsync());
        Assert.Equal([HumanInputKind.HowChangesRequested, HumanInputKind.HowApproved], entries.Select(e => e.Kind).ToArray());
        Assert.True(HumanInputHistory.TryRender(entries, out var history));
        Assert.True(history.IndexOf("Hard rename only.", StringComparison.Ordinal) < history.IndexOf("Human approved the HOW", StringComparison.Ordinal));

        rig.Store.Records.Clear();   // nothing recorded to resume
        rig.Baseline = rig.Baselines.Saved.Last().Baseline;
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.NoConversationRecorded), result.Continuation);
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains("Rename the public method", prompt, StringComparison.Ordinal);   // the fresh session is told the task
        Assert.Contains("Hard rename only.", prompt, StringComparison.Ordinal);
        Assert.Contains("Hard rename, no alias.", prompt, StringComparison.Ordinal);      // the approved artifact, verbatim
        Assert.Contains("Human approved the HOW", prompt, StringComparison.Ordinal);
    }

    // ---- recovery ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_after_approval_leaves_the_run_BLOCKED_and_continue_retries_without_a_second_decision_revision_or_artifact()
    {
        var (rig, _, gate) = await ProposedAsync();
        await rig.Hows.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        var failed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.StartsWith(RecoverableContinuation.BlockedReasonPrefix, blocked.StateReason, StringComparison.Ordinal);
        var pending = RecoverableContinuation.Find(blocked)!;
        Assert.Equal(HumanInputKind.HowApproved, pending.Kind);
        Assert.Equal(How().Strategy, pending.How!.Strategy);
        Assert.Empty(blocked.ImplementationRevisions);

        rig.Conversations.FailResume = null;
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var retried = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.Completed, retried.Status);
        Assert.Equal(AgentContinuationKind.ResumedNative, retried.Continuation!.Kind);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.Equal(1, rig.Events<GateOpened>());
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains("The HOW proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains(How().Strategy, prompt, StringComparison.Ordinal);
        Assert.Contains("Inspect the current workspace", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_after_a_request_for_changes_is_recoverable_the_same_way()
    {
        var (rig, _, gate) = await ProposedAsync();
        await rig.Gates.RequestChangesAsync(Fixture.TaskOne, gate.Id, "Narrower.");
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        await rig.ContinueAsync();

        var blocked = await rig.LoadAsync();
        var pending = RecoverableContinuation.Find(blocked)!;
        Assert.Equal(HumanInputKind.HowChangesRequested, pending.Kind);
        Assert.Equal("Narrower.", pending.Text);

        rig.Conversations.FailResume = null;
        rig.Signals.RaiseHow(2, How("Narrower."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var retried = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingHow, retried.Status);
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.Contains("Do not implement yet.", rig.H.Runtime.Sends.Last().Prompt, StringComparison.Ordinal);
    }
}
