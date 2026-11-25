using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M4D at the controller: the human's REQUEST CHANGES is decided once; when its continuation cannot run because the runtime failed, the workflow is BLOCKED
/// (never a dead RUNNING), and a retry executes the same pending work without a second decision, a second semantic revision or a second gate. Delivery of the
/// instruction to the agent is at least once: nothing here claims exactly-once execution, and a retry assumes nothing about the workspace.
/// </summary>
public sealed class RecoverableContinuationTests
{
    private const string Feedback = "Also rename the documentation references.";

    private sealed class FreshProcessIds : IIdSource
    {
        public Guid Next() => Guid.NewGuid();
    }

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        /// <summary>The workspace as it was when the human asked for the changes: what the request is judged against.</summary>
        public ImplementationState Baseline { get; set; } = null!;

        public DirectPathRunner Runner(DirectRunOptions? options = null)
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options, evidence: null, new AgentConversationSupport(Conversations, Store));

        public Task<DirectRunResult> RunAsync() => Runner().RunAsync(Fixture.TaskOne, H.Definition());

        /// <summary>The continuation that follows the decision (what <c>aw request-changes</c> runs).</summary>
        public Task<DirectRunResult> ContinueAsync(CancellationToken ct = default, DirectRunOptions? options = null)
            => Runner(options).ContinueAfterHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline, ct);

        /// <summary>What <c>aw continue</c> runs: re-enter RUNNING (one state change), then retry the same work against the recorded baseline.</summary>
        public async Task<DirectRunResult> RetryAsync(DirectRunOptions? options = null)
        {
            await new ContinuationRecoveryService(H.Service).ResumeAsync(Fixture.TaskOne);
            return await Runner(options).RecoverHumanInputAsync(Fixture.TaskOne, H.Definition(), Baseline);
        }

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);
        return new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
    }

    /// <summary>First stretch to the ACCEPT gate, then the human's decision (recorded once, with its semantic revision, before any agent work).</summary>
    private static async Task<Rig> RequestedChangesAsync()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var first = await rig.RunAsync();
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, first.Status);

        rig.Baseline = await rig.H.Workspace.CaptureAsync(@"C:\fixture");
        await rig.H.Service.RequestChangesAsync(first.Task!, first.Task!.Task.PendingGate!.Id, Feedback);

        return rig;
    }

    private static async Task<(Rig Rig, DirectRunResult Failed)> BlockedAsync()
    {
        var rig = await RequestedChangesAsync();
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        var failed = await rig.ContinueAsync();
        rig.Conversations.FailResume = null;

        return (rig, failed);
    }

    private static void AssertDecidedOnce(Rig rig)
    {
        Assert.Equal(1, rig.Events<GateDecided>());
        Assert.Equal(2, rig.Events<SemanticRevisionCreated>() + 1);   // the initial revision is part of TaskCreated; exactly one more was created
        Assert.Equal(1, rig.Events<SemanticRevisionCreated>());
    }

    // ---- the state: BLOCKED, not a dead RUNNING -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transient_resume_failure_after_the_decision_leaves_the_workflow_BLOCKED_not_RUNNING()
    {
        var (rig, failed) = await BlockedAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        Assert.Equal(AgentContinuationKind.ResumeFailed, failed.Continuation!.Kind);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Equal(WorkflowState.Blocked, failed.Task!.Task.WorkflowState);
        Assert.StartsWith(RecoverableContinuation.BlockedReasonPrefix, task.StateReason, StringComparison.Ordinal);
        Assert.Contains("aw continue", task.StateReason, StringComparison.Ordinal);

        // no fresh session was substituted
        Assert.Single(rig.H.Runtime.Starts);

        // the decision was recorded once, with exactly one semantic revision, and nothing else was decided or opened
        AssertDecidedOnce(rig);
        Assert.Equal(1, rig.Events<GateOpened>());
        Assert.Equal(GateAction.RequestChanges, task.GateDecisions[^1].Action);
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, task.CurrentSemanticRevision.Kind);
    }

    [Fact]
    public async Task A_fresh_replay_in_a_fresh_process_sees_the_blocked_continuation_and_everything_needed_to_retry_it()
    {
        var (rig, _) = await BlockedAsync();

        var fresh = Fixture.NewService(rig.H.Store, new FreshProcessIds());
        var task = (await fresh.LoadAsync(Fixture.TaskOne)).Task;

        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);

        var pending = RecoverableContinuation.Find(task)!;
        Assert.Equal(Feedback, pending.Text);
        Assert.Equal(GateAction.RequestChanges, pending.Decision!.Action);
        Assert.Equal(task.CurrentSemanticRevisionId, pending.Revision.Id);
        Assert.StartsWith(RecoverableContinuation.BlockedReasonPrefix, pending.BlockedReason, StringComparison.Ordinal);

        // and a fresh process may inspect it without writing anything
        var before = rig.H.Store.History(Fixture.TaskOne).Count;
        Assert.Equal(Feedback, (await new ContinuationRecoveryService(fresh).InspectAsync(Fixture.TaskOne)).Text);
        Assert.Equal(before, rig.H.Store.History(Fixture.TaskOne).Count);
    }

    [Fact]
    public async Task A_runtime_failure_in_the_first_stretch_is_blocked_but_not_as_a_recoverable_continuation()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.FailStart = new AgentRuntimeException("the core is unreachable");

        var result = await rig.RunAsync();

        // A first stretch has no recorded human decision to retry: it is BLOCKED by the runtime failure, with a reason that is not M4D's marker, so 'aw continue' refuses it
        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(RuntimeFailureBlock.IsBlockedBy(task));
        Assert.Null(RecoverableContinuation.Find(task));
        Assert.Equal(ContinuationRefusal.NotRecoverable, RecoverableContinuation.Refusal(task)!.Reason);
    }

    [Fact]
    public async Task The_acceptance_none_path_is_unchanged_it_has_no_request_changes_to_continue_or_recover()
    {
        var rig = await NewRigAsync(AcceptanceRequirement.NotRequired);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var done = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, done.Status);
        Assert.Equal(0, rig.Events<GateOpened>());

        rig.Baseline = await rig.H.Workspace.CaptureAsync(@"C:ixture");
        var refused = await rig.ContinueAsync();
        Assert.Equal(DirectRunStatus.Rejected, refused.Status);
        Assert.Equal(WorkflowState.Completed, (await rig.LoadAsync()).WorkflowState);
        await Assert.ThrowsAsync<ContinuationRefusedException>(() => new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne));
    }

    // ---- where it failed: what is and is not known ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FailurePhase.Resume, "resuming the agent conversation", 0)]
    [InlineData(FailurePhase.Send, "sending the turn to the agent", 1)]
    [InlineData(FailurePhase.Wait, "waiting for the agent's turn", 1)]
    [InlineData(FailurePhase.Observe, "observing and verifying the result", 1)]
    public async Task Whichever_phase_the_runtime_fails_in_the_workflow_is_BLOCKED_and_the_phase_is_recorded_as_a_diagnosis(FailurePhase phase, string described, int sendsAttempted)
    {
        var rig = await RequestedChangesAsync();
        var sendsBefore = rig.H.Runtime.Sends.Count;

        switch (phase)
        {
            case FailurePhase.Resume:
                rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");
                break;
            case FailurePhase.Send:
                rig.H.Runtime.FailSend = new AgentRuntimeException("the pipe broke while sending");
                break;
            case FailurePhase.Wait:
                rig.H.Runtime.FailWait = new AgentRuntimeException("the connection was lost");
                break;
            case FailurePhase.Observe:
                rig.H.Runtime.Enqueue(rig.H.Renames());
                rig.H.Verifier.Override = _ => throw new IOException("the disk failed");
                break;
        }

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.NotNull(RecoverableContinuation.Find(task));
        Assert.Contains(described, task.StateReason, StringComparison.Ordinal);

        // what the controller really knows: nothing was sent before the conversation was ready; once a send was attempted, whether the agent got it is not
        // known (the send may have failed after the text reached the agent), and once it returned the turn was submitted. Never "exactly once".
        Assert.Equal(sendsAttempted, rig.H.Runtime.Sends.Count - sendsBefore);

        AssertDecidedOnce(rig);
    }

    public enum FailurePhase
    {
        Resume,
        Send,
        Wait,
        Observe,
    }

    [Fact]
    public async Task A_failure_to_start_even_the_explicit_fresh_fallback_is_BLOCKED_too()
    {
        var rig = await RequestedChangesAsync();
        rig.Conversations.FailResume = new AgentConversationNotFoundException("gone");
        rig.H.Runtime.FailStart = new AgentRuntimeException("the core is unreachable");

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);
    }

    [Fact]
    public async Task An_agent_that_could_not_start_because_of_a_dialog_the_controller_never_answers_is_BLOCKED_and_retryable()
    {
        var rig = await RequestedChangesAsync();
        rig.Conversations.FailResume = new AgentStartupBlockedException("the folder is not trusted yet");

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.StartupBlocked, result.Status);
        Assert.NotNull(RecoverableContinuation.Find(await rig.LoadAsync()));
    }

    [Fact]
    public async Task An_interruption_of_the_continuation_is_BLOCKED_too_nothing_is_left_working()
    {
        var rig = await RequestedChangesAsync();
        using var cancel = new CancellationTokenSource();
        rig.H.Runtime.OnWait = _ =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        };

        var result = await rig.ContinueAsync(cancel.Token);

        Assert.Equal(DirectRunStatus.Interrupted, result.Status);
        Assert.NotNull(RecoverableContinuation.Find(await rig.LoadAsync()));
    }

    [Fact]
    public async Task A_session_that_could_not_be_confirmed_stopped_is_said_in_the_reason()
    {
        var rig = await RequestedChangesAsync();
        rig.H.Runtime.FailWait = new AgentRuntimeException("the connection was lost");
        rig.H.Runtime.FailStop = new IOException("the terminal is unreachable");

        await rig.ContinueAsync();

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Contains("could not be confirmed stopped", task.StateReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_recorded_reason_is_one_bounded_line_whatever_the_runtime_said()
    {
        var rig = await RequestedChangesAsync();
        rig.H.Runtime.FailWait = new AgentRuntimeException("line one\nline two\u001b[31m " + new string('x', 2000));

        await rig.ContinueAsync();

        var reason = (await rig.LoadAsync()).StateReason;
        Assert.DoesNotContain('\n', reason);
        Assert.DoesNotContain('\u001b', reason);
        Assert.True(reason.Length < 700, $"the reason was {reason.Length} characters");
    }

    // ---- the retry: the same work, no second decision ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_retry_resumes_the_same_conversation_re_enters_RUNNING_once_and_records_no_second_decision_revision_or_gate()
    {
        var (rig, _) = await BlockedAsync();
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var stateChangesBefore = rig.Events<WorkflowStateChanged>();
        var retry = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        Assert.Equal(AgentContinuationKind.ResumedNative, retry.Continuation!.Kind);

        // both attempts attached to the one recorded conversation; no new session was ever started
        Assert.Equal(2, rig.Conversations.Resumes.Count);
        Assert.All(rig.Conversations.Resumes, r => Assert.Equal(recorded, r.Conversation));
        Assert.Single(rig.H.Runtime.Starts);

        // the semantic state is exactly what the human made it: one decision, one revision; only the new gate is added
        AssertDecidedOnce(rig);
        Assert.Equal(2, rig.Events<GateOpened>());

        // the retry itself wrote one operational state change (BLOCKED to RUNNING); the gate opening moves the state by its own event
        Assert.Equal(stateChangesBefore + 1, rig.Events<WorkflowStateChanged>());

        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.True(task.IsCurrent(task.PendingGate!));
        Assert.Equal(task.CurrentSemanticRevisionId, task.PendingGate!.SemanticRevisionId);
    }

    [Fact]
    public async Task The_retry_restates_the_authoritative_feedback_and_tells_the_agent_to_inspect_what_the_interrupted_attempt_left()
    {
        var (rig, _) = await BlockedAsync();
        rig.H.Runtime.Enqueue(new TurnScript());

        await rig.RetryAsync();

        var prompt = rig.H.Runtime.Sends[^1].Prompt;
        Assert.Contains(Feedback, prompt, StringComparison.Ordinal);
        Assert.Contains("authoritative feedback", prompt, StringComparison.Ordinal);
        Assert.Contains("Inspect the current workspace first and preserve any correct work", prompt, StringComparison.Ordinal);

        // the conversation already knows the task: it is not restated, and no transcript is replayed
        Assert.DoesNotContain("Task:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("tests", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_failures_stay_retryable_and_never_accumulate_decisions_or_revisions()
    {
        var (rig, _) = await BlockedAsync();

        var changesAfterFirstBlock = rig.Events<WorkflowStateChanged>();

        // second and third attempts fail in different places; every one of them ends BLOCKED
        rig.Conversations.FailResume = new AgentRuntimeException("still unreachable");
        var second = await rig.RetryAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, second.Status);
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);

        rig.Conversations.FailResume = null;
        rig.H.Runtime.FailWait = new AgentRuntimeException("lost it mid-turn");
        var third = await rig.RetryAsync();
        Assert.Equal(DirectRunStatus.ExecutionFailure, third.Status);
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);
        Assert.NotNull(RecoverableContinuation.Find(await rig.LoadAsync()));

        // the history of what happened is kept: every block and every re-entry is its own recorded, reasoned transition
        Assert.Equal(changesAfterFirstBlock + 4, rig.Events<WorkflowStateChanged>());
        AssertDecidedOnce(rig);
        Assert.Equal(1, rig.Events<GateOpened>());

        // and it still completes
        rig.H.Runtime.FailWait = null;
        rig.H.Runtime.Enqueue(new TurnScript());
        var fourth = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, fourth.Status);
        AssertDecidedOnce(rig);
        Assert.Equal(2, rig.Events<GateOpened>());
        Assert.Single(rig.H.Runtime.Starts);
    }

    [Fact]
    public async Task A_retry_verifies_the_resulting_state_and_opens_a_new_gate_never_before_verification()
    {
        var (rig, _) = await BlockedAsync();
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var verificationsBefore = (await rig.LoadAsync()).Verifications.Length;
        var retry = await rig.RetryAsync();

        var task = await rig.LoadAsync();
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        Assert.Equal(verificationsBefore + 1, task.Verifications.Length);
        Assert.True(task.HasCurrentPassingVerification);
        Assert.Equal(task.CurrentVerification!.Id, task.PendingGate!.VerificationRunId);

        // no gate existed while the work was blocked
        Assert.Equal(1, task.Gates.Length - 1);
    }

    [Fact]
    public async Task A_failure_in_a_later_repair_turn_is_the_same_pending_request_and_is_recoverable_too()
    {
        var rig = await RequestedChangesAsync();

        // the workspace is back to the unfinished state when the work resumes; the agent "finishes" without the change, so an implementation is recorded and
        // verification fails; the repair turn then loses the runtime
        rig.H.Workspace.Files["src/Greeting.cs"] = rig.H.Workspace.Files["src/Greeting.cs"].Replace("GetFood", "GetMeal");
        rig.H.Workspace.Files["tests/GreetingTests.cs"] = rig.H.Workspace.Files["tests/GreetingTests.cs"].Replace("GetFood", "GetMeal");
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        rig.H.Runtime.OnWait = index =>
        {
            if (rig.H.Runtime.Sends.Count >= 3)
                throw new AgentRuntimeException("the connection was lost during the repair turn");

            return Task.CompletedTask;
        };

        var failed = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);
        var blocked = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(blocked.CurrentSemanticRevisionId, blocked.CurrentImplementationRevision!.SemanticRevisionId);   // an implementation already exists under it
        Assert.NotNull(RecoverableContinuation.Find(blocked));

        rig.H.Runtime.OnWait = null;
        // the earlier attempt left the repository failing its check; the retry's agent finishes it
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var retry = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        AssertDecidedOnce(rig);
    }

    // ---- partial progress is never assumed clean ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Work_the_interrupted_attempt_already_did_outside_the_scope_is_still_caught_by_the_retry()
    {
        var rig = await RequestedChangesAsync();

        // the first attempt edits a protected file and part of the work, then the runtime dies
        rig.H.Runtime.Enqueue(new TurnScript(
            () =>
            {
                rig.H.Workspace.Files["Fixture.sln"] = "tampered by the interrupted attempt";
                rig.H.Workspace.Files["src/Docs.cs"] = "// half done";
            },
            WhileWorking: () => throw new AgentRuntimeException("the connection was lost mid-turn")));

        var failed = await rig.ContinueAsync();
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);
        Assert.Equal(DirectRunStatus.ExecutionFailure, failed.Status);

        // the retry's agent does its work but, not having noticed, leaves the protected file as it was found; then the repair turn reverts it
        rig.H.Runtime.Enqueue(
            new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"),
            new TurnScript(() => rig.H.Workspace.Files["Fixture.sln"] = "sln"));

        var retry = await rig.RetryAsync();

        // had the retry captured a fresh baseline, the tampered file would have been absorbed into it and never reported
        var first = retry.Turns[0];
        Assert.Contains(first.ScopeViolations, v => v.Path == "Fixture.sln");
        Assert.Equal(VerificationOutcome.Failed, first.Verification);
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        Assert.Empty(retry.Turns[^1].ScopeViolations);
    }

    [Fact]
    public async Task The_retry_does_not_assume_the_workspace_is_clean_it_only_tells_the_agent_to_look()
    {
        var rig = await RequestedChangesAsync();
        rig.H.Runtime.FailWait = new AgentRuntimeException("the connection was lost");
        await rig.ContinueAsync();

        // somebody (or the interrupted attempt) changed the workspace while the work was blocked
        rig.H.Workspace.Files["src/Docs.cs"] = "// changed meanwhile";
        rig.H.Runtime.FailWait = null;
        rig.H.Runtime.Enqueue(new TurnScript());

        var retry = await rig.RetryAsync();

        // whatever state results is observed and verified afresh; nothing was reused from before the interruption
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        Assert.Equal(retry.Task!.Task.CurrentImplementationRevisionId, retry.Task.Task.PendingGate!.ImplementationRevisionId);
        Assert.Equal(1, retry.TurnsUsed);
    }

    // ---- resume-first policy on a retry ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_retry_whose_conversation_no_longer_exists_falls_back_explicitly_to_a_fresh_session_with_the_task_and_the_feedback()
    {
        var (rig, _) = await BlockedAsync();
        rig.Conversations.FailResume = new AgentConversationNotFoundException("gone");
        rig.H.Runtime.Enqueue(new TurnScript());

        var retry = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        Assert.Equal(AgentContinuationKind.FreshSession, retry.Continuation!.Kind);
        Assert.Equal(AgentContinuationReasons.ConversationNotFound, retry.Continuation.Reason);
        Assert.Equal(2, rig.H.Runtime.Starts.Count);

        // a fresh session has no memory: it is sent the task as well as the feedback, and told an earlier attempt may have changed the workspace
        var prompt = rig.H.Runtime.Sends[^1].Prompt;
        Assert.Contains("Task:", prompt, StringComparison.Ordinal);
        Assert.Contains(Feedback, prompt, StringComparison.Ordinal);
        Assert.Contains("interrupted by a runtime failure", prompt, StringComparison.Ordinal);
        Assert.Contains(retry.Notes, n => n.Contains("fresh agent session", StringComparison.Ordinal));

        AssertDecidedOnce(rig);
    }

    [Fact]
    public async Task A_retry_with_no_recorded_conversation_or_no_resume_capability_falls_back_explicitly()
    {
        var (rig, _) = await BlockedAsync();
        rig.Store.Records.Clear();
        rig.H.Runtime.Enqueue(new TurnScript());

        var legacy = await rig.RetryAsync();
        Assert.Equal(AgentContinuationReasons.NoConversationRecorded, legacy.Continuation!.Reason);

        var (second, _) = await BlockedAsync();
        second.Conversations.Supported = false;
        second.H.Runtime.Enqueue(new TurnScript());

        var unsupported = await second.RetryAsync();
        Assert.Equal(AgentContinuationReasons.ResumeUnsupported, unsupported.Continuation!.Reason);
    }

    [Fact]
    public async Task A_transient_error_on_a_retry_never_becomes_a_fresh_session_it_is_BLOCKED_again()
    {
        var (rig, _) = await BlockedAsync();
        rig.Conversations.FailResume = new AgentRuntimeException("the core is unreachable");

        var retry = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, retry.Status);
        Assert.Equal(AgentContinuationKind.ResumeFailed, retry.Continuation!.Kind);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);
    }

    [Fact]
    public async Task A_corrupt_or_foreign_conversation_record_is_not_resumed_nor_replaced_on_a_retry()
    {
        var (rig, _) = await BlockedAsync();
        rig.Store.FailFind = new InvalidAgentConversationException("'conversation.json' is unreadable");

        var retry = await rig.RetryAsync();

        Assert.Equal(DirectRunStatus.Rejected, retry.Status);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Single(rig.Conversations.Resumes);   // only the failed original attempt
        Assert.Equal(WorkflowState.Blocked, (await rig.LoadAsync()).WorkflowState);
    }

    // ---- what is not recoverable ---------------------------------------------------------------------------------------------------

    private static async Task AssertRefusedAsync(Rig rig, ContinuationRefusal expected)
    {
        var before = rig.H.Store.History(Fixture.TaskOne).Count;
        var service = new ContinuationRecoveryService(rig.H.Service);

        var inspect = await Assert.ThrowsAsync<ContinuationRefusedException>(() => service.InspectAsync(Fixture.TaskOne));
        var resume = await Assert.ThrowsAsync<ContinuationRefusedException>(() => service.ResumeAsync(Fixture.TaskOne));

        Assert.Equal(expected, inspect.Reason);
        Assert.Equal(expected, resume.Reason);
        Assert.Equal(before, rig.H.Store.History(Fixture.TaskOne).Count);   // nothing was recorded
    }

    [Fact]
    public async Task A_run_that_is_still_RUNNING_is_refused()
    {
        var rig = await RequestedChangesAsync();   // the decision was just recorded and nothing failed: RUNNING

        await AssertRefusedAsync(rig, ContinuationRefusal.NotBlocked);
    }

    [Fact]
    public async Task A_run_AWAITING_HUMAN_at_its_gate_is_refused()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());
        await rig.RunAsync();

        await AssertRefusedAsync(rig, ContinuationRefusal.NotBlocked);
    }

    [Fact]
    public async Task A_COMPLETED_run_is_refused()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var first = await rig.RunAsync();
        await rig.H.Service.AcceptGateAsync(first.Task!, first.Task!.Task.PendingGate!.Id);

        await AssertRefusedAsync(rig, ContinuationRefusal.NotBlocked);
    }

    [Fact]
    public async Task A_CANCELLED_run_is_refused_even_if_it_was_a_recoverable_block_when_aborted()
    {
        var (rig, _) = await BlockedAsync();
        await rig.H.Service.ChangeWorkflowStateAsync((await rig.H.Service.LoadAsync(Fixture.TaskOne)), WorkflowState.Cancelled, "Cancelled by the person with 'aw abort'.");

        await AssertRefusedAsync(rig, ContinuationRefusal.NotBlocked);
    }

    [Fact]
    public async Task A_run_BLOCKED_for_another_reason_is_refused()
    {
        var rig = await NewRigAsync();
        var snapshot = await rig.H.Service.LoadAsync(Fixture.TaskOne);
        await rig.H.Service.ChangeWorkflowStateAsync(snapshot, WorkflowState.Blocked, "Native agent interaction was required (permission: approve Bash), but nobody was attending.");

        await AssertRefusedAsync(rig, ContinuationRefusal.NotRecoverable);
    }

    [Fact]
    public async Task The_marker_alone_is_not_enough_there_must_be_a_pending_human_request()
    {
        var rig = await NewRigAsync();
        var snapshot = await rig.H.Service.LoadAsync(Fixture.TaskOne);
        await rig.H.Service.ChangeWorkflowStateAsync(snapshot, WorkflowState.Blocked, RecoverableContinuation.BlockedReason("sending", "forged", true));

        await AssertRefusedAsync(rig, ContinuationRefusal.NotRecoverable);
    }

    [Fact]
    public async Task A_semantic_change_after_the_block_makes_the_old_request_not_current_and_the_retry_is_refused()
    {
        var (rig, _) = await BlockedAsync();
        var snapshot = await rig.H.Service.LoadAsync(Fixture.TaskOne);
        await rig.H.Service.CreateSemanticRevisionAsync(snapshot, SemanticChangeKind.RequirementChange, "The requirement itself changed.");

        await AssertRefusedAsync(rig, ContinuationRefusal.NotRecoverable);
    }

    [Fact]
    public async Task A_native_interaction_nobody_can_answer_during_a_continuation_is_blocked_but_is_not_a_recoverable_continuation()
    {
        var rig = await RequestedChangesAsync();
        var sendsBefore = rig.H.Runtime.Sends.Count;
        rig.H.Runtime.Enqueue(new TurnScript(Observed: [AgentTurnOutcome.AwaitingHuman(new AgentInteraction(AgentInteractionKind.Permission, "approve Bash: rm -rf build", null))]));

        var result = await rig.ContinueAsync();

        // M3B behaviour is unchanged: observed, never answered, BLOCKED with its own reason...
        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.StartsWith("Native agent interaction was required", task.StateReason, StringComparison.Ordinal);
        Assert.Equal(sendsBefore + 1, rig.H.Runtime.Sends.Count);

        // ...and `aw continue` does not take it over
        await AssertRefusedAsync(rig, ContinuationRefusal.NotRecoverable);
    }

    [Fact]
    public async Task A_resumed_conversation_whose_agent_waits_for_a_person_during_a_retry_is_observed_the_same_way()
    {
        var (rig, _) = await BlockedAsync();
        rig.H.Runtime.Enqueue(new TurnScript(Observed: [AgentTurnOutcome.AwaitingHuman(new AgentInteraction(AgentInteractionKind.Permission, "approve Bash", null)), AgentTurnOutcome.Resumed]));

        var retry = await rig.RetryAsync(new DirectRunOptions { WaitForHuman = true });

        // the person answered natively; the controller only observed AWAITING_HUMAN and RUNNING, and the work went on to a new gate
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, retry.Status);
        AssertDecidedOnce(rig);
    }
}
