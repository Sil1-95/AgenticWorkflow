using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code translation of the HOW signal, offline: the MCP tool and its closed schema, the hook that records the proposal and only then stops the
/// turn, the session-only configuration, and the runtime's identity and window checks. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeHowTests : IDisposable
{
    private const string Full = "mcp__aw__propose_how";
    private const string WhatFull = "mcp__aw__propose_what";
    private const string Question = "mcp__aw__request_clarification";

    private static readonly ProfileId Profile = new("claude-local");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-how-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public ClaudeHowTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceOptions Options(bool clarification = false, bool what = false, bool how = true)
        => new() { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferClarification = clarification, OfferWhat = what, OfferHow = how };

    private ClaudeEvidenceChannel Channel(bool clarification = false, bool what = false, bool how = true)
        => ClaudeEvidenceChannel.TryCreate(Options(clarification, what, how), _workspace, [], sessionId: null, clarification, what, how, out var failure)
           ?? throw new InvalidOperationException(failure);

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a HOW proposal must never capture the workspace");
    }

    private static Dictionary<string, object?> Sections(string strategy = "Rename in place and keep a forwarding alias.")
        => new()
        {
            ["strategy"] = strategy,
            ["components"] = "Greeting owns the rename; callers move to GetFood().",
            ["data_and_control_flow"] = "GetMeal() forwards to GetFood().",
            ["compatibility"] = "The alias is marked obsolete for one release.",
            ["risks_and_tradeoffs"] = "An alias keeps old callers working but leaves a deprecated name.",
            ["verification_approach"] = "Existing tests plus one for the alias.",
            ["rationale"] = "An alias and a hard rename have different compatibility costs.",
        };

    private static string Payload(string session, Dictionary<string, object?>? input = null, string tool = Full, string id = "toolu_h")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session,
            ["hook_event_name"] = "PostToolUse",
            ["prompt_id"] = "p1",
            ["tool_name"] = tool,
            ["tool_use_id"] = id,
            ["tool_input"] = input ?? Sections(),
        });

    private static async Task<(int Code, string Out)> RunHook(ClaudeEvidenceChannel channel, string stdin)
    {
        var output = new StringWriter();
        var code = await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), new NoState(), stdout: output);
        return (code, output.ToString());
    }

    private static string[] Records(ClaudeEvidenceChannel channel) => Directory.EnumerateFiles(channel.Directory, "e-*.json").Order().ToArray();

    // ---- the MCP server ---------------------------------------------------------------------------------------------------------

    private static JsonDocument Reply(string request, ClaudeClarificationServer.Tools tools, ref bool raised, ref bool proposed, ref bool proposedHow)
        => JsonDocument.Parse(ClaudeClarificationServer.Handle(request, tools, ref raised, ref proposed, ref proposedHow)!.ToJsonString());

    private static string Call(Dictionary<string, object?> arguments)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?> { ["name"] = "propose_how", ["arguments"] = arguments },
        });

    [Fact]
    public void The_server_offers_only_the_tools_the_session_was_given_and_the_HOW_schema_is_closed_and_bounded()
    {
        bool raised = false, proposed = false, how = false;
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        using var only = Reply(list, ClaudeClarificationServer.Tools.How, ref raised, ref proposed, ref how);
        var tool = Assert.Single(only.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentHowSignal.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["strategy", "components", "risks_and_tradeoffs", "rationale"],
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(HowProposal.MaxSectionLength, schema.GetProperty("properties").GetProperty("strategy").GetProperty("maxLength").GetInt32());
        Assert.Equal(HowProposal.MaxRationaleLength, schema.GetProperty("properties").GetProperty("rationale").GetProperty("maxLength").GetInt32());

        using var all = Reply(
            list,
            ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.What | ClaudeClarificationServer.Tools.How,
            ref raised,
            ref proposed,
            ref how);
        Assert.Equal(
            [AgentHowSignal.ToolName, AgentWhatSignal.ToolName, AgentClarificationSignal.ToolName],
            all.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order().ToArray());

        using var question = Reply(list, ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.What, ref raised, ref proposed, ref how);
        Assert.DoesNotContain(
            AgentHowSignal.ToolName,
            question.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    [Fact]
    public void A_well_formed_proposal_tells_the_agent_to_stop_and_a_second_proposal_is_refused()
    {
        bool raised = false, proposed = false, how = false;

        using var first = Reply(Call(Sections()), ClaudeClarificationServer.Tools.How, ref raised, ref proposed, ref how);
        Assert.False(first.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Stop now", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(how);
        Assert.False(raised);
        Assert.False(proposed);   // the WHAT latch is separate

        using var second = Reply(Call(Sections("Another.")), ClaudeClarificationServer.Tools.How, ref raised, ref proposed, ref how);
        Assert.True(second.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void The_tool_is_refused_when_the_session_was_not_given_it()
    {
        bool raised = false, proposed = false, how = false;

        using var reply = Reply(Call(Sections()), ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.What, ref raised, ref proposed, ref how);

        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(how);
    }

    [Fact]
    public void The_previous_two_tool_entry_point_still_serves_the_other_tools()
    {
        bool raised = false, proposed = false;

        var reply = ClaudeClarificationServer.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ClaudeClarificationServer.Tools.What, ref raised, ref proposed)!;

        Assert.Equal(AgentWhatSignal.ToolName, ((System.Text.Json.Nodes.JsonArray)reply["result"]!["tools"]!)[0]!["name"]!.GetValue<string>());
    }

    public static TheoryData<string> Malformed() => new()
    {
        """{"strategy":"","components":"c","risks_and_tradeoffs":"r","rationale":"why"}""",
        """{"strategy":"s","components":"c","risks_and_tradeoffs":"r"}""",
        """{"strategy":"s","components":"c","risks_and_tradeoffs":"r","rationale":"why","extra":1}""",
        """{"strategy":1,"components":"c","risks_and_tradeoffs":"r","rationale":"why"}""",
        "{\"strategy\":\"" + new string('x', HowProposal.MaxSectionLength + 1) + "\",\"components\":\"c\",\"risks_and_tradeoffs\":\"r\",\"rationale\":\"why\"}",
        "{\"strategy\":\"s\",\"components\":\"c\",\"risks_and_tradeoffs\":\"r\",\"rationale\":\"" + new string('r', HowProposal.MaxRationaleLength + 1) + "\"}",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_or_oversized_proposal_is_an_error_the_agent_can_correct_and_does_not_count_as_the_proposal(string arguments)
    {
        bool raised = false, proposed = false, how = false;
        var request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"propose_how\",\"arguments\":" + arguments + "}}";

        using var reply = Reply(request, ClaudeClarificationServer.Tools.How, ref raised, ref proposed, ref how);

        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(how);
    }

    // ---- the hook: record, then stop ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_from_the_owned_session_is_recorded_durably_before_the_turn_is_stopped()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId));

        Assert.Equal(0, code);
        Assert.Contains("\"continue\":false", output);
        Assert.Contains("HOW", output);

        var json = File.ReadAllText(Assert.Single(Records(channel)));
        Assert.Contains("\"kind\":\"how_proposal\"", json);
        var signal = Assert.Single(channel.CollectHowProposals(0, null));
        Assert.Equal("Rename in place and keep a forwarding alias.", signal.Strategy);
        Assert.Equal("The alias is marked obsolete for one release.", signal.Compatibility);
        Assert.Equal("An alias and a hard rename have different compatibility costs.", signal.Rationale);
        Assert.Empty(channel.CollectClarifications(0, null));   // a HOW is never read as a question
        Assert.Empty(channel.CollectWhatProposals(0, null));    // nor as a WHAT
    }

    [Fact]
    public async Task The_optional_sections_may_be_omitted()
    {
        using var channel = Channel();
        var input = Sections();
        input.Remove("data_and_control_flow");
        input.Remove("compatibility");
        input.Remove("verification_approach");

        var (_, output) = await RunHook(channel, Payload(channel.SessionId, input));

        Assert.Contains("\"continue\":false", output);
        var signal = Assert.Single(channel.CollectHowProposals(0, null));
        Assert.Null(signal.DataAndControlFlow);
        Assert.Null(signal.Compatibility);
        Assert.Null(signal.VerificationApproach);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task A_proposal_from_another_session_is_never_recorded_as_one_and_stops_nothing(string session)
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(session));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectHowProposals(0, null));
    }

    [Fact]
    public async Task A_malformed_proposal_and_a_session_that_was_not_given_the_tool_stop_nothing()
    {
        using var offered = Channel();
        var oversized = Sections(new string('o', HowProposal.MaxSectionLength + 1));
        var (_, malformed) = await RunHook(offered, Payload(offered.SessionId, oversized));
        Assert.Equal(string.Empty, malformed);
        Assert.Empty(offered.CollectHowProposals(0, null));

        var missing = Sections();
        missing.Remove("risks_and_tradeoffs");
        var (_, incomplete) = await RunHook(offered, Payload(offered.SessionId, missing, id: "toolu_2"));
        Assert.Equal(string.Empty, incomplete);
        Assert.Empty(offered.CollectHowProposals(0, null));

        using var plain = Channel(clarification: true, how: false);
        var (_, unexpected) = await RunHook(plain, Payload(plain.SessionId));
        Assert.Equal(string.Empty, unexpected);
        Assert.Empty(plain.CollectHowProposals(0, null));
    }

    [Fact]
    public async Task A_question_a_WHAT_and_a_HOW_are_three_different_records()
    {
        using var channel = Channel(clarification: true, what: true, how: true);

        await RunHook(channel, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = channel.SessionId, ["hook_event_name"] = "PostToolUse", ["tool_name"] = Question, ["tool_use_id"] = "toolu_q",
            ["tool_input"] = new { question = "Which dessert?", reason = "Not in the repo." },
        }));
        await RunHook(channel, Payload(channel.SessionId, new Dictionary<string, object?>
        {
            ["objective"] = "o", ["required_behavior"] = "b", ["scope"] = "s", ["acceptance_conditions"] = "c", ["rationale"] = "r",
        }, tool: WhatFull, id: "toolu_w"));
        await RunHook(channel, Payload(channel.SessionId, id: "toolu_h2"));

        Assert.Single(channel.CollectClarifications(0, null));
        Assert.Single(channel.CollectWhatProposals(0, null));
        Assert.Single(channel.CollectHowProposals(0, null));
    }

    [Fact]
    public async Task Duplicate_proposals_are_both_reported_so_the_controller_can_reject_them()
    {
        using var channel = Channel();

        await RunHook(channel, Payload(channel.SessionId, Sections("One.")));
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId, Sections("Two."), id: "toolu_2"));

        Assert.Equal(["One.", "Two."], channel.CollectHowProposals(0, null).Select(s => s.Strategy).ToArray());
    }

    [Fact]
    public async Task Proposals_are_read_only_from_the_window_of_the_turn()
    {
        using var channel = Channel();
        await RunHook(channel, Payload(channel.SessionId, Sections("First.")));
        var between = DateTime.UtcNow.Ticks + 1;
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId, Sections("Second."), id: "toolu_2"));

        Assert.Equal(["First."], channel.CollectHowProposals(0, between).Select(s => s.Strategy).ToArray());
        Assert.Equal(["Second."], channel.CollectHowProposals(between, null).Select(s => s.Strategy).ToArray());
    }

    [Fact]
    public void A_forged_record_of_another_session_or_run_is_never_a_proposal_and_a_corrupt_one_is_ignored()
    {
        using var channel = Channel();
        string Forge(string session, string run) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["kind"] = "how_proposal", ["at"] = DateTime.UtcNow.Ticks, ["session_id"] = session, ["run_id"] = run, ["how"] = new Dictionary<string, object?>(Sections()),
        });

        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks)), Forge("someone-else", "r"));
        File.WriteAllText(Path.Combine(channel.Directory, ClaudeEvidenceFormat.EventFileName(DateTime.UtcNow.Ticks + 1)), "{ corrupt");

        Assert.Empty(channel.CollectHowProposals(0, null));
    }

    // ---- session-only configuration ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_session_with_the_tool_gets_a_session_only_mcp_server_one_extra_hook_and_an_allow_rule_for_exactly_that_tool()
    {
        using var channel = Channel();

        using var settings = JsonDocument.Parse(File.ReadAllText(channel.SettingsPath));
        var matchers = settings.RootElement.GetProperty("hooks").GetProperty("PostToolUse").EnumerateArray().Select(e => e.GetProperty("matcher").GetString()!).ToArray();
        Assert.Contains(Full, matchers);
        Assert.DoesNotContain(WhatFull, matchers);
        Assert.DoesNotContain(Question, matchers);
        Assert.Equal([Full], settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).ToArray());

        using var mcp = JsonDocument.Parse(File.ReadAllText(channel.McpConfigPath!));
        var server = mcp.RootElement.GetProperty("mcpServers").GetProperty("aw");
        Assert.Equal(["--mcp", channel.Directory], server.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.True(channel.OffersHow);
        Assert.False(channel.OffersWhat);
        Assert.False(channel.OffersClarification);
        Assert.Contains("--mcp-config", channel.Augment(["claude"]));
    }

    [Fact]
    public void Offering_all_tools_allows_exactly_those_three_and_the_channel_config_records_what_was_given()
    {
        using var all = Channel(clarification: true, what: true, how: true);
        using var settings = JsonDocument.Parse(File.ReadAllText(all.SettingsPath));
        Assert.Equal(
            [Full, WhatFull, Question],
            settings.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray().Select(e => e.GetString()!).Order().ToArray());

        var config = File.ReadAllText(Path.Combine(all.Directory, ClaudeEvidenceFormat.ConfigFile));
        Assert.Contains("\"how\":true", config);

        var evidenceOnly = ClaudeEvidenceChannel.TryCreate(
            Options(how: false),
            _workspace,
            [new VerificationCheckSpec(new VerificationCheckId("build"), "builds", "dotnet", ["build"], TimeSpan.FromMinutes(1))],
            sessionId: null,
            clarification: false,
            what: false,
            how: false,
            out _)!;
        using var evidenceOnlyScope = evidenceOnly;
        Assert.False(evidenceOnly.OffersHow);
        Assert.Null(evidenceOnly.McpConfigPath);
        Assert.DoesNotContain("permissions", File.ReadAllText(evidenceOnly.SettingsPath));
    }

    [Fact]
    public async Task A_HOW_record_is_not_a_tracked_tool_event_in_the_verification_evidence()
    {
        using var channel = Channel();
        channel.MarkTurnStart();
        await Task.Delay(20);
        await RunHook(channel, Payload(channel.SessionId));

        var collected = channel.Collect();

        Assert.Empty(collected.Activities);
        Assert.Empty(collected.Problems);   // not even a problem: it is a workflow signal, not a tool event
    }

    // ---- the runtime -----------------------------------------------------------------------------------------------------------

    private HerdrAgentRuntime Runtime(out FakeHerdrCore core, bool how = true)
    {
        core = new FakeHerdrCore();

        return new HerdrAgentRuntime(
            new HerdrAgentRuntimeOptions
            {
                Session = "aw-how-unit",
                Profiles = [ClaudeCodeLaunch.Profile(Profile, evidence: Options(how: how))],
                StartupTimeout = TimeSpan.FromSeconds(10),
                CommandTimeout = TimeSpan.FromSeconds(10),
            },
            core,
            core);
    }

    [Fact]
    public async Task A_session_with_no_checks_gets_the_HOW_channel_and_reports_only_its_own_turns_proposals()
    {
        await using var runtime = Runtime(out _);
        Assert.True(runtime.CanSignal(Profile));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var channel = runtime.EvidenceChannelOf(session)!;
        Assert.True(channel.OffersHow);

        var turn1 = await runtime.SendTurnAsync(session, "work");
        await RunHook(channel, Payload(channel.SessionId, Sections("In turn one.")));
        await Task.Delay(20);
        var turn2 = await runtime.SendTurnAsync(session, "more");

        Assert.Equal(["In turn one."], (await runtime.CollectHowProposalsAsync(session, turn1)).Select(s => s.Strategy).ToArray());
        Assert.Empty(await runtime.CollectHowProposalsAsync(session, turn2));
        await Assert.ThrowsAsync<AgentRuntimeException>(() => runtime.CollectHowProposalsAsync(session, new AgentTurnNumber(7)));
    }

    [Fact]
    public async Task A_profile_without_the_option_cannot_signal_and_reports_no_proposals()
    {
        await using var runtime = Runtime(out var core, how: false);

        Assert.False(runtime.CanSignal(Profile));

        var session = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var turn = await runtime.SendTurnAsync(session, "work");

        Assert.Empty(await runtime.CollectHowProposalsAsync(session, turn));
        Assert.DoesNotContain("--mcp-config", core.ParamsOf("terminal.run").Single().GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task The_resumed_conversation_keeps_the_HOW_tool_bound_to_exactly_that_conversation()
    {
        await using var runtime = Runtime(out var core);

        var first = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"));
        var conversation = runtime.ConversationOf(first)!;
        await runtime.StopSessionAsync(first);

        var resumed = await runtime.ResumeSessionAsync(new AgentSessionRequest(Profile, _workspace, "task"), conversation);

        var channel = runtime.EvidenceChannelOf(resumed)!;
        Assert.Equal(conversation.Reference, channel.SessionId);
        Assert.True(channel.OffersHow);
        var argv = core.ParamsOf("terminal.run").Last().GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        Assert.Contains("--mcp-config", argv);
        Assert.Contains("--resume", argv);
    }
}
