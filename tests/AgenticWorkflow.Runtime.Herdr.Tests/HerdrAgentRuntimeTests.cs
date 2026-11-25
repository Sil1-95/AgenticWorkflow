using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>The production Herdr runtime against an in-memory core: no Herdr, no Claude, no model calls.</summary>
public sealed class HerdrAgentRuntimeTests : IAsyncLifetime
{
    private static readonly ProfileId Profile = new("claude-local");
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    private readonly FakeHerdrCore _core = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "aw-herdr-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private HerdrAgentRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _runtime = NewRuntime();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
    }

    private HerdrAgentRuntime NewRuntime(TimeSpan? startup = null)
        => new(
            new HerdrAgentRuntimeOptions
            {
                Session = "agenticworkflow-m2b-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile)],
                StartupTimeout = startup ?? Short,
                CommandTimeout = Short,
            },
            _core,
            _core);

    private Task<AgentSessionId> StartAsync(HerdrAgentRuntime? runtime = null)
        => (runtime ?? _runtime).StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task one"));

    private FakeTerminal Ours() => _core.TerminalSnapshot().Single(t => t.Labels.ContainsKey("aw_runtime"));

    private static string Text(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    // ---- start ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_launches_the_configured_profile_in_a_labelled_terminal_and_waits_for_the_idle_status()
    {
        var session = await StartAsync();

        var run = Assert.Single(_core.ParamsOf("terminal.run"));

        // The profile's fixed launch, with the conversation named up front so it never has to be discovered afterwards.
        var argv = run.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        var reference = _runtime.ConversationOf(session)!.Reference;
        Assert.Equal([ClaudeCodeLaunch.Profile(Profile).Argv[0], "--session-id", reference, .. ClaudeCodeLaunch.Profile(Profile).Argv.Skip(1)], argv);
        Assert.Equal(_workspace, Text(run, "cwd"));
        Assert.Equal(session.Value, Text(run, "name"));
        Assert.True(run.GetProperty("no_parent").GetBoolean());

        var labels = run.GetProperty("labels");
        Assert.Equal(_runtime.Marker, Text(labels, "aw_runtime"));
        Assert.Equal("task-one", Text(labels, "aw_label"));

        Assert.Equal(1, _core.ServerStarts);
    }

    [Fact]
    public async Task Start_handshakes_protocol_generation_one_on_each_connection_and_uses_a_dedicated_event_connection()
    {
        await StartAsync();

        Assert.Equal(2, _core.ConnectionsOpened);
        Assert.All(_core.ParamsOf("hello"), p => Assert.Equal(1, p.GetProperty("generation").GetInt32()));
        Assert.Equal(2, _core.Methods.Count(m => m == "hello"));

        var subscribeConnection = _core.Requests.Single(r => r.Method == "events.subscribe").Connection;
        Assert.Equal(_runtime.Marker, Text(_core.ParamsOf("events.subscribe").Single().GetProperty("labels"), "aw_runtime"));

        // no terminal command shares the notification connection
        Assert.DoesNotContain(
            _core.Requests,
            r => r.Method.StartsWith("terminal.", StringComparison.Ordinal) && r.Connection == subscribeConnection);
    }

    [Fact]
    public async Task An_unconfigured_profile_is_refused_before_the_core_is_even_started()
    {
        var error = await Assert.ThrowsAsync<AgentRuntimeException>(
            () => _runtime.StartSessionAsync(new AgentSessionRequest(new ProfileId("some-other-profile"), _workspace, "x")));

        Assert.Contains("no launch configuration", error.Message);
        Assert.Equal(0, _core.ServerStarts);
        Assert.Equal(0, _core.ConnectionsOpened);
    }

    [Fact]
    public async Task A_missing_workspace_is_refused_before_launching()
    {
        var error = await Assert.ThrowsAsync<AgentRuntimeException>(
            () => _runtime.StartSessionAsync(new AgentSessionRequest(Profile, Path.Combine(_workspace, "missing"), "x")));

        Assert.Contains("does not exist", error.Message);
        Assert.Equal(0, _core.ServerStarts);
    }

    [Fact]
    public async Task A_blocked_agent_at_startup_fails_clearly_is_never_answered_and_is_cleaned_up()
    {
        _core.StatusAtLaunch = "blocked";
        _core.StatusKindAtLaunch = "permission";

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => StartAsync());

        Assert.Contains("blocked at startup", error.Message);
        Assert.Contains("never answers dialogs", error.Message);
        Assert.DoesNotContain("terminal.send", _core.Methods);
        Assert.DoesNotContain("terminal.keys", _core.Methods);
        AssertOnlyOurTerminalWasRemoved();
    }

    [Fact]
    public async Task No_structured_status_within_the_startup_budget_fails_naming_the_unanswered_dialog_and_cleans_up()
    {
        _core.StatusAtLaunch = null;
        await using var runtime = NewRuntime(TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => StartAsync(runtime));

        Assert.Contains("no structured ready status", error.Message);
        Assert.Contains("was not answered", error.Message);
        AssertOnlyOurTerminalWasRemoved();
    }

    [Fact]
    public async Task An_agent_that_exits_during_startup_is_reported_and_cleaned_up()
    {
        _core.StatusAtLaunch = null;
        var start = StartAsync();
        await WaitUntilAsync(() => _core.TerminalSnapshot().Any(t => t.Labels.ContainsKey("aw_runtime")));

        Ours().End("exited", 1);

        var error = await Assert.ThrowsAsync<AgentStartupBlockedException>(() => start);
        Assert.Contains("exited during startup", error.Message);
    }

    [Fact]
    public async Task A_core_error_on_launch_surfaces_as_a_runtime_failure()
    {
        _core.FailRunWith = "spawn_failed";

        var error = await Assert.ThrowsAsync<AgentRuntimeException>(() => StartAsync());

        Assert.Contains("spawn_failed", error.Message);
    }

    [Fact]
    public async Task An_incompatible_protocol_generation_fails_fast()
    {
        _core.Generation = 2;

        var error = await Assert.ThrowsAsync<AgentRuntimeException>(() => StartAsync());

        Assert.Contains("generation", error.Message);
        Assert.DoesNotContain("terminal.run", _core.Methods);
    }

    // ---- turns ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_turn_ends_done_when_the_agent_goes_working_then_done()
    {
        var session = await StartAsync();

        var turn = await _runtime.SendTurnAsync(session, "do the thing");
        var wait = _runtime.WaitForTurnAsync(session, turn, Short);
        Assert.False(wait.IsCompleted);

        Ours().Report("done", null, null);

        Assert.Equal(AgentTurnOutcome.Done, await wait);
        Assert.Equal(1, turn.Value);
    }

    [Fact]
    public async Task A_turn_that_finished_before_waiting_started_is_still_seen()
    {
        _core.OnSend = (terminal, _) =>
        {
            terminal.Report("working", null, null);
            terminal.Report("done", null, null);
        };
        var session = await StartAsync();

        var turn = await _runtime.SendTurnAsync(session, "quick");

        Assert.Equal(AgentTurnOutcome.Done, await _runtime.WaitForTurnAsync(session, turn, Short));
    }

    [Fact]
    public async Task A_stale_done_from_the_previous_turn_is_never_taken_as_the_next_turns_outcome()
    {
        var session = await StartAsync();
        var first = await _runtime.SendTurnAsync(session, "first");
        Ours().Report("done", null, null);
        Assert.Equal(AgentTurnOutcome.Done, await _runtime.WaitForTurnAsync(session, first, Short));

        // second prompt: the terminal still says "done" (turn 1) until the agent actually starts working
        _core.OnSend = (_, _) => { };
        var second = await _runtime.SendTurnAsync(session, "second");
        var wait = _runtime.WaitForTurnAsync(session, second, Short);

        await Task.Delay(300);
        Assert.False(wait.IsCompleted, "the previous turn's done must not complete the new turn");

        Ours().Report("working", null, null);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        Ours().Report("done", null, null);
        Assert.Equal(AgentTurnOutcome.Done, await wait);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public async Task A_prompt_the_agent_never_started_a_turn_for_is_reported_as_not_taken_instead_of_being_waited_on()
    {
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "agenticworkflow-m2b-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile)],
                StartupTimeout = Short,
                CommandTimeout = Short,
                SubmissionTimeout = TimeSpan.FromMilliseconds(300),
            },
            _core,
            _core);
        var session = await StartAsync(runtime);

        // the prompt reaches the terminal, but the agent never reports working and its turn counter never moves
        _core.OnSend = (_, _) => { };
        var turn = await runtime.SendTurnAsync(session, "work");

        var outcome = await runtime.WaitForTurnAsync(session, turn, TimeSpan.FromSeconds(30));

        Assert.Equal(AgentTurnStatus.Error, outcome.Status);
        Assert.Contains("not taken", outcome.Reason);
        Assert.Single(_core.ParamsOf("terminal.send"));   // nothing was typed again
    }

    [Fact]
    public async Task A_turn_that_really_started_is_not_mistaken_for_a_prompt_that_was_not_taken()
    {
        await using var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions { Session = "agenticworkflow-m2b-unit", Profiles = [ClaudeCodeLaunch.Profile(Profile)], StartupTimeout = Short, CommandTimeout = Short, SubmissionTimeout = TimeSpan.FromMilliseconds(200) },
            _core,
            _core);
        var session = await StartAsync(runtime);
        var turn = await runtime.SendTurnAsync(session, "work");   // the scripted core reports working

        var wait = runtime.WaitForTurnAsync(session, turn, TimeSpan.FromSeconds(30));
        await Task.Delay(600);
        Assert.False(wait.IsCompleted);

        Ours().Report("done", null, null);
        Assert.Equal(AgentTurnOutcome.Done, await wait);
    }

    [Fact]
    public async Task Every_turn_goes_to_the_same_terminal_launched_once()
    {
        var session = await StartAsync();

        for (var i = 1; i <= 3; i++)
        {
            var turn = await _runtime.SendTurnAsync(session, $"prompt {i}");
            Ours().Report("done", null, null);
            Assert.Equal(AgentTurnOutcome.Done, await _runtime.WaitForTurnAsync(session, turn, Short));
        }

        Assert.Single(_core.ParamsOf("terminal.run"));
        Assert.Equal(["prompt 1", "prompt 2", "prompt 3"], Ours().Sent);
        Assert.All(_core.ParamsOf("terminal.send"), p => Assert.Equal(Ours().Address, Text(p, "terminal")));
    }

    [Fact]
    public async Task The_prompt_is_delivered_unchanged_including_newlines_and_never_forced()
    {
        var session = await StartAsync();
        const string prompt = "line one\nline two\n- a list item\n\"quoted\" and 'single' and C:\\path";

        await _runtime.SendTurnAsync(session, prompt);

        Assert.Equal([prompt], Ours().Sent);
        Assert.All(_core.ParamsOf("terminal.send"), p => Assert.False(p.TryGetProperty("force", out _)));
    }

    [Fact]
    public async Task A_blocked_status_is_an_observed_native_interaction_with_the_agents_own_words_and_nothing_is_answered()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("working", null, null);
        Ours().Report("blocked", "permission", "approve Bash: dotnet --list-sdks");

        var outcome = await _runtime.WaitForTurnAsync(session, turn, Short);

        Assert.Equal(AgentTurnStatus.AwaitingHuman, outcome.Status);
        var interaction = Assert.IsType<AgentInteraction>(outcome.Interaction);
        Assert.Equal(AgentInteractionKind.Permission, interaction.Kind);
        Assert.Equal("approve Bash: dotnet --list-sdks", interaction.Description);
        Assert.Equal(new NativeAttachTarget("agenticworkflow-m2b-unit", session.Value), interaction.Attach);

        // observed only: no key, no forced input, nothing but the one prompt ever reached the terminal
        Assert.DoesNotContain("terminal.keys", _core.Methods);
        Assert.Equal(["go"], Ours().Sent);
        Assert.All(_core.ParamsOf("terminal.send"), p => Assert.False(p.TryGetProperty("force", out _)));
    }

    [Theory]
    [InlineData("permission", AgentInteractionKind.Permission)]
    [InlineData("question", AgentInteractionKind.Question)]
    [InlineData("auth", AgentInteractionKind.Authentication)]
    [InlineData("something-new", AgentInteractionKind.Unspecified)]
    public async Task The_harness_kind_maps_to_the_interaction_kind(string kind, AgentInteractionKind expected)
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("working", null, null);
        Ours().Report("blocked", kind, "waiting");

        var outcome = await _runtime.WaitForTurnAsync(session, turn, Short);

        Assert.Equal(AgentTurnStatus.AwaitingHuman, outcome.Status);
        Assert.Equal(expected, outcome.Interaction!.Kind);
    }

    [Fact]
    public async Task A_wait_is_reported_once_then_the_agent_resuming_and_finishing_end_the_same_turn()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("working", null, null);
        Ours().Report("blocked", "permission", "approve Bash: one");
        Assert.Equal(AgentTurnStatus.AwaitingHuman, (await _runtime.WaitForTurnAsync(session, turn, Short)).Status);

        // still blocked: not reported again, the caller simply keeps waiting
        var resumed = _runtime.WaitForTurnAsync(session, turn, Short);
        await Task.Delay(300);
        Assert.False(resumed.IsCompleted);

        // the person acted natively (allow, a grant or a denial: invisible here); the agent works again
        Ours().Report("working", null, null);
        Assert.Equal(AgentTurnOutcome.Resumed, await resumed);

        Ours().Report("done", null, null);
        Assert.Equal(AgentTurnOutcome.Done, await _runtime.WaitForTurnAsync(session, turn, Short));

        Assert.DoesNotContain("terminal.keys", _core.Methods);
        Assert.Equal(["go"], Ours().Sent);
    }

    [Fact]
    public async Task A_second_wait_in_the_same_turn_is_reported_again_after_the_first_resumed()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("working", null, null);
        Ours().Report("blocked", "permission", "approve Bash: one");
        Assert.Equal(AgentTurnStatus.AwaitingHuman, (await _runtime.WaitForTurnAsync(session, turn, Short)).Status);

        Ours().Report("working", null, null);
        Assert.Equal(AgentTurnStatus.Resumed, (await _runtime.WaitForTurnAsync(session, turn, Short)).Status);

        Ours().Report("blocked", "permission", "approve Bash: two");
        var second = await _runtime.WaitForTurnAsync(session, turn, Short);

        Assert.Equal(AgentTurnStatus.AwaitingHuman, second.Status);
        Assert.Equal("approve Bash: two", second.Interaction!.Description);
    }

    [Fact]
    public async Task A_blocked_agent_whose_terminal_ends_is_an_error_not_a_wait()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("working", null, null);
        Ours().Report("blocked", "permission", "approve Bash: one");
        Assert.Equal(AgentTurnStatus.AwaitingHuman, (await _runtime.WaitForTurnAsync(session, turn, Short)).Status);

        var pending = _runtime.WaitForTurnAsync(session, turn, Short);
        Ours().End("exited", 1);

        Assert.Equal(AgentTurnStatus.Error, (await pending).Status);
    }

    [Fact]
    public async Task An_error_status_is_an_error_outcome()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().Report("error", null, "API error");

        var outcome = await _runtime.WaitForTurnAsync(session, turn, Short);
        Assert.Equal(AgentTurnStatus.Error, outcome.Status);
        Assert.Contains("API error", outcome.Reason);
    }

    [Fact]
    public async Task The_agent_process_ending_mid_turn_is_an_error_outcome_with_the_reason_and_exit_code()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        Ours().End("exited", 3);

        var outcome = await _runtime.WaitForTurnAsync(session, turn, Short);
        Assert.Equal(AgentTurnStatus.Error, outcome.Status);
        Assert.Contains("exited", outcome.Reason);
        Assert.Contains("exit code 3", outcome.Reason);
    }

    [Fact]
    public async Task Stopping_the_session_while_a_turn_is_pending_ends_it_cancelled()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");
        var wait = _runtime.WaitForTurnAsync(session, turn, Short);

        await _runtime.StopSessionAsync(session);

        Assert.Equal(AgentTurnStatus.Cancelled, (await wait).Status);
    }

    [Fact]
    public async Task A_turn_that_never_ends_times_out_as_an_error_outcome()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");

        var outcome = await _runtime.WaitForTurnAsync(session, turn, TimeSpan.FromMilliseconds(300));

        Assert.Equal(AgentTurnStatus.Error, outcome.Status);
        Assert.Contains("did not finish its turn", outcome.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_change_the_agent()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runtime.WaitForTurnAsync(session, turn, Short, cts.Token));

        Assert.True(Ours().Live);
    }

    [Fact]
    public async Task Sending_to_a_blocked_agent_is_refused_and_reported_not_forced()
    {
        var session = await StartAsync();
        Ours().Report("blocked", "question", "which file?");

        var error = await Assert.ThrowsAsync<AgentRuntimeException>(() => _runtime.SendTurnAsync(session, "answer"));

        Assert.Contains("blocked", error.Message);
        Assert.Empty(Ours().Sent);
        Assert.All(_core.ParamsOf("terminal.send"), p => Assert.False(p.TryGetProperty("force", out _)));
    }

    [Fact]
    public async Task Lifecycle_is_decided_from_structured_state_even_when_the_event_stream_is_silent()
    {
        _core.SuppressEvents = true;
        _core.OnSend = (terminal, _) =>
        {
            terminal.Report("working", null, null);
            terminal.Report("done", null, null);
        };
        var session = await StartAsync();

        var turn = await _runtime.SendTurnAsync(session, "go");

        Assert.Equal(AgentTurnOutcome.Done, await _runtime.WaitForTurnAsync(session, turn, Short));
    }

    [Fact]
    public async Task Losing_the_event_connection_mid_turn_is_an_error_outcome_not_a_hang()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");
        var wait = _runtime.WaitForTurnAsync(session, turn, Short);

        _core.DropEventConnections();

        var outcome = await wait;
        Assert.Equal(AgentTurnStatus.Error, outcome.Status);
        Assert.Contains("event connection was lost", outcome.Reason);
    }

    [Fact]
    public async Task Waiting_for_a_turn_that_was_never_sent_is_a_usage_error()
    {
        var session = await StartAsync();

        await Assert.ThrowsAsync<AgentRuntimeException>(() => _runtime.WaitForTurnAsync(session, new AgentTurnNumber(1), Short));
    }

    [Fact]
    public async Task An_unknown_session_is_refused()
    {
        await Assert.ThrowsAsync<AgentRuntimeException>(() => _runtime.SendTurnAsync(new AgentSessionId("nope"), "x"));
    }

    // ---- what the runtime never does --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_runtime_never_reads_the_screen_or_sends_keys_to_decide_or_unblock_anything()
    {
        var session = await StartAsync();
        var turn = await _runtime.SendTurnAsync(session, "go");
        Ours().Report("blocked", "permission", "x");
        await _runtime.WaitForTurnAsync(session, turn, Short);
        await _runtime.StopSessionAsync(session);

        var allowed = new[] { "hello", "events.subscribe", "terminal.run", "terminal.get", "terminal.send", "terminal.kill", "terminal.rm" };
        Assert.Empty(_core.Methods.Except(allowed));
    }

    // ---- stop and ownership -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_kills_and_removes_only_the_sessions_own_terminal_and_is_idempotent()
    {
        var session = await StartAsync();
        var ours = Ours().Address;

        await _runtime.StopSessionAsync(session);
        var requestsAfterFirstStop = _core.Requests.Count;
        await _runtime.StopSessionAsync(session);

        Assert.Equal(requestsAfterFirstStop, _core.Requests.Count);
        AssertOnlyOurTerminalWasRemoved();
        Assert.DoesNotContain(ours, _core.Terminals.Keys);
        Assert.True(_core.Terminals["t99"].Live);
    }

    [Fact]
    public async Task Stopping_a_terminal_that_is_already_gone_is_not_an_error()
    {
        var session = await StartAsync();
        Ours().End("exited", 0);
        _core.Terminals.Remove(Ours().Address);

        await _runtime.StopSessionAsync(session);
    }

    [Fact]
    public async Task Dispose_stops_remaining_sessions_and_stops_the_isolated_core_once()
    {
        await using (var runtime = NewRuntime())
        {
            await StartAsync(runtime);
            await StartAsync(runtime);
            Assert.Equal(2, _core.TerminalSnapshot().Count(t => t.Labels.ContainsKey("aw_runtime")));
        }

        Assert.DoesNotContain(_core.TerminalSnapshot(), t => t.Labels.ContainsKey("aw_runtime"));
        Assert.True(_core.Terminals["t99"].Live);
        Assert.Equal(1, _core.ServerStops);
    }

    [Fact]
    public async Task A_runtime_that_never_started_anything_does_not_stop_a_core()
    {
        await using (var runtime = NewRuntime())
        {
        }

        Assert.Equal(0, _core.ServerStarts);
        Assert.Equal(0, _core.ServerStops);
    }

    [Fact]
    public async Task Two_sessions_get_distinct_terminals_and_independent_turn_numbers()
    {
        var first = await StartAsync();
        var second = await StartAsync();

        Assert.NotEqual(first, second);

        var a = await _runtime.SendTurnAsync(first, "a");
        var b = await _runtime.SendTurnAsync(second, "b");
        Assert.Equal(1, a.Value);
        Assert.Equal(1, b.Value);
    }

    // ---- profile and configuration ------------------------------------------------------------------------------------------

    [Fact]
    public void The_claude_profile_uses_the_least_permissive_session_local_flags()
    {
        var argv = ClaudeCodeLaunch.Profile(Profile).Argv;

        Assert.Equal("claude", argv[0]);
        Assert.Equal("acceptEdits", argv[argv.ToList().IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain(argv, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--add-dir", argv);
        Assert.DoesNotContain("--mcp-config", argv);

        var denied = argv.SkipWhile(a => a != "--disallowedTools").Skip(1).ToList();
        Assert.Contains("Bash(git push:*)", denied);
        Assert.Contains("Bash(git commit:*)", denied);
        Assert.Contains("WebFetch", denied);

        var allowed = argv.SkipWhile(a => a != "--allowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
        Assert.Contains("Bash(dotnet test:*)", allowed);
        Assert.DoesNotContain("Bash", allowed);
        Assert.DoesNotContain(allowed, a => a.Contains('*') && !a.Contains(':'));
    }

    [Fact]
    public void A_session_and_at_least_one_profile_are_required()
    {
        Assert.Throws<ArgumentException>(() => new HerdrAgentRuntime(new HerdrAgentRuntimeOptions { Session = " ", Profiles = [ClaudeCodeLaunch.Profile(Profile)] }, _core, _core));
        Assert.Throws<ArgumentException>(() => new HerdrAgentRuntime(new HerdrAgentRuntimeOptions { Session = "s", Profiles = [] }, _core, _core));
    }

    [Fact]
    public void A_launch_profile_needs_an_executable_and_a_reported_app_name()
    {
        Assert.Throws<ArgumentException>(() => new AgentLaunchProfile(Profile, [], "claude-code"));
        Assert.Throws<ArgumentException>(() => new AgentLaunchProfile(Profile, ["claude"], " "));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private void AssertOnlyOurTerminalWasRemoved()
    {
        var kills = _core.ParamsOf("terminal.kill").Select(p => Text(p, "terminal")).ToList();
        var removals = _core.ParamsOf("terminal.rm").Select(p => Text(p, "terminal")).ToList();

        Assert.All(kills.Concat(removals), address => Assert.NotEqual("t99", address));
        Assert.DoesNotContain(_core.TerminalSnapshot(), t => t.Labels.ContainsKey("aw_runtime"));
        Assert.True(_core.Terminals["t99"].Live);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Short;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not reached");

            await Task.Delay(10);
        }
    }
}
