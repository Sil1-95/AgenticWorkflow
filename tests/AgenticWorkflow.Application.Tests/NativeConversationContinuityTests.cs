using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Tests;

/// <summary>An in-memory <see cref="IAgentConversationStore"/>: what the CLI keeps in the run directory, without the file system.</summary>
internal sealed class InMemoryConversationStore : IAgentConversationStore
{
    public Dictionary<TaskId, AgentConversationRecord> Records { get; } = [];

    /// <summary>Thrown by <see cref="FindAsync"/> when set: what the real store does for a record it cannot trust.</summary>
    public Exception? FailFind { get; set; }

    /// <summary>Returned by <see cref="FindAsync"/> instead of the stored record: a store that hands back the wrong thing.</summary>
    public AgentConversationRecord? Override { get; set; }

    public Exception? FailSave { get; set; }

    public int Saves { get; private set; }

    public Task<AgentConversationRecord?> FindAsync(TaskId task, CancellationToken cancellationToken = default)
    {
        if (FailFind is { } failure)
            throw failure;

        if (Override is { } forced)
            return Task.FromResult<AgentConversationRecord?>(forced);

        return Task.FromResult(Records.TryGetValue(task, out var record) ? record : null);
    }

    public Task SaveAsync(AgentConversationRecord record, CancellationToken cancellationToken = default)
    {
        if (FailSave is { } failure)
            throw failure;

        Saves++;
        Records[record.Task] = record;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A scripted resumable runtime beside <see cref="FakeAgentRuntime"/>: every session it hosts has a conversation named after it, and a resume attaches a new
/// session (a new process) to the recorded one.
/// </summary>
internal sealed class FakeConversationRuntime(FakeAgentRuntime sessions) : IAgentConversationRuntime
{
    private readonly Dictionary<AgentSessionId, AgentConversationRef> _conversations = [];

    public bool Supported { get; set; } = true;

    /// <summary>Thrown by <see cref="ResumeSessionAsync"/> when set (after it has been recorded).</summary>
    public Exception? FailResume { get; set; }

    public List<(AgentSessionRequest Request, AgentConversationRef Conversation)> Resumes { get; } = [];

    public bool CanResume(ProfileId profile) => Supported;

    public AgentConversationRef? ConversationOf(AgentSessionId session)
    {
        if (_conversations.TryGetValue(session, out var known))
            return known;

        // a session started through the plain port: its conversation is named after it
        var start = sessions.Starts.ElementAtOrDefault(int.Parse(session.Value["session-".Length..]) - 1);
        return start is null ? null : _conversations[session] = new AgentConversationRef(start.Profile, "fake", $"conversation-of-{session.Value}");
    }

    public Task<AgentSessionId> ResumeSessionAsync(AgentSessionRequest request, AgentConversationRef conversation, CancellationToken cancellationToken = default)
    {
        Resumes.Add((request, conversation));

        if (FailResume is { } failure)
            throw failure;

        var session = sessions.Attach();
        _conversations[session] = conversation;
        return Task.FromResult(session);
    }
}

/// <summary>
/// M4C at the controller: after the human requests changes the same native conversation is resumed when it can be, and a fresh session is an explicit,
/// observable fallback only for the conditions that positively mean there is nothing to resume. Everything else is an error, never silent context loss.
/// </summary>
public sealed class NativeConversationContinuityTests
{
    private const string Feedback = "Also rename the documentation references.";

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        public DirectPathRunner Runner(DirectRunOptions? options = null)
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options, evidence: null, new AgentConversationSupport(Conversations, Store));

        public Task<DirectRunResult> RunAsync(DirectRunOptions? options = null)
            => Runner(options).RunAsync(Fixture.TaskOne, H.Definition());

        /// <summary>A fresh runner (as in a fresh process) continuing after the human's request for changes.</summary>
        public Task<DirectRunResult> ContinueAsync()
            => Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, H.Definition());
    }

    private static async Task<Rig> NewRigAsync(AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);
        var runtime = new FakeConversationRuntime(h.Runtime);
        return new Rig { H = h, Conversations = runtime, Store = new InMemoryConversationStore() };
    }

    /// <summary>First stretch to the ACCEPT gate, then the human requests changes (the controller's own decision, recorded before any agent work).</summary>
    private static async Task<Gate> ReachGateAndRequestChangesAsync(Rig rig)
    {
        rig.H.Runtime.Enqueue(rig.H.Renames());
        var first = await rig.RunAsync();
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, first.Status);

        var gate = first.Task!.Task.PendingGate!;
        var requested = await rig.H.Service.RequestChangesAsync(first.Task, gate.Id, Feedback);
        rig.H.Snapshot = requested.Snapshot;

        return gate;
    }

    // ---- recording the conversation ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_session_that_has_a_resumable_conversation_has_it_recorded_durably_for_that_run_and_profile()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(AgentContinuationKind.NewConversation, result.Continuation!.Kind);

        var record = Assert.Contains(Fixture.TaskOne, rig.Store.Records);
        Assert.Equal(Fixture.TaskOne, record.Task);
        Assert.Equal(new ProfileId(DirectHarness.ProfileName), record.Conversation.Profile);
        Assert.Equal("fake", record.Conversation.Runtime);
        Assert.Equal(rig.Conversations.ConversationOf(result.Session!.Value), record.Conversation);
    }

    [Fact]
    public async Task A_runtime_without_the_capability_records_nothing_and_the_run_is_otherwise_unchanged()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(AgentContinuationKind.NewConversation, result.Continuation!.Kind);
        Assert.Single(h.Runtime.Starts);
    }

    [Fact]
    public async Task A_store_that_cannot_save_never_fails_the_run_it_only_costs_the_ability_to_resume()
    {
        var rig = await NewRigAsync();
        rig.Store.FailSave = new IOException("disk full");
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("could not be recorded", StringComparison.Ordinal));
        Assert.Empty(rig.Store.Records);
    }

    // ---- resume first --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_changes_resumes_the_recorded_conversation_instead_of_starting_a_new_session()
    {
        var rig = await NewRigAsync();
        var gate = await ReachGateAndRequestChangesAsync(rig);
        var recorded = rig.Store.Records[Fixture.TaskOne].Conversation;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        Assert.Equal(AgentContinuationKind.ResumedNative, result.Continuation!.Kind);
        Assert.Null(result.Continuation.Reason);

        // the same conversation, attached to a new process; no new session was started
        var resume = Assert.Single(rig.Conversations.Resumes);
        Assert.Equal(recorded, resume.Conversation);
        Assert.Single(rig.H.Runtime.Starts);

        // the closed gate stays closed and a new one is open
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.NotEqual(gate.Id, result.Task!.Task.PendingGate!.Id);
    }

    [Fact]
    public async Task The_resumed_conversation_receives_the_feedback_and_not_the_task_again_or_any_transcript()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        var resumedSession = result.Session!.Value;
        var sent = rig.H.Runtime.Sends.Where(s => s.Session == resumedSession).Select(s => s.Prompt).ToList();
        var prompt = Assert.Single(sent);

        Assert.Contains(Feedback, prompt, StringComparison.Ordinal);
        Assert.Contains("Continue working from the current workspace.", prompt, StringComparison.Ordinal);

        // what the agent already knows is not restated: no objective, no scope, no checks
        Assert.DoesNotContain("Rename the public method", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You may change only", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("tests:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_resumed_session_is_launched_for_the_same_profile_and_workspace_as_the_run()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        await rig.ContinueAsync();

        var (request, conversation) = Assert.Single(rig.Conversations.Resumes);
        var original = Assert.Single(rig.H.Runtime.Starts);
        Assert.Equal(original.Profile, request.Profile);
        Assert.Equal(original.WorkspacePath, request.WorkspacePath);
        Assert.Equal(request.Profile, conversation.Profile);
    }

    [Fact]
    public async Task A_resumed_conversation_that_still_fails_verification_is_repaired_in_that_same_session()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);

        // first resumed turn breaks the build, the repair turn fixes it
        rig.H.Runtime.Enqueue(
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] = "public string GetMeal() => \"hello\";"),
            new TurnScript(rig.H.Workspace.RenameToGetFood));

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        var resumed = result.Session!.Value;
        var prompts = rig.H.Runtime.Sends.Where(s => s.Session == resumed).Select(s => s.Prompt).ToList();
        Assert.Equal(2, prompts.Count);
        Assert.StartsWith("Controller verification failed", prompts[1], StringComparison.Ordinal);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Single(rig.Conversations.Resumes);
    }

    [Fact]
    public async Task The_resumed_session_is_stopped_when_the_run_ends_and_only_that_session()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));
        var stopsBefore = rig.H.Runtime.Stops.Count;

        var result = await rig.ContinueAsync();

        Assert.Equal([result.Session!.Value], rig.H.Runtime.Stops.Skip(stopsBefore).ToArray());
        Assert.DoesNotContain(rig.H.Runtime.Unrelated[0], rig.H.Runtime.Stops);
    }

    // ---- semantic currentness is independent of the conversation -------------------------------------------------------------

    [Fact]
    public async Task Resuming_the_conversation_does_not_reuse_the_semantic_revision_or_the_old_verification_and_gate()
    {
        var rig = await NewRigAsync();
        var before = rig.H.Snapshot.Task.CurrentSemanticRevisionId;
        var gate = await ReachGateAndRequestChangesAsync(rig);

        var afterDecision = rig.H.Snapshot.Task;
        Assert.NotEqual(before, afterDecision.CurrentSemanticRevisionId);
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, afterDecision.CurrentSemanticRevision.Kind);
        Assert.False(afterDecision.IsCurrent(gate));

        var firstImplementation = afterDecision.CurrentImplementationRevisionId;
        var firstVerification = afterDecision.CurrentVerification!.Id;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        var task = result.Task!.Task;
        Assert.Equal(AgentContinuationKind.ResumedNative, result.Continuation!.Kind);
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);

        // a NEW implementation revision under the NEW semantic revision, verified anew, with a NEW gate; the old gate is closed for good
        Assert.NotEqual(firstImplementation, task.CurrentImplementationRevisionId);
        Assert.Equal(task.CurrentSemanticRevisionId, task.CurrentImplementationRevision!.SemanticRevisionId);
        Assert.NotEqual(firstVerification, task.CurrentVerification!.Id);
        Assert.Equal(GateStatus.ChangesRequested, task.StatusOf(gate));
        Assert.True(task.IsCurrent(task.PendingGate!));
        Assert.False(task.IsCurrent(gate));
    }

    // ---- explicit fallback ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_with_no_recorded_conversation_falls_back_to_a_fresh_session_explicitly_with_the_original_task_and_the_feedback()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.Store.Records.Clear();   // a run from before native continuity
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.NoConversationRecorded), result.Continuation);
        Assert.Empty(rig.Conversations.Resumes);
        Assert.Equal(2, rig.H.Runtime.Starts.Count);
        Assert.Contains(result.Notes, n => n.Contains("fresh agent session", StringComparison.Ordinal));

        // the fresh session has no memory, so it gets the whole task as well as the feedback (the M4A prompt)
        var prompt = rig.H.Runtime.Sends.Last(s => s.Session == result.Session!.Value).Prompt;
        Assert.Contains(Feedback, prompt, StringComparison.Ordinal);
        Assert.Contains("Rename the public method", prompt, StringComparison.Ordinal);
        Assert.Contains("Workspace:", prompt, StringComparison.Ordinal);

        // and the fresh session is now the run's conversation
        Assert.Equal(rig.Conversations.ConversationOf(result.Session!.Value), rig.Store.Records[Fixture.TaskOne].Conversation);
    }

    [Fact]
    public async Task A_runtime_that_does_not_advertise_resume_falls_back_explicitly_and_never_asks_it_to_resume()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.Conversations.Supported = false;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.ResumeUnsupported), result.Continuation);
        Assert.Empty(rig.Conversations.Resumes);
    }

    [Fact]
    public async Task A_runner_composed_without_conversation_support_falls_back_explicitly()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());
        var first = await h.RunAsync();
        var requested = await h.Service.RequestChangesAsync(first.Task!, first.Task!.Task.PendingGate!.Id, Feedback);
        h.Snapshot = requested.Snapshot;
        h.Runtime.Enqueue(new TurnScript(() => h.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await h.Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, h.Definition());

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.ResumeUnsupported), result.Continuation);
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
    }

    [Fact]
    public async Task A_conversation_the_runtime_positively_says_does_not_exist_falls_back_explicitly()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.Conversations.FailResume = new AgentConversationNotFoundException("no such conversation");
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Docs.cs"] = "// docs use GetFood"));

        var result = await rig.ContinueAsync();

        Assert.Equal(new AgentContinuation(AgentContinuationKind.FreshSession, AgentContinuationReasons.ConversationNotFound), result.Continuation);
        Assert.Single(rig.Conversations.Resumes);          // it was tried first
        Assert.Equal(2, rig.H.Runtime.Starts.Count);       // and then, explicitly, a fresh session
        Assert.Contains("Rename the public method", rig.H.Runtime.Sends.Last().Prompt, StringComparison.Ordinal);
    }

    // ---- errors are not fallbacks --------------------------------------------------------------------------------------------

    public static TheoryData<Exception> TransientResumeFailures => new()
    {
        new AgentRuntimeException("the core crashed"),
        new AgentRuntimeException("timed out"),
        new AgentStartupBlockedException("a dialog is waiting"),
        new IOException("pipe broken"),
    };

    [Theory]
    [MemberData(nameof(TransientResumeFailures))]
    public async Task A_resume_that_fails_for_any_other_reason_ends_the_run_and_never_silently_starts_a_fresh_session(Exception failure)
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        var recordedBefore = rig.Store.Records[Fixture.TaskOne];
        var savesBefore = rig.Store.Saves;
        rig.Conversations.FailResume = failure;

        var result = await rig.ContinueAsync();

        Assert.NotEqual(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(AgentContinuationKind.ResumeFailed, result.Continuation!.Kind);
        Assert.Single(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);                 // no second, fresh session
        Assert.DoesNotContain(rig.H.Runtime.Sends, s => s.Prompt.Contains(Feedback, StringComparison.Ordinal));

        // the recorded conversation is untouched, so the person can try again
        Assert.Equal(recordedBefore, rig.Store.Records[Fixture.TaskOne]);
        Assert.Equal(savesBefore, rig.Store.Saves);
    }

    [Fact]
    public async Task A_corrupt_recorded_conversation_is_refused_and_nothing_is_started_or_resumed()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        rig.Store.FailFind = new InvalidAgentConversationException("'conversation.json' is unreadable");

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("conversation_reference_invalid:", result.Reason, StringComparison.Ordinal);
        Assert.Equal(AgentContinuationKind.ResumeFailed, result.Continuation!.Kind);
        Assert.Empty(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task A_conversation_recorded_for_another_run_is_never_resumed()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        var own = rig.Store.Records[Fixture.TaskOne];
        rig.Store.Override = new AgentConversationRecord(Fixture.TaskTwo, own.Conversation);

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.Contains("conversation_reference_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);
    }

    [Fact]
    public async Task A_conversation_recorded_for_another_profile_is_never_resumed()
    {
        var rig = await NewRigAsync();
        await ReachGateAndRequestChangesAsync(rig);
        var own = rig.Store.Records[Fixture.TaskOne];
        rig.Store.Override = new AgentConversationRecord(Fixture.TaskOne, new AgentConversationRef(new ProfileId("someone-elses-profile"), own.Conversation.Runtime, own.Conversation.Reference));

        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.Contains("profile", result.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);
    }

    // ---- other paths are unchanged -------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_acceptance_required_the_run_completes_exactly_as_before_and_nothing_is_ever_resumed()
    {
        var rig = await NewRigAsync(AcceptanceRequirement.NotRequired);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Null((await rig.H.ReloadAsync()).PendingGate);
        Assert.Empty(rig.Conversations.Resumes);

        // there is no gate and so no request-changes path: a continuation is rejected, still without touching the runtime
        var again = await rig.ContinueAsync();
        Assert.Equal(DirectRunStatus.Rejected, again.Status);
        Assert.Empty(rig.Conversations.Resumes);
        Assert.Single(rig.H.Runtime.Starts);
    }

    [Fact]
    public async Task A_native_human_interaction_is_still_only_observed_with_conversation_support_wired_in()
    {
        var rig = await NewRigAsync();
        var waiting = AgentTurnOutcome.AwaitingHuman(new AgentInteraction(AgentInteractionKind.Permission, "approve Bash: dotnet --list-sdks"));
        rig.H.Runtime.Enqueue(new TurnScript(rig.H.Workspace.RenameToGetFood, Observed: [waiting, AgentTurnOutcome.Resumed]));

        var result = await rig.RunAsync(new DirectRunOptions { WaitForHuman = true });

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Single(rig.H.Runtime.Sends);                  // nothing was sent to answer the prompt
        Assert.Empty((await rig.H.ReloadAsync()).PolicyDecisions);
        Assert.Empty(rig.Conversations.Resumes);
    }

    [Fact]
    public async Task A_continuation_that_is_not_requested_is_rejected_before_any_conversation_is_looked_up()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames());
        await rig.RunAsync();   // the gate is open; nothing was requested

        rig.Store.FailFind = new InvalidOperationException("must not be consulted");
        var result = await rig.ContinueAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.Contains("task_not_running", result.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Conversations.Resumes);
    }

    [Fact]
    public void A_conversation_reference_is_bounded_single_line_opaque_text()
    {
        var profile = new ProfileId("p");

        Assert.Throws<ArgumentException>(() => new AgentConversationRef(profile, "", "x"));
        Assert.Throws<ArgumentException>(() => new AgentConversationRef(profile, "r", " "));
        Assert.Throws<ArgumentException>(() => new AgentConversationRef(profile, "r", "line1\nline2"));
        Assert.Throws<ArgumentException>(() => new AgentConversationRef(profile, "r", new string('x', AgentConversationRef.MaxLength + 1)));
        Assert.Equal("x", new AgentConversationRef(profile, "r", "x").Reference);
    }
}
