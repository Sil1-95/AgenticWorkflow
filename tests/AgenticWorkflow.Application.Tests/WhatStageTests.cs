using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M5B at the controller: a typed signal from the agent (or the human's requirement) puts a durable WHAT gate in front of the person before anything is
/// implemented; approval or a request for changes is recorded once with a new semantic revision; the same native conversation is resumed; and the
/// stop-before-implementation invariant holds whatever the agent does.
/// </summary>
public sealed class WhatStageTests
{
    private static AgentWhatSignal What(string objective = "Rename GetMeal() to GetFood() everywhere it is public.", string rationale = "Two readings of 'everywhere' are plausible.")
        => new(objective, "GetMeal() no longer exists; GetFood() has the same behavior.", "src/ and tests/.", "Keep the public API otherwise unchanged.", "No change to Fixture.sln.", "The tests pass and no GetMeal reference remains.", rationale);

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

        public AcceptGateService Gates => new(H.Service, H.Workspace);

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired, bool required = false)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance, whatRequired: required);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    /// <summary>The agent reads, proposes the WHAT in its first turn (changing nothing) and stops.</summary>
    private static async Task<(Rig Rig, DirectRunResult Proposed, Gate Gate)> ProposedAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired, bool required = true)
    {
        var rig = await NewRigAsync(acceptance, required);
        rig.Signals.RaiseWhat(1, What());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var proposed = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved.Count == 1 ? rig.Baselines.Saved[0].Baseline : null!;

        return (rig, proposed, proposed.Task!.Task.PendingGate!);
    }

    // ---- 16-20: the agent -> controller signal -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_proposal_opens_a_durable_WHAT_gate_bound_to_the_semantic_revision_and_records_nothing_as_implemented()
    {
        var (rig, proposed, gate) = await ProposedAsync(AcceptanceRequirement.HumanRequired);

        Assert.Equal(DirectRunStatus.AwaitingWhat, proposed.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.What, gate.Type);
        Assert.Equal(What().Objective, gate.What!.Objective);
        Assert.Equal(1, gate.What.Turn);
        Assert.Equal(task.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Single(task.Gates);   // no ACCEPT gate, even though acceptance is required
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task The_runtime_is_asked_for_exactly_the_session_and_turn_that_just_finished_and_the_baseline_is_kept_before_the_gate_exists()
    {
        var (rig, proposed, gate) = await ProposedAsync();

        var call = Assert.Single(rig.Signals.WhatCalls);
        Assert.Equal(proposed.Session, call.Session);
        Assert.Equal(1, call.Turn.Value);

        var saved = Assert.Single(rig.Baselines.Saved);
        Assert.Equal(gate.Id, saved.Gate);
        Assert.Equal(0, saved.GatesAtSave);
    }

    [Fact]
    public async Task Two_proposals_in_one_turn_an_invalid_or_oversized_one_and_an_unkeepable_baseline_record_nothing()
    {
        var two = await NewRigAsync(required: true);
        two.Signals.RaiseWhat(1, What("One."), What("Two."));
        two.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var conflict = await two.RunAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, conflict.Status);
        Assert.StartsWith("what_conflict", conflict.Reason, StringComparison.Ordinal);
        Assert.Empty((await two.LoadAsync()).Gates);

        foreach (var invalid in new[]
                 {
                     What(" "),
                     What(new string('x', WhatProposal.MaxSectionLength + 1)),
                     What(rationale: new string('r', WhatProposal.MaxRationaleLength + 1)),
                 })
        {
            var rig = await NewRigAsync(required: true);
            rig.Signals.RaiseWhat(1, invalid);
            rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

            var result = await rig.RunAsync();

            Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
            Assert.StartsWith("what_invalid", result.Reason, StringComparison.Ordinal);
            Assert.Empty((await rig.LoadAsync()).Gates);
            Assert.Empty(rig.Baselines.Saved);
        }

        var unkept = await NewRigAsync(required: true);
        unkept.Baselines.Fail = new IOException("disk full");
        unkept.Signals.RaiseWhat(1, What());
        unkept.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var failed = await unkept.RunAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        Assert.StartsWith("what_baseline_unavailable", failed.Reason, StringComparison.Ordinal);
        Assert.Empty((await unkept.LoadAsync()).Gates);
    }

    [Fact]
    public async Task A_proposal_ends_the_stretch_cleanly_with_the_native_process_stopped_and_nothing_verified()
    {
        var (rig, proposed, _) = await ProposedAsync();

        Assert.Contains(rig.H.Runtime.Stops, s => s == proposed.Session);
        Assert.Single(rig.H.Runtime.Sends);   // no extra turn was spent
        Assert.Empty(rig.H.Verifier.Ran);
        Assert.Equal(1, rig.Events<GateOpened>());
    }

    [Fact]
    public async Task A_proposal_made_after_the_workspace_changed_is_a_stage_violation_and_is_not_recorded()
    {
        var rig = await NewRigAsync(required: true);
        rig.Signals.RaiseWhat(1, What());
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Notes.cs"] = "// started anyway"));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.WhatStageViolation, result.Status);
        Assert.StartsWith("work_before_what_approval", result.Reason, StringComparison.Ordinal);
        var task = await rig.LoadAsync();
        Assert.Empty(task.Gates);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(WhatStageBlock.IsBlockedBy(task));
        Assert.Empty(rig.H.Verifier.Ran);
    }

    // ---- the stage cannot be skipped ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_required_stage_blocks_work_done_without_a_proposal_and_never_verifies_or_completes_it()
    {
        var rig = await NewRigAsync(required: true);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.WhatStageViolation, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Empty(rig.H.Verifier.Ran);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_required_stage_reminds_an_agent_that_finished_without_proposing_and_then_accepts_the_proposal()
    {
        var rig = await NewRigAsync(required: true);
        rig.Signals.RaiseWhat(2, What());   // nothing in the first turn; proposed after the reminder
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingWhat, result.Status);
        Assert.Equal(2, rig.H.Runtime.Sends.Count);
        Assert.Contains(AgentWhatSignal.ToolName, rig.H.Runtime.Sends[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("Do not create, edit or delete", rig.H.Runtime.Sends[1].Prompt, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).ImplementationRevisions);
    }

    [Fact]
    public async Task A_required_stage_that_is_never_proposed_within_the_turn_budget_is_a_violation()
    {
        var rig = await NewRigAsync(required: true);
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync(maxTurns: 2);

        Assert.Equal(DirectRunStatus.WhatStageViolation, result.Status);
        Assert.StartsWith("what_not_proposed", result.Reason, StringComparison.Ordinal);
        Assert.True(WhatStageBlock.IsBlockedBy(await rig.LoadAsync()));
    }

    [Fact]
    public async Task A_required_stage_is_rejected_up_front_when_the_runtime_cannot_receive_a_proposal()
    {
        var rig = await NewRigAsync(required: true);
        rig.Signals.Supported = false;

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("what_unavailable", result.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.H.Runtime.Starts);   // nothing was started
    }

    [Fact]
    public async Task The_instructions_say_must_when_required_may_only_for_open_outcomes_otherwise_and_nothing_when_not_offered()
    {
        var required = await NewRigAsync(required: true);
        required.Signals.RaiseWhat(1, What());
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
        Assert.Contains("requires a human-approved WHAT", requiredPrompt, StringComparison.Ordinal);
        Assert.Contains(AgentWhatSignal.ToolName, requiredPrompt, StringComparison.Ordinal);

        var optionalPrompt = optional.H.Runtime.Sends[0].Prompt;
        Assert.Contains(AgentWhatSignal.ToolName, optionalPrompt, StringComparison.Ordinal);
        Assert.Contains("genuinely open", optionalPrompt, StringComparison.Ordinal);
        Assert.Contains("If the outcome is clear, do not use it", optionalPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain(AgentWhatSignal.ToolName, withheld.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_WHAT_instruction_says_a_proposal_is_not_a_question_and_an_open_human_decision_is_clarified_first()
    {
        var required = await NewRigAsync(required: true);
        required.Signals.RaiseWhat(1, What());
        required.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        await required.RunAsync();

        var optional = await NewRigAsync();
        optional.H.Runtime.Enqueue(optional.H.Renames());
        await optional.RunAsync();

        foreach (var prompt in new[] { required.H.Runtime.Sends[0].Prompt, optional.H.Runtime.Sends[0].Prompt })
        {
            Assert.Contains("A WHAT proposal is a contract a person approves, not a question", prompt, StringComparison.Ordinal);
            Assert.Contains($"call the {AgentClarificationSignal.ToolName} tool first, and propose the WHAT once it is answered", prompt, StringComparison.Ordinal);
            Assert.Contains("BEFORE you propose any WHAT or HOW", prompt, StringComparison.Ordinal);
            Assert.Contains("feedback on a proposal that already exists is not a substitute", prompt, StringComparison.Ordinal);   // requesting changes is not the clarification mechanism
        }
    }

    [Fact]
    public async Task The_not_a_question_rule_names_the_clarification_tool_only_where_the_runtime_offers_it_and_covers_every_WHAT_state()
    {
        var task = (await (await NewRigAsync(required: true)).LoadAsync());

        var withTool = DirectPathPrompts.WhatGuidance(task, mayAskForClarification: true)!;
        var withoutTool = DirectPathPrompts.WhatGuidance(task, mayAskForClarification: false)!;

        Assert.Contains(AgentClarificationSignal.ToolName, withTool, StringComparison.Ordinal);
        Assert.Contains("not a question", withoutTool, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentClarificationSignal.ToolName, withoutTool, StringComparison.Ordinal);
    }

    // ---- 21-23: deciding ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_records_one_decision_and_one_approval_revision_returns_to_RUNNING_and_implements_nothing()
    {
        var (rig, _, gate) = await ProposedAsync();

        var outcome = await rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id);

        var task = outcome.Change.Task;
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanWhatApproval, task.CurrentSemanticRevision.Kind);
        Assert.Equal(GateStatus.Approved, task.StatusOf(gate));
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Empty(rig.H.Verifier.Ran);
    }

    [Fact]
    public async Task A_second_approval_an_unknown_gate_a_stale_one_and_the_wrong_kind_of_gate_are_refused_and_record_nothing()
    {
        var (rig, _, gate) = await ProposedAsync();

        var unknown = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Whats.ApproveAsync(Fixture.TaskOne, new GateId(Guid.NewGuid())));
        Assert.Equal(GateRefusal.UnknownGate, unknown.Reason);
        Assert.Equal(0, rig.Events<GateDecided>());

        await rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id);

        var again = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id));
        Assert.Equal(GateRefusal.AlreadyDecided, again.Reason);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());

        // an ACCEPT gate is not approvable
        var accept = await NewRigAsync(AcceptanceRequirement.HumanRequired);
        accept.H.Runtime.Enqueue(accept.H.Renames());
        var awaiting = await accept.RunAsync();
        var wrong = await Assert.ThrowsAsync<GateRefusedException>(() => accept.Whats.ApproveAsync(Fixture.TaskOne, awaiting.Task!.Task.PendingGate!.Id));
        Assert.Equal(GateRefusal.WrongGateKind, wrong.Reason);

        // and a WHAT gate is not answerable or acceptable
        var (other, _, whatGate) = await ProposedAsync();
        var answered = await Assert.ThrowsAsync<GateRefusedException>(() => new ClarificationService(other.H.Service).AnswerAsync(Fixture.TaskOne, whatGate.Id, "x"));
        Assert.Equal(GateRefusal.WrongGateKind, answered.Reason);
        var accepted = await Assert.ThrowsAsync<GateRefusedException>(() => other.Gates.AcceptAsync(Fixture.TaskOne, whatGate.Id, "ws"));
        Assert.Equal(GateRefusal.WrongGateKind, accepted.Reason);
        Assert.Equal(0, other.Events<GateDecided>());
    }

    [Fact]
    public async Task Approval_resumes_the_same_conversation_with_the_approved_WHAT_and_nothing_else_restated()
    {
        var (rig, proposed, gate) = await ProposedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;
        await rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(AgentContinuationKind.ResumedNative, result.Continuation!.Kind);
        Assert.Equal(recorded, Assert.Single(rig.Conversations.Resumes).Conversation);
        Assert.Single(rig.H.Runtime.Starts);   // no new session was started
        Assert.NotEqual(proposed.Session, result.Session);

        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == result.Session!.Value).Prompt;
        Assert.StartsWith("The WHAT proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains("Approved WHAT:", prompt, StringComparison.Ordinal);
        Assert.Contains(What().Objective, prompt, StringComparison.Ordinal);
        Assert.Contains(What().AcceptanceConditions, prompt, StringComparison.Ordinal);
        Assert.EndsWith("Continue from the current workspace.", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);   // the task is not restated
        Assert.DoesNotContain(AgentWhatSignal.ToolName, prompt, StringComparison.Ordinal);      // nothing more to propose
    }

    [Fact]
    public async Task Requesting_changes_sends_the_feedback_into_the_same_conversation_and_the_stage_stays_unsatisfied()
    {
        var (rig, _, gate) = await ProposedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;

        var outcome = await rig.Gates.RequestChangesAsync(Fixture.TaskOne, gate.Id, "  Keep GetMeal() as an obsolete alias.  ");

        var task = outcome.Change.Task;
        Assert.Equal(GateStatus.ChangesRequested, task.StatusOf(gate));
        Assert.Equal(SemanticChangeKind.HumanWhatChanges, task.CurrentSemanticRevision.Kind);
        Assert.Equal("Keep GetMeal() as an obsolete alias.", task.DecisionFor(gate.Id)!.Feedback);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.False(task.MayImplement);

        rig.Signals.RaiseWhat(2, What("Rename GetMeal() to GetFood() and keep an obsolete alias."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingWhat, result.Status);   // revised, not implemented
        Assert.Equal(recorded, Assert.Single(rig.Conversations.Resumes).Conversation);
        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == result.Session!.Value).Prompt;
        Assert.StartsWith("The human requested changes to the WHAT proposal:", prompt, StringComparison.Ordinal);
        Assert.Contains("Keep GetMeal() as an obsolete alias.", prompt, StringComparison.Ordinal);
        Assert.Contains("Revise the WHAT contract", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Do not implement yet.", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);
    }

    // ---- 24: the whole loop -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_round_of_changes_then_approval_keeps_v1_historical_v2_current_and_implements_only_after_approval()
    {
        var (rig, _, v1) = await ProposedAsync();
        await rig.Gates.RequestChangesAsync(Fixture.TaskOne, v1.Id, "Cap the scope to src/ only.");

        rig.Signals.RaiseWhat(2, What("Rename GetMeal() to GetFood() in src/ only."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var second = await rig.ContinueAsync();
        Assert.Equal(DirectRunStatus.AwaitingWhat, second.Status);

        var v2 = second.Task!.Task.PendingGate!;
        var atV2 = await rig.LoadAsync();
        Assert.NotEqual(v1.Id, v2.Id);
        Assert.False(atV2.IsCurrent(atV2.Gates[0]));        // v1 stays historical
        Assert.True(atV2.IsCurrent(v2));                     // v2 is current
        Assert.Equal("Rename GetMeal() to GetFood() everywhere it is public.", atV2.Gates[0].What!.Objective);
        Assert.Equal("Rename GetMeal() to GetFood() in src/ only.", v2.What!.Objective);
        Assert.Empty(atV2.ImplementationRevisions);          // nothing was implemented before approval

        // v1 can no longer be approved; v2 can
        await Assert.ThrowsAsync<GateRefusedException>(() => rig.Whats.ApproveAsync(Fixture.TaskOne, v1.Id));
        await rig.Whats.ApproveAsync(Fixture.TaskOne, v2.Id);

        // the baseline of v2's stretch is the one saved when v2 opened
        rig.Baseline = rig.Baselines.Saved.Last().Baseline;
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var done = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Completed, done.Status);
        var final = await rig.LoadAsync();
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatChanges, SemanticChangeKind.HumanWhatApproval],
            final.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(final.CurrentSemanticRevisionId, final.CurrentImplementationRevision!.SemanticRevisionId);   // implemented under the approval
        Assert.Equal(2, rig.Events<GateDecided>());
        Assert.Equal(2, rig.Events<GateOpened>());
    }

    // ---- 28-31: compatibility -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired, DirectRunStatus.Completed)]
    [InlineData(AcceptanceRequirement.HumanRequired, DirectRunStatus.AwaitingAcceptance)]
    public async Task After_approval_verified_work_ends_as_conditional_acceptance_says(AcceptanceRequirement acceptance, DirectRunStatus expected)
    {
        var (rig, _, gate) = await ProposedAsync(acceptance);
        await rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(expected, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(acceptance == AcceptanceRequirement.HumanRequired, task.PendingGate is { Type: GateType.Accept });
        Assert.Equal(acceptance == AcceptanceRequirement.NotRequired, task.WorkflowState == WorkflowState.Completed);
    }

    [Fact]
    public async Task A_task_that_never_proposes_pays_nothing_one_cheap_read_per_turn_no_gate_and_no_extra_turn()
    {
        var rig = await NewRigAsync();   // not required: the agent chooses, and does not
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Empty((await rig.LoadAsync()).Gates);
        Assert.Single(rig.Signals.WhatCalls);
        Assert.Single(rig.H.Runtime.Sends);
    }

    [Fact]
    public async Task A_runtime_that_cannot_signal_is_never_asked_and_a_task_that_does_not_require_a_WHAT_still_completes()
    {
        var rig = await NewRigAsync();
        rig.Signals.Supported = false;
        rig.H.Runtime.Enqueue(rig.H.Renames());

        Assert.Equal(DirectRunStatus.Completed, (await rig.RunAsync()).Status);
        Assert.Empty(rig.Signals.WhatCalls);
    }

    [Fact]
    public async Task A_CLARIFY_question_still_works_beside_the_WHAT_capability_and_is_not_confused_with_it()
    {
        var rig = await NewRigAsync(required: true);
        rig.Signals.Raise(1, new AgentClarificationSignal("Which dessert?", "Not in the repo."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var asked = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingClarification, asked.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(GateType.Clarify, task.PendingGate!.Type);
        Assert.Null(task.LatestWhatGate);   // every signal kind is read (so a mix can be refused), but only the question became a gate

        await new ClarificationService(rig.H.Service).AnswerAsync(Fixture.TaskOne, task.PendingGate.Id, "Tiramisu.");
        var after = await rig.LoadAsync();
        Assert.Equal(SemanticChangeKind.HumanClarification, after.CurrentSemanticRevision.Kind);
        Assert.False(after.MayImplement);   // answering a question does not satisfy a required WHAT
    }

    // ---- 25, 27: the fresh fallback and its bound ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_conversation_that_cannot_be_resumed_falls_back_explicitly_with_the_ordered_WHAT_history_and_no_summary()
    {
        var (rig, _, v1) = await ProposedAsync();
        await rig.Gates.RequestChangesAsync(Fixture.TaskOne, v1.Id, "Cap the scope to src/ only.");
        rig.Signals.RaiseWhat(2, What("Rename GetMeal() to GetFood() in src/ only."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var second = await rig.ContinueAsync();
        await rig.Whats.ApproveAsync(Fixture.TaskOne, second.Task!.Task.PendingGate!.Id);

        var entries = HumanInputHistory.Of(await rig.LoadAsync());
        Assert.Equal([HumanInputKind.WhatChangesRequested, HumanInputKind.WhatApproved], entries.Select(e => e.Kind).ToArray());
        Assert.True(HumanInputHistory.TryRender(entries, out var history));
        Assert.True(history.IndexOf("Cap the scope to src/ only.", StringComparison.Ordinal) < history.IndexOf("Human approved the WHAT", StringComparison.Ordinal));

        rig.Store.Records.Clear();   // nothing recorded to resume
        rig.Baseline = rig.Baselines.Saved.Last().Baseline;
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.NoConversationRecorded), result.Continuation);
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains("Rename the public method", prompt, StringComparison.Ordinal);       // the fresh session is told the task
        Assert.Contains("Cap the scope to src/ only.", prompt, StringComparison.Ordinal);
        Assert.Contains("in src/ only.", prompt, StringComparison.Ordinal);                  // the approved artifact, verbatim
        Assert.Contains("Human approved the WHAT", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_WHAT_history_too_large_for_a_fresh_session_is_refused_never_truncated()
    {
        var (rig, _, gate) = await ProposedAsync();
        var big = new string('a', GateDecision.MaxFeedbackLength);
        var snapshot = (await rig.H.Service.RequestChangesAsync(await rig.H.Service.LoadAsync(Fixture.TaskOne), gate.Id, big)).Snapshot;

        // more rounds of proposal and requested changes, until the ordered history no longer fits
        for (var round = 0; round < HumanInputHistory.MaxCharacters / GateDecision.MaxFeedbackLength + 1; round++)
        {
            var id = rig.H.Service.NewGateId();
            snapshot = (await rig.H.Service.OpenWhatGateAsync(snapshot, id, new WhatProposal($"Objective {round}", "b", "s", null, null, "c", "r", 1))).Snapshot;
            snapshot = (await rig.H.Service.RequestChangesAsync(snapshot, id, big)).Snapshot;
        }

        Assert.False(HumanInputHistory.TryRender(HumanInputHistory.Of(snapshot.Task), out _));

        rig.Store.Records.Clear();   // nothing to resume: the fresh fallback would be needed
        var sessionsBefore = rig.H.Runtime.Starts.Count;

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.Contains("fresh_context_too_large", result.Reason, StringComparison.Ordinal);
        Assert.Equal(sessionsBefore, rig.H.Runtime.Starts.Count);
    }

    // ---- 26: recovery ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_after_approval_leaves_the_run_BLOCKED_and_continue_retries_without_a_second_decision_revision_or_artifact()
    {
        var (rig, _, gate) = await ProposedAsync();
        await rig.Whats.ApproveAsync(Fixture.TaskOne, gate.Id);
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        var failed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.StartsWith(RecoverableContinuation.BlockedReasonPrefix, blocked.StateReason, StringComparison.Ordinal);
        var pending = RecoverableContinuation.Find(blocked)!;
        Assert.Equal(HumanInputKind.WhatApproved, pending.Kind);
        Assert.Equal(What().Objective, pending.What!.Objective);
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
        Assert.Contains("The WHAT proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains(What().Objective, prompt, StringComparison.Ordinal);
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
        Assert.Equal(HumanInputKind.WhatChangesRequested, pending.Kind);
        Assert.Equal("Narrower.", pending.Text);

        rig.Conversations.FailResume = null;
        rig.Signals.RaiseWhat(2, What("Narrower."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var retried = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingWhat, retried.Status);
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.Contains("Do not implement yet.", rig.H.Runtime.Sends.Last().Prompt, StringComparison.Ordinal);
    }
}
