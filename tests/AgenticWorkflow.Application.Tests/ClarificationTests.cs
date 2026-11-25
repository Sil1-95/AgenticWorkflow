using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Tests;

/// <summary>A scripted <see cref="IAgentSignalSource"/>: what a runtime would report for a (session, turn).</summary>
internal sealed class FakeSignals : IAgentSignalSource
{
    public bool Supported { get; set; } = true;

    public Exception? FailCollect { get; set; }

    /// <summary>The signals reported for the n-th call (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentClarificationSignal>> ByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> Calls { get; } = [];

    public void Raise(int call, params AgentClarificationSignal[] signals) => ByCall[call] = signals;

    /// <summary>The WHAT proposals reported for the n-th call of <see cref="CollectWhatProposalsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentWhatSignal>> WhatByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> WhatCalls { get; } = [];

    public void RaiseWhat(int call, params AgentWhatSignal[] signals) => WhatByCall[call] = signals;

    /// <summary>The HOW proposals reported for the n-th call of <see cref="CollectHowProposalsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentHowSignal>> HowByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> HowCalls { get; } = [];

    public void RaiseHow(int call, params AgentHowSignal[] signals) => HowByCall[call] = signals;

    public Task<IReadOnlyList<AgentHowSignal>> CollectHowProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        HowCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(HowByCall.TryGetValue(HowCalls.Count, out var signals) ? signals : []);
    }

    public Task<IReadOnlyList<AgentWhatSignal>> CollectWhatProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        WhatCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(WhatByCall.TryGetValue(WhatCalls.Count, out var signals) ? signals : []);
    }

    /// <summary>The reclassification reports for the n-th call of <see cref="CollectReclassificationsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentReclassificationSignal>> ReclassificationByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> ReclassificationCalls { get; } = [];

    public void RaiseReclassification(int call, params AgentReclassificationSignal[] signals) => ReclassificationByCall[call] = signals;

    public Task<IReadOnlyList<AgentReclassificationSignal>> CollectReclassificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        ReclassificationCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ReclassificationByCall.TryGetValue(ReclassificationCalls.Count, out var signals) ? signals : []);
    }

    /// <summary>The review requests reported for the n-th call of <see cref="CollectReviewRequestsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentReviewRequestSignal>> ReviewRequestByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> ReviewRequestCalls { get; } = [];

    public void RaiseReviewRequest(int call, params AgentReviewRequestSignal[] signals) => ReviewRequestByCall[call] = signals;

    public Task<IReadOnlyList<AgentReviewRequestSignal>> CollectReviewRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        ReviewRequestCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ReviewRequestByCall.TryGetValue(ReviewRequestCalls.Count, out var signals) ? signals : []);
    }

    /// <summary>The review reports the reviewer submitted for the n-th call of <see cref="CollectReviewSubmissionsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentReviewSubmission>> ReviewByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> ReviewCalls { get; } = [];

    public void RaiseReview(int call, params AgentReviewSubmission[] submissions) => ReviewByCall[call] = submissions;

    public Task<IReadOnlyList<AgentReviewSubmission>> CollectReviewSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        ReviewCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ReviewByCall.TryGetValue(ReviewCalls.Count, out var submissions) ? submissions : []);
    }

    /// <summary>The Proof Contracts reported for the n-th call of <see cref="CollectProveRequestsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentProveRequestSignal>> ProveRequestByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> ProveRequestCalls { get; } = [];

    public void RaiseProveRequest(int call, params AgentProveRequestSignal[] signals) => ProveRequestByCall[call] = signals;

    public Task<IReadOnlyList<AgentProveRequestSignal>> CollectProveRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        ProveRequestCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ProveRequestByCall.TryGetValue(ProveRequestCalls.Count, out var signals) ? signals : []);
    }

    /// <summary>The proofs the prover submitted for the n-th call of <see cref="CollectProofSubmissionsAsync"/> (1-based); anything not scripted reports none.</summary>
    public Dictionary<int, IReadOnlyList<AgentProofSubmission>> ProofByCall { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn)> ProofCalls { get; } = [];

    public void RaiseProof(int call, params AgentProofSubmission[] submissions) => ProofByCall[call] = submissions;

    public Task<IReadOnlyList<AgentProofSubmission>> CollectProofSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        ProofCalls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ProofByCall.TryGetValue(ProofCalls.Count, out var submissions) ? submissions : []);
    }

    public bool CanSignal(ProfileId profile) => Supported;

    public Task<IReadOnlyList<AgentClarificationSignal>> CollectClarificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
    {
        Calls.Add((session, turn));

        if (FailCollect is { } failure)
            throw failure;

        return Task.FromResult(ByCall.TryGetValue(Calls.Count, out var signals) ? signals : []);
    }
}

