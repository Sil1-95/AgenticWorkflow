using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code evidence adapter, offline: injected settings, the hook handler, the controller-owned channel and the
/// translation into provider-neutral evidence. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-evidence-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    private static readonly VerificationCheckSpec Build = new(new VerificationCheckId("build"), "builds", "dotnet", ["build", "Fixture.sln"], TimeSpan.FromMinutes(1));
    private static readonly VerificationCheckSpec Tests = new(new VerificationCheckId("tests"), "tests", "dotnet", ["test", "Fixture.sln"], TimeSpan.FromMinutes(1));

    public ClaudeEvidenceTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceOptions Options() => new() { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence") };

    private ClaudeEvidenceChannel Channel(params VerificationCheckSpec[] checks)
        => ClaudeEvidenceChannel.TryCreate(Options(), _workspace, checks.Length == 0 ? [Build, Tests] : checks, out var failure)
           ?? throw new InvalidOperationException(failure);

    // ---- injected settings -----------------------------------------------------------------------------------------

    [Fact]
    public void The_session_settings_contain_only_observational_command_hooks()
    {
        using var channel = Channel();

        using var settings = JsonDocument.Parse(File.ReadAllText(channel.SettingsPath));
        var root = settings.RootElement;

        Assert.Equal(["hooks"], root.EnumerateObject().Select(p => p.Name).ToArray()); // no permissions, env, model, statusLine ...
        Assert.Equal(["PostToolBatch", "PostToolUse", "PostToolUseFailure"], root.GetProperty("hooks").EnumerateObject().Select(p => p.Name).Order().ToArray());

        foreach (var entry in root.GetProperty("hooks").EnumerateObject().SelectMany(e => e.Value.EnumerateArray()))
        {
            var hook = Assert.Single(entry.GetProperty("hooks").EnumerateArray());
            Assert.Equal("command", hook.GetProperty("type").GetString());
            Assert.Equal(["command", "type"], hook.EnumerateObject().Select(p => p.Name).Order().ToArray());

            var command = hook.GetProperty("command").GetString()!;
            Assert.Equal($"\"{_hookExe.Replace('\\', '/')}\" \"{channel.Directory.Replace('\\', '/')}\"", command);
        }

        Assert.DoesNotContain("PreToolUse", File.ReadAllText(channel.SettingsPath)); // nothing that could allow, deny or ask
        Assert.DoesNotContain("PermissionRequest", File.ReadAllText(channel.SettingsPath));
    }

    [Fact]
    public void Launch_arguments_gain_only_the_session_settings_and_a_session_id_and_the_original_is_unchanged()
    {
        using var channel = Channel();
        IReadOnlyList<string> original = ["claude", "--permission-mode", "acceptEdits"];

        var augmented = channel.Augment(original);

        Assert.Equal(["claude", "--settings", channel.SettingsPath, "--session-id", channel.SessionId, "--permission-mode", "acceptEdits"], augmented);
        Assert.Equal(["claude", "--permission-mode", "acceptEdits"], original);
        Assert.True(Guid.TryParse(channel.SessionId, out _));
    }

    [Fact]
    public void The_channel_lives_outside_the_workspace_and_is_removed_on_dispose()
    {
        var channel = Channel();
        var directory = channel.Directory;

        Assert.StartsWith(Path.Combine(_root, "evidence"), directory);
        Assert.DoesNotContain(_workspace, directory);
        Assert.True(Directory.Exists(directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace)); // nothing written into the target repository

        channel.Dispose();

        Assert.False(Directory.Exists(directory));
        channel.Dispose(); // idempotent
    }

    [Fact]
    public void A_channel_that_would_sit_inside_the_workspace_is_refused()
    {
        var options = new ClaudeEvidenceOptions { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_workspace, ".evidence") };

        var channel = ClaudeEvidenceChannel.TryCreate(options, _workspace, [Build], out var failure);

        Assert.Null(channel);
        Assert.Contains("inside the workspace", failure);
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".evidence")));
    }

    [Fact]
    public void A_missing_hook_executable_or_no_checks_means_no_channel_not_an_error()
    {
        Assert.Null(ClaudeEvidenceChannel.TryCreate(new ClaudeEvidenceOptions { HookExecutable = Path.Combine(_root, "nope.exe"), EvidenceRoot = _root }, _workspace, [Build], out var missing));
        Assert.Contains("does not exist", missing);

        Assert.Null(ClaudeEvidenceChannel.TryCreate(Options(), _workspace, [], out var empty));
        Assert.Contains("no checks", empty);
    }

    // ---- the hook handler ------------------------------------------------------------------------------------------

    private sealed class FakeState(string fingerprint = "fp-after") : IImplementationStateProvider
    {
        public int Captures { get; private set; }

        public Exception? Fail { get; set; }

        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
        {
            Captures++;

            return Fail is { } failure
                ? Task.FromException<ImplementationState>(failure)
                : Task.FromResult(new ImplementationState(new ImplementationFingerprint(fingerprint), "head", new Dictionary<string, string>()));
        }
    }

    private static string Payload(string channelSession, string eventName, object? extra = null, string tool = "Bash", string command = "dotnet test Fixture.sln", string id = "toolu_1", string? cwd = null, string prompt = "prompt-1")
    {
        var fields = new Dictionary<string, object?>
        {
            ["session_id"] = channelSession,
            ["hook_event_name"] = eventName,
            ["cwd"] = cwd,
            ["prompt_id"] = prompt,
            ["tool_name"] = tool,
            ["tool_use_id"] = id,
            ["tool_input"] = new { command },
            ["duration_ms"] = 1234,
        };

        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties())
                fields[p.Name] = p.GetValue(extra);

        return JsonSerializer.Serialize(fields);
    }

    private static string BatchPayload(string session, string cwd, string prompt, params (string Tool, string Id, string Command)[] calls)
        => JsonSerializer.Serialize(new
        {
            session_id = session,
            hook_event_name = "PostToolBatch",
            cwd,
            prompt_id = prompt,
            tool_calls = calls.Select(c => new { tool_name = c.Tool, tool_use_id = c.Id, tool_input = new { command = c.Command } }).ToArray(),
        });

    private static async Task<int> Run(ClaudeEvidenceChannel channel, string stdin, IImplementationStateProvider state)
        => await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), state);

    private static string[] Records(ClaudeEvidenceChannel channel)
        => Directory.EnumerateFiles(channel.Directory, "e-*.json").Order().ToArray();

    [Fact]
    public async Task The_handler_exits_zero_prints_nothing_and_records_a_small_event()
    {
        using var channel = Channel();
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);

        try
        {
            channel.MarkTurnStart();
            var code = await Run(channel, Payload(channel.SessionId, "PostToolUse", cwd: _workspace), new FakeState());

            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(string.Empty, output.ToString());
        var record = Assert.Single(Records(channel));
        var json = File.ReadAllText(record);
        Assert.Contains("\"kind\":\"tool_success\"", json);
        Assert.Contains("\"tool_use_id\":\"toolu_1\"", json);
        Assert.DoesNotContain("tool_response", json); // never any command output
        Assert.DoesNotContain("stdout", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public async Task Garbage_on_stdin_still_exits_zero_and_becomes_a_problem_record(string stdin)
    {
        using var channel = Channel();

        Assert.Equal(0, await Run(channel, stdin, new FakeState()));

        Assert.Contains("\"kind\":\"problem\"", File.ReadAllText(Assert.Single(Records(channel))));
    }

    [Fact]
    public async Task The_handler_never_throws_for_a_missing_or_unusable_channel_directory()
    {
        var state = new FakeState();

        Assert.Equal(0, await ClaudeHookHandler.RunAsync([], new StringReader("{}"), state));
        Assert.Equal(0, await ClaudeHookHandler.RunAsync([Path.Combine(_root, "gone")], new StringReader("{}"), state));

        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Equal(0, await ClaudeHookHandler.RunAsync([Path.Combine(_root, "empty")], new StringReader("{}"), state));
    }

    [Fact]
    public async Task An_event_of_another_session_is_recorded_as_a_problem_not_as_evidence()
    {
        using var channel = Channel();
        channel.MarkTurnStart();

        await Run(channel, Payload(Guid.NewGuid().ToString(), "PostToolUse", cwd: _workspace), new FakeState());

        var evidence = channel.Collect();
        Assert.Empty(evidence.Activities);
        Assert.Contains(evidence.Problems, p => p.StartsWith("foreign_session"));
    }

    [Fact]
    public async Task The_state_is_captured_only_for_a_lone_command_that_is_exactly_a_configured_check_in_its_directory()
    {
        using var channel = Channel();
        var state = new FakeState();

        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "t1", "dotnet test Fixture.sln")), state);
        Assert.Equal(1, state.Captures);

        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "t2", "dotnet test Fixture.sln --verbosity quiet")), state); // not exact
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "t3", "git status")), state); // not a check
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Read", "t4", "")), state); // a read
        await Run(channel, BatchPayload(channel.SessionId, Path.Combine(_workspace, "src"), "prompt-1", ("Bash", "t5", "dotnet test Fixture.sln")), state); // wrong directory
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "t6", "dotnet test Fixture.sln"), ("Edit", "t7", "")), state); // parallel
        Assert.Equal(1, state.Captures);
    }

    [Fact]
    public async Task A_state_capture_failure_is_recorded_and_never_escapes()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        var state = new FakeState { Fail = new IOException("repository locked") };

        var code = await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "t1", "dotnet test Fixture.sln")), state);

        Assert.Equal(0, code);
        Assert.Contains("fingerprint_error", File.ReadAllText(Assert.Single(Records(channel))));
    }

    // ---- channel -> neutral evidence -------------------------------------------------------------------------------

    private static async Task Tick() => await Task.Delay(5); // file names are tick-ordered; keep records strictly ordered

    private async Task Observe(ClaudeEvidenceChannel channel, string id, string command, string outcomeEvent = "PostToolUse", string fingerprint = "fp-after", string? cwd = null, string prompt = "prompt-1")
    {
        await Run(channel, Payload(channel.SessionId, outcomeEvent, command: command, id: id, cwd: cwd ?? _workspace, prompt: prompt), new FakeState());
        await Tick();
        await Run(channel, BatchPayload(channel.SessionId, cwd ?? _workspace, prompt, ("Bash", id, command)), new FakeState(fingerprint));
        await Tick();
    }

    [Fact]
    public async Task A_successful_lone_check_becomes_a_command_activity_with_state_and_duration()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Observe(channel, "toolu_1", "dotnet test Fixture.sln");

        var evidence = channel.Collect();

        Assert.Empty(evidence.Problems);
        Assert.Equal("claude-code", evidence.Runtime);
        var activity = Assert.Single(evidence.Activities);
        Assert.Equal("toolu_1", activity.ToolUseId);
        Assert.Equal(ObservedActivityKind.Command, activity.Kind);
        Assert.Equal("dotnet test Fixture.sln", activity.Command);
        Assert.Equal(_workspace, activity.WorkingDirectory);
        Assert.True(activity.Succeeded);
        Assert.Equal(1234, activity.DurationMilliseconds);
        Assert.Equal(1, activity.BatchSize);
        Assert.Equal("fp-after", activity.StateAfterBatch!.Value);
    }

    [Theory]
    [InlineData("git grep -q -F GetFood")]
    [InlineData("grep -q GetFood")]
    [InlineData("diff a b")]
    [InlineData("test -f x")]
    [InlineData("git status")]
    public async Task A_successful_looking_outcome_of_a_semantic_exit_code_command_is_not_proof_of_exit_zero(string command)
    {
        // Claude Code reports e.g. `git grep` exiting 1 as a PostToolUse success and the payload has no exit code.
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Observe(channel, "toolu_1", command);

        var activity = Assert.Single(channel.Collect().Activities);
        Assert.False(activity.Succeeded);
        Assert.True(activity.ExitUnproven, "the harness reported no failure, so this is unproven rather than failed");
    }

    [Fact]
    public void Only_executables_with_literal_exit_status_are_trusted_and_only_when_the_command_is_a_plain_word_list()
    {
        Assert.True(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test Fixture.sln"));
        Assert.True(ClaudeEvidenceTranslator.ProvesExitZero("DOTNET build Fixture.sln"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test Fixture.sln | tail -3")); // a pipeline's status is the pipeline's
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("cd src && dotnet test"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero(null));
        Assert.True(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build --filter FullyQualifiedName!~Slow.Test"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build --filter FullyQualifiedName!~Slow.Test | tail -3"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build --filter FullyQualifiedName!~Slow.Test && echo pwned"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build --filter FullyQualifiedName!~Slow.Test; echo pwned"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build --filter FullyQualifiedName!~Slow.Test > x"));
        Assert.False(ClaudeEvidenceTranslator.ProvesExitZero("dotnet test --no-build FullyQualifiedName!~Slow.Test"));
        Assert.Equal(["dotnet"], ClaudeEvidenceTranslator.LiteralExitStatusExecutables.Order().ToArray());
    }

    [Fact]
    public async Task A_failed_command_is_not_a_success()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Observe(channel, "toolu_1", "dotnet test Fixture.sln", outcomeEvent: "PostToolUseFailure");

        var activity = Assert.Single(channel.Collect().Activities);
        Assert.False(activity.Succeeded);
        Assert.False(activity.ExitUnproven, "a reported failure is a failure, not an unproven success");
    }

    [Fact]
    public async Task A_command_in_a_batch_without_any_recorded_outcome_did_not_demonstrably_succeed()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "toolu_1", "dotnet test Fixture.sln")), new FakeState());

        var activity = Assert.Single(channel.Collect().Activities);
        Assert.False(activity.Succeeded);
        Assert.False(activity.ExitUnproven);
    }

    [Fact]
    public async Task Edits_reads_and_unknown_tools_are_classified_and_batches_are_numbered_in_order()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Edit", "a", "")), new FakeState());
        await Tick();
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Grep", "b", "")), new FakeState());
        await Tick();
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("mcp__something__call", "c", "")), new FakeState());
        await Tick();
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Read", "d", ""), ("Edit", "e", "")), new FakeState());

        var activities = channel.Collect().Activities;

        Assert.Equal(
            [("a", 0, 1, ObservedActivityKind.WorkspaceMutation), ("b", 1, 1, ObservedActivityKind.ReadOnly), ("c", 2, 1, ObservedActivityKind.Unknown), ("d", 3, 2, ObservedActivityKind.ReadOnly), ("e", 3, 2, ObservedActivityKind.WorkspaceMutation)],
            activities.Select(a => (a.ToolUseId, a.Batch, a.BatchSize, a.Kind)).ToArray());
    }

    [Fact]
    public async Task Only_records_after_the_latest_turn_mark_belong_to_the_turn()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();
        await Observe(channel, "turn-one", "dotnet test Fixture.sln", fingerprint: "fp-1");

        channel.MarkTurnStart();
        await Tick();
        await Observe(channel, "turn-two", "dotnet build Fixture.sln", fingerprint: "fp-2");

        var second = channel.Collect();

        Assert.Equal(["turn-two"], second.Activities.Select(a => a.ToolUseId).ToArray());
        Assert.Empty(second.Problems);
    }

    [Fact]
    public async Task A_correction_turn_in_the_same_conversation_keeps_the_commands_of_both_turns_and_a_third_prompt_does_not()
    {
        // A prover that is refused and asked for one correction is sent a second turn, which Claude Code gives its own prompt. The controller recorded that it sent two turns.
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();
        MarkSentTurn(channel);
        await Tick();
        await Observe(channel, "turn-one", "dotnet test Fixture.sln", prompt: "prompt-1");
        await Tick();
        MarkSentTurn(channel);
        await Tick();
        await Observe(channel, "turn-two", "dotnet test Fixture.sln", prompt: "prompt-2");

        var evidence = channel.Collect();

        Assert.Empty(evidence.Problems);
        Assert.Equal(["turn-one", "turn-two"], evidence.Activities.Select(a => a.ToolUseId).Order().ToArray());

        await Tick();
        await Observe(channel, "turn-three", "dotnet test Fixture.sln", prompt: "prompt-3");   // more prompts than turns the controller sent

        Assert.Contains(channel.Collect().Problems, p => p.StartsWith("multiple_prompts_in_turn"));
    }

    [Fact]
    public async Task Two_prompts_in_a_window_in_which_the_controller_sent_one_turn_are_still_a_problem()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();
        MarkSentTurn(channel);
        await Tick();
        await Observe(channel, "a", "dotnet test Fixture.sln", prompt: "prompt-1");
        await Tick();
        await Observe(channel, "b", "dotnet test Fixture.sln", prompt: "prompt-2");

        Assert.Contains(channel.Collect().Problems, p => p.StartsWith("multiple_prompts_in_turn"));
    }

    private static void MarkSentTurn(ClaudeEvidenceChannel channel)
        => File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.TurnFileName(DateTime.UtcNow.Ticks)), string.Empty);

    [Fact]
    public async Task A_duplicated_record_is_reported_as_two_activities_with_the_same_id()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Run(channel, Payload(channel.SessionId, "PostToolUse", id: "dup", cwd: _workspace), new FakeState());
        await Tick();
        await Run(channel, Payload(channel.SessionId, "PostToolUse", id: "dup", cwd: _workspace), new FakeState());
        await Tick();
        await Run(channel, BatchPayload(channel.SessionId, _workspace, "prompt-1", ("Bash", "dup", "dotnet test Fixture.sln")), new FakeState());

        var evidence = channel.Collect();

        Assert.Equal(2, evidence.Activities.Count(a => a.ToolUseId == "dup"));
    }

    [Fact]
    public async Task A_tool_outcome_with_no_batch_and_a_corrupt_record_make_the_whole_turn_untrusted()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Run(channel, Payload(channel.SessionId, "PostToolUse", id: "orphan", cwd: _workspace), new FakeState());
        await Tick();
        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)), "{ torn");

        var problems = channel.Collect().Problems;

        Assert.Contains(problems, p => p.StartsWith("tool_event_without_batch"));
        Assert.Contains(problems, p => p.StartsWith("unreadable_record"));
    }

    [Fact]
    public async Task A_record_with_an_unknown_member_is_unreadable_not_silently_accepted()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        File.WriteAllText(
            Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)),
            "{\"v\":1,\"kind\":\"tool_success\",\"stdout\":\"everything passed\"}");

        Assert.Contains(channel.Collect().Problems, p => p.StartsWith("unreadable_record"));
    }

    [Fact]
    public async Task A_record_of_another_channel_is_a_foreign_run_problem()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        var foreign = JsonSerializer.Serialize(new RawEvent { Kind = ClaudeEvidenceFormat.KindBatch, RunId = "somebody-else", SessionId = channel.SessionId, AtTicks = DateTime.UtcNow.Ticks, Calls = [] }, ClaudeEvidenceFormat.Json);
        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)), foreign);

        Assert.Contains(channel.Collect().Problems, p => p.StartsWith("foreign_run"));
    }

    [Fact]
    public void Collecting_before_any_turn_was_started_is_untrusted()
    {
        using var channel = Channel();

        Assert.Contains(channel.Collect().Problems, p => p.StartsWith("no_turn_window"));
    }

    [Fact]
    public async Task Work_delegated_to_a_subagent_is_never_classified_as_the_agents_own_command()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Tick();

        await Run(channel, Payload(channel.SessionId, "PostToolUse", extra: new { agent_id = "sub-1" }, cwd: _workspace), new FakeState());
        await Tick();
        await Run(
            channel,
            JsonSerializer.Serialize(new
            {
                session_id = channel.SessionId,
                hook_event_name = "PostToolBatch",
                cwd = _workspace,
                tool_calls = new[] { new { tool_name = "Bash", tool_use_id = "toolu_1", agent_id = "sub-1", tool_input = new { command = "dotnet test Fixture.sln" } } },
            }),
            new FakeState());

        var activity = Assert.Single(channel.Collect().Activities);
        Assert.Equal(ObservedActivityKind.Unknown, activity.Kind);
        Assert.Null(activity.Command);
        Assert.False(activity.Succeeded);
    }

    // ---- the runtime ---------------------------------------------------------------------------------------------------

    private static readonly ProfileId Profile = new("claude-local");

    [Fact]
    public async Task A_runtime_session_with_evidence_launches_with_the_session_settings_and_removes_the_channel_on_stop()
    {
        var core = new FakeHerdrCore();
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-evidence-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: Options())],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task") { ObservableChecks = [Build, Tests] });

        var channel = runtime.EvidenceChannelOf(session)!;
        Assert.NotNull(channel);

        var argv = core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Equal(["--settings", channel.SettingsPath, "--session-id", channel.SessionId], argv.Skip(1).Take(4).ToArray());
        Assert.True(Directory.Exists(channel.Directory));

        await runtime.BeginTurnAsync(session);
        var evidence = await runtime.CollectTurnAsync(session);
        Assert.NotNull(evidence);
        Assert.Empty(evidence!.Activities);

        await runtime.StopSessionAsync(session);

        Assert.False(Directory.Exists(channel.Directory));
    }

    [Fact]
    public async Task A_runtime_session_without_the_evidence_option_is_launched_exactly_as_before_and_reports_no_evidence()
    {
        var core = new FakeHerdrCore();
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-evidence-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile)],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task") { ObservableChecks = [Build] });

        var argv = core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        // Launched as before apart from naming its conversation: no settings file, no hooks.
        Assert.Equal([argv[0], "--session-id", runtime.ConversationOf(session)!.Reference, .. ClaudeCodeLaunch.Profile(Profile).Argv.Skip(1)], argv);
        Assert.DoesNotContain("--settings", argv);
        Assert.Null(runtime.EvidenceChannelOf(session));

        await runtime.BeginTurnAsync(session);
        Assert.Null(await runtime.CollectTurnAsync(session));
    }

    [Fact]
    public async Task An_unusable_hook_executable_launches_the_session_without_evidence_rather_than_failing()
    {
        var core = new FakeHerdrCore();
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-evidence-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: new ClaudeEvidenceOptions { HookExecutable = Path.Combine(_root, "missing.exe"), EvidenceRoot = _root })],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task") { ObservableChecks = [Build] });

        Assert.Null(runtime.EvidenceChannelOf(session));
        Assert.DoesNotContain("--settings", core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_session_with_no_observable_checks_gets_no_channel_even_when_the_profile_has_the_option()
    {
        var core = new FakeHerdrCore();
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-evidence-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: Options())],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));

        Assert.Null(runtime.EvidenceChannelOf(session));
    }
}
