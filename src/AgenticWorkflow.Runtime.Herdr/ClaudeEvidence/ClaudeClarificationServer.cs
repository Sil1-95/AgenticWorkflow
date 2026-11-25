using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;

namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

/// <summary>
/// The body of the tiny MCP (stdio) server Claude Code starts for a session that may ask the workflow a question or propose its WHAT or HOW. It exposes one tool per
/// capability the session was given (<see cref="AgentClarificationSignal.ToolName"/>, <see cref="AgentWhatSignal.ToolName"/>, <see cref="AgentHowSignal.ToolName"/>), and holds no workflow state: it
/// only checks that the call is well-formed and bounded (so the agent can correct a malformed one) and tells the agent to stop. It records nothing. The durable record is written by the <c>PostToolUse</c> hook
/// (<see cref="ClaudeHookHandler"/>), because only the harness's hook payload carries the session id that proves WHICH session asked.
/// <para>
/// It is the Claude-specific translation of the provider-neutral clarification signal: MCP JSON, tool names and hook JSON stop here and never reach
/// Application or Core. One call per tool per server process (that is, per agent session): a second is refused, never merged.
/// </para>
/// </summary>
public static class ClaudeClarificationServer
{
    /// <summary>The first argument of the hook executable when it is started as this server instead of as a hook.</summary>
    public const string ModeArgument = "--mcp";

    private const string ProtocolFallback = "2025-06-18";

    // The root agent's signal tools (clarification, WHAT, HOW, reclassification, review request, prove request) are each fully described where they are listed (their tool description
    // says when to use them and when not to), and the direct-path prompt says when a signal is worth raising at all. These instructions therefore only orient: they are loaded
    // into the agent's context for every call of the session, so they say once what the tools are and who owns what happens after one, and repeat nothing.
    private const string SignalInstructions =
        "These tools are signals to the workflow controller; each tool's description says when to use it. Clear, fully specified work needs none. "
        + "When a tool's result says to stop, end your turn: the controller resumes your conversation once a person has decided.";

    private const Tools SignalTools = Tools.Clarification | Tools.What | Tools.How | Tools.Reclassification | Tools.ReviewRequest | Tools.ProveRequest;

    private const string ReviewInstructions =
        "You are an independent reviewer. Read the implementation and decide; then call submit_review exactly once with your verdict (PASS or CHANGES_REQUIRED), a short "
        + "summary and, for CHANGES_REQUIRED, concrete findings with evidence. Nothing else records your review. You cannot and must not change any file.";

    private const string ProveInstructions =
        "You are an independent prover. Establish each claim of the Proof Contract from evidence, then call submit_proof exactly once with one result per claim (pass, fail or "
        + "inconclusive) citing the evidence behind each. Nothing else records your proof. You cannot and must not change any file.";

    /// <summary>
    /// Accepts one structured report (a review or a proof) per controller turn. The controller may refuse a well-formed report for a reason only it can judge (a proof citing
    /// evidence that does not exist) and ask for one correction in the same conversation, so the first report must not close the tool for good. A second report in the same turn
    /// is still refused, and the controller records at most one whatever this tool accepted. Without a turn marker the latch never reopens.
    /// </summary>
    internal sealed class SubmissionLatch(Func<long?>? latestTurnStart = null)
    {
        private long? _acceptedAt;

        public bool Taken => _acceptedAt is not null;

        /// <summary>True, and the latch closes, unless a report was already accepted and no turn has been sent since.</summary>
        public bool TryAccept()
        {
            if (_acceptedAt is { } accepted && !(latestTurnStart?.Invoke() is { } started && started > accepted))
                return false;

            _acceptedAt = DateTime.UtcNow.Ticks;

            return true;
        }
    }

    /// <summary>What one server process (one agent session) already did with the PROVE tools: the contract is accepted once, never merged; a proof once per turn.</summary>
    internal sealed class ProveCalls(Func<long?>? latestTurnStart = null)
    {
        private readonly SubmissionLatch _proof = new(latestTurnStart);

        public bool Requested { get; set; }

        public bool Submitted => _proof.Taken;

        public bool TryAcceptProof() => _proof.TryAccept();
    }

    /// <summary>What one server process (one agent session) already did with the review tools: the request is accepted once, never merged; a report once per turn.</summary>
    internal sealed class ReviewCalls(Func<long?>? latestTurnStart = null)
    {
        private readonly SubmissionLatch _report = new(latestTurnStart);

        public bool Requested { get; set; }

        public bool Submitted => _report.Taken;

        public bool TryAcceptReport() => _report.TryAccept();
    }

    /// <summary>The workflow tools a session was given.</summary>
    [Flags]
    internal enum Tools
    {
        None = 0,
        Clarification = 1,
        What = 2,
        How = 4,
        Reclassification = 8,
        ReviewRequest = 16,
        Review = 32,
        ProveRequest = 64,
        Prove = 128,
    }

    /// <summary>Which tools the session behind <paramref name="channelDirectory"/> was given, from the channel's own configuration. Unreadable means clarification only, the original contract.</summary>
    private static Tools ToolsOf(string? channelDirectory)
    {
        if (channelDirectory is null)
            return Tools.Clarification;

        try
        {
            var config = JsonSerializer.Deserialize<ChannelConfig>(File.ReadAllText(Path.Combine(channelDirectory, ClaudeEvidenceFormat.ConfigFile)), ClaudeEvidenceFormat.Json);

            return config is null
                ? Tools.Clarification
                : (config.Clarification ? Tools.Clarification : Tools.None) | (config.What ? Tools.What : Tools.None) | (config.How ? Tools.How : Tools.None) | (config.Reclassification ? Tools.Reclassification : Tools.None) | (config.ReviewRequest ? Tools.ReviewRequest : Tools.None) | (config.Review ? Tools.Review : Tools.None) | (config.ProveRequest ? Tools.ProveRequest : Tools.None) | (config.Prove ? Tools.Prove : Tools.None);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Tools.Clarification;
        }
    }

