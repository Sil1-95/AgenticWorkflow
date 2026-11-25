using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code translation of formal PROVE, offline: the additive <c>request_prove</c> Proof Contract tool and the typed <c>submit_proof</c> tool with their closed,
/// bounded schemas, the hooks that record the contract (without stopping the implementer) and the proof (stopping the prover's turn), the prover's read-only launch policy and the
/// per-purpose launch configuration. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeProofTests : IDisposable
{
    private static readonly ProfileId Profile = new("claude-local");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-proof-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public ClaudeProofTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceChannel Channel(bool request = false, bool prove = true)
        => ClaudeEvidenceChannel.TryCreate(
               new ClaudeEvidenceOptions { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferProveRequest = request, OfferProve = prove },
               _workspace,
               [],
               sessionId: null,
               clarification: false,
               what: false,
               how: false,
               reclassification: false,
               reviewRequest: false,
               review: false,
               proveRequest: request,
               prove: prove,
               out var failure)
           ?? throw new InvalidOperationException(failure);

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a proof must never capture the workspace");
    }

    private static Dictionary<string, object?> ClaimOf(string id = "c1", params string[] evidence)
        => new()
        {
            ["id"] = id,
            ["statement"] = "An empty file yields an empty list.",
            ["acceptance_criterion"] = "The importer returns [] and exits 0.",
            ["evidence"] = evidence.Length == 0 ? new[] { "verification:tests", "observation:run it on an empty file" } : evidence,
        };

    private static Dictionary<string, object?> Contract(params Dictionary<string, object?>[] claims)
        => new() { ["rationale"] = "The edge is where a retry can go wrong.", ["claims"] = claims.Length == 0 ? new[] { ClaimOf() } : claims };

    private static Dictionary<string, object?> ResultOf(string id = "c1", string verdict = "pass", params string[] evidence)
        => new()
        {
            ["claim_id"] = id,
            ["verdict"] = verdict,
            ["evidence"] = evidence.Length == 0 ? new[] { "verification:tests", "command:1" } : evidence,
            ["summary"] = "Ran it on an empty file.",
        };

    private static Dictionary<string, object?> Proof(params Dictionary<string, object?>[] results)
        => new() { ["summary"] = "Judged.", ["claims"] = results.Length == 0 ? new[] { ResultOf() } : results };

    private static string Payload(string session, Dictionary<string, object?> input, string tool = ClaudeEvidenceFormat.ProofToolFullName, string id = "toolu_pr")
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session,
            ["hook_event_name"] = "PostToolUse",
            ["prompt_id"] = "p1",
            ["tool_name"] = tool,
            ["tool_use_id"] = id,
            ["tool_input"] = input,
        });

    private static async Task<(int Code, string Out)> RunHook(ClaudeEvidenceChannel channel, string stdin)
    {
        var output = new StringWriter();
        var code = await ClaudeHookHandler.RunAsync([channel.Directory], new StringReader(stdin), new NoState(), stdout: output);
        return (code, output.ToString());
    }

    private static JsonDocument Reply(string request, ClaudeClarificationServer.Tools tools, ClaudeClarificationServer.ProveCalls? proves = null)
    {
        bool raised = false, proposed = false, how = false, reported = false;
        return JsonDocument.Parse(ClaudeClarificationServer.Handle(request, tools, ref raised, ref proposed, ref how, ref reported, proves: proves)!.ToJsonString());
    }

    private static string Call(string tool, Dictionary<string, object?> arguments)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?> { ["name"] = tool, ["arguments"] = arguments },
        });

    private static bool IsError(JsonDocument reply) => reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();

    private static string TextOf(JsonDocument reply) => reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    // ---- the MCP server ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_prover_is_offered_exactly_one_workflow_tool_with_a_closed_bounded_schema()
    {
        using var list = Reply("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ClaudeClarificationServer.Tools.Prove);

        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentProofSubmission.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["summary", "claims"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var items = schema.GetProperty("properties").GetProperty("claims").GetProperty("items");
        Assert.False(items.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["pass", "fail", "inconclusive"], items.GetProperty("properties").GetProperty("verdict").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(ProofContract.MaxClaims, schema.GetProperty("properties").GetProperty("claims").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void The_contract_tool_has_a_closed_bounded_schema_with_the_closed_evidence_vocabulary_in_its_description()
    {
        using var list = Reply("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ClaudeClarificationServer.Tools.ProveRequest);

        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentProveRequestSignal.ToolName, tool.GetProperty("name").GetString());

        var claims = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("claims");
        Assert.Equal(1, claims.GetProperty("minItems").GetInt32());
        Assert.Equal(ProofContract.MaxClaims, claims.GetProperty("maxItems").GetInt32());
        var evidence = claims.GetProperty("items").GetProperty("properties").GetProperty("evidence");
        Assert.Equal(ProofClaim.MaxEvidence, evidence.GetProperty("maxItems").GetInt32());
        foreach (var kind in new[] { "verification:", "review:current-pass", "observation:" })
            Assert.Contains(kind, evidence.GetProperty("description").GetString());
    }

    [Fact]
    public void The_implementer_is_offered_the_contract_tool_but_never_the_proof_and_the_prover_never_the_contract_tool()
    {
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        string[] Names(JsonDocument d) => d.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

        using var implementer = Reply(list, ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.ProveRequest);
        Assert.Contains(AgentProveRequestSignal.ToolName, Names(implementer));
        Assert.DoesNotContain(AgentProofSubmission.ToolName, Names(implementer));

        using var prover = Reply(list, ClaudeClarificationServer.Tools.Prove);
        Assert.DoesNotContain(AgentProveRequestSignal.ToolName, Names(prover));
        Assert.DoesNotContain(AgentClarificationSignal.ToolName, Names(prover));
        Assert.DoesNotContain(AgentReviewRequestSignal.ToolName, Names(prover));
    }

    [Fact]
    public async Task A_proof_the_controller_refuses_can_be_corrected_in_the_same_conversation_and_never_twice_in_one_turn()
    {
        // The regression of the live defect: the server latched on the first well-formed proof, so the one correction the controller asks for could never be submitted.
        using var channel = Channel();
        await using var server = new ServerPipe(channel.Directory);

        var uncited = Proof(ResultOf("c1", "pass", "verification:tests"));                       // well-formed; the controller will refuse it (no command for an observation)
        var corrected = Proof(ResultOf("c1", "pass", "verification:tests", "command:1"));

        var turn1 = DateTime.UtcNow.Ticks;
        channel.MarkSubmissionTurn(turn1);

        using var first = await server.CallAsync(Call(AgentProofSubmission.ToolName, uncited));
        Assert.False(IsError(first));
        var (_, firstHook) = await RunHook(channel, Payload(channel.SessionId, uncited, id: "toolu_1"));
        Assert.Contains("\"continue\":false", firstHook);

        using var sameTurn = await server.CallAsync(Call(AgentProofSubmission.ToolName, corrected));
        Assert.True(IsError(sameTurn));
        Assert.Contains("already submitted in this turn", TextOf(sameTurn));

        await Task.Delay(50);
        var turn2 = DateTime.UtcNow.Ticks;
        channel.MarkSubmissionTurn(turn2);                                                          // the controller sends its one correction to the same conversation

        using var second = await server.CallAsync(Call(AgentProofSubmission.ToolName, corrected));
        Assert.False(IsError(second));
        var (_, secondHook) = await RunHook(channel, Payload(channel.SessionId, corrected, id: "toolu_2"));
        Assert.Contains("\"continue\":false", secondHook);

        // The controller reads one turn at a time: each turn holds exactly one submission, so exactly one proof can ever become durable.
        var inFirstTurn = Assert.Single(channel.CollectProofSubmissions(turn1, turn2));
        Assert.DoesNotContain("command:1", Assert.Single(inFirstTurn.Claims).Evidence);
        var inSecondTurn = Assert.Single(channel.CollectProofSubmissions(turn2, null));
        Assert.Contains("command:1", Assert.Single(inSecondTurn.Claims).Evidence);

        using var third = await server.CallAsync(Call(AgentProofSubmission.ToolName, corrected));  // a second correction in the same turn
        Assert.True(IsError(third));
        Assert.Single(channel.CollectProofSubmissions(turn2, null));
    }

    [Fact]
    public async Task A_malformed_proof_in_a_correction_turn_still_does_not_use_up_the_turn()
    {
        using var channel = Channel();
        await using var server = new ServerPipe(channel.Directory);
        channel.MarkSubmissionTurn(DateTime.UtcNow.Ticks);

        using var rejected = await server.CallAsync(Call(AgentProofSubmission.ToolName, Proof(ResultOf(verdict: "mostly"))));
        Assert.True(IsError(rejected));

        using var accepted = await server.CallAsync(Call(AgentProofSubmission.ToolName, Proof()));
        Assert.False(IsError(accepted));
    }

    [Fact]
    public async Task A_proof_is_refused_for_good_when_the_controller_never_sends_another_turn()
    {
        using var channel = Channel();
        await using var server = new ServerPipe(channel.Directory);
        channel.MarkSubmissionTurn(DateTime.UtcNow.Ticks);

        using var first = await server.CallAsync(Call(AgentProofSubmission.ToolName, Proof()));
        Assert.False(IsError(first));

        await Task.Delay(50);
        using var again = await server.CallAsync(Call(AgentProofSubmission.ToolName, Proof()));
        Assert.True(IsError(again));
    }

    [Fact]
    public void A_well_formed_proof_is_accepted_once_and_a_second_in_the_same_turn_is_refused()
    {
        var calls = new ClaudeClarificationServer.ProveCalls();

        using var first = Reply(Call(AgentProofSubmission.ToolName, Proof()), ClaudeClarificationServer.Tools.Prove, calls);
        Assert.False(IsError(first));
        Assert.Contains("Stop now", TextOf(first));

        using var second = Reply(Call(AgentProofSubmission.ToolName, Proof()), ClaudeClarificationServer.Tools.Prove, calls);
        Assert.True(IsError(second));
    }

    [Fact]
    public void A_well_formed_contract_never_tells_the_agent_to_stop_and_is_accepted_once()
    {
        var calls = new ClaudeClarificationServer.ProveCalls();

        using var first = Reply(Call(AgentProveRequestSignal.ToolName, Contract()), ClaudeClarificationServer.Tools.ProveRequest, calls);
        Assert.False(IsError(first));
        Assert.DoesNotContain("Stop now", TextOf(first));
        Assert.Contains("Carry on", TextOf(first));

        using var second = Reply(Call(AgentProveRequestSignal.ToolName, Contract()), ClaudeClarificationServer.Tools.ProveRequest, calls);
        Assert.True(IsError(second));
    }

    [Fact]
    public void A_malformed_contract_is_rejected_whole_and_does_not_latch()
    {
        var calls = new ClaudeClarificationServer.ProveCalls();
        var tools = ClaudeClarificationServer.Tools.ProveRequest;

        void Rejected(Dictionary<string, object?> input, string expected)
        {
            using var reply = Reply(Call(AgentProveRequestSignal.ToolName, input), tools, calls);
            Assert.True(IsError(reply));
            Assert.Contains(expected, TextOf(reply), StringComparison.OrdinalIgnoreCase);
        }

        Rejected(new Dictionary<string, object?> { ["rationale"] = "why", ["claims"] = Array.Empty<object>() }, "claim");
        Rejected(Contract(Enumerable.Range(1, ProofContract.MaxClaims + 1).Select(i => ClaimOf($"c{i}")).ToArray()), "claim");
        Rejected(Contract(ClaimOf(new string('x', ProofClaim.MaxIdLength + 1))), "20");
        Rejected(Contract(ClaimOf("c1", "transcript:everything")), string.Empty);
        Rejected(Contract(ClaimOf("c1", "verification")), string.Empty);
        Rejected(Contract(ClaimOf("c1", "review:someone-elses")), string.Empty);
        Rejected(Contract(ClaimOf("c1", "a:1", "b:2", "c:3", "d:4", "e:5")), string.Empty);
        Rejected(Contract(ClaimOf("c1"), ClaimOf("c1")), string.Empty);

        var extra = Contract();
        extra["confidence"] = 0.9;
        Rejected(extra, "unexpected field");

        var tooLong = ClaimOf();
        tooLong["statement"] = new string('s', ProofClaim.MaxStatementLength + 1);
        Rejected(Contract(tooLong), "statement");

        Assert.False(calls.Requested);
    }

    [Fact]
    public void A_malformed_proof_is_an_error_and_does_not_latch()
    {
        var calls = new ClaudeClarificationServer.ProveCalls();
        var tools = ClaudeClarificationServer.Tools.Prove;

        void Rejected(Dictionary<string, object?> input, string expected)
        {
            using var reply = Reply(Call(AgentProofSubmission.ToolName, input), tools, calls);
            Assert.True(IsError(reply));
            Assert.Contains(expected, TextOf(reply), StringComparison.OrdinalIgnoreCase);
        }

        Rejected(Proof(ResultOf(verdict: "mostly")), "verdict");
        Rejected(Proof(ResultOf(evidence: "command:zero")), string.Empty);
        Rejected(Proof(ResultOf(evidence: "file:/etc/passwd")), string.Empty);
        Rejected(new Dictionary<string, object?> { ["summary"] = "Judged.", ["claims"] = Array.Empty<object>() }, "claim");

        var verdictField = Proof();
        verdictField["verdict"] = "PASS";
        Rejected(verdictField, "unexpected field");   // the overall verdict is derived, never submitted

        var oversized = Proof();
        oversized["summary"] = new string('s', ProofReport.MaxSummaryLength + 1);
        Rejected(oversized, "summary");

        Assert.False(calls.Submitted);
    }

    // ---- the hook --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hook_records_the_structured_proof_for_its_own_session_and_stops_the_provers_turn()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId, Proof(ResultOf("c1", "fail", "verification:tests"))));

        Assert.Equal(0, code);
        Assert.Contains("\"continue\":false", output);
        var submission = Assert.Single(channel.CollectProofSubmissions(0, null));
        Assert.Equal("Judged.", submission.Summary);
        var result = Assert.Single(submission.Claims);
        Assert.Equal("c1", result.ClaimId);
        Assert.Equal("fail", result.Verdict);
        Assert.Equal(["verification:tests"], result.Evidence.ToArray());
        Assert.Empty(channel.CollectProveRequests(0, null));   // a proof is never read as a contract
    }

    [Fact]
    public async Task A_proof_carries_no_transcript_or_hidden_reasoning_in_the_record_the_hook_writes()
    {
        using var channel = Channel();

        await RunHook(channel, Payload(channel.SessionId, Proof()));

        var json = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(channel.Directory, "e-*.json")));
        Assert.Contains("\"kind\":\"proof_submission\"", json);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thinking", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_contract_hook_records_the_contract_without_stopping_the_turn()
    {
        using var channel = Channel(request: true, prove: false);

        var (code, output) = await RunHook(channel, Payload(channel.SessionId, Contract(), ClaudeEvidenceFormat.ProveRequestToolFullName));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);   // the agent's turn goes on: a contract is additive
        var signal = Assert.Single(channel.CollectProveRequests(0, null));
        Assert.Equal("The edge is where a retry can go wrong.", signal.Rationale);
        var claim = Assert.Single(signal.Claims);
        Assert.Equal("c1", claim.Id);
        Assert.Equal(["verification", "observation"], claim.Evidence.Select(e => e.Kind).ToArray());
        Assert.Equal("tests", claim.Evidence[0].Subject);
        Assert.Empty(channel.CollectProofSubmissions(0, null));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task A_proof_from_another_session_is_never_recorded_and_stops_nothing(string session)
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(session, Proof()));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectProofSubmissions(0, null));
    }

    [Fact]
    public async Task A_malformed_proof_and_a_session_that_was_not_given_the_tool_record_and_stop_nothing()
    {
        using var prover = Channel();
        var (_, malformed) = await RunHook(prover, Payload(prover.SessionId, Proof(ResultOf(verdict: "maybe"))));
        Assert.Equal(string.Empty, malformed);
        Assert.Empty(prover.CollectProofSubmissions(0, null));

        using var implementer = Channel(request: true, prove: false);
        var (_, notOffered) = await RunHook(implementer, Payload(implementer.SessionId, Proof(), id: "toolu_2"));
        Assert.Equal(string.Empty, notOffered);
        Assert.Empty(implementer.CollectProofSubmissions(0, null));

        var (_, wrongTool) = await RunHook(implementer, Payload(implementer.SessionId, Contract(), ClaudeEvidenceFormat.ProofToolFullName, "toolu_3"));
        Assert.Equal(string.Empty, wrongTool);
    }

    [Fact]
    public async Task The_settings_allow_exactly_the_one_tool_each_session_was_given()
    {
        using var prover = Channel();
        using var implementer = Channel(request: true, prove: false);

        var proverSettings = File.ReadAllText(prover.SettingsPath);
        var implementerSettings = File.ReadAllText(implementer.SettingsPath);

        Assert.Contains(ClaudeEvidenceFormat.ProofToolFullName, proverSettings);
        Assert.DoesNotContain(ClaudeEvidenceFormat.ProveRequestToolFullName, proverSettings);
        Assert.Contains(ClaudeEvidenceFormat.ProveRequestToolFullName, implementerSettings);
        Assert.DoesNotContain(ClaudeEvidenceFormat.ProofToolFullName, implementerSettings);
        await Task.CompletedTask;
    }

    // ---- read-only launch and per-purpose configuration ----------------------------------------------------------------------------

    [Fact]
    public void The_prover_policy_denies_every_file_editing_tool_and_never_runs_in_the_edit_accepting_mode()
    {
        var implementer = new ClaudeCodeToolPolicy();
        var prover = implementer.ForProver();

        Assert.Equal("acceptEdits", implementer.PermissionMode);
        Assert.Equal("default", prover.PermissionMode);
        foreach (var tool in new[] { "Edit", "Write", "MultiEdit", "NotebookEdit" })
        {
            Assert.Contains(tool, prover.DeniedTools);
            Assert.DoesNotContain(tool, implementer.DeniedTools);   // the implementer's own policy is untouched
        }

        Assert.All(implementer.DeniedTools, tool => Assert.Contains(tool, prover.DeniedTools));
        Assert.Equal(implementer.AllowedTools, prover.AllowedTools);
    }

    [Fact]
    public void The_prover_launch_is_a_separate_purpose_with_the_edit_tools_denied_and_the_other_launches_are_unchanged()
    {
        var policy = new ClaudeCodeToolPolicy();

        var implementer = ClaudeCodeLaunch.Profile(Profile, policy);
        var reviewer = ClaudeCodeLaunch.Profile(Profile, policy.ForReviewer(), purpose: AgentSessionPurpose.Review);
        var prover = ClaudeCodeLaunch.Profile(Profile, policy.ForProver(), purpose: AgentSessionPurpose.Prove);

        Assert.Equal(AgentSessionPurpose.Implementation, implementer.Purpose);
        Assert.Equal(AgentSessionPurpose.Review, reviewer.Purpose);
        Assert.Equal(AgentSessionPurpose.Prove, prover.Purpose);
        Assert.Equal(Profile, prover.Profile);

        var denied = prover.Argv.SkipWhile(a => a != "--disallowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Contains("Edit", denied);
        Assert.Contains("Write", denied);
        Assert.Equal("default", prover.Argv[prover.Argv.ToList().IndexOf("--permission-mode") + 1]);

        var implementerDenied = implementer.Argv.SkipWhile(a => a != "--disallowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain("Edit", implementerDenied);
    }

    [Fact]
    public void The_runtime_resolves_launch_profiles_per_purpose_with_no_fallback_between_them()
    {
        var policy = new ClaudeCodeToolPolicy();

        var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
        {
            Session = "aw-test-" + Guid.NewGuid().ToString("N")[..6],
            Profiles =
            [
                ClaudeCodeLaunch.Profile(Profile, policy, evidence: new ClaudeEvidenceOptions { HookExecutable = _hookExe, OfferProveRequest = true }),
                ClaudeCodeLaunch.Profile(Profile, policy.ForProver(), evidence: new ClaudeEvidenceOptions { HookExecutable = _hookExe, OfferProve = true }, purpose: AgentSessionPurpose.Prove),
            ],
        });

        Assert.True(runtime.CanSignal(Profile, AgentSessionPurpose.Implementation));
        Assert.True(runtime.CanSignal(Profile, AgentSessionPurpose.Prove));
        Assert.False(runtime.CanSignal(Profile, AgentSessionPurpose.Review));   // no reviewer launch was configured, and none is borrowed
    }
}
