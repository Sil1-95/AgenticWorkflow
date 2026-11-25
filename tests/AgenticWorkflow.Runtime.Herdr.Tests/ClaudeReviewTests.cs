using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The Claude Code translation of the independent REVIEW, offline: the typed <c>submit_review</c> tool and its closed, bounded schema, the hook that records the report and
/// stops the reviewer's turn, the additive <c>request_review</c> that never stops the implementer's, the reviewer's read-only launch policy, and the per-purpose launch
/// configuration. No Claude, no Herdr, no model.
/// </summary>
public sealed class ClaudeReviewTests : IDisposable
{
    private static readonly ProfileId Profile = new("claude-local");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-review-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _hookExe;

    public ClaudeReviewTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _hookExe = Path.Combine(_root, "hook.exe");
        File.WriteAllText(_hookExe, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ClaudeEvidenceChannel Channel(bool request = false, bool review = true)
        => ClaudeEvidenceChannel.TryCreate(
               new ClaudeEvidenceOptions { HookExecutable = _hookExe, EvidenceRoot = Path.Combine(_root, "evidence"), OfferReviewRequest = request, OfferReview = review },
               _workspace,
               [],
               sessionId: null,
               clarification: false,
               what: false,
               how: false,
               reclassification: false,
               reviewRequest: request,
               review: review,
               out var failure)
           ?? throw new InvalidOperationException(failure);

    private sealed class NoState : IImplementationStateProvider
    {
        public Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("a review must never capture the workspace");
    }

    private static Dictionary<string, object?> Finding(string title = "Empty input returns null")
        => new()
        {
            ["title"] = title,
            ["evidence"] = "Importer.cs:12 returns null for an empty file.",
            ["impact"] = "Callers crash.",
            ["recommendation"] = "Return an empty list.",
            ["location"] = "Importer.cs:12",
        };

    private static Dictionary<string, object?> Changes(params Dictionary<string, object?>[] findings)
        => new() { ["verdict"] = "CHANGES_REQUIRED", ["summary"] = "One real defect.", ["findings"] = findings.Length == 0 ? new[] { Finding() } : findings };

    private static Dictionary<string, object?> Pass()
        => new() { ["verdict"] = "PASS", ["summary"] = "The implementation does what the task asks.", ["findings"] = Array.Empty<object>(), ["notes"] = "A non-blocking remark." };

    private static string Payload(string session, Dictionary<string, object?> input, string tool = ClaudeEvidenceFormat.ReviewToolFullName, string id = "toolu_rv")
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

    private static JsonDocument Reply(string request, ClaudeClarificationServer.Tools tools, ClaudeClarificationServer.ReviewCalls? calls = null)
    {
        bool raised = false, proposed = false, how = false, reported = false;
        return JsonDocument.Parse(ClaudeClarificationServer.Handle(request, tools, ref raised, ref proposed, ref how, ref reported, calls)!.ToJsonString());
    }

    private static string Call(string tool, Dictionary<string, object?> arguments)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?> { ["name"] = tool, ["arguments"] = arguments },
        });

    private static bool IsError(JsonDocument reply) => reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean();

    // ---- the MCP server ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_reviewer_is_offered_exactly_one_workflow_tool_with_a_closed_bounded_schema()
    {
        using var list = Reply("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ClaudeClarificationServer.Tools.Review);

        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal(AgentReviewSubmission.ToolName, tool.GetProperty("name").GetString());

        var schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["verdict", "summary"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["PASS", "CHANGES_REQUIRED"], schema.GetProperty("properties").GetProperty("verdict").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(ReviewReport.MaxFindings, schema.GetProperty("properties").GetProperty("findings").GetProperty("maxItems").GetInt32());
        Assert.False(schema.GetProperty("properties").GetProperty("findings").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void The_implementer_is_offered_the_request_but_never_the_review_and_the_reviewer_never_the_request()
    {
        const string list = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        string[] Names(JsonDocument d) => d.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

        using var implementer = Reply(list, ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.ReviewRequest);
        Assert.Contains(AgentReviewRequestSignal.ToolName, Names(implementer));
        Assert.DoesNotContain(AgentReviewSubmission.ToolName, Names(implementer));

        using var reviewer = Reply(list, ClaudeClarificationServer.Tools.Review);
        Assert.DoesNotContain(AgentReviewRequestSignal.ToolName, Names(reviewer));
        Assert.DoesNotContain(AgentClarificationSignal.ToolName, Names(reviewer));
        Assert.DoesNotContain(AgentHowSignal.ToolName, Names(reviewer));
    }

    [Fact]
    public async Task A_reviewer_that_must_be_asked_again_can_submit_in_the_next_turn_and_never_twice_in_one_turn()
    {
        // Same latch as the prover's: a report accepted by the tool but never recorded (nothing reached the controller) must not make the controller's reminder unanswerable.
        using var channel = Channel();
        await using var server = new ServerPipe(channel.Directory);

        var turn1 = DateTime.UtcNow.Ticks;
        channel.MarkSubmissionTurn(turn1);

        using var first = await server.CallAsync(Call(AgentReviewSubmission.ToolName, Pass()));
        Assert.False(IsError(first));

        using var sameTurn = await server.CallAsync(Call(AgentReviewSubmission.ToolName, Changes()));
        Assert.True(IsError(sameTurn));

        await Task.Delay(50);
        var turn2 = DateTime.UtcNow.Ticks;
        channel.MarkSubmissionTurn(turn2);                                                          // the controller's reminder, in the same reviewer conversation

        using var second = await server.CallAsync(Call(AgentReviewSubmission.ToolName, Changes()));
        Assert.False(IsError(second));
        var (_, hook) = await RunHook(channel, Payload(channel.SessionId, Changes(), id: "toolu_2"));
        Assert.Contains("\"continue\":false", hook);

        var recorded = Assert.Single(channel.CollectReviewSubmissions(turn2, null));
        Assert.Equal("CHANGES_REQUIRED", recorded.Verdict);

        using var third = await server.CallAsync(Call(AgentReviewSubmission.ToolName, Pass()));
        Assert.True(IsError(third));
    }

    [Fact]
    public void A_well_formed_review_is_accepted_once_and_a_second_in_the_same_turn_is_refused()
    {
        var calls = new ClaudeClarificationServer.ReviewCalls();

        using var first = Reply(Call(AgentReviewSubmission.ToolName, Pass()), ClaudeClarificationServer.Tools.Review, calls);
        Assert.False(IsError(first));
        Assert.Contains("Stop now", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        using var second = Reply(Call(AgentReviewSubmission.ToolName, Changes()), ClaudeClarificationServer.Tools.Review, calls);
        Assert.True(IsError(second));
    }

    [Fact]
    public void A_request_for_review_never_tells_the_agent_to_stop_and_is_accepted_once()
    {
        var calls = new ClaudeClarificationServer.ReviewCalls();
        var input = new Dictionary<string, object?> { ["reason"] = "A subtle change to shared behaviour." };

        using var first = Reply(Call(AgentReviewRequestSignal.ToolName, input), ClaudeClarificationServer.Tools.ReviewRequest, calls);
        Assert.False(IsError(first));
        Assert.DoesNotContain("Stop now", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains("Carry on", first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        using var second = Reply(Call(AgentReviewRequestSignal.ToolName, input), ClaudeClarificationServer.Tools.ReviewRequest, calls);
        Assert.True(IsError(second));
    }

    [Fact]
    public void A_malformed_oversized_or_unknown_field_review_is_an_error_and_does_not_latch()
    {
        var calls = new ClaudeClarificationServer.ReviewCalls();
        var tools = ClaudeClarificationServer.Tools.Review;

        var badVerdict = Pass();
        badVerdict["verdict"] = "LOOKS_FINE";
        using var verdict = Reply(Call(AgentReviewSubmission.ToolName, badVerdict), tools, calls);
        Assert.True(IsError(verdict));
        Assert.Contains("exactly 'PASS' or 'CHANGES_REQUIRED'", verdict.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        var oversized = Pass();
        oversized["summary"] = new string('s', ReviewReport.MaxSummaryLength + 1);
        using var big = Reply(Call(AgentReviewSubmission.ToolName, oversized), tools, calls);
        Assert.True(IsError(big));

        var severity = Changes();
        severity["severity"] = "high";
        using var unknown = Reply(Call(AgentReviewSubmission.ToolName, severity), tools, calls);
        Assert.True(IsError(unknown));

        Assert.False(calls.Submitted);
    }

    [Fact]
    public void CHANGES_REQUIRED_needs_a_finding_and_PASS_takes_none_and_both_are_enforced_at_the_tool()
    {
        var calls = new ClaudeClarificationServer.ReviewCalls();
        var tools = ClaudeClarificationServer.Tools.Review;

        var empty = Changes();
        empty["findings"] = Array.Empty<object>();
        using var noFindings = Reply(Call(AgentReviewSubmission.ToolName, empty), tools, calls);
        Assert.True(IsError(noFindings));

        var withFinding = Pass();
        withFinding["findings"] = new[] { Finding() };
        using var passWithFinding = Reply(Call(AgentReviewSubmission.ToolName, withFinding), tools, calls);
        Assert.True(IsError(passWithFinding));

        var tooMany = Changes(Enumerable.Range(0, ReviewReport.MaxFindings + 1).Select(i => Finding($"t{i}")).ToArray());
        using var many = Reply(Call(AgentReviewSubmission.ToolName, tooMany), tools, calls);
        Assert.True(IsError(many));

        Assert.False(calls.Submitted);
    }

    // ---- the hook --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hook_records_the_structured_review_for_its_own_session_and_stops_the_reviewers_turn()
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(channel.SessionId, Changes()));

        Assert.Equal(0, code);
        Assert.Contains("\"continue\":false", output);
        var submission = Assert.Single(channel.CollectReviewSubmissions(0, null));
        Assert.Equal("CHANGES_REQUIRED", submission.Verdict);
        Assert.Equal("One real defect.", submission.Summary);
        var finding = Assert.Single(submission.Findings);
        Assert.Equal("Empty input returns null", finding.Title);
        Assert.Equal("Importer.cs:12", finding.Location);
        Assert.Empty(channel.CollectReviewRequests(0, null));   // a review is never read as a request
    }

    [Fact]
    public async Task A_review_carries_no_transcript_or_hidden_reasoning_in_the_record_the_hook_writes()
    {
        using var channel = Channel();

        await RunHook(channel, Payload(channel.SessionId, Pass()));

        var json = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(channel.Directory, "e-*.json")));
        Assert.Contains("\"kind\":\"review_submission\"", json);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thinking", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_review_request_hook_records_without_stopping_the_turn()
    {
        using var channel = Channel(request: true, review: false);

        var (code, output) = await RunHook(channel, Payload(channel.SessionId, new Dictionary<string, object?> { ["reason"] = "Worth a second look." }, ClaudeEvidenceFormat.ReviewRequestToolFullName));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);   // the agent's turn goes on: a request is additive
        Assert.Equal("Worth a second look.", Assert.Single(channel.CollectReviewRequests(0, null)).Reason);
        Assert.Empty(channel.CollectReviewSubmissions(0, null));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("")]
    public async Task A_review_from_another_session_is_never_recorded_and_stops_nothing(string session)
    {
        using var channel = Channel();

        var (code, output) = await RunHook(channel, Payload(session, Pass()));

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, output);
        Assert.Empty(channel.CollectReviewSubmissions(0, null));
    }

    [Fact]
    public async Task A_malformed_review_and_a_session_that_was_not_given_the_tool_record_and_stop_nothing()
    {
        using var reviewer = Channel();
        var bad = Pass();
        bad["verdict"] = "MAYBE";

        var (_, malformed) = await RunHook(reviewer, Payload(reviewer.SessionId, bad));
        Assert.Equal(string.Empty, malformed);
        Assert.Empty(reviewer.CollectReviewSubmissions(0, null));

        using var implementer = Channel(request: true, review: false);
        var (_, notOffered) = await RunHook(implementer, Payload(implementer.SessionId, Pass(), id: "toolu_2"));
        Assert.Equal(string.Empty, notOffered);
        Assert.Empty(implementer.CollectReviewSubmissions(0, null));
    }

    // ---- read-only launch and per-purpose configuration ----------------------------------------------------------------------------

    [Fact]
    public void The_reviewer_policy_denies_every_file_editing_tool_and_never_runs_in_the_edit_accepting_mode()
    {
        var implementer = new ClaudeCodeToolPolicy();
        var reviewer = implementer.ForReviewer();

        Assert.Equal("acceptEdits", implementer.PermissionMode);
        Assert.Equal("default", reviewer.PermissionMode);
        foreach (var tool in new[] { "Edit", "Write", "MultiEdit", "NotebookEdit" })
        {
            Assert.Contains(tool, reviewer.DeniedTools);
            Assert.DoesNotContain(tool, implementer.DeniedTools);   // the implementer's own policy is untouched
        }

        Assert.All(implementer.DeniedTools, tool => Assert.Contains(tool, reviewer.DeniedTools));   // every baseline denial stays
        Assert.Equal(implementer.AllowedTools, reviewer.AllowedTools);                              // the same read-only inspection and toolchain checks
    }

    [Fact]
    public void The_reviewer_launch_is_a_separate_purpose_with_the_edit_tools_on_its_deny_list_and_the_implementers_launch_is_unchanged()
    {
        var policy = new ClaudeCodeToolPolicy();

        var implementer = ClaudeCodeLaunch.Profile(Profile, policy);
        var reviewer = ClaudeCodeLaunch.Profile(Profile, policy.ForReviewer(), purpose: AgentSessionPurpose.Review);

        Assert.Equal(AgentSessionPurpose.Implementation, implementer.Purpose);
        Assert.Equal(AgentSessionPurpose.Review, reviewer.Purpose);
        Assert.Equal(Profile, reviewer.Profile);   // the same profile, when the project bound it explicitly, is still a different launch

        var denied = reviewer.Argv.SkipWhile(a => a != "--disallowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Contains("Edit", denied);
        Assert.Contains("Write", denied);
        Assert.Equal("default", reviewer.Argv[reviewer.Argv.ToList().IndexOf("--permission-mode") + 1]);

        var implementerDenied = implementer.Argv.SkipWhile(a => a != "--disallowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain("Edit", implementerDenied);
        Assert.Equal("acceptEdits", implementer.Argv[implementer.Argv.ToList().IndexOf("--permission-mode") + 1]);
    }
}
