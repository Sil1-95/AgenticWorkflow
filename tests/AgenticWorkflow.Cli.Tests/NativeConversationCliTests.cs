using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M4C through the real CLI composition: the conversation reference outlives the first <c>aw</c> process (it is read back from the run directory), a later
/// <c>aw request-changes</c> resumes it and says so, and every way the reference can be wrong is refused or reported, never papered over. Only the agent
/// runtime is scripted.
/// </summary>
public sealed class NativeConversationCliTests
{
    private const string Feedback = "Please also add a test for the empty case.";

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes(string content = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), content + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static async Task<(CliHarness Harness, TaskId RunId, GateId GateId)> AwaitingAsync(bool resumable = true)
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Writes();

        if (resumable)
            harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedGateId());
        harness.ResetOutput();

        return result;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static RunConversationStore StoreOf(CliHarness harness) => new(new RunStore(harness.Home), new SystemClock());

    // ---- persistence ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_runs_conversation_is_persisted_in_its_own_run_directory_with_no_secret_path_or_transcript()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        var path = StoreOf(harness).PathOf(runId);
        Assert.True(File.Exists(path));

        var found = StoreOf(harness).Find(runId)!;
        Assert.Equal(runId, found.Task);
        Assert.Equal(ScriptedConversations.FirstReference, found.Conversation.Reference);
        Assert.Equal(ScriptedConversations.RuntimeKind, found.Conversation.Runtime);

        // exactly the documented members: nothing else was written
        var json = File.ReadAllText(path);
        foreach (var member in new[] { "conversationVersion", "runId", "profile", "runtime", "reference", "recordedAt" })
            Assert.Contains($"\"{member}\"", json);

        Assert.DoesNotContain(harness.Workspace, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task A_runtime_that_cannot_resume_records_no_conversation()
    {
        var (harness, runId, _) = await AwaitingAsync(resumable: false);
        using var _ = harness;

        Assert.False(File.Exists(StoreOf(harness).PathOf(runId)));
    }

    [Fact]
    public async Task Two_runs_keep_two_separate_conversations_and_one_cannot_see_the_other()
    {
        var (harness, first, _) = await AwaitingAsync();
        using var _ = harness;

        harness.ResetOutput();
        Assert.Equal(17, await harness.RunAsync("run", "another task"));
        var second = harness.PrintedRunId();

        Assert.NotEqual(first, second);
        Assert.NotEqual(StoreOf(harness).PathOf(first), StoreOf(harness).PathOf(second));
        Assert.Equal(first, StoreOf(harness).Find(first)!.Task);
        Assert.Equal(second, StoreOf(harness).Find(second)!.Task);

        // a record copied from one run's directory to another's is refused by the second, not followed
        File.Copy(StoreOf(harness).PathOf(first), StoreOf(harness).PathOf(second), overwrite: true);
        Assert.Throws<InvalidAgentConversationException>(() => StoreOf(harness).Find(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{"conversationVersion":99,"runId":"{RUN}","profile":"p","runtime":"r","reference":"x","recordedAt":"2026-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"conversationVersion":1,"runId":"{RUN}","profile":"p","runtime":"r","reference":"","recordedAt":"2026-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"conversationVersion":1,"runId":"{RUN}","profile":"p","runtime":"r","reference":"x","recordedAt":"2026-01-01T00:00:00+00:00","token":"secret"}""")]
    public async Task A_record_that_cannot_be_trusted_is_refused(string content)
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        File.WriteAllText(StoreOf(harness).PathOf(runId), content.Replace("{RUN}", runId.ToString()));

        Assert.Throws<InvalidAgentConversationException>(() => StoreOf(harness).Find(runId));
    }

    // ---- request-changes resumes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_changes_from_a_fresh_process_resumes_the_recorded_conversation_and_says_so()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: resumed native conversation", output);
        Assert.DoesNotContain("Agent continuation: fresh session", output);

        // the recorded conversation was resumed; no second session was started
        var (request, conversation) = Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Equal(ScriptedConversations.FirstReference, conversation.Reference);
        Assert.Equal(conversation.Profile, request.Profile);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        // the opaque reference is not dumped by default
        Assert.DoesNotContain(ScriptedConversations.FirstReference, output);

        // only the feedback was sent: no task, no scope, no checks restated
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains(Feedback, prompt);
        Assert.Contains("Continue working from the current workspace.", prompt);
        Assert.DoesNotContain("symbol_present", prompt);
        Assert.DoesNotContain("Workspace", prompt);

        // meaning still advances: new semantic revision, old gate closed for good, new gate current, verified again
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, task.CurrentSemanticRevision.Kind);
        Assert.Equal(GateAction.RequestChanges, task.DecisionFor(firstGate)!.Action);
        Assert.NotEqual(firstGate, task.PendingGate!.Id);
        Assert.True(task.IsCurrent(task.PendingGate));
        Assert.NotNull(task.CurrentVerification);

        // and the conversation is still the same one afterwards
        Assert.Equal(ScriptedConversations.FirstReference, StoreOf(harness).Find(runId)!.Conversation.Reference);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString(), "--gate", task.PendingGate.Id.ToString()));
        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);
    }

    // ---- explicit fallback ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_from_before_native_continuity_gets_an_explicit_fresh_session_with_the_reason()
    {
        var (harness, runId, firstGate) = await AwaitingAsync(resumable: true);
        using var _ = harness;
        File.Delete(StoreOf(harness).PathOf(runId));   // a legacy run: nothing was ever recorded
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: fresh session", output);
        Assert.Contains("Reason: native conversation reference unavailable", output);
        Assert.Empty(harness.Factory.Conversations!.Resumes);
        Assert.Equal(2, harness.Factory.Runtime.StartRequests.Count);

        // the fresh session had no memory, so it was sent the task as well as the feedback
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains(Feedback, prompt);
        Assert.Contains("symbol_present", prompt);
    }

    [Fact]
    public async Task A_runtime_that_cannot_resume_gets_an_explicit_fresh_session_with_the_reason()
    {
        var (harness, runId, firstGate) = await AwaitingAsync(resumable: false);
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: fresh session", output);
        Assert.Contains("Reason: native resume unavailable", output);
        Assert.Equal(2, harness.Factory.Runtime.StartRequests.Count);
    }

    [Fact]
    public async Task A_conversation_the_runtime_says_does_not_exist_is_an_explicit_fresh_session()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentConversationNotFoundException("gone");
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("Agent continuation: fresh session", output);
        Assert.Contains("Reason: the native conversation no longer exists", output);
        Assert.Single(harness.Factory.Conversations.Resumes);
        Assert.Equal(2, harness.Factory.Runtime.StartRequests.Count);
    }

    // ---- errors are not fallbacks -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resume_that_fails_for_another_reason_is_an_error_not_a_fresh_session()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        var output = harness.Out.ToString();
        Assert.Equal(14, exit);
        Assert.Contains("Agent continuation: resume failed", output);
        Assert.DoesNotContain("Agent continuation: fresh session", output);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        // the recorded conversation is untouched
        Assert.Equal(ScriptedConversations.FirstReference, StoreOf(harness).Find(runId)!.Conversation.Reference);
    }

    [Fact]
    public async Task A_corrupt_record_is_refused_before_the_decision_is_recorded_and_nothing_is_started()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        File.WriteAllText(StoreOf(harness).PathOf(runId), "{ not json");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        Assert.Equal(18, exit);
        Assert.Contains("recorded agent conversation cannot be trusted", harness.Out.ToString() + harness.Error.ToString());

        // refused up front: the gate is still open and nothing was resumed or started
        var task = await LoadAsync(harness, runId);
        Assert.Equal(firstGate, task.PendingGate!.Id);
        Assert.Null(task.DecisionFor(firstGate));
        Assert.Empty(harness.Factory.Conversations!.Resumes);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_record_for_another_profile_is_refused_before_the_decision_is_recorded()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        var own = StoreOf(harness).Find(runId)!;
        await StoreOf(harness).SaveAsync(new AgentConversationRecord(runId, new AgentConversationRef(new ProfileId("someone-elses-profile"), own.Conversation.Runtime, own.Conversation.Reference)));

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", Feedback);

        Assert.Equal(18, exit);
        Assert.Null((await LoadAsync(harness, runId)).DecisionFor(firstGate));
        Assert.Empty(harness.Factory.Conversations!.Resumes);
    }
}
