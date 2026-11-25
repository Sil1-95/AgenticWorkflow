using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M4D through the real CLI composition: a REQUEST CHANGES whose agent continuation cannot run is BLOCKED (exit 14), and <c>aw continue</c> from a later, fresh
/// process retries the same work: it rebuilds everything from the run directory and the event stream, records no second decision, revision or gate before
/// verification, resumes the same native conversation, and is held to the same scope as the original request. Only the agent runtime is scripted.
/// </summary>
public sealed class RecoverableContinuationCliTests
{
    private const string Feedback = "Please also add a test for the empty case.";

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes(string content = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), content + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static async Task<(CliHarness Harness, TaskId RunId, GateId GateId)> AwaitingAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Writes();
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedGateId());
        harness.ResetOutput();

        return result;
    }

    /// <summary>The run after a request-changes whose resume failed on a transient error: BLOCKED, exit 14.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId, GateId GateId)> BlockedAsync()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");

        Assert.Equal(14, await harness.RunAsync("request-changes", runId.ToString(), "--gate", gateId.ToString(), "--feedback", Feedback));

        harness.Factory.Conversations.ResumeFailure = null;
        harness.ResetOutput();

        return (harness, runId, gateId);
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static async Task<int> DecisionsAsync(CliHarness harness, TaskId runId) => (await LoadAsync(harness, runId)).GateDecisions.Length;

    private static async Task<int> RevisionsAsync(CliHarness harness, TaskId runId) => (await LoadAsync(harness, runId)).SemanticRevisions.Length;

    private static string Everything(CliHarness harness) => harness.Out + harness.Error.ToString();

    // ---- the failure leaves a recoverable state ---------------------------------------------------------------------------

    [Fact]
    public async Task A_resume_failure_after_the_decision_leaves_the_run_BLOCKED_and_status_says_what_to_do()
    {
        var (harness, runId, gateId) = await BlockedAsync();
        using var _ = harness;

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Equal(GateAction.RequestChanges, task.DecisionFor(gateId)!.Action);
        Assert.Null(task.PendingGate);

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("BLOCKED", status);
        Assert.Contains("Pending work:", status);
        Assert.Contains(Feedback, status);
        Assert.Contains($"aw continue {runId}", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.Contains("last continuation: resume failed", status);
        Assert.DoesNotContain("Resume is not yet supported", status);

        // the opaque conversation id is never printed
        Assert.DoesNotContain(ScriptedConversations.FirstReference, status);
    }

    [Fact]
    public async Task The_baseline_is_recorded_in_the_run_directory_before_any_agent_work_and_holds_no_content()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;

        var path = new ContinuationBaselineStore(new RunStore(harness.Home), new SystemClock()).PathOf(runId);
        Assert.True(File.Exists(path));

        var json = File.ReadAllText(path);
        Assert.DoesNotContain(CliHarness.Symbol, json);
        Assert.DoesNotContain(harness.Workspace, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"pathDigests\"", json);
    }

    // ---- aw continue ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Continue_from_a_fresh_process_resumes_the_same_conversation_and_opens_a_new_gate_with_no_second_decision()
    {
        var (harness, runId, gateId) = await BlockedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var decisionsBefore = await DecisionsAsync(harness, runId);
        var revisionsBefore = await RevisionsAsync(harness, runId);

        var exit = await harness.RunAsync("continue", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: resumed native conversation", output);
        Assert.Contains("no new decision or revision is recorded", output);
        Assert.DoesNotContain(ScriptedConversations.FirstReference, output);

        // both attempts attached to the one recorded conversation; no fresh session was ever started
        Assert.Equal(2, harness.Factory.Conversations!.Resumes.Count);
        Assert.All(harness.Factory.Conversations.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));
        Assert.Single(harness.Factory.Runtime.StartRequests);

        // the retry restates the authoritative feedback and says to inspect what an interrupted attempt left
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains(Feedback, prompt);
        Assert.Contains("Inspect the current workspace first", prompt);

        // nothing semantic was recorded again
        Assert.Equal(decisionsBefore, await DecisionsAsync(harness, runId));
        Assert.Equal(revisionsBefore, await RevisionsAsync(harness, runId));

        // verification came first, and the new gate is current for the request that was made
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, task.CurrentSemanticRevision.Kind);
        Assert.NotEqual(gateId, task.PendingGate!.Id);
        Assert.True(task.IsCurrent(task.PendingGate));
        Assert.NotNull(task.CurrentVerification);

        // and it can then be accepted like any other gate
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString(), "--gate", task.PendingGate.Id.ToString()));
        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task Status_after_a_successful_continue_is_the_ordinary_gate_view_with_the_last_continuation_recorded()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");
        Assert.Equal(17, await harness.RunAsync("continue", runId.ToString()));
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("AWAITING_HUMAN", status);
        Assert.DoesNotContain("Pending work:", status);
        Assert.DoesNotContain($"aw continue {runId}", status);
        Assert.Contains("aw accept", status);
    }

    [Fact]
    public async Task Continue_that_fails_again_stays_BLOCKED_and_retryable_and_never_accumulates_decisions()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("still unreachable");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            harness.ResetOutput();
            Assert.Equal(14, await harness.RunAsync("continue", runId.ToString()));
            Assert.Contains("Agent continuation: resume failed", harness.Out.ToString());
            Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
        }

        Assert.Equal(2, await RevisionsAsync(harness, runId));
        Assert.Single(harness.Factory.Runtime.StartRequests);

        // the problem goes away: the same request is finally carried out
        harness.Factory.Conversations.ResumeFailure = null;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");
        harness.ResetOutput();

        Assert.Equal(17, await harness.RunAsync("continue", runId.ToString()));
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Equal(2, await RevisionsAsync(harness, runId));
    }

    [Fact]
    public async Task A_failure_during_the_agents_turn_is_BLOCKED_and_a_retry_completes_it()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromException<AgentTurnOutcome>(new AgentRuntimeException("the connection was lost mid-turn"));

        Assert.Equal(14, await harness.RunAsync("request-changes", runId.ToString(), "--gate", gateId.ToString(), "--feedback", Feedback));
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);

        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");
        harness.ResetOutput();

        Assert.Equal(17, await harness.RunAsync("continue", runId.ToString()));
        Assert.Equal(2, await RevisionsAsync(harness, runId));
    }

    [Fact]
    public async Task A_conversation_that_no_longer_exists_falls_back_to_an_explicit_fresh_session_on_continue()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentConversationNotFoundException("gone");
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("continue", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: fresh session", output);
        Assert.Contains("Reason: the native conversation no longer exists", output);
        Assert.Equal(2, harness.Factory.Runtime.StartRequests.Count);

        // a fresh session has no memory: the task is restated with the feedback
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains(Feedback, prompt);
        Assert.Contains("symbol_present", prompt);
    }

    // ---- scope is judged against the original request, however many attempts ------------------------------------------------

    [Fact]
    public async Task A_protected_path_the_interrupted_attempt_already_changed_is_still_reported_by_the_retry()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;

        // the interrupted attempt writes a protected file, then the runtime is lost
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "locked.txt"), "tampered\n");
            return Task.FromException<AgentTurnOutcome>(new AgentRuntimeException("the connection was lost mid-turn"));
        };

        Assert.Equal(14, await harness.RunAsync("request-changes", runId.ToString(), "--gate", gateId.ToString(), "--feedback", Feedback));

        // the retry's agent only does the requested work; had the baseline been captured afresh the tampering would be absorbed and never reported
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");
        harness.ResetOutput();

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.NotEqual(17, exit);
        Assert.Contains("locked.txt", Everything(harness));
        Assert.NotEqual(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
    }

    // ---- refusals: nothing is recorded -------------------------------------------------------------------------------------

    private static async Task AssertRefusedAsync(CliHarness harness, TaskId runId, string expectedInOutput)
    {
        var before = (await LoadAsync(harness, runId)).LastSequence;
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;
        var resumesBefore = harness.Factory.Conversations?.Resumes.Count ?? 0;
        harness.ResetOutput();

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.Equal(18, exit);
        Assert.Contains(expectedInOutput, Everything(harness));
        Assert.Contains("Nothing was recorded", Everything(harness));
        Assert.Equal(before, (await LoadAsync(harness, runId)).LastSequence);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(resumesBefore, harness.Factory.Conversations?.Resumes.Count ?? 0);
    }

    [Fact]
    public async Task Continue_is_refused_for_a_run_waiting_at_its_gate()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        await AssertRefusedAsync(harness, runId, "not blocked");
    }

    [Fact]
    public async Task Continue_is_refused_for_a_completed_run()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString()));

        await AssertRefusedAsync(harness, runId, "not blocked");
    }

    [Fact]
    public async Task Continue_is_refused_for_a_run_blocked_by_a_native_interaction_nobody_answered()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        using var _ = harness;
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());

        Assert.Equal(15, await harness.RunAsync("run", "--non-interactive", "task"));
        var runId = harness.PrintedRunId();
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);

        await AssertRefusedAsync(harness, runId, "not a recoverable continuation");
    }

    [Fact]
    public async Task Abort_cancels_the_recoverable_block_and_continue_is_refused_afterwards()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;

        Assert.Equal(16, await harness.RunAsync("abort", runId.ToString()));
        Assert.Equal(WorkflowState.Cancelled, (await LoadAsync(harness, runId)).WorkflowState);

        await AssertRefusedAsync(harness, runId, "not blocked");
    }

    [Fact]
    public async Task A_missing_or_untrustworthy_baseline_refuses_the_retry_instead_of_guessing_one()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        var path = new ContinuationBaselineStore(new RunStore(harness.Home), new SystemClock()).PathOf(runId);

        File.WriteAllText(path, "{ not json");
        await AssertRefusedAsync(harness, runId, "baseline");

        File.Delete(path);
        await AssertRefusedAsync(harness, runId, "baseline");

        // a refused retry leaves the run exactly as retryable as it was: aborting still works
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task A_baseline_recorded_for_another_decision_is_refused()
    {
        var (harness, runId, gateId) = await BlockedAsync();
        using var _ = harness;
        var baselines = new ContinuationBaselineStore(new RunStore(harness.Home), new SystemClock());
        baselines.Save(runId, new GateId(Guid.NewGuid()), new ImplementationState(new ImplementationFingerprint("fp"), "HEAD", new Dictionary<string, string>()));

        Assert.Null(baselines.Find(runId, gateId).Baseline);
        await AssertRefusedAsync(harness, runId, "baseline");
    }

    [Fact]
    public async Task Continue_refuses_before_recording_anything_when_the_configuration_changed()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        harness.Write("agenticworkflow.json", CliHarness.ValidConfig(maxTurns: 9));
        var before = (await LoadAsync(harness, runId)).LastSequence;
        harness.ResetOutput();

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.Equal(2, exit);
        Assert.Contains("changed since this run started", harness.Error.ToString());
        Assert.Equal(before, (await LoadAsync(harness, runId)).LastSequence);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_corrupt_conversation_record_is_refused_not_replaced_by_a_fresh_session()
    {
        var (harness, runId, _) = await BlockedAsync();
        using var _ = harness;
        File.WriteAllText(new RunConversationStore(new RunStore(harness.Home), new SystemClock()).PathOf(runId), "{ not json");

        await AssertRefusedAsync(harness, runId, "cannot be used");
    }

    [Fact]
    public async Task A_run_that_does_not_exist_is_reported_as_not_found()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(3, await harness.RunAsync("continue", Guid.NewGuid().ToString()));
    }

    // ---- the command line ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("continue")]
    [InlineData("continue", "a", "b")]
    [InlineData("continue", "x", "--feedback", "change everything")]
    [InlineData("continue", "x", "--gate", "y")]
    [InlineData("continue", "x", "--interactive", "--non-interactive")]
    public async Task The_command_takes_a_run_id_and_nothing_that_could_change_what_the_work_is(params string[] args)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, await harness.RunAsync(args));
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task Help_describes_continue_as_an_operational_retry()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(0, await harness.RunAsync("continue", "--help"));

        var help = harness.Out.ToString();
        Assert.Contains("operational retry, not a decision", help);
        Assert.Contains("no new gate decision, no new semantic revision", help);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("--help"));
        Assert.Contains("aw continue <run-id>", harness.Out.ToString());
    }
}