internal sealed class FakeBaselineSink : IContinuationBaselineSink
{
    public Exception? Fail { get; set; }

    /// <summary>What was saved, with how many gates the run had at that moment (a baseline must be kept before the question exists).</summary>
    public List<(GateId Gate, ImplementationState Baseline, int GatesAtSave)> Saved { get; } = [];

    public Func<int>? GatesNow { get; set; }

    public Task SaveAsync(TaskId task, GateId gate, ImplementationState baseline, CancellationToken cancellationToken = default)
    {
        if (Fail is { } failure)
            throw failure;

        Saved.Add((gate, baseline, GatesNow?.Invoke() ?? -1));
        return Task.CompletedTask;
    }
}

/// <summary>
/// M5A at the controller: a typed signal from the agent opens a durable CLARIFY gate (and nothing else); the human's answer is recorded once with a new
/// semantic revision; the same native conversation is resumed with only the question and the answer, and every failure after the answer is recoverable.
/// </summary>
public sealed class ClarificationTests
{
    private const string Question = "Which dessert should the menu offer?";
    private const string Reason = "The name is not in the repository.";
    private const string Answer = "Tiramisu.";

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

        public Task<DirectRunResult> RunAsync() => Runner().RunAsync(Fixture.TaskOne, H.Definition());

