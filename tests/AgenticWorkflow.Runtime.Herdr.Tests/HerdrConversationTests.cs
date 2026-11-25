using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// M4C at the Herdr adapter: native conversation identity and resume, against the in-memory core. Nothing here starts Herdr or Claude; the real
/// behavior the adapter relies on (same conversation, same transcript, hard limits re-applied) is proven live.
/// </summary>
public sealed class HerdrConversationTests : IAsyncLifetime
{
    private static readonly ProfileId Profile = new("claude-local");
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    private readonly FakeHerdrCore _core = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "aw-conv-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private HerdrAgentRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _runtime = NewRuntime(ClaudeCodeLaunch.Profile(Profile));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
    }

    private HerdrAgentRuntime NewRuntime(params AgentLaunchProfile[] profiles)
        => new(
            new HerdrAgentRuntimeOptions { Session = "agenticworkflow-m4c-unit", Profiles = profiles, StartupTimeout = Short, CommandTimeout = Short },
            _core,
            _core);

    private AgentSessionRequest Request(ProfileId? profile = null) => new(profile ?? Profile, _workspace, "task");

    private string[] ArgvOfRun(int index)
        => _core.ParamsOf("terminal.run").ElementAt(index).GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();

    // ---- capability and identity ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_profile_with_a_conversation_launch_advertises_resume_and_one_without_does_not()
    {
        var plain = PlainProfile(new ProfileId("plain"));

        Assert.True(_runtime.CanResume(Profile));
        Assert.False(_runtime.CanResume(new ProfileId("not-configured")));

        var other = NewRuntime(plain);
        Assert.False(other.CanResume(plain.Profile));
    }

    [Fact]
    public async Task A_new_session_is_launched_with_a_conversation_reference_chosen_up_front_and_reports_it()
    {
        var session = await _runtime.StartSessionAsync(Request());

        var conversation = _runtime.ConversationOf(session)!;
        Assert.Equal(Profile, conversation.Profile);
        Assert.Equal("claude-code", conversation.Runtime);
        Assert.True(new ClaudeConversationLaunch().IsValidReference(conversation.Reference));

        var argv = ArgvOfRun(0);
        Assert.Equal(["--session-id", conversation.Reference], argv.Skip(1).Take(2));
        Assert.DoesNotContain("--resume", argv);
    }

    [Fact]
    public async Task Two_new_sessions_are_two_conversations()
    {
        var first = await _runtime.StartSessionAsync(Request());
        var second = await _runtime.StartSessionAsync(Request());

        Assert.NotEqual(_runtime.ConversationOf(first)!.Reference, _runtime.ConversationOf(second)!.Reference);
    }

    [Fact]
    public void An_unknown_session_has_no_conversation()
        => Assert.Null(_runtime.ConversationOf(new AgentSessionId("nope")));

    // ---- resume --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resume_launches_a_new_terminal_in_the_same_conversation_with_the_profiles_full_hard_limits_applied_again()
    {
        var original = await _runtime.StartSessionAsync(Request());
        var conversation = _runtime.ConversationOf(original)!;

        await _runtime.StopSessionAsync(original);

        // a fresh process, a fresh runtime: nothing of the first one remains
        await using var fresh = NewRuntime(ClaudeCodeLaunch.Profile(Profile));
        var resumed = await fresh.ResumeSessionAsync(Request(), conversation);

        Assert.NotEqual(original, resumed);
        Assert.Equal(conversation, fresh.ConversationOf(resumed));

        var firstArgv = ArgvOfRun(0);
        var resumedArgv = ArgvOfRun(1);

        // same conversation: --resume <reference>, never a second --session-id, never --fork-session
        Assert.Equal(["--resume", conversation.Reference], resumedArgv.Skip(1).Take(2));
        Assert.DoesNotContain("--session-id", resumedArgv);
        Assert.DoesNotContain("--fork-session", resumedArgv);

        // The conversation does not remember its launch limits: the deny list and permission mode are applied on EVERY launch, byte for byte as for a new session.
        Assert.Equal(firstArgv.Skip(3), resumedArgv.Skip(3));
        Assert.Equal(ClaudeCodeToolPolicy.BaselineDeniedTools, resumedArgv.SkipWhile(a => a != "--disallowedTools").Skip(1).ToArray());
        Assert.Equal("acceptEdits", resumedArgv[Array.IndexOf(resumedArgv, "--permission-mode") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", resumedArgv);
    }

    [Fact]
    public async Task A_resumed_session_takes_turns_like_any_other()
    {
        var original = await _runtime.StartSessionAsync(Request());
        var conversation = _runtime.ConversationOf(original)!;
        await _runtime.StopSessionAsync(original);

        var resumed = await _runtime.ResumeSessionAsync(Request(), conversation);

        var turn = await _runtime.SendTurnAsync(resumed, "feedback");
        Assert.Equal(1, turn.Value);
        Assert.Equal(["feedback"], _core.TerminalSnapshot().Single(t => t.Name == resumed.Value).Sent);
    }

    [Fact]
    public async Task Resuming_with_evidence_binds_the_hooks_to_exactly_the_resumed_conversation()
    {
        var hook = Path.Combine(_workspace, "hook.exe");
        File.WriteAllText(hook, string.Empty);
        var evidenceRoot = Path.Combine(_workspace, "..", "aw-conv-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        var check = new VerificationCheckSpec(new VerificationCheckId("build"), "build", "dotnet", ["build"], TimeSpan.FromMinutes(1));
        var options = new ClaudeEvidenceOptions { HookExecutable = hook, EvidenceRoot = evidenceRoot };

        await using var runtime = NewRuntime(ClaudeCodeLaunch.Profile(Profile, evidence: options));
        var original = await runtime.StartSessionAsync(Request() with { ObservableChecks = [check] });
        var conversation = runtime.ConversationOf(original)!;
        Assert.Equal(conversation.Reference, runtime.EvidenceChannelOf(original)!.SessionId);
        await runtime.StopSessionAsync(original);

        var resumed = await runtime.ResumeSessionAsync(Request() with { ObservableChecks = [check] }, conversation);

        var channel = runtime.EvidenceChannelOf(resumed)!;
        Assert.Equal(conversation.Reference, channel.SessionId);

        var argv = ArgvOfRun(1);
        Assert.Equal(["--settings", channel.SettingsPath, "--resume", conversation.Reference], argv.Skip(1).Take(4));
        Assert.Single(argv, a => a == conversation.Reference);

        await runtime.StopSessionAsync(resumed);

        if (Directory.Exists(evidenceRoot))
            Directory.Delete(evidenceRoot, recursive: true);
    }

    [Theory]
    [InlineData(AgentSessionPurpose.Review, true)]
    [InlineData(AgentSessionPurpose.Prove, true)]
    [InlineData(AgentSessionPurpose.Implementation, false)]
    public async Task Every_turn_sent_to_a_reviewer_or_prover_is_recorded_for_its_report_tool_and_an_implementers_is_not(AgentSessionPurpose purpose, bool recorded)
    {
        var hook = Path.Combine(_workspace, "hook.exe");
        File.WriteAllText(hook, string.Empty);
        var evidenceRoot = Path.Combine(_workspace, "..", "aw-turn-evidence-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new ClaudeEvidenceOptions { HookExecutable = hook, EvidenceRoot = evidenceRoot, OfferReview = purpose == AgentSessionPurpose.Review, OfferProve = purpose == AgentSessionPurpose.Prove, OfferClarification = true };
        var policy = new ClaudeCodeToolPolicy();
        var launch = purpose switch
        {
            AgentSessionPurpose.Review => ClaudeCodeLaunch.Profile(Profile, policy.ForReviewer(), evidence: options, purpose: purpose),
            AgentSessionPurpose.Prove => ClaudeCodeLaunch.Profile(Profile, policy.ForProver(), evidence: options, purpose: purpose),
            _ => ClaudeCodeLaunch.Profile(Profile, evidence: options),
        };

        await using var runtime = NewRuntime(launch);
        var session = await runtime.StartSessionAsync(Request() with { Purpose = purpose });
        var channel = runtime.EvidenceChannelOf(session)!;

        await runtime.SendTurnAsync(session, "the packet");
        await runtime.SendTurnAsync(session, "the one correction");

        var turns = Directory.EnumerateFiles(channel.Directory, ClaudeEvidenceFormat.TurnPrefix + "*").ToList();
        Assert.Equal(recorded ? 2 : 0, turns.Count);

        await runtime.StopSessionAsync(session);

        if (Directory.Exists(evidenceRoot))
            Directory.Delete(evidenceRoot, recursive: true);
    }

    // ---- refusals: nothing is launched ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reference_of_another_profile_runtime_or_a_malformed_one_is_refused_before_anything_is_launched()
    {
        var good = new AgentConversationRef(Profile, "claude-code", Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidAgentConversationException>(
            () => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(new ProfileId("someone-else"), "claude-code", good.Reference)));

        await Assert.ThrowsAsync<InvalidAgentConversationException>(
            () => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "codex", good.Reference)));

        // Claude would treat a non-UUID as a search term and open an interactive picker: it must never reach the command line.
        foreach (var malformed in new[] { "not-a-uuid", good.Reference.ToUpperInvariant(), "{" + good.Reference + "}", good.Reference + " --dangerously-skip-permissions", "--fork-session" })
        {
            await Assert.ThrowsAsync<InvalidAgentConversationException>(
                () => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", malformed)));
        }

        Assert.Empty(_core.ParamsOf("terminal.run"));
    }

    [Fact]
    public async Task A_profile_that_cannot_resume_refuses_to()
    {
        await using var runtime = NewRuntime(PlainProfile(Profile));

        var error = await Assert.ThrowsAsync<AgentRuntimeException>(
            () => runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", Guid.NewGuid().ToString())));

        Assert.IsNotType<AgentConversationNotFoundException>(error);
        Assert.Empty(_core.ParamsOf("terminal.run"));
    }

    // ---- failure modes are distinguishable -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_exit_that_says_the_conversation_does_not_exist_is_reported_as_not_found()
    {
        var reference = Guid.NewGuid().ToString();
        _core.StatusAtLaunch = null;
        _core.OnLaunch = t =>
        {
            t.Output = $"No conversation found with session ID: {reference}\n";
            t.End("exited", exitCode: 1);
        };

        await Assert.ThrowsAsync<AgentConversationNotFoundException>(
            () => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", reference)));

        // the dead terminal was cleaned up
        Assert.DoesNotContain(_core.TerminalSnapshot(), t => t.Labels.ContainsKey("aw_runtime"));
    }

    [Theory]
    [InlineData("Error: something else went wrong", 1)]
    [InlineData("", 1)]
    [InlineData("No conversation found with session ID: 00000000-0000-0000-0000-000000000000", 1)] // another conversation's id
    public async Task Any_other_startup_exit_is_a_startup_failure_not_a_missing_conversation(string output, int exitCode)
    {
        var reference = Guid.NewGuid().ToString();
        _core.StatusAtLaunch = null;
        _core.OnLaunch = t =>
        {
            t.Output = output;
            t.End("exited", exitCode);
        };

        var error = await Record.ExceptionAsync(() => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", reference)));

        Assert.IsType<AgentStartupBlockedException>(error);
    }

    [Fact]
    public async Task An_exit_with_zero_or_after_a_reported_status_is_never_not_found()
    {
        var reference = Guid.NewGuid().ToString();
        _core.StatusAtLaunch = null;
        _core.OnLaunch = t =>
        {
            t.Output = $"No conversation found with session ID: {reference}";
            t.End("exited", exitCode: 0);
        };

        Assert.IsType<AgentStartupBlockedException>(
            await Record.ExceptionAsync(() => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", reference))));

        // a new session that dies at startup is never classified as a missing conversation either
        _core.OnLaunch = t =>
        {
            t.Output = $"No conversation found with session ID: {reference}";
            t.End("exited", exitCode: 1);
        };

        Assert.IsType<AgentStartupBlockedException>(await Record.ExceptionAsync(() => _runtime.StartSessionAsync(Request())));
    }

    [Fact]
    public async Task A_core_that_refuses_the_launch_is_a_runtime_error_never_not_found()
    {
        _core.FailRunWith = "internal";

        var error = await Assert.ThrowsAsync<AgentRuntimeException>(
            () => _runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", Guid.NewGuid().ToString())));

        Assert.IsNotType<AgentConversationNotFoundException>(error);
    }

    // ---- the Claude launch knowledge -----------------------------------------------------------------------------------------

    [Fact]
    public void Claude_references_are_canonical_lowercase_uuids_and_only_those()
    {
        var launch = new ClaudeConversationLaunch();
        var issued = launch.NewReference();

        Assert.True(launch.IsValidReference(issued));
        Assert.False(launch.IsValidReference(issued.ToUpperInvariant()));
        Assert.False(launch.IsValidReference(issued.Replace("-", string.Empty)));
        Assert.False(launch.IsValidReference(string.Empty));
        Assert.False(launch.IsValidReference("latest"));
    }

    /// <summary>A launch profile whose program cannot name conversations.</summary>
    private static AgentLaunchProfile PlainProfile(ProfileId id) => new(id, ["some-agent"], "some-agent");
}