    /// <summary>Serves the tools the channel at <paramref name="channelDirectory"/> was configured with (clarification alone when it cannot be read).</summary>
    /// <returns>0 when the client closed the stream.</returns>
    public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, string? channelDirectory, CancellationToken cancellationToken = default)
        => await RunAsync(stdin, stdout, ToolsOf(channelDirectory), cancellationToken, channelDirectory is null ? null : () => LatestTurnStart(channelDirectory)).ConfigureAwait(false);

    /// <returns>0 when the client closed the stream.</returns>
    public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, CancellationToken cancellationToken = default)
        => await RunAsync(stdin, stdout, Tools.Clarification, cancellationToken).ConfigureAwait(false);

    /// <summary>When the controller last sent the session a turn (the channel's turn records), or null when it recorded none or the channel cannot be read.</summary>
    private static long? LatestTurnStart(string channelDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(channelDirectory, ClaudeEvidenceFormat.TurnPrefix + "*")
                .Select(path => ClaudeEvidenceFormat.TicksOf(Path.GetFileName(path), ClaudeEvidenceFormat.TurnPrefix))
                .OfType<long>()
                .Select(ticks => (long?)ticks)
                .Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task<int> RunAsync(TextReader stdin, TextWriter stdout, Tools tools, CancellationToken cancellationToken, Func<long?>? latestTurnStart = null)
    {
        var raised = false;
        var proposed = false;
        var proposedHow = false;
        var reported = false;
        var reviews = new ReviewCalls(latestTurnStart);
        var proves = new ProveCalls(latestTurnStart);

        while (await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var reply = Handle(line, tools, ref raised, ref proposed, ref proposedHow, ref reported, reviews, proves);

            if (reply is not null)
            {
                await stdout.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
                await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>The answer of a server that offers the clarification tool only (the original contract) to one JSON-RPC line, or null for a notification.</summary>
    internal static JsonObject? Handle(string line, ref bool raised)
    {
        var proposed = false;

        return Handle(line, Tools.Clarification, ref raised, ref proposed);
    }

    /// <summary>The answer of a server that offers the clarification and WHAT tools (the WHAT contract) to one JSON-RPC line, or null for a notification.</summary>
    internal static JsonObject? Handle(string line, Tools tools, ref bool raised, ref bool proposed)
    {
        var proposedHow = false;

        return Handle(line, tools, ref raised, ref proposed, ref proposedHow);
    }

    /// <summary>The answer of a server that offers the clarification, WHAT and HOW tools (the HOW contract) to one JSON-RPC line, or null for a notification.</summary>
    internal static JsonObject? Handle(string line, Tools tools, ref bool raised, ref bool proposed, ref bool proposedHow)
    {
        var reported = false;

        return Handle(line, tools, ref raised, ref proposed, ref proposedHow, ref reported);
    }

    /// <summary>The server's answer to one JSON-RPC line, or null for a notification (which is never answered).</summary>
    internal static JsonObject? Handle(string line, Tools tools, ref bool raised, ref bool proposed, ref bool proposedHow, ref bool reported, ReviewCalls? reviews = null, ProveCalls? proves = null)
    {
        reviews ??= new ReviewCalls();
        proves ??= new ProveCalls();

        JsonNode? message;

        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null; // not a message this server can answer; the client will time out its own request
        }

        if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method)
            return null;

        var id = request["id"]?.DeepClone();

        // A notification carries no id and gets no reply.
        if (id is null)
            return null;

        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? ProtocolFallback,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = ClaudeEvidenceFormat.ServerName, ["version"] = "1" },
                ["instructions"] = InstructionsOf(tools),
            }),
            "tools/list" => Result(id, new JsonObject { ["tools"] = ToolList(tools) }),
            "tools/call" => Result(id, Call(request["params"] as JsonObject, tools, ref raised, ref proposed, ref proposedHow, ref reported, reviews, proves)),
            "ping" => Result(id, new JsonObject()),
            _ => Error(id, -32601, $"method '{method}' is not supported"),
        };
    }

    /// <summary>The bounded text of a well-formed call, or the reason it is not one. Shared with the hook, which re-checks what it records.</summary>
    internal static bool TryRead(JsonElement input, out string question, out string reason, out string problem)
    {
        question = reason = problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = "the tool input must be an object with a 'question' and a 'reason'";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.Name is not ("question" or "reason"))
            {
                problem = $"unexpected field '{property.Name}'; only 'question' and 'reason' are accepted";
                return false;
            }
        }

        return TryField(input, "question", ClarificationRequest.MaxQuestionLength, out question, out problem)
               && TryField(input, "reason", ClarificationRequest.MaxReasonLength, out reason, out problem);
    }

    private static bool TryField(JsonElement input, string name, int maxLength, out string value, out string problem)
    {
        value = string.Empty;
        problem = string.Empty;

        if (!input.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            problem = $"'{name}' is required and must be non-blank text";
            return false;
        }

        var text = element.GetString()!.Trim();

        if (text.Length > maxLength)
        {
            problem = $"'{name}' must not exceed {maxLength} characters (was {text.Length}); say it more briefly";
            return false;
        }

        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            problem = $"'{name}' must not contain control characters";
            return false;
        }

        value = text;
        return true;
    }

    private static string InstructionsOf(Tools tools)
        => string.Join(
            ' ',
            new[]
            {
                (tools & SignalTools) != Tools.None ? SignalInstructions : null,
                tools.HasFlag(Tools.Review) ? ReviewInstructions : null,
                tools.HasFlag(Tools.Prove) ? ProveInstructions : null,
            }.OfType<string>());

    private static JsonArray ToolList(Tools tools)
    {
        var list = new JsonArray();

        if (tools.HasFlag(Tools.Clarification))
            list.Add(ToolDefinition());

        if (tools.HasFlag(Tools.What))
            list.Add(WhatToolDefinition());

        if (tools.HasFlag(Tools.How))
            list.Add(HowToolDefinition());

        if (tools.HasFlag(Tools.Reclassification))
            list.Add(ReclassificationToolDefinition());

        if (tools.HasFlag(Tools.ReviewRequest))
            list.Add(ReviewRequestToolDefinition());

        if (tools.HasFlag(Tools.Review))
            list.Add(ReviewToolDefinition());

        if (tools.HasFlag(Tools.ProveRequest))
            list.Add(ProveRequestToolDefinition());

        if (tools.HasFlag(Tools.Prove))
            list.Add(ProofToolDefinition());

        return list;
    }

    private static JsonObject Call(JsonObject? parameters, Tools tools, ref bool raised, ref bool proposed, ref bool proposedHow, ref bool reported, ReviewCalls reviews, ProveCalls proves)
    {
        var name = parameters?["name"]?.GetValue<string>();

        if (name == AgentReviewRequestSignal.ToolName && tools.HasFlag(Tools.ReviewRequest))
            return CallReviewRequest(parameters!, reviews);

        if (name == AgentReviewSubmission.ToolName && tools.HasFlag(Tools.Review))
            return CallReview(parameters!, reviews);

        if (name == AgentProveRequestSignal.ToolName && tools.HasFlag(Tools.ProveRequest))
            return CallProveRequest(parameters!, proves);

        if (name == AgentProofSubmission.ToolName && tools.HasFlag(Tools.Prove))
            return CallProof(parameters!, proves);

        if (name == AgentWhatSignal.ToolName && tools.HasFlag(Tools.What))
            return CallWhat(parameters!, ref proposed);

        if (name == AgentHowSignal.ToolName && tools.HasFlag(Tools.How))
            return CallHow(parameters!, ref proposedHow);

        if (name == AgentReclassificationSignal.ToolName && tools.HasFlag(Tools.Reclassification))
            return CallReclassification(parameters!, ref reported);

        if (name != AgentClarificationSignal.ToolName || !tools.HasFlag(Tools.Clarification))
            return Text("unknown tool", isError: true);

        using var document = JsonDocument.Parse((parameters!["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryRead(document.RootElement, out _, out _, out var problem))
            return Text($"The question was not sent: {problem}. Correct it and call the tool again.", isError: true);

        if (raised)
            return Text("A question was already raised in this session; ask only one and wait for the answer.", isError: true);

        raised = true;

        return Text("Your question was sent to the person who owns this task. Stop now: end your turn without doing anything further. Your conversation will be resumed with their answer.", isError: false);
    }

    private static JsonObject CallWhat(JsonObject parameters, ref bool proposed)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadWhat(document.RootElement, out _, out var problem))
            return Text($"The WHAT was not sent: {problem}. Correct it and call the tool again.", isError: true);

        if (proposed)
            return Text("A WHAT was already proposed in this session; propose only one and wait for the decision.", isError: true);

        proposed = true;

        return Text("Your WHAT proposal was sent to the person who owns this task. Stop now: end your turn without doing anything further and do not change any file. Your conversation will be resumed with their decision.", isError: false);
    }

    private static JsonObject CallHow(JsonObject parameters, ref bool proposed)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadHow(document.RootElement, out _, out var problem))
            return Text($"The HOW was not sent: {problem}. Correct it and call the tool again.", isError: true);

        if (proposed)
            return Text("A HOW was already proposed in this session; propose only one and wait for the decision.", isError: true);

        proposed = true;

        return Text("Your HOW proposal was sent to the person who owns this task. Stop now: end your turn without doing anything further and do not change any file. Your conversation will be resumed with their decision.", isError: false);
    }

    private static JsonObject CallReclassification(JsonObject parameters, ref bool reported)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadReclassification(document.RootElement, out _, out var problem))
            return Text($"The report was not sent: {problem}. Correct it and call the tool again.", isError: true);

        if (reported)
            return Text("New evidence was already reported in this session; report only once and wait for what to do next.", isError: true);

        reported = true;

        return Text("Your report was sent to the person who owns this task and recorded. Stop now: end your turn without doing anything further and do not change any file. Your conversation will be resumed with what to do next.", isError: false);
    }

    private static JsonObject CallReviewRequest(JsonObject parameters, ReviewCalls reviews)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadReviewRequest(document.RootElement, out _, out var problem))
            return Text($"The review request was not sent: {problem}. Correct it and call the tool again.", isError: true);

        if (reviews.Requested)
            return Text("An independent review was already requested in this session; once is enough.", isError: true);

        reviews.Requested = true;

        return Text("Recorded: an independent review of your verified work will happen before this task completes. Carry on with your work as normal; this changes nothing else.", isError: false);
    }

    private static JsonObject CallReview(JsonObject parameters, ReviewCalls reviews)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadReview(document.RootElement, out _, out var problem))
            return Text($"The review was not recorded: {problem}. Correct it and call the tool again.", isError: true);

        if (!reviews.TryAcceptReport())
            return Text("A review was already submitted in this turn; submit only one.", isError: true);

        return Text("Your review was recorded. Stop now: end your turn without doing anything further.", isError: false);
    }

    /// <summary>The bounded reason of a well-formed review request, or why it is not one. Bounds are Core's own. Shared with the hook.</summary>
    internal static bool TryReadReviewRequest(JsonElement input, out string reason, out string problem)
    {
        reason = problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = "the tool input must be an object with a 'reason'";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.Name != "reason")
            {
                problem = $"unexpected field '{property.Name}'; only 'reason' is accepted";
                return false;
            }
        }

        if (!TryField(input, "reason", ReviewRequest.MaxReasonLength, out reason, out problem))
            return false;

        if (ReviewRequest.TextProblem(reason) is { } textProblem)
        {
            problem = textProblem;
            return false;
        }

        return true;
    }

    private static readonly string[] ReviewFindingFields = ["title", "evidence", "impact", "recommendation", "location"];

    /// <summary>
    /// The structured report of a well-formed review call, or the reason it is not one. The verdict is a closed choice and every bound is Core's own
    /// (<see cref="ReviewReport"/>), so a report that is recorded can always be turned into a review. Nothing is parsed out of prose. Shared with the hook.
    /// </summary>
    internal static bool TryReadReview(JsonElement input, out AgentReviewSubmission submission, out string problem)
    {
        submission = new AgentReviewSubmission(string.Empty, string.Empty, [], null);
        problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = "the tool input must be an object with a 'verdict', a 'summary', 'findings' and optional 'notes'";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.Name is not ("verdict" or "summary" or "findings" or "notes"))
            {
                problem = $"unexpected field '{property.Name}'; only 'verdict', 'summary', 'findings' and 'notes' are accepted";
                return false;
            }
        }

        if (!TryField(input, "verdict", 40, out var verdict, out problem) || !TryField(input, "summary", ReviewReport.MaxSummaryLength, out var summary, out problem))
            return false;

        verdict = verdict.ToUpperInvariant();
        if (verdict is not ("PASS" or "CHANGES_REQUIRED"))
        {
            problem = "'verdict' must be exactly 'PASS' or 'CHANGES_REQUIRED'";
            return false;
        }

        string? notes = null;
        if (input.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind != JsonValueKind.Null)
        {
            if (notesElement.ValueKind != JsonValueKind.String)
            {
                problem = "'notes' must be text";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(notesElement.GetString()))
            {
                if (!TryField(input, "notes", ReviewReport.MaxNotesLength, out var noteText, out problem))
                    return false;

                notes = noteText;
            }
        }

        var findings = new List<AgentReviewFinding>();
        if (input.TryGetProperty("findings", out var findingsElement) && findingsElement.ValueKind != JsonValueKind.Null)
        {
            if (findingsElement.ValueKind != JsonValueKind.Array)
            {
                problem = "'findings' must be a list";
                return false;
            }

            foreach (var element in findingsElement.EnumerateArray())
            {
                if (!TryReadSections(element, ReviewFindingFields, ["location"], "finding", out var values, out problem))
                    return false;

                findings.Add(new AgentReviewFinding(values["title"]!, values["evidence"]!, values["impact"]!, values["recommendation"]!, values["location"]));
            }
        }

        try
        {
            _ = new ReviewReport(
                verdict == "PASS" ? ReviewVerdict.Pass : ReviewVerdict.ChangesRequired,
                summary,
                findings.Select(f => new ReviewFinding(f.Title, f.Evidence, f.Impact, f.Recommendation, f.Location)),
                notes);
        }
        catch (Exception e) when (e is ArgumentException or DomainException)
        {
            problem = e.Message.Split(" (Parameter", 2)[0];
            return false;
        }

        submission = new AgentReviewSubmission(verdict, summary, findings, notes);
        return true;
    }

    private static JsonObject CallProveRequest(JsonObject parameters, ProveCalls proves)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadProveRequest(document.RootElement, out _, out var problem))
            return Text($"The Proof Contract was not recorded: {problem}. Correct it and call the tool again.", isError: true);

        if (proves.Requested)
            return Text("A Proof Contract was already submitted in this session; a contract is immutable once recorded.", isError: true);

        proves.Requested = true;

        return Text("Recorded: your Proof Contract will be judged by an independent prover after your work verifies. Carry on with your work as normal; this changes nothing else.", isError: false);
    }

    private static JsonObject CallProof(JsonObject parameters, ProveCalls proves)
    {
        using var document = JsonDocument.Parse((parameters["arguments"] ?? new JsonObject()).ToJsonString());

        if (!TryReadProof(document.RootElement, out _, out var problem))
            return Text($"The proof was not recorded: {problem}. Correct it and call the tool again.", isError: true);

        if (!proves.TryAcceptProof())
            return Text("A proof was already submitted in this turn; submit only one.", isError: true);

        return Text("Your proof was recorded. Stop now: end your turn without doing anything further.", isError: false);
    }

    private static readonly string[] ProofContractFields = ["rationale", "claims"];

    private static readonly string[] ProofClaimFields = ["id", "statement", "acceptance_criterion", "evidence"];

    /// <summary>
    /// The structured Proof Contract of a well-formed contract call, or the reason it is not one. Every bound is Core's own (<see cref="ProofContract.Problem"/>), the
    /// evidence of a claim is a list of closed-vocabulary strings (<c>verification:&lt;check&gt;</c>, <c>review:current-pass</c>, <c>observation:&lt;description&gt;</c>), and a contract that is
    /// recorded can always be turned into a <see cref="ProofContract"/>. Nothing is parsed out of prose. Shared with the hook.
    /// </summary>
    internal static bool TryReadProveRequest(JsonElement input, out AgentProveRequestSignal signal, out string problem)
    {
        signal = new AgentProveRequestSignal(string.Empty, []);
        problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = "the tool input must be an object with a 'rationale' and 'claims'";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (!ProofContractFields.Contains(property.Name))
            {
                problem = $"unexpected field '{property.Name}'; only 'rationale' and 'claims' are accepted";
                return false;
            }
        }

        if (!TryField(input, "rationale", ProofContract.MaxRationaleLength, out var rationale, out problem))
            return false;

        if (!input.TryGetProperty("claims", out var claimsElement) || claimsElement.ValueKind != JsonValueKind.Array)
        {
            problem = "'claims' is required and must be a list";
            return false;
        }

        var claims = new List<AgentProofClaim>();

        foreach (var element in claimsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                problem = "every claim must be an object with 'id', 'statement', 'acceptance_criterion' and 'evidence'";
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!ProofClaimFields.Contains(property.Name))
                {
                    problem = $"unexpected claim field '{property.Name}'; only 'id', 'statement', 'acceptance_criterion' and 'evidence' are accepted";
                    return false;
                }
            }

            if (!TryField(element, "id", ProofClaim.MaxIdLength, out var id, out problem)
                || !TryField(element, "statement", ProofClaim.MaxStatementLength, out var statement, out problem)
                || !TryField(element, "acceptance_criterion", ProofClaim.MaxAcceptanceCriterionLength, out var criterion, out problem))
            {
                return false;
            }

            if (!element.TryGetProperty("evidence", out var evidenceElement) || evidenceElement.ValueKind != JsonValueKind.Array)
            {
                problem = $"claim '{id}': 'evidence' is required and must be a list";
                return false;
            }

            var evidence = new List<AgentProofEvidenceRequirement>();

            foreach (var item in evidenceElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    problem = $"claim '{id}': every evidence entry must be non-blank text like 'verification:<check>', 'review:current-pass' or 'observation:<description>'";
                    return false;
                }

                var parts = item.GetString()!.Trim().Split(':', 2);
                evidence.Add(new AgentProofEvidenceRequirement(parts[0].Trim().ToLowerInvariant(), parts.Length == 2 ? parts[1].Trim() : null));
            }

            claims.Add(new AgentProofClaim(id, statement, evidence, criterion));
        }

        try
        {
            var built = claims.Select(c => new ProofClaim(
                c.Id,
                c.Statement,
                c.Evidence.Select(e => ProofEvidenceRequirement.Parse(e.Kind, e.Subject)),
                c.AcceptanceCriterion)).ToList();

            if (ProofContract.Problem(rationale, built) is { } contractProblem)
            {
                problem = contractProblem;
                return false;
            }
        }
        catch (Exception e) when (e is ArgumentException or DomainException)
        {
            problem = e.Message.Split(" (Parameter", 2)[0];
            return false;
        }

        signal = new AgentProveRequestSignal(rationale, claims);
        return true;
    }

    private static readonly string[] ProofFields = ["summary", "claims"];

    private static readonly string[] ProofClaimResultFields = ["claim_id", "verdict", "evidence", "summary"];

    /// <summary>
    /// The structured proof of a well-formed proof call, or the reason it is not one. The verdict of each claim is a closed choice, every evidence reference is typed
    /// (<c>verification:&lt;check&gt;</c>, <c>review:&lt;id&gt;</c>, <c>command:&lt;n&gt;</c>) and every bound is Core's own (<see cref="ProofReport"/>), so a proof that is recorded can always be turned
    /// into a <see cref="ProofReport"/>. Whether the references resolve is the controller's check against the state, not this one's. Nothing is parsed out of prose. Shared with the hook.
    /// </summary>
    internal static bool TryReadProof(JsonElement input, out AgentProofSubmission submission, out string problem)
    {
        submission = new AgentProofSubmission(string.Empty, []);
        problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = "the tool input must be an object with a 'summary' and 'claims'";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (!ProofFields.Contains(property.Name))
            {
                problem = $"unexpected field '{property.Name}'; only 'summary' and 'claims' are accepted";
                return false;
            }
        }

        if (!TryField(input, "summary", ProofReport.MaxSummaryLength, out var summary, out problem))
            return false;

        if (!input.TryGetProperty("claims", out var claimsElement) || claimsElement.ValueKind != JsonValueKind.Array)
        {
            problem = "'claims' is required and must be a list with one result per claim of the contract";
            return false;
        }

        var results = new List<AgentProofClaimResult>();

        foreach (var element in claimsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                problem = "every claim result must be an object with 'claim_id', 'verdict', 'evidence' and 'summary'";
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!ProofClaimResultFields.Contains(property.Name))
                {
                    problem = $"unexpected claim result field '{property.Name}'; only 'claim_id', 'verdict', 'evidence' and 'summary' are accepted";
                    return false;
                }
            }

            if (!TryField(element, "claim_id", ProofClaim.MaxIdLength, out var claimId, out problem)
                || !TryField(element, "verdict", 20, out var verdict, out problem)
                || !TryField(element, "summary", ProofClaimResult.MaxSummaryLength, out var claimSummary, out problem))
            {
                return false;
            }

            verdict = verdict.ToLowerInvariant();
            if (verdict is not ("pass" or "fail" or "inconclusive"))
            {
                problem = $"claim '{claimId}': 'verdict' must be exactly 'pass', 'fail' or 'inconclusive'";
                return false;
            }

            var evidence = new List<string>();

            if (element.TryGetProperty("evidence", out var evidenceElement) && evidenceElement.ValueKind != JsonValueKind.Null)
            {
                if (evidenceElement.ValueKind != JsonValueKind.Array)
                {
                    problem = $"claim '{claimId}': 'evidence' must be a list";
                    return false;
                }

                foreach (var item in evidenceElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        problem = $"claim '{claimId}': every evidence reference must be text like 'verification:<check>', 'review:<id>' or 'command:<n>'";
                        return false;
                    }

                    evidence.Add(item.GetString()!.Trim());
                }
            }

            results.Add(new AgentProofClaimResult(claimId, verdict, evidence, claimSummary));
        }

        try
        {
            _ = new ProofReport(
                summary,
                results.Select(r => new ProofClaimResult(r.ClaimId, ProofReport.ParseVerdict(r.Verdict), r.Evidence.Select(ProofEvidenceRef.Parse), r.Summary)));
        }
        catch (Exception e) when (e is ArgumentException or DomainException)
        {
            problem = e.Message.Split(" (Parameter", 2)[0];
            return false;
        }

        submission = new AgentProofSubmission(summary, results);
        return true;
    }

    private static JsonObject ProveRequestToolDefinition()
    {
        JsonObject Field(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

        return new JsonObject
        {
            ["name"] = AgentProveRequestSignal.ToolName,
            ["description"] =
                "Submit the Proof Contract an independent prover will judge after your work verifies: the consequential claims that must be true for this work to be correct, each with "
                + "an acceptance criterion and the evidence it needs. Submit it once; it is immutable for the current task semantics. It does not stop your turn, changes nothing else and cannot be withdrawn.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["rationale"] = Field("Why these claims are the ones that matter for this task.", ProofContract.MaxRationaleLength),
                    ["claims"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = $"1 to {ProofContract.MaxClaims} claims.",
                        ["minItems"] = 1,
                        ["maxItems"] = ProofContract.MaxClaims,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["id"] = Field("A short stable identifier, starting with a letter (for example c1).", ProofClaim.MaxIdLength),
                                ["statement"] = Field("What must be true.", ProofClaim.MaxStatementLength),
                                ["acceptance_criterion"] = Field("How a prover can tell it is true.", ProofClaim.MaxAcceptanceCriterionLength),
                                ["evidence"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["description"] = $"1 to {ProofClaim.MaxEvidence} requirements from the closed vocabulary: 'verification:<configured check name>', 'review:current-pass', 'observation:<short description of a targeted observation the prover must make>'.",
                                    ["minItems"] = 1,
                                    ["maxItems"] = ProofClaim.MaxEvidence,
                                    ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = ProofEvidenceRequirement.MaxObservationLength + 20 },
                                },
                            },
                            ["required"] = new JsonArray("id", "statement", "acceptance_criterion", "evidence"),
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["required"] = new JsonArray("rationale", "claims"),
                ["additionalProperties"] = false,
            },
        };
    }

    private static JsonObject ProofToolDefinition()
    {
        JsonObject Field(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

        return new JsonObject
        {
            ["name"] = AgentProofSubmission.ToolName,
            ["description"] =
                "Submit your independent proof of the Proof Contract, exactly once. This is the only way your proof is recorded. Give one result for EVERY claim of the contract: pass only when "
                + "its acceptance criterion is established by evidence you cite and every evidence requirement is met, fail when you found it does not hold, inconclusive when it cannot be established.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["summary"] = Field("Your overall judgment in a few sentences.", ReportSummaryLimit),
                    ["claims"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "One result per claim of the Proof Contract, no more, no fewer.",
                        ["minItems"] = 1,
                        ["maxItems"] = ProofContract.MaxClaims,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["claim_id"] = Field("The id of the claim.", ProofClaim.MaxIdLength),
                                ["verdict"] = new JsonObject { ["type"] = "string", ["description"] = "pass, fail or inconclusive.", ["enum"] = new JsonArray("pass", "fail", "inconclusive") },
                                ["evidence"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["description"] = "Typed references to the evidence behind the verdict: 'verification:<check>', 'review:<id>' or 'command:<n>' (your own n-th shell command, counted from 1).",
                                    ["maxItems"] = ProofClaimResult.MaxEvidenceRefs,
                                    ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = ProofEvidenceRef.MaxValueLength + 20 },
                                },
                                ["summary"] = Field("Why, briefly.", ProofClaimResult.MaxSummaryLength),
                            },
                            ["required"] = new JsonArray("claim_id", "verdict", "summary"),
                            ["additionalProperties"] = false,
                        },
                    },
                },
                ["required"] = new JsonArray("summary", "claims"),
                ["additionalProperties"] = false,
            },
        };
    }

    private const int ReportSummaryLimit = ProofReport.MaxSummaryLength;

    private static readonly string[] ReclassificationFields = ["evidence", "stage", "reason"];

    /// <summary>
    /// The bounded report of a well-formed reclassification call, or the reason it is not one. The bounds are Core's own (<see cref="Reclassification.TextProblem"/>), and
    /// the stage is a closed choice, "what" or "how"; nothing is parsed out of prose. Shared with the hook.
    /// </summary>
    internal static bool TryReadReclassification(JsonElement input, out AgentReclassificationSignal signal, out string problem)
    {
        signal = new AgentReclassificationSignal(string.Empty, ReclassificationStage.How, string.Empty);

        if (!TryReadSections(input, ReclassificationFields, [], "reclassification", out var values, out problem))
            return false;

        var stage = values["stage"]!.ToLowerInvariant() switch
        {
            "what" => ReclassificationStage.What,
            "how" => ReclassificationStage.How,
            _ => (ReclassificationStage?)null,
        };

        if (stage is null)
        {
            problem = "'stage' must be exactly \"what\" or \"how\"";
            return false;
        }

        if (Reclassification.TextProblem(values["evidence"], values["reason"]) is { } textProblem)
        {
            problem = textProblem;
            return false;
        }

        signal = new AgentReclassificationSignal(values["evidence"]!, stage.Value, values["reason"]!);
        return true;
    }

    private static readonly string[] WhatFields = ["objective", "required_behavior", "scope", "constraints", "non_goals", "acceptance_conditions", "rationale"];

    /// <summary>
    /// The bounded proposal of a well-formed WHAT call, or the reason it is not one. The bounds are Core's own (<see cref="WhatProposal"/>): the server and the hook
    /// re-check what Core would refuse, so a proposal that is recorded can always be opened as a gate. Shared with the hook.
    /// </summary>
    internal static bool TryReadWhat(JsonElement input, out AgentWhatSignal signal, out string problem)
    {
        signal = new AgentWhatSignal(string.Empty, string.Empty, string.Empty, null, null, string.Empty, string.Empty);

        if (!TryReadSections(input, WhatFields, ["constraints", "non_goals"], "WHAT", out var values, out problem))
            return false;

        try
        {
            // Core's own constructor is the one place the bounds live: sections, rationale, total size, control characters.
            _ = new WhatProposal(
                values["objective"]!,
                values["required_behavior"]!,
                values["scope"]!,
                values["constraints"],
                values["non_goals"],
                values["acceptance_conditions"]!,
                values["rationale"]!,
                turn: 1);
        }
        catch (ArgumentException e)
        {
            problem = e.Message.Split(" (Parameter", 2)[0];
            return false;
        }

        signal = new AgentWhatSignal(
            values["objective"]!,
            values["required_behavior"]!,
            values["scope"]!,
            values["constraints"],
            values["non_goals"],
            values["acceptance_conditions"]!,
            values["rationale"]!);

        return true;
    }

    private static readonly string[] HowFields = ["strategy", "components", "data_and_control_flow", "compatibility", "risks_and_tradeoffs", "verification_approach", "rationale"];

    /// <summary>
    /// The bounded proposal of a well-formed HOW call, or the reason it is not one. As for the WHAT, the bounds are Core's own (<see cref="HowProposal"/>), so a
    /// proposal that is recorded can always be opened as a gate. Shared with the hook.
    /// </summary>
    internal static bool TryReadHow(JsonElement input, out AgentHowSignal signal, out string problem)
    {
        signal = new AgentHowSignal(string.Empty, string.Empty, null, null, string.Empty, null, string.Empty);

        if (!TryReadSections(input, HowFields, ["data_and_control_flow", "compatibility", "verification_approach"], "HOW", out var values, out problem))
            return false;

        try
        {
            _ = new HowProposal(
                values["strategy"]!,
                values["components"]!,
                values["data_and_control_flow"],
                values["compatibility"],
                values["risks_and_tradeoffs"]!,
                values["verification_approach"],
                values["rationale"]!,
                turn: 1);
        }
        catch (ArgumentException e)
        {
            problem = e.Message.Split(" (Parameter", 2)[0];
            return false;
        }

        signal = new AgentHowSignal(
            values["strategy"]!,
            values["components"]!,
            values["data_and_control_flow"],
            values["compatibility"],
            values["risks_and_tradeoffs"]!,
            values["verification_approach"],
            values["rationale"]!);

        return true;
    }

    /// <summary>
    /// Reads the named text fields of a closed, object-shaped tool input: no field beyond <paramref name="fields"/>, every non-optional one present and non-blank
    /// text, an optional one null, absent or blank. Length and character bounds are Core's, checked by the caller's constructor.
    /// </summary>
    private static bool TryReadSections(JsonElement input, string[] fields, string[] optionalFields, string what, out Dictionary<string, string?> values, out string problem)
    {
        values = new Dictionary<string, string?>(StringComparer.Ordinal);
        problem = string.Empty;

        if (input.ValueKind != JsonValueKind.Object)
        {
            problem = $"the tool input must be an object with the {what} sections";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (!fields.Contains(property.Name))
            {
                problem = $"unexpected field '{property.Name}'; only {string.Join(", ", fields.Select(f => $"'{f}'"))} are accepted";
                return false;
            }
        }

        foreach (var field in fields)
        {
            var optional = optionalFields.Contains(field);

            if (!input.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                if (optional)
                {
                    values[field] = null;
                    continue;
                }

                problem = $"'{field}' is required and must be non-blank text";
                return false;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                problem = $"'{field}' must be text";
                return false;
            }

            var text = element.GetString()!.Trim();

            if (text.Length == 0 && !optional)
            {
                problem = $"'{field}' is required and must be non-blank text";
                return false;
            }

            values[field] = text.Length == 0 ? null : text;
        }

        return true;
    }

    private static JsonObject WhatToolDefinition()
    {
        JsonObject Field(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

        return new JsonObject
        {
            ["name"] = AgentWhatSignal.ToolName,
            ["description"] =
                "Put the WHAT of this task in front of the person who owns it for approval, before you implement anything: what is to be achieved, stated as intent and externally visible "
                + "outcomes, never as implementation steps. It is a contract the person approves, not a question: never leave open in it a decision only the person can make; if one is open, "
                + "ask it first with the " + AgentClarificationSignal.ToolName + " tool and propose the WHAT after the answer. After calling it, stop and wait, and change no file until the proposal is approved.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["objective"] = Field("What the task is for, in one coherent statement.", WhatProposal.MaxSectionLength),
                    ["required_behavior"] = Field("The externally visible behavior that must hold when the task is done.", WhatProposal.MaxSectionLength),
                    ["scope"] = Field("What is in play: the parts of the product or repository the task is about.", WhatProposal.MaxSectionLength),
                    ["constraints"] = Field("Optional. Limits the result must respect.", WhatProposal.MaxSectionLength),
                    ["non_goals"] = Field("Optional. What the task deliberately does not do.", WhatProposal.MaxSectionLength),
                    ["acceptance_conditions"] = Field("The conditions under which the result counts as meeting the requirement.", WhatProposal.MaxSectionLength),
                    ["rationale"] = Field("Why a human decision on this contract is warranted (what it commits to and why that matters). Never a decision left for the person to make: that is asked first with " + AgentClarificationSignal.ToolName + ".", WhatProposal.MaxRationaleLength),
                },
                ["required"] = new JsonArray("objective", "required_behavior", "scope", "acceptance_conditions", "rationale"),
                ["additionalProperties"] = false,
            },
        };
    }

    private static JsonObject ReviewRequestToolDefinition() => new()
    {
        ["name"] = AgentReviewRequestSignal.ToolName,
        ["description"] =
            "Ask for an independent review of your verified work before this task completes, when the task carries a risk that a second pair of eyes is worth having. "
            + "It only adds rigor: it does not stop your turn, changes nothing else and cannot be withdrawn. Do not use it for routine work.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Why this work is worth an independent review.", ["maxLength"] = ReviewRequest.MaxReasonLength },
            },
            ["required"] = new JsonArray("reason"),
            ["additionalProperties"] = false,
        },
    };

    private static JsonObject ReviewToolDefinition()
    {
        JsonObject Field(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

        return new JsonObject
        {
            ["name"] = AgentReviewSubmission.ToolName,
            ["description"] =
                "Submit your independent review of the implementation, exactly once. This is the only way your review is recorded. Use PASS when the work is acceptable (no findings) "
                + "and CHANGES_REQUIRED when a concrete, evidenced problem must be fixed (at least one finding). Do not block on style, naming or preference.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["verdict"] = new JsonObject { ["type"] = "string", ["description"] = "PASS or CHANGES_REQUIRED.", ["enum"] = new JsonArray("PASS", "CHANGES_REQUIRED") },
                    ["summary"] = Field("Your overall judgment in a few sentences.", ReviewReport.MaxSummaryLength),
                    ["findings"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "The concrete problems that must be fixed. Empty for PASS.",
                        ["maxItems"] = ReviewReport.MaxFindings,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["title"] = Field("What is wrong, briefly.", ReviewFinding.MaxTitleLength),
                                ["evidence"] = Field("What you saw and where (file, line, command result).", ReviewFinding.MaxEvidenceLength),
                                ["impact"] = Field("Why it matters.", ReviewFinding.MaxImpactLength),
                                ["recommendation"] = Field("What to change.", ReviewFinding.MaxRecommendationLength),
                                ["location"] = Field("Optional. file:line.", ReviewFinding.MaxLocationLength),
                            },
                            ["required"] = new JsonArray("title", "evidence", "impact", "recommendation"),
                            ["additionalProperties"] = false,
                        },
                    },
                    ["notes"] = Field("Optional non-blocking remarks. They never prevent completion.", ReviewReport.MaxNotesLength),
                },
                ["required"] = new JsonArray("verdict", "summary"),
                ["additionalProperties"] = false,
            },
        };
    }

    private static JsonObject ReclassificationToolDefinition()
    {
        return new JsonObject
        {
            ["name"] = AgentReclassificationSignal.ToolName,
            ["description"] =
                "Report concrete new evidence you found while working that invalidates an assumption of a stage a person already approved, so the workflow can bring that stage "
                + "back to the person. Name the earliest stage that must be reconsidered: \"what\" when the intent itself is in question, \"how\" when only the implementation strategy is. "
                + "It can only add rigor. After calling it, stop and wait, and change no file until the revised stage is approved.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["evidence"] = new JsonObject { ["type"] = "string", ["description"] = "What you found, concretely and verifiably (a command result, a file, a failing check).", ["maxLength"] = Reclassification.MaxEvidenceLength },
                    ["stage"] = new JsonObject { ["type"] = "string", ["description"] = "The earliest stage that must be reconsidered.", ["enum"] = new JsonArray("what", "how") },
                    ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Why this evidence means the approved stage no longer holds.", ["maxLength"] = Reclassification.MaxReasonLength },
                },
                ["required"] = new JsonArray("evidence", "stage", "reason"),
                ["additionalProperties"] = false,
            },
        };
    }

    private static JsonObject HowToolDefinition()
    {
        JsonObject Field(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

        return new JsonObject
        {
            ["name"] = AgentHowSignal.ToolName,
            ["description"] =
                "Put the implementation strategy of this task in front of the person who owns it for approval, before you implement anything, when the strategy is consequential: "
                + "state the approach, the components and their responsibilities and the tradeoffs, never source code, a file-by-file plan or a task checklist. It is a contract the person approves, "
                + "not a question: never leave open in it a decision only the person can make; if one is open, ask it first with the " + AgentClarificationSignal.ToolName + " tool. After calling it, "
                + "stop and wait, and change no file until the proposal is approved.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["strategy"] = Field("The chosen approach, in one coherent statement, and the architectural shape it gives the change.", HowProposal.MaxSectionLength),
                    ["components"] = Field("The components or interfaces involved and what each is responsible for.", HowProposal.MaxSectionLength),
                    ["data_and_control_flow"] = Field("Optional. The important data or control flow, where it is part of the design.", HowProposal.MaxSectionLength),
                    ["compatibility"] = Field("Optional. The persistence, migration or compatibility strategy, when it applies.", HowProposal.MaxSectionLength),
                    ["risks_and_tradeoffs"] = Field("The main risks and the tradeoffs made, including the principal alternative you did not choose and why.", HowProposal.MaxSectionLength),
                    ["verification_approach"] = Field("Optional. How the strategy will be verified, where that is part of the design.", HowProposal.MaxSectionLength),
                    ["rationale"] = Field("Why a human decision on this strategy is warranted (for example, which approaches are plausible and differ materially).", HowProposal.MaxRationaleLength),
                },
                ["required"] = new JsonArray("strategy", "components", "risks_and_tradeoffs", "rationale"),
                ["additionalProperties"] = false,
            },
        };
    }

    private static JsonNode ToolDefinition() => new JsonObject
    {
        ["name"] = AgentClarificationSignal.ToolName,
        ["description"] =
            "Ask the person who owns this task one question, when a requirement the task depends on is missing or ambiguous, cannot be settled from the repository, "
            + "and the answer would materially change the work. A decision only the person can make and the task leaves open is asked with this tool BEFORE any WHAT or HOW is proposed "
            + "(a proposal is not a place to ask it, and requesting changes to a proposal does not replace it). After calling it, stop and wait. Not for permissions or for details you can decide yourself.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["question"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The question, answerable in a sentence or two.",
                    ["maxLength"] = ClarificationRequest.MaxQuestionLength,
                },
                ["reason"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Why the answer materially changes what you would build.",
                    ["maxLength"] = ClarificationRequest.MaxReasonLength,
                },
            },
            ["required"] = new JsonArray("question", "reason"),
            ["additionalProperties"] = false,
        },
    };

    private static JsonObject Text(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