        public Task<DirectRunResult> ContinueAsync(DirectRunOptions? options = null)
            => Runner(options).ContinueAfterHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline);

        public async Task<DirectRunResult> RetryAsync()
        {
            await new ContinuationRecoveryService(H.Service).ResumeAsync(Fixture.TaskOne);
            return await Runner().RecoverHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline);
        }

        public ClarificationService Clarifications => new(H.Service);

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    /// <summary>The agent starts the work and asks (it edited nothing before asking).</summary>
    private static async Task<(Rig Rig, DirectRunResult Asked, Gate Gate)> AskedAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var rig = await NewRigAsync(acceptance);
        rig.Signals.Raise(1, new AgentClarificationSignal(Question, Reason));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var asked = await rig.RunAsync();
        rig.Baseline = rig.Baselines.Saved.Count == 1 ? rig.Baselines.Saved[0].Baseline : null!;

        return (rig, asked, asked.Task!.Task.PendingGate!);
    }

    // ---- the agent -> controller signal -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_signal_opens_a_durable_CLARIFY_gate_and_records_nothing_as_implemented_verified_or_accepted()
    {
        var (rig, asked, gate) = await AskedAsync(AcceptanceRequirement.HumanRequired);

        Assert.Equal(DirectRunStatus.AwaitingClarification, asked.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Clarify, gate.Type);
        Assert.Equal(Question, gate.Clarification!.Question);
        Assert.Equal(Reason, gate.Clarification.Reason);
        Assert.Equal(1, gate.Clarification.Turn);
        Assert.Equal(task.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);
        Assert.Single(task.Gates);   // no ACCEPT gate, even though acceptance is required
        Assert.Empty(rig.H.Verifier.Ran);
        Assert.Contains(rig.H.Runtime.Stops, s => s == asked.Session);   // the native process is stopped cleanly
    }

    [Fact]
    public async Task The_workspace_baseline_is_kept_before_the_question_exists_and_keyed_by_the_question()
    {
        var (rig, _, gate) = await AskedAsync();

        var saved = Assert.Single(rig.Baselines.Saved);
        Assert.Equal(gate.Id, saved.Gate);
        Assert.Equal(0, saved.GatesAtSave);
    }

    [Fact]
    public async Task The_runtime_is_asked_for_exactly_the_session_and_turn_that_just_finished()
    {
        var (rig, asked, _) = await AskedAsync();

        var call = Assert.Single(rig.Signals.Calls);
        Assert.Equal(asked.Session, call.Session);
        Assert.Equal(1, call.Turn.Value);
    }

    [Fact]
    public async Task Two_questions_in_one_turn_are_a_conflict_and_record_nothing()
    {
        var rig = await NewRigAsync();
        rig.Signals.Raise(1, new AgentClarificationSignal("One?", "a"), new AgentClarificationSignal("Two?", "b"));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("clarification_conflict", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Gates);
        Assert.Empty(rig.Baselines.Saved);
    }

    [Theory]
    [InlineData("", "why")]
    [InlineData("what", "   ")]
    public async Task An_invalid_signal_is_rejected_and_records_nothing(string question, string reason)
    {
        var rig = await NewRigAsync();
        rig.Signals.Raise(1, new AgentClarificationSignal(question, reason));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("clarification_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Gates);
        Assert.Empty(rig.Baselines.Saved);
    }

    [Fact]
    public async Task A_question_whose_baseline_cannot_be_kept_is_not_recorded()
    {
        var rig = await NewRigAsync();
        rig.Baselines.Fail = new IOException("disk full");
        rig.Signals.Raise(1, new AgentClarificationSignal(Question, Reason));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("clarification_baseline_unavailable", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Gates);
    }

    [Fact]
    public async Task A_task_that_raises_nothing_pays_nothing_and_a_runtime_that_cannot_signal_is_never_asked()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var plain = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, plain.Status);   // unchanged completion, no gate
        Assert.Empty((await rig.LoadAsync()).Gates);
        Assert.Single(rig.Signals.Calls);                        // one cheap read per turn, no extra turn or prompt
        Assert.Single(rig.H.Runtime.Sends);

        var other = await NewRigAsync();
        other.Signals.Supported = false;
        other.H.Runtime.Enqueue(other.H.Renames());

        Assert.Equal(DirectRunStatus.Completed, (await other.RunAsync()).Status);
        Assert.Empty(other.Signals.Calls);
    }

    [Fact]
    public async Task A_runtime_that_fails_to_report_signals_does_not_fail_the_run_it_only_loses_the_question()
    {
        var rig = await NewRigAsync();
        rig.Signals.FailCollect = new IOException("channel unreadable");
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("signals could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_instructions_mention_the_capability_only_when_the_runtime_can_signal()
    {
        var offered = await NewRigAsync();
        offered.H.Runtime.Enqueue(offered.H.Renames());
        await offered.RunAsync();

        var withheld = await NewRigAsync();
        withheld.Signals.Supported = false;
        withheld.H.Runtime.Enqueue(withheld.H.Renames());
        await withheld.RunAsync();

        Assert.Contains(AgentClarificationSignal.ToolName, offered.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentClarificationSignal.ToolName, withheld.H.Runtime.Sends[0].Prompt, StringComparison.Ordinal);
    }

    // ---- answering --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Answering_records_one_decision_and_one_human_clarification_revision_and_returns_to_RUNNING()
    {
        var (rig, _, gate) = await AskedAsync();

        var outcome = await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, $"  {Answer}  ");

        var task = outcome.Change.Task;
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanClarification, task.CurrentSemanticRevision.Kind);
        Assert.Equal(Answer, task.DecisionFor(gate.Id)!.Answer);
        Assert.Equal(GateStatus.Answered, task.StatusOf(gate));
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
    }

    [Fact]
    public async Task A_second_answer_an_unknown_question_and_a_blank_or_oversized_answer_are_refused_and_record_nothing()
    {
        var (rig, _, gate) = await AskedAsync();

        var unknown = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Clarifications.AnswerAsync(Fixture.TaskOne, new GateId(Guid.NewGuid()), Answer));
        Assert.Equal(GateRefusal.UnknownGate, unknown.Reason);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, "   "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, new string('a', GateDecision.MaxAnswerLength + 1)));
        Assert.Equal(0, rig.Events<GateDecided>());

        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, Answer);

        var again = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, "Panna cotta."));
        Assert.Equal(GateRefusal.AlreadyDecided, again.Reason);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
    }

    [Fact]
    public async Task A_question_is_answered_with_answer_and_an_accept_gate_is_not_answerable()
    {
        var rig = await NewRigAsync(AcceptanceRequirement.HumanRequired);
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var awaiting = await rig.RunAsync();
        var acceptGate = awaiting.Task!.Task.PendingGate!;

        var refused = await Assert.ThrowsAsync<GateRefusedException>(() => rig.Clarifications.AnswerAsync(Fixture.TaskOne, acceptGate.Id, Answer));

        Assert.Equal(GateRefusal.WrongGateKind, refused.Reason);
        Assert.Equal(0, rig.Events<GateDecided>());
    }

    [Fact]
    public async Task Aborting_a_question_cancels_the_run_even_when_acceptance_is_not_required()
    {
        var (rig, _, gate) = await AskedAsync();

        var outcome = await new AcceptGateService(rig.H.Service, rig.H.Workspace).CancelAsync(Fixture.TaskOne, null);

        Assert.Equal(WorkflowState.Cancelled, outcome.Change.Task.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, outcome.Change.Task.StatusOf(gate));
    }

    // ---- continuing in the same native conversation ----------------------------------------------------------------------------

    [Fact]
    public async Task The_answer_resumes_the_same_conversation_with_only_the_question_and_the_answer()
    {
        var (rig, asked, gate) = await AskedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;
        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, Answer);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(AgentContinuationKind.ResumedNative, result.Continuation!.Kind);
        Assert.Equal(recorded, Assert.Single(rig.Conversations.Resumes).Conversation);
        Assert.Single(rig.H.Runtime.Starts);   // no new session was started

        var prompt = Assert.Single(rig.H.Runtime.Sends, s => s.Session == result.Session!.Value).Prompt;
        Assert.Contains(Question, prompt, StringComparison.Ordinal);
        Assert.Contains(Answer, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);   // the task is not restated
        Assert.DoesNotContain("You may change only", prompt, StringComparison.Ordinal);
        Assert.NotEqual(asked.Session, result.Session);
    }

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired, DirectRunStatus.Completed)]
    [InlineData(AcceptanceRequirement.HumanRequired, DirectRunStatus.AwaitingAcceptance)]
    public async Task After_the_answer_verified_work_ends_as_conditional_acceptance_says(AcceptanceRequirement acceptance, DirectRunStatus expected)
    {
        var (rig, _, gate) = await AskedAsync(acceptance);
        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, Answer);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(expected, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(task.CurrentSemanticRevisionId, task.CurrentImplementationRevision!.SemanticRevisionId);   // bound to the post-answer meaning
        Assert.Equal(acceptance == AcceptanceRequirement.HumanRequired, task.PendingGate is { Type: GateType.Accept });
    }

    [Fact]
    public async Task A_conversation_that_cannot_be_resumed_falls_back_explicitly_with_the_ordered_human_input_and_no_summary()
    {
        var (rig, _, gate) = await AskedAsync();
        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, Answer);
        rig.Store.Records.Clear();   // nothing recorded to resume
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.NoConversationRecorded), result.Continuation);
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains("Rename the public method", prompt, StringComparison.Ordinal);   // the fresh session is told the task
        Assert.Contains(Question, prompt, StringComparison.Ordinal);
        Assert.Contains(Answer, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fresh_projection_lists_every_human_input_in_order_and_is_refused_deterministically_when_it_does_not_fit()
    {
        var (rig, _, gate) = await AskedAsync();
        var first = new string('a', GateDecision.MaxAnswerLength);
        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, first);
        var task = await rig.LoadAsync();

        var entries = HumanInputHistory.Of(task);
        Assert.Equal([HumanInputKind.Clarification], entries.Select(e => e.Kind).ToArray());
        Assert.True(HumanInputHistory.TryRender(entries, out var text));
        Assert.Contains(Question, text, StringComparison.Ordinal);

        // more than the bounded budget: refused, never truncated or summarised
        var many = Enumerable.Repeat(entries[0], HumanInputHistory.MaxCharacters / GateDecision.MaxAnswerLength + 2).ToList();
        Assert.False(HumanInputHistory.TryRender(many, out _));
    }

    [Fact]
    public async Task A_history_too_large_for_a_fresh_session_is_refused_before_any_session_is_started()
    {
        var (rig, _, gate) = await AskedAsync();
        var big = new string('a', GateDecision.MaxAnswerLength);
        var snapshot = (await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, big)).Change.Snapshot;

        // more rounds of question and answer, until the ordered history no longer fits
        for (var round = 0; round < HumanInputHistory.MaxCharacters / GateDecision.MaxAnswerLength + 1; round++)
        {
            snapshot = (await rig.H.Service.OpenClarifyGateAsync(snapshot, rig.H.Service.NewGateId(), new ClarificationRequest($"Question {round}?", "why", 1))).Snapshot;
            snapshot = (await rig.H.Service.AnswerClarificationAsync(snapshot, snapshot.Task.PendingGate!.Id, big)).Snapshot;
        }

        rig.Store.Records.Clear();   // nothing to resume: the fresh fallback would be needed
        var sessionsBefore = rig.H.Runtime.Starts.Count;

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.Contains("fresh_context_too_large", result.Reason, StringComparison.Ordinal);
        Assert.Equal(sessionsBefore, rig.H.Runtime.Starts.Count);
    }

    // ---- recovery -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_answer_leaves_the_run_BLOCKED_and_continue_retries_without_a_second_answer_or_revision()
    {
        var (rig, _, gate) = await AskedAsync();
        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, gate.Id, Answer);
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        var failed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.StartsWith(RecoverableContinuation.BlockedReasonPrefix, blocked.StateReason, StringComparison.Ordinal);
        var pending = RecoverableContinuation.Find(blocked)!;
        Assert.Equal(HumanInputKind.Clarification, pending.Kind);
        Assert.Equal(Answer, pending.Text);
        Assert.Equal(Question, pending.Question!.Question);

        rig.Conversations.FailResume = null;
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var retried = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.Completed, retried.Status);
        Assert.Equal(AgentContinuationKind.ResumedNative, retried.Continuation!.Kind);
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
        Assert.Equal(1, rig.Events<GateOpened>());
        var prompt = rig.H.Runtime.Sends.Last().Prompt;
        Assert.Contains(Answer, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_baseline_from_before_the_question_still_judges_the_scope_of_the_work_that_follows()
    {
        var rig = await NewRigAsync();
        rig.Signals.Raise(1, new AgentClarificationSignal(Question, Reason));

        // the agent edits a protected file and THEN asks: the edit was made before the question
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["Fixture.sln"] = "tampered"));
        var asked = await rig.RunAsync();
        Assert.Equal(DirectRunStatus.AwaitingClarification, asked.Status);
        rig.Baseline = rig.Baselines.Saved.Single().Baseline;

        await rig.Clarifications.AnswerAsync(Fixture.TaskOne, asked.Task!.Task.PendingGate!.Id, Answer);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.ContinueAsync();

        // The baseline predates the question, so the protected-file edit made before asking is still seen and still fails verification.
        Assert.NotEqual(DirectRunStatus.Completed, result.Status);
        Assert.Contains(result.Turns.SelectMany(t => t.ScopeViolations), v => v.ToString()!.Contains("Fixture.sln", StringComparison.Ordinal));
    }
}
