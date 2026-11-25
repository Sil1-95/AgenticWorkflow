using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code translation of the reclassification signal, offline: the MCP tool and its closed schema, the hook that records the report and only then stops the
/// turn (so no edit is made under the stale assumption), the session-only configuration, and the runtime's identity and window checks. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeReclassificationTests : IDisposable
{
    private const string Full = "mcp__aw__report_reclassification";
    private const string HowFull = "mcp__aw__propose_how";

    private static readonly ProfileId Profile = new("claude-local");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-reclass-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public ClaudeReclassificationTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceOptions Options(bool how = false, bool reclassification = true)
        => new() { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferHow = how, OfferReclassification = reclassification };

    private ClaudeEvidenceChannel Channel(bool how = false, bool reclassification = true)
        => ClaudeEvidenceChannel.TryCreate(Options(how, reclassification), _workspace, [], sessionId: null, clarification: false, what: false, how, reclassification, out var failure)
           ?? throw new InvalidOperationException(failure);

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a reclassification report must never capture the workspace");
    }

    private static Dictionary<string, object?> Report(string evidence = "The checker rejects any call not routed through the audit log.", string stage = "how")
        => new()
        {
            ["evidence"] = evidence,
            ["stage"] = stage,
            ["reason"] = "The approved strategy renames in place, which bypasses the audit log.",
        };

    private static string Payload(string session, Dictionary<string, object?>? input = null, string tool = Full, string id = "toolu_r")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session,
            ["hook_event_name"] = "PostToolUse",
            ["prompt_id"] = "p1",
            ["tool_name"] = tool,
            ["tool_use_id"] = id,
            ["tool_input"] = input ?? Report(),
        });

    private static async Task<(int Code, string Out)> RunHook(ClaudeEvidenceChannel channel, string stdin)
    {
        var output = new StringWriter();
        var code = await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), new NoState(), stdout: output);
        return (code, output.ToString());
    }

    private static string[] Records(ClaudeEvidenceChannel channel) => Directory.EnumerateFiles(channel.Directory, "e-*.json").Order().ToArray();

    // ---- the MCP server ---------------------------------------------------------------------------------------------------------

    private static JsonDocument Reply(string request, ClaudeClarificationServer.Tools tools, ref bool reported)
    {
        bool raised = false, proposed = false, how = false;
        return JsonDocument.Parse(ClaudeClarificationServer.Handle(request, tools, ref raised, ref proposed, ref how, ref reported)!.ToJsonString());
    }

    private static string Call(Dictionary<string, object?> arguments)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?> { ["name"] = "report_reclassification", ["arguments"] = arguments },
        });

    [Fact]
    public void The_server_offers_the_tool_only_to_a_session_that_was_given_it_and_the_schema_is_closed_and_bounded()
    {
        var reported = false;
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        using var only = Reply(list, ClaudeClarificationServer.Tools.Reclassification, ref reported);
        var tool = Assert.Single(only.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentReclassificationSignal.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["evidence", "stage", "reason"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Reclassification.MaxEvidenceLength, schema.GetProperty("properties").GetProperty("evidence").GetProperty("maxLength").GetInt32());
        Assert.Equal(Reclassification.MaxReasonLength, schema.GetProperty("properties").GetProperty("reason").GetProperty("maxLength").GetInt32());
        Assert.Equal(["what", "how"], schema.GetProperty("properties").GetProperty("stage").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());

        using var without = Reply(list, ClaudeClarificationServer.Tools.How, ref reported);
        Assert.DoesNotContain(
            AgentReclassificationSignal.ToolName,
            without.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    [Fact]
    public void A_well_formed_report_tells_the_agent_to_stop_a_second_is_refused_and_the_latch_is_its_own()
    {
        var reported = false;

        using var first = Reply(Call(Report()), ClaudeClarificationServer.Tools.Reclassification, ref reported);
        Assert.False(first.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Stop now", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(reported);

        using var second = Reply(Call(Report("Another.")), ClaudeClarificationServer.Tools.Reclassification, ref reported);
        Assert.True(second.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("evidence", "")]
    [InlineData("evidence", "   ")]
    [InlineData("stage", "implementation")]
    [InlineData("stage", "")]
    public void A_malformed_report_is_an_error_and_does_not_latch(string field, string value)
    {
        var reported = false;
        var input = Report();
        input[field] = value;

        using var reply = Reply(Call(input), ClaudeClarificationServer.Tools.Reclassification, ref reported);

        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(reported);
    }

    [Fact]
    public void An_oversized_a_control_character_and_an_unknown_field_report_is_refused()
    {
        var reported = false;
        var tools = ClaudeClarificationServer.Tools.Reclassification;

        using var oversized = Reply(Call(Report(new string('e', Reclassification.MaxEvidenceLength + 1))), tools, ref reported);
        Assert.True(oversized.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

        using var control = Reply(Call(Report("bad\u001bescape")), tools, ref reported);
        Assert.True(control.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

        var extra = Report();
        extra["severity"] = "high";
        using var unknown = Reply(Call(extra), tools, ref reported);
        Assert.True(unknown.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(reported);
    }

    [Fact]
    public void The_stage_is_a_closed_choice_not_prose_and_either_case_is_accepted()
    {
        using var upper = JsonDocument.Parse(JsonSerializer.Serialize(Report(stage: "WHAT")));
        Assert.True(ClaudeClarificationServer.TryReadReclassification(upper.RootElement, out var signal, out _));
        Assert.Equal(ReclassificationStage.What, signal.Stage);

        using var prose = JsonDocument.Parse(JsonSerializer.Serialize(Report(stage: "the how, probably")));
        Assert.False(ClaudeClarificationServer.TryReadReclassification(prose.RootElement, out _, out var problem));
        Assert.Contains("exactly", problem, StringComparison.Ordinal);
    }

    // ---- the hook --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hook_records_the_report_for_its_own_session_and_only_then_stops_the_turn()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId));

        Assert.Equal(0, code);
        Assert.Contains("\"continue\":false", output);
        var json = File.ReadAllText(Assert.Single(Records(channel)));
        Assert.Contains("\"kind\":\"reclassification\"", json);
        var signal = Assert.Single(channel.CollectReclassifications(0, null));
        Assert.Equal(ReclassificationStage.How, signal.Stage);
        Assert.Equal("The checker rejects any call not routed through the audit log.", signal.Evidence);
        Assert.Equal("The approved strategy renames in place, which bypasses the audit log.", signal.Reason);
        Assert.Empty(channel.CollectClarifications(0, null));
        Assert.Empty(channel.CollectHowProposals(0, null));   // a report is never read as a proposal
    }

    [Fact]
    public async Task A_WHAT_report_is_recorded_with_its_stage()
    {
        using var channel = Channel();

        await RunHook(channel, Payload(channel.SessionId, Report(stage: "what")));

        Assert.Equal(ReclassificationStage.What, Assert.Single(channel.CollectReclassifications(0, null)).Stage);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task A_report_from_another_session_is_never_recorded_and_stops_nothing(string session)
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(session));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectReclassifications(0, null));
    }

    [Fact]
    public async Task A_malformed_report_and_a_session_that_was_not_given_the_tool_stop_nothing()
    {
        using var offered = Channel();
        var (_, malformed) = await RunHook(offered, Payload(offered.SessionId, Report(new string('o', Reclassification.MaxEvidenceLength + 1))));
        Assert.Equal(string.Empty, malformed);
        Assert.Empty(offered.CollectReclassifications(0, null));

        var (_, wrongStage) = await RunHook(offered, Payload(offered.SessionId, Report(stage: "accept"), id: "toolu_2"));
        Assert.Equal(string.Empty, wrongStage);
        Assert.Empty(offered.CollectReclassifications(0, null));

        using var plain = Channel(how: true, reclassification: false);
        var (_, unexpected) = await RunHook(plain, Payload(plain.SessionId));
        Assert.Equal(string.Empty, unexpected);
        Assert.Empty(plain.CollectReclassifications(0, null));
    }

    [Fact]
    public async Task Duplicate_reports_are_both_reported_so_the_controller_can_reject_them()
    {
        using var channel = Channel();

        await RunHook(channel, Payload(channel.SessionId, Report("One.")));
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId, Report("Two."), id: "toolu_2"));

        Assert.Equal(["One.", "Two."], channel.CollectReclassifications(0, null).Select(s => s.Evidence).ToArray());
    }

    [Fact]
    public async Task Reports_are_read_only_from_the_window_of_the_turn()
    {
        using var channel = Channel();
        await RunHook(channel, Payload(channel.SessionId, Report("First.")));
        var between = DateTime.UtcNow.Ticks + 1;
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId, Report("Second."), id: "toolu_2"));

        Assert.Equal(["First."], channel.CollectReclassifications(0, between).Select(s => s.Evidence).ToArray());
        Assert.Equal(["Second."], channel.CollectReclassifications(between, null).Select(s => s.Evidence).ToArray());
    }

    [Fact]
    public void A_forged_record_of_another_session_is_never_a_report_and_a_corrupt_one_is_ignored()
    {
        using var channel = Channel();
        var forged = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "reclassification", ["at"] = DateTime.UtcNow.Ticks, ["session_id"] = "someone-else", ["run_id"] = "r",
            ["reclassification"] = new Dictionary<string, object?>(Report()),
        });

        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)), forged);
        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks + 1)), "{ corrupt");

        Assert.Empty(channel.CollectReclassifications(0, null));
    }

    // ---- session-only configuration ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_session_with_the_tool_gets_one_extra_hook_and_an_allow_rule_for_exactly_that_tool()
    {
        using var channel = Channel();

        using var settings = JsonDocument.Parse(File.ReadAllText(channel.SettingsPath));
        var matchers = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse").EnumerateArray().Select(e => e.GetProperty("matcher").GetString()!).ToArray();
        Assert.Contains(Full, matchers);
        Assert.DoesNotContain(HowFull, matchers);
        Assert.Equal([Full], settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.True(channel.OffersReclassification);
        Assert.False(channel.OffersHow);
        Assert.Contains("--mcp-config", channel.Augment(["claude"]));
        Assert.Contains("\"reclassification\":true", File.ReadAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.ConfigFile)));
    }

    [Fact]
    public void Offering_the_tool_beside_the_HOW_tool_allows_exactly_those_two()
    {
        using var channel = Channel(how: true);

        using var settings = JsonDocument.Parse(File.ReadAllText(channel.SettingsPath));
        Assert.Equal(
            [HowFull, Full],
            settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_report_is_not_a_tracked_tool_event_in_the_verification_evidence()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId));

        var collected = channel.Collect();

        Assert.Empty(collected.Activities);
        Assert.Empty(collected.Problems);
    }

    // ---- the runtime -----------------------------------------------------------------------------------------------------------

    private HerdrAgentRuntime Runtime(out FakeHerdrCore core, bool reclassification = true)
    {
        core = new FakeHerdrCore();

        return new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-reclass-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: Options(reclassification: reclassification))],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);
    }

    [Fact]
    public async Task A_session_with_no_checks_gets_the_channel_and_reports_only_its_own_turns_reports()
    {
        await using var runtime = Runtime(out _);
        Assert.True(runtime.CanSignal(Profile));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var channel = runtime.EvidenceChannelOf(session)!;
        Assert.True(channel.OffersReclassification);

        var turn1 = await runtime.SendTurnAsync(session, "work");
        await RunHook(channel, Payload(channel.SessionId, Report("In turn one.")));
        await Task.Delay(20);
        var turn2 = await runtime.SendTurnAsync(session, "more");

        Assert.Equal(["In turn one."], (await runtime.CollectReclassificationsAsync(session, turn1)).Select(s => s.Evidence).ToArray());
        Assert.Empty(await runtime.CollectReclassificationsAsync(session, turn2));
        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.CollectReclassificationsAsync(session, new AgentTurnNumber(7)));
    }

    [Fact]
    public async Task A_profile_without_the_option_cannot_signal_and_reports_nothing()
    {
        await using var runtime = Runtime(out var core, reclassification: false);

        Assert.False(runtime.CanSignal(Profile));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var turn = await runtime.SendTurnAsync(session, "work");

        Assert.Empty(await runtime.CollectReclassificationsAsync(session, turn));
        Assert.DoesNotContain("--mcp-config", core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task The_resumed_conversation_keeps_the_tool_bound_to_exactly_that_conversation()
    {
        await using var runtime = Runtime(out var core);

        var first = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var conversation = runtime.ConversationOf(first)!;
        await runtime.StopSessionAsync(first);

        var resumed = await runtime.ResumeSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"), conversation);

        var channel = runtime.EvidenceChannelOf(resumed)!;
        Assert.Equal(conversation.Reference, channel.SessionId);
        Assert.True(channel.OffersReclassification);
        var argv = core.ParamsOf("terminal.run").Last().GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Contains("--mcp-config", argv);
        Assert.Contains("--resume", argv);
    }
}
