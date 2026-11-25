using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;
using AgenticWorkflow.Runtime.Herdr.Pi;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Pi adapter, offline: the exact launch line, native conversation identity and resume, how the session's workflow signals are attributed and recorded exactly once, and the
/// refusal of a launch that does not report the model and reasoning it was asked for. The in-memory Herdr core stands in for Herdr; a stand-in for Pi's extension writes the report
/// the real extension writes. No Pi, no Herdr, no model.
/// </summary>
public sealed class PiHarnessTests : IDisposable
{
    private const string HowTool = "mcp__aw__propose_how";
    private const string ReviewRequestTool = "mcp__aw__request_review";
    private const string Model = "llama/Qwen3.8-27B";

    private static readonly ProfileId Profile = new("pi-main");
    private static readonly string[] Program = ["node", "cli.js"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-pi-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public PiHarnessTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceOptions Options(string? hook = null)
        => new() { HookExecutable = hook ?? _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferClarification = true, OfferWhat = true, OfferHow = true, OfferReclassification = true, OfferReviewRequest = true, OfferProveRequest = true };

    private AgentLaunchProfile PiProfile(string? model = Model, string? reasoning = "low", TimeSpan? reportTimeout = null, string? hook = null, bool readOnly = false)
    {
        var profile = PiLaunch.Profile(Profile, Program, Options(hook), PiLaunch.DeniedCommandsOf(ClaudeCodeToolPolicy.BaselineDeniedTools), model: model, reasoning: reasoning, readOnly: readOnly);

        if (reportTimeout is null)
            return profile;

        // the same launch with a short wait for Pi's report, so a missing one does not take the production 20 seconds
        return new AgentLaunchProfile(
            Profile,
            profile.Argv,
            profile.AgentApp,
            profile.Evidence! with { Harness = new PiChannelHarness(Program.Length, model, reasoning, [], reportTimeout) },
            profile.Conversation);
    }

    // ---- the launch line --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_profile_that_chooses_neither_launches_the_bare_program()
        => Assert.Equal(Program, PiLaunch.Profile(Profile, Program, Options(), []).Argv);

    [Fact]
    public void A_model_and_a_reasoning_level_are_separate_arguments_after_the_program_and_nothing_else_is_added()
        => Assert.Equal(["node", "cli.js", "--model", Model, "--thinking", "low"], PiProfile().Argv);

    [Fact]
    public void Only_the_value_that_was_chosen_is_passed()
    {
        Assert.Equal(["node", "cli.js", "--model", Model], PiProfile(reasoning: null).Argv);
        Assert.Equal(["node", "cli.js", "--thinking", "high"], PiProfile(model: null, reasoning: "high").Argv);
    }

    [Fact]
    public void A_reviewer_or_prover_gets_the_read_only_built_in_tools_and_nothing_that_edits_or_runs_commands()
    {
        var argv = PiProfile(readOnly: true).Argv;

        Assert.Equal(["--tools", "read,grep,find,ls"], argv.SkipWhile(a => a != "--tools").ToArray());
        Assert.DoesNotContain("bash", string.Join(' ', argv));
        Assert.DoesNotContain("edit", string.Join(' ', argv.SkipWhile(a => a != "--tools")));
    }

    [Theory]
    [InlineData("--no-approve")]
    [InlineData("-x")]
    [InlineData("llama/x --tools bash")]
    [InlineData("a\nb")]
    [InlineData("")]
    [InlineData("  ")]
    public void A_value_that_is_a_flag_or_more_than_one_argument_is_refused_not_cleaned(string value)
    {
        Assert.Throws<ArgumentException>(() => PiLaunch.Profile(Profile, Program, Options(), [], model: value));
        Assert.Throws<ArgumentException>(() => PiLaunch.Profile(Profile, Program, Options(), [], reasoning: value));
    }

    [Fact]
    public void Nothing_in_the_launch_asks_pi_to_skip_its_own_checks_or_to_bypass_a_permission()
    {
        var argv = string.Join(' ', PiProfile().Argv);

        Assert.DoesNotContain("--no-approve", argv);
        Assert.DoesNotContain("--api-key", argv);
        Assert.DoesNotContain("--no-session", argv);
        Assert.DoesNotContain("--no-extensions", argv);
    }

    [Fact]
    public void The_denied_commands_are_the_shell_prefixes_of_the_deny_rules_and_the_web_tools_have_no_pi_counterpart()
    {
        var commands = PiLaunch.DeniedCommandsOf(ClaudeCodeToolPolicy.BaselineDeniedTools);

        Assert.Contains("git commit", commands);
        Assert.Contains("git push", commands);
        Assert.Contains("curl", commands);
        Assert.Contains("dotnet nuget", commands);
        Assert.DoesNotContain(commands, c => c.Contains("WebFetch", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(commands.Distinct(), commands);
    }

    // ---- native conversation -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_new_conversation_is_named_with_session_id_and_a_resumed_one_is_entered_with_session_never_session_id()
    {
        var launch = new PiConversationLaunch(2);
        var reference = launch.NewReference();
        var argv = (IReadOnlyList<string>)["node", "cli.js", "--model", Model];

        Assert.Equal(["node", "cli.js", "--session-id", reference, "--model", Model], launch.ForNewConversation(argv, reference));
        Assert.Equal(["node", "cli.js", "--session", reference, "--model", Model], launch.ForResume(argv, reference));
        Assert.DoesNotContain("--session-id", launch.ForResume(argv, reference));   // --session-id would silently create a conversation that does not exist
    }

    [Theory]
    [InlineData("not-a-guid", false)]
    [InlineData("2251302D-BE89-49BC-914B-2EC818A88326", false)]    // only the canonical lower-case form this launch issues
    [InlineData("2251302d-be89-49bc-914b-2ec818a88326", true)]
    [InlineData("2251302d", false)]                               // Pi would match a partial id; it is never passed
    [InlineData("../../session.jsonl", false)]                    // nor a path
    public void Only_canonical_uuids_are_ever_passed_as_a_conversation(string reference, bool valid)
        => Assert.Equal(valid, new PiConversationLaunch().IsValidReference(reference));

    [Fact]
    public void A_missing_conversation_is_recognised_only_from_pis_own_words_about_exactly_that_reference()
    {
        var launch = new PiConversationLaunch();
        var reference = launch.NewReference();

        Assert.True(launch.IndicatesMissingConversation($"No session found matching '{reference}'", reference));
        Assert.False(launch.IndicatesMissingConversation($"No session found matching '{launch.NewReference()}'", reference));
        Assert.False(launch.IndicatesMissingConversation("model not found", reference));
    }

    // ---- the per-session extension ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_channel_is_the_embedded_extension_plus_a_bridge_naming_only_the_hook_the_channel_and_the_denied_commands()
    {
        using var channel = ClaudeEvidenceChannel.TryCreate(PiProfile().Evidence!, _workspace, [], sessionId: null, true, true, true, true, true, false, true, false, out var failure)
            ?? throw new InvalidOperationException(failure);

        var extension = File.ReadAllText(channel.SettingsPath);
        using var bridge = JsonDocument.Parse(File.ReadAllText(Path.Combine(channel.Directory, PiChannelHarness.BridgeFile)));

        Assert.Equal(PiChannelHarness.ExtensionFile, Path.GetFileName(channel.SettingsPath));
        Assert.Contains("registerMcpServer", extension);
        Assert.Equal(Path.GetFullPath(_hookExe), bridge.RootElement.GetProperty("hook").GetString());
        Assert.Equal(channel.Directory, bridge.RootElement.GetProperty("channel").GetString());
        Assert.True(bridge.RootElement.GetProperty("mcp").GetBoolean());
        Assert.Contains("git commit", bridge.RootElement.GetProperty("deniedCommands").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(["channel", "deniedCommands", "hook", "mcp"], bridge.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(channel.ObservesExecutions);
        Assert.Null(channel.McpConfigPath);
    }

    [Fact]
    public void The_extension_is_loaded_for_this_session_only_after_the_program_and_before_its_options()
    {
        using var channel = ClaudeEvidenceChannel.TryCreate(PiProfile().Evidence!, _workspace, [], sessionId: null, true, false, true, out var failure)
            ?? throw new InvalidOperationException(failure);

        var argv = channel.Augment(["node", "cli.js", "--session-id", channel.SessionId, "--model", Model], nameSession: false);

        Assert.Equal(["node", "cli.js", "-e", channel.SettingsPath, "--session-id", channel.SessionId, "--model", Model], argv);
    }

    // ---- verification of what Pi started with ------------------------------------------------------------------------------------

    private static void WriteReport(string directory, string? sessionId, string? model, string? thinking)
        => File.WriteAllText(Path.Combine(directory, PiChannelHarness.LaunchReportFile), JsonSerializer.Serialize(new { sessionId, model, thinking }));

    private async Task<string?> Verify(string? expectedModel, string? expectedReasoning, Action<string>? report, string session = "s-1")
    {
        var directory = Path.Combine(_root, "verify-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        report?.Invoke(directory);

        return await new PiChannelHarness(1, expectedModel, expectedReasoning, [], TimeSpan.FromMilliseconds(200)).VerifyStartedAsync(directory, session, CancellationToken.None);
    }

    [Fact]
    public async Task A_launch_that_reports_exactly_what_was_asked_for_is_accepted()
        => Assert.Null(await Verify(Model, "low", dir => WriteReport(dir, "s-1", Model, "low")));

    [Fact]
    public async Task A_profile_that_asked_for_nothing_accepts_whatever_pis_own_setting_is()
        => Assert.Null(await Verify(null, null, dir => WriteReport(dir, "s-1", "other/model", "high")));

    [Fact]
    public async Task A_model_pi_substituted_is_refused_naming_both()
    {
        var problem = await Verify(Model, null, dir => WriteReport(dir, "s-1", "llama/Plain-NoReason", "off"));

        Assert.Contains("llama/Plain-NoReason", problem);
        Assert.Contains(Model, problem);
    }

    [Fact]
    public async Task A_thinking_level_pi_clamped_is_refused_naming_both()
    {
        var problem = await Verify(Model, "xhigh", dir => WriteReport(dir, "s-1", Model, "high"));

        Assert.Contains("'high'", problem);
        Assert.Contains("'xhigh'", problem);
    }

    [Fact]
    public async Task A_different_conversation_than_the_one_named_is_refused()
        => Assert.Contains("different conversation", await Verify(null, null, dir => WriteReport(dir, "someone-else", null, null)));

    [Fact]
    public async Task A_launch_that_never_reports_is_refused_because_it_cannot_be_verified()
        => Assert.Contains("did not report", await Verify(Model, "low", report: null));

    // ---- the runtime ------------------------------------------------------------------------------------------------------------

    /// <summary>What Pi's extension does at start-up: it reports the session id, model and thinking level Pi actually started with, as read from the launch line.</summary>
    private static void SimulateExtension(FakeTerminal terminal, string? model = null, string? thinking = null)
    {
        var argv = terminal.Argv;
        var extension = argv[Array.IndexOf(argv, "-e") + 1];
        var session = argv[Math.Max(Array.IndexOf(argv, "--session-id"), Array.IndexOf(argv, "--session")) + 1];

        WriteReport(
            Path.GetDirectoryName(extension)!,
            session,
            model ?? (Array.IndexOf(argv, "--model") is var m and >= 0 ? argv[m + 1] : null),
            thinking ?? (Array.IndexOf(argv, "--thinking") is var t and >= 0 ? argv[t + 1] : null));
    }

    private HerdrAgentRuntime Runtime(out FakeHerdrCore core, AgentLaunchProfile? profile = null, Action<FakeTerminal>? onLaunch = null)
    {
        var fake = core = new FakeHerdrCore { OnLaunch = onLaunch ?? (terminal => SimulateExtension(terminal)) };

        return new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions { Session = "aw-pi-unit", Profiles = [profile ?? PiProfile()], StartupTimeout = TimeSpan.FromSeconds(10), CommandTimeout = TimeSpan.FromSeconds(10) },
            fake,
            fake);
    }

    private AgentSessionRequest Request() => new(Profile, _workspace, "task");

    private static string[] ArgvOfRun(FakeHerdrCore core, int index)
        => core.ParamsOf("terminal.run").ElementAt(index).GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();

    [Fact]
    public async Task A_new_pi_session_is_launched_with_the_exact_argument_list_and_reports_the_conversation_it_named()
    {
        await using var runtime = Runtime(out var core);

        var session = await runtime.StartSessionAsync(Request());
        var conversation = runtime.ConversationOf(session)!;
        var channel = runtime.EvidenceChannelOf(session)!;

        Assert.Equal("pi", conversation.Runtime);
        Assert.Equal(Profile, conversation.Profile);
        Assert.Equal(["node", "cli.js", "-e", channel.SettingsPath, "--session-id", conversation.Reference, "--model", Model, "--thinking", "low"], ArgvOfRun(core, 0));
        Assert.Equal(conversation.Reference, channel.SessionId);   // signals are attributed to exactly the conversation that was named
    }

    [Fact]
    public async Task A_resumed_pi_session_is_the_same_conversation_in_a_fresh_process_with_the_same_recorded_model_and_reasoning()
    {
        await using var first = Runtime(out var firstCore);
        var original = await first.StartSessionAsync(Request());
        var conversation = first.ConversationOf(original)!;
        await first.StopSessionAsync(original);

        await using var fresh = Runtime(out var freshCore);
        var resumed = await fresh.ResumeSessionAsync(Request(), conversation);

        Assert.Equal(conversation, fresh.ConversationOf(resumed));

        var firstArgv = ArgvOfRun(firstCore, 0);
        var resumedArgv = ArgvOfRun(freshCore, 0);

        Assert.Equal(["--session", conversation.Reference], resumedArgv.SkipWhile(a => a != "--session").Take(2));
        Assert.DoesNotContain("--session-id", resumedArgv);
        Assert.DoesNotContain("--fork", resumedArgv);
        Assert.Equal(firstArgv.SkipWhile(a => a != "--model"), resumedArgv.SkipWhile(a => a != "--model"));   // model and reasoning replayed, never re-chosen
        Assert.Equal(conversation.Reference, fresh.EvidenceChannelOf(resumed)!.SessionId);
    }

    [Fact]
    public async Task A_conversation_of_another_runtime_or_a_malformed_reference_is_refused_before_anything_starts()
    {
        await using var runtime = Runtime(out var core);

        await Assert.ThrowsAsync<InvalidAgentConversationException>(() => runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "claude-code", Guid.NewGuid().ToString())));
        await Assert.ThrowsAsync<InvalidAgentConversationException>(() => runtime.ResumeSessionAsync(Request(), new AgentConversationRef(Profile, "pi", "2251302d")));
        Assert.Empty(core.ParamsOf("terminal.run"));
    }

    [Fact]
    public async Task A_pi_that_started_with_another_model_is_stopped_and_refused_and_nothing_else_is_launched()
    {
        await using var runtime = Runtime(out var core, onLaunch: terminal => SimulateExtension(terminal, model: "llama/Plain-NoReason"));

        var failure = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.StartSessionAsync(Request()));

        Assert.Contains("did not start as requested", failure.Message);
        Assert.Contains("llama/Plain-NoReason", failure.Message);
        Assert.Single(core.ParamsOf("terminal.run"));                       // no retry, no other model, no other harness
        Assert.Contains("terminal.kill", core.Methods);                     // the session that did not start as asked is ended, not used
    }

    [Fact]
    public async Task A_pi_that_clamped_the_reasoning_level_is_refused()
    {
        await using var runtime = Runtime(out _, onLaunch: terminal => SimulateExtension(terminal, thinking: "off"));

        var failure = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.StartSessionAsync(Request()));

        Assert.Contains("'off'", failure.Message);
    }

    [Fact]
    public async Task A_pi_that_never_reports_how_it_started_is_refused()
    {
        await using var runtime = Runtime(out _, profile: PiProfile(reportTimeout: TimeSpan.FromMilliseconds(150)), onLaunch: _ => { });

        var failure = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.StartSessionAsync(Request()));

        Assert.Contains("did not report", failure.Message);
    }

