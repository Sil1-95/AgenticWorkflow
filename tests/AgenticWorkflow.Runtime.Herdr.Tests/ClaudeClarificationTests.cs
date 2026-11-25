using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code translation of the CLARIFY signal, offline: the one-tool MCP server, the hook that records the question and stops the turn, the
/// session-only configuration and the runtime's identity checks. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeClarificationTests : IDisposable
{
    private const string Full = "mcp__aw__request_clarification";

    private static readonly ProfileId Profile = new("claude-local");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-clarify-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public ClaudeClarificationTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceOptions Options(bool clarification = true)
        => new() { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferClarification = clarification };

    private static readonly VerificationCheckSpec Build = new(new VerificationCheckId("build"), "builds", "dotnet", ["build", "Fixture.sln"], TimeSpan.FromMinutes(1));

    /// <summary>With the tool a channel needs no checks; without it the channel exists only to observe one.</summary>
    private ClaudeEvidenceChannel Channel(bool clarification = true)
        => ClaudeEvidenceChannel.TryCreate(Options(), _workspace, clarification ? [] : [Build], sessionId: null, clarification, out var failure)
           ?? throw new InvalidOperationException(failure);

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a clarification must never capture the workspace");
    }

    private static string Payload(string session, string question = "Which dessert?", string reason = "Not in the repo.", string tool = Full, string id = "toolu_q")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session,
            ["hook_event_name"] = "PostToolUse",
            ["prompt_id"] = "p1",
            ["tool_name"] = tool,
            ["tool_use_id"] = id,
            ["tool_input"] = new { question, reason },
        });

    private static async Task<(int Code, string Out)> RunHook(ClaudeEvidenceChannel channel, string stdin)
    {
        var output = new StringWriter();
        var code = await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), new NoState(), stdout: output);
        return (code, output.ToString());
    }

    private static string[] Records(ClaudeEvidenceChannel channel) => Directory.EnumerateFiles(channel.Directory, "e-*.json").Order().ToArray();

    // ---- the MCP server ---------------------------------------------------------------------------------------------------------

    private static JsonDocument Reply(string request, ref bool raised)
        => JsonDocument.Parse(ClaudeClarificationServer.Handle(request, ref raised)!.ToJsonString());

    [Fact]
    public void The_server_exposes_exactly_one_tool_with_a_bounded_closed_schema()
    {
        var raised = false;
        using var reply = Reply("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ref raised);

        var tool = Assert.Single(reply.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentClarificationSignal.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["question", "reason"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(500, schema.GetProperty("properties").GetProperty("question").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void The_server_tells_the_agent_to_stop_after_a_well_formed_question_and_refuses_a_second()
    {
        var raised = false;
        var call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"request_clarification","arguments":{"question":"Which?","reason":"Why."}}}""";

        using var first = Reply(call, ref raised);
        Assert.False(first.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Stop now", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        using var second = Reply(call, ref raised);
        Assert.True(second.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("""{"question":"","reason":"x"}""")]
    [InlineData("""{"question":"x"}""")]
    [InlineData("""{"question":"x","reason":"y","extra":1}""")]
    public void A_malformed_call_is_an_error_the_agent_can_correct_and_does_not_count_as_the_question(string arguments)
    {
        var raised = false;
        using var reply = Reply("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"request_clarification\",\"arguments\":" + arguments + "}}", ref raised);

        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(raised);
    }

    [Fact]
    public void Notifications_get_no_reply_and_unknown_methods_are_errors()
    {
        var raised = false;

        Assert.Null(ClaudeClarificationServer.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", ref raised));
        Assert.Null(ClaudeClarificationServer.Handle("not json", ref raised));
        using var unknown = Reply("""{"jsonrpc":"2.0","id":9,"method":"resources/list"}""", ref raised);
        Assert.Equal(-32601, unknown.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ---- the hook: record, then stop -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_question_from_the_owned_session_is_recorded_and_only_then_the_turn_is_stopped()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId));

        Assert.Equal(0, code);
        Assert.Contains("\"continue\":false", output);
        var json = File.ReadAllText(Assert.Single(Records(channel)));
        Assert.Contains("\"kind\":\"clarification\"", json);
        Assert.Contains("Which dessert?", json);
        Assert.Single(channel.CollectClarifications(0, null));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task A_question_from_another_session_is_never_recorded_as_one_and_never_stops_anything(string session)
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(session));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectClarifications(0, null));
    }

    [Fact]
    public async Task A_malformed_question_and_a_session_that_was_not_given_the_tool_stop_nothing()
    {
        using var offered = Channel();
        var (_, malformed) = await RunHook(offered, Payload(offered.SessionId, question: new string('q', 501)));
        Assert.Equal(string.Empty, malformed);
        Assert.Empty(offered.CollectClarifications(0, null));

        using var plain = Channel(clarification: false);
        var (_, unexpected) = await RunHook(plain, Payload(plain.SessionId));
        Assert.Equal(string.Empty, unexpected);
        Assert.Empty(plain.CollectClarifications(0, null));
    }

    [Fact]
    public async Task Other_tools_are_still_observed_silently()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId, tool: "Bash"));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectClarifications(0, null));
    }

    // ---- the channel: records are windowed by turn -----------------------------------------------------------------------------

    [Fact]
    public async Task Questions_are_read_only_from_the_window_of_the_turn_and_in_order()
    {
        using var channel = Channel();
        await RunHook(channel, Payload(channel.SessionId, question: "First?"));
        var between = DateTime.UtcNow.Ticks + 1;
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId, question: "Second?", id: "toolu_2"));

        Assert.Equal(["First?"], channel.CollectClarifications(0, between).Select(s => s.Question).ToArray());
        Assert.Equal(["Second?"], channel.CollectClarifications(between, null).Select(s => s.Question).ToArray());
        Assert.Equal(["First?", "Second?"], channel.CollectClarifications(0, null).Select(s => s.Question).ToArray());
    }

    [Fact]
    public void A_forged_record_of_another_session_or_run_is_never_a_question_and_a_corrupt_one_is_ignored()
    {
        using var channel = Channel();
        string Forge(string session, string run) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "clarification", ["at"] = DateTime.UtcNow.Ticks, ["session_id"] = session, ["run_id"] = run, ["question"] = "Forged?", ["reason"] = "x",
        });

        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)), Forge("someone-else", "r"));
        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks + 1)), "{ corrupt");

        Assert.Empty(channel.CollectClarifications(0, null));
    }

    // ---- session-only configuration ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_session_with_the_tool_gets_a_session_only_mcp_server_one_extra_hook_and_an_allow_rule_for_exactly_that_tool()
    {
        using var channel = Channel();

        using var settings = JsonDocument.Parse(File.ReadAllText(channel.SettingsPath));
        var matchers = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse").EnumerateArray().Select(e => e.GetProperty("matcher").GetString()!).ToArray();
        Assert.Equal(["Bash|PowerShell", Full], matchers);
        Assert.Equal([Full], settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).ToArray());

        using var mcp = JsonDocument.Parse(File.ReadAllText(channel.McpConfigPath!));
        var server = mcp.RootElement.GetProperty("mcpServers").GetProperty("aw");
        Assert.Equal(Path.GetFullPath(_hookExe), server.GetProperty("command").GetString());
        Assert.Equal(["--mcp", channel.Directory], server.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var argv = channel.Augment(["claude", "--permission-mode", "acceptEdits"]);
        Assert.Equal(["claude", "--settings", channel.SettingsPath, "--mcp-config", channel.McpConfigPath!, "--session-id", channel.SessionId, "--permission-mode", "acceptEdits"], argv);
        Assert.True(channel.OffersClarification);
    }

    [Fact]
    public void Without_the_tool_the_channel_is_exactly_the_evidence_only_one()
    {
        using var channel = Channel(clarification: false);

        Assert.False(channel.OffersClarification);
        Assert.Null(channel.McpConfigPath);
        Assert.DoesNotContain("permissions", File.ReadAllText(channel.SettingsPath));
        Assert.DoesNotContain("--mcp-config", channel.Augment(["claude"]));
    }

    // ---- the runtime -----------------------------------------------------------------------------------------------------------

    private async Task<(HerdrAgentRuntime Runtime, FakeHerdrCore Core)> RuntimeAsync(bool clarification = true)
    {
        var core = new FakeHerdrCore();
        var runtime = new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-clarify-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: Options(clarification))],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);

        await Task.CompletedTask;
        return (runtime, core);
    }

    [Fact]
    public async Task A_session_with_no_checks_still_gets_the_clarification_channel_and_reports_only_its_own_turns_questions()
    {
        var (runtime, _) = await RuntimeAsync();
        await using var _ = runtime;
        Assert.True(runtime.CanSignal(Profile));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var channel = runtime.EvidenceChannelOf(session)!;
        Assert.True(channel.OffersClarification);

        var turn1 = await runtime.SendTurnAsync(session, "work");
        await RunHook(channel, Payload(channel.SessionId, question: "In turn one?"));
        await Task.Delay(20);
        var turn2 = await runtime.SendTurnAsync(session, "more");

        Assert.Equal(["In turn one?"], (await runtime.CollectClarificationsAsync(session, turn1)).Select(s => s.Question).ToArray());
        Assert.Empty(await runtime.CollectClarificationsAsync(session, turn2));
        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.CollectClarificationsAsync(session, new AgentTurnNumber(7)));
    }

    [Fact]
    public async Task A_profile_without_the_option_cannot_signal_and_reports_nothing()
    {
        var (runtime, core) = await RuntimeAsync(clarification: false);
        await using var _ = runtime;

        Assert.False(runtime.CanSignal(Profile));
        Assert.False(runtime.CanSignal(new ProfileId("unknown")));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var turn = await runtime.SendTurnAsync(session, "work");

        Assert.Empty(await runtime.CollectClarificationsAsync(session, turn));
        Assert.DoesNotContain("--mcp-config", core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task The_resumed_conversation_keeps_the_tool_bound_to_exactly_that_conversation()
    {
        var (runtime, core) = await RuntimeAsync();
        await using var _ = runtime;

        var first = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var conversation = runtime.ConversationOf(first)!;
        await runtime.StopSessionAsync(first);

        var resumed = await runtime.ResumeSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"), conversation);

        var channel = runtime.EvidenceChannelOf(resumed)!;
        Assert.Equal(conversation.Reference, channel.SessionId);
        Assert.True(channel.OffersClarification);
        var argv = core.ParamsOf("terminal.run").Last().GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Contains("--mcp-config", argv);
        Assert.Contains("--resume", argv);
    }
}