    [Fact]
    public async Task A_pi_session_whose_channel_cannot_be_set_up_is_never_started_unverified()
    {
        await using var runtime = Runtime(out var core, profile: PiProfile(hook: Path.Combine(_root, "missing-hook.exe")));

        var failure = await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.StartSessionAsync(Request()));

        Assert.Contains("nothing was started", failure.Message);
        Assert.Empty(core.ParamsOf("terminal.run"));
    }

    [Fact]
    public async Task A_pi_session_observes_no_executions_so_the_controller_runs_every_check()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());

        await runtime.BeginTurnAsync(session);

        Assert.Null(await runtime.CollectTurnAsync(session));
    }

    // ---- signals: attribution and exactly-once recording ------------------------------------------------------------------------

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a workflow signal must never capture the workspace");
    }

    private static Dictionary<string, object?> How(string strategy = "Add the function in a new file.")
        => new()
        {
            ["strategy"] = strategy,
            ["components"] = "One new file.",
            ["data_and_control_flow"] = "A pure function.",
            ["compatibility"] = "Nothing existing changes.",
            ["risks_and_tradeoffs"] = "A static method over an instance: simpler.",
            ["verification_approach"] = "One test.",
            ["rationale"] = "A new public API shape is a person's call.",
        };

    /// <summary>What the extension hands the hook for one tool result: Pi's own session id for the session, the tool, its input and the call id.</summary>
    private static string ExtensionPayload(string piSessionId, string tool, object? input, string callId)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = piSessionId,
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = input,
            ["tool_use_id"] = callId,
            ["cwd"] = "ignored",
        });

    private static async Task<string> RunHook(ClaudeEvidenceChannel channel, string stdin)
    {
        var output = new StringWriter();
        await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), new NoState(), stdout: output);

        return output.ToString();
    }

    [Fact]
    public async Task A_how_proposal_the_extension_reports_for_this_pi_session_is_recorded_stops_the_turn_and_is_collected_for_exactly_that_turn()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());
        var channel = runtime.EvidenceChannelOf(session)!;
        Assert.True(runtime.CanSignal(Profile));

        var turn = await runtime.SendTurnAsync(session, "work");
        var output = await RunHook(channel, ExtensionPayload(channel.SessionId, HowTool, How(), "call-1"));

        Assert.Contains("\"continue\":false", output);                       // the extension stops the turn on exactly this answer
        var signal = Assert.Single(await runtime.CollectHowProposalsAsync(session, turn));
        Assert.Equal("Add the function in a new file.", signal.Strategy);
    }

    [Fact]
    public async Task A_signal_reported_for_any_other_pi_session_is_never_this_sessions_signal_and_does_not_stop_its_turn()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());
        var channel = runtime.EvidenceChannelOf(session)!;
        var turn = await runtime.SendTurnAsync(session, "work");

        var output = await RunHook(channel, ExtensionPayload(Guid.NewGuid().ToString(), HowTool, How(), "call-x"));

        Assert.DoesNotContain("\"continue\":false", output);
        Assert.Empty(await runtime.CollectHowProposalsAsync(session, turn));
    }

    [Fact]
    public async Task A_run_or_session_id_the_model_puts_in_the_tool_input_is_never_trusted()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());
        var channel = runtime.EvidenceChannelOf(session)!;
        var turn = await runtime.SendTurnAsync(session, "work");

        var forged = How();
        forged["session_id"] = channel.SessionId;
        forged["run_id"] = "someone-elses-run";

        var output = await RunHook(channel, ExtensionPayload(Guid.NewGuid().ToString(), HowTool, forged, "call-f"));

        Assert.DoesNotContain("\"continue\":false", output);
        Assert.Empty(await runtime.CollectHowProposalsAsync(session, turn));
    }

    [Fact]
    public async Task The_same_tool_call_observed_twice_is_one_signal_and_two_calls_are_two()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());
        var channel = runtime.EvidenceChannelOf(session)!;
        var turn = await runtime.SendTurnAsync(session, "work");

        await RunHook(channel, ExtensionPayload(channel.SessionId, HowTool, How("Once."), "call-1"));
        await RunHook(channel, ExtensionPayload(channel.SessionId, HowTool, How("Once."), "call-1"));   // a duplicate observation of the same call
        Assert.Single(await runtime.CollectHowProposalsAsync(session, turn));

        await RunHook(channel, ExtensionPayload(channel.SessionId, HowTool, How("Twice."), "call-2"));
        Assert.Equal(["Once.", "Twice."], (await runtime.CollectHowProposalsAsync(session, turn)).Select(s => s.Strategy).ToArray());
    }

    [Fact]
    public async Task A_review_request_is_recorded_for_the_pi_session_without_stopping_its_turn()
    {
        await using var runtime = Runtime(out _);
        var session = await runtime.StartSessionAsync(Request());
        var channel = runtime.EvidenceChannelOf(session)!;
        var turn = await runtime.SendTurnAsync(session, "work");

        var output = await RunHook(channel, ExtensionPayload(channel.SessionId, ReviewRequestTool, new Dictionary<string, object?> { ["reason"] = "A public API changed shape." }, "call-r"));

        Assert.DoesNotContain("\"continue\":false", output);
        Assert.Equal("A public API changed shape.", Assert.Single(await runtime.CollectReviewRequestsAsync(session, turn)).Reason);
    }
}
