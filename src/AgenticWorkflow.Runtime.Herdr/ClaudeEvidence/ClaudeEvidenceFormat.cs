using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

// The on-disk contract between the hook process (writer) and the controller (reader) of one evidence channel. Internal on
// purpose: the controller never reads Claude's transcript or screen, only these records, which the hook handler wrote from the
// harness's structured hook payloads.

internal static class ClaudeEvidenceFormat
{
    public const int Version = 1;

    public const string ConfigFile = "config.json";

    public const string SettingsFile = "settings.json";

    public const string EventPrefix = "e-";

    public const string MarkPrefix = "m-";

    /// <summary>A controller turn was sent to a session that submits structured reports: the moment from which the session's report tool may accept one more.</summary>
    public const string TurnPrefix = "t-";

    public const string Suffix = ".json";

    public const string KindToolSuccess = "tool_success";

    public const string KindToolFailure = "tool_failure";

    public const string KindBatch = "batch";

    public const string KindProblem = "problem";

    /// <summary>The agent deliberately asked the workflow a question: written by the hook for the clarification tool, never for anything else.</summary>
    public const string KindClarification = "clarification";

    /// <summary>The agent deliberately proposed the WHAT of the task: written by the hook for the WHAT tool, never for anything else.</summary>
    public const string KindWhat = "what_proposal";

    /// <summary>The agent deliberately proposed the HOW of the task: written by the hook for the HOW tool, never for anything else.</summary>
    public const string KindHow = "how_proposal";

    /// <summary>The agent deliberately reported new evidence that invalidates a settled stage: written by the hook for the reclassification tool, never for anything else.</summary>
    public const string KindReclassification = "reclassification";

    /// <summary>The agent deliberately asked for an independent REVIEW of its verified work: written by the hook for the review-request tool, never for anything else. It never stops the turn.</summary>
    public const string KindReviewRequest = "review_request";

    /// <summary>The independent reviewer submitted its structured report: written by the hook for the review tool, never for anything else.</summary>
    public const string KindReview = "review_submission";

    /// <summary>The implementer submitted its structured Proof Contract: written by the hook for the contract tool, never for anything else. It never stops the turn.</summary>
    public const string KindProveRequest = "prove_request";

    /// <summary>The independent prover submitted its structured proof: written by the hook for the proof tool, never for anything else.</summary>
    public const string KindProof = "proof_submission";

    /// <summary>The MCP server name Claude Code is given for the clarification tool; with the tool name it forms <see cref="ClarificationToolFullName"/>.</summary>
    public const string ServerName = "aw";

    /// <summary>The tool as the harness names it (<c>mcp__&lt;server&gt;__&lt;tool&gt;</c>): what the hook matcher and the session's allow rule refer to.</summary>
    public const string ClarificationToolFullName = "mcp__" + ServerName + "__" + AgentClarificationSignal.ToolName;

    /// <summary>The WHAT tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string WhatToolFullName = "mcp__" + ServerName + "__" + AgentWhatSignal.ToolName;

    /// <summary>The HOW tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string HowToolFullName = "mcp__" + ServerName + "__" + AgentHowSignal.ToolName;

    /// <summary>The reclassification tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string ReclassificationToolFullName = "mcp__" + ServerName + "__" + AgentReclassificationSignal.ToolName;

    /// <summary>The review-request tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string ReviewRequestToolFullName = "mcp__" + ServerName + "__" + AgentReviewRequestSignal.ToolName;

    /// <summary>The review-report tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string ReviewToolFullName = "mcp__" + ServerName + "__" + AgentReviewSubmission.ToolName;

    /// <summary>The Proof Contract tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string ProveRequestToolFullName = "mcp__" + ServerName + "__" + AgentProveRequestSignal.ToolName;

    /// <summary>The proof-report tool as the harness names it: what the hook matcher and the session's allow rule refer to.</summary>
    public const string ProofToolFullName = "mcp__" + ServerName + "__" + AgentProofSubmission.ToolName;

    public const string McpConfigFile = "mcp.json";

    /// <summary>The first argument of the hook executable when it is started as the clarification MCP server instead of as a hook.</summary>
    public const string McpModeArgument = ClaudeClarificationServer.ModeArgument;

    public const string RuntimeName = "claude-code";

    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The tools whose input is a shell command line the controller may recognise.</summary>
    public static bool IsCommandTool(string? toolName) => toolName is "Bash" or "PowerShell";

    public static string EventFileName(long ticks) => $"{EventPrefix}{ticks:D19}-{Guid.NewGuid():N}{Suffix}";

    public static string MarkFileName(long ticks) => $"{MarkPrefix}{ticks:D19}-{Guid.NewGuid():N}{Suffix}";

    public static string TurnFileName(long ticks) => $"{TurnPrefix}{ticks:D19}-{Guid.NewGuid():N}{Suffix}";

    /// <summary>The timestamp embedded in a record's file name, or null if the name is not one of ours.</summary>
    public static long? TicksOf(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var span = fileName.AsSpan(prefix.Length);

        return span.Length > 19 && long.TryParse(span[..19], out var ticks) ? ticks : null;
    }
}

/// <summary>What the controller tells the hook process once, before the session starts.</summary>
internal sealed class ChannelConfig
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ClaudeEvidenceFormat.Version;

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    /// <summary>The Claude session id the controller chose with <c>--session-id</c>; events of any other session are refused.</summary>
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("workspace")]
    public required string Workspace { get; init; }

    /// <summary>True when the session was given the clarification tool; only then does the hook record (and stop the turn for) a clarification.</summary>
    [JsonPropertyName("clarification")]
    public bool Clarification { get; init; }

    /// <summary>True when the session was given the WHAT tool; only then does the hook record (and stop the turn for) a WHAT proposal.</summary>
    [JsonPropertyName("what")]
    public bool What { get; init; }

    /// <summary>True when the session was given the HOW tool; only then does the hook record (and stop the turn for) a HOW proposal.</summary>
    [JsonPropertyName("how")]
    public bool How { get; init; }

    /// <summary>True when the session was given the reclassification tool; only then does the hook record (and stop the turn for) a report.</summary>
    [JsonPropertyName("reclassification")]
    public bool Reclassification { get; init; }

    /// <summary>True when the session was given the review-request tool; only then does the hook record a request. It never stops the turn.</summary>
    [JsonPropertyName("review_request")]
    public bool ReviewRequest { get; init; }

    /// <summary>True when the session is an independent reviewer and was given the review-report tool; only then does the hook record (and stop the turn for) a report.</summary>
    [JsonPropertyName("review")]
    public bool Review { get; init; }

    /// <summary>True when the session was given the Proof Contract tool; only then does the hook record a contract. It never stops the turn.</summary>
    [JsonPropertyName("prove_request")]
    public bool ProveRequest { get; init; }

    /// <summary>True when the session is an independent prover and was given the proof-report tool; only then does the hook record (and stop the turn for) a proof.</summary>
    [JsonPropertyName("prove")]
    public bool Prove { get; init; }

    [JsonPropertyName("candidates")]
    public required IReadOnlyList<ChannelCandidate> Candidates { get; init; }
}

/// <summary>A configured check the hook should prove (capture state after) when the agent runs it alone.</summary>
internal sealed class ChannelCandidate
{
    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("arguments")]
    public required IReadOnlyList<string> Arguments { get; init; }

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; init; }
}

internal sealed class RawCall
{
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("command_truncated")]
    public bool CommandTruncated { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }
}

/// <summary>The sections of a WHAT proposal as the hook recorded them.</summary>
internal sealed class RawWhat
{
    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("required_behavior")]
    public required string RequiredBehavior { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("constraints")]
    public string? Constraints { get; init; }

    [JsonPropertyName("non_goals")]
    public string? NonGoals { get; init; }

    [JsonPropertyName("acceptance_conditions")]
    public required string AcceptanceConditions { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }
}

/// <summary>The fields of a reclassification report as the hook recorded them.</summary>
internal sealed class RawReclassification
{
    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    /// <summary>"what" or "how".</summary>
    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>One finding of a review report as the hook recorded it.</summary>
internal sealed class RawReviewFinding
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    [JsonPropertyName("impact")]
    public required string Impact { get; init; }

    [JsonPropertyName("recommendation")]
    public required string Recommendation { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }
}

/// <summary>One evidence requirement of a Proof Contract claim as the hook recorded it.</summary>
internal sealed class RawProofEvidenceRequirement
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }
}

/// <summary>One claim of a Proof Contract as the hook recorded it.</summary>
internal sealed class RawProofClaim
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<RawProofEvidenceRequirement> Evidence { get; init; }

    [JsonPropertyName("acceptance_criterion")]
    public required string AcceptanceCriterion { get; init; }
}

/// <summary>The Proof Contract as the hook recorded it (already validated against Core's bounds).</summary>
internal sealed class RawProofContract
{
    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<RawProofClaim> Claims { get; init; }
}

/// <summary>One claim result of a proof as the hook recorded it.</summary>
internal sealed class RawProofClaimResult
{
    [JsonPropertyName("claim_id")]
    public required string ClaimId { get; init; }

    /// <summary>"pass", "fail" or "inconclusive".</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<string> Evidence { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }
}

/// <summary>The proof as the hook recorded it (already validated against Core's bounds).</summary>
internal sealed class RawProof
{
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<RawProofClaimResult> Claims { get; init; }
}

/// <summary>The review report as the hook recorded it (already validated against Core's bounds).</summary>
internal sealed class RawReview
{
    /// <summary>"PASS" or "CHANGES_REQUIRED".</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("findings")]
    public required IReadOnlyList<RawReviewFinding> Findings { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>The sections of a HOW proposal as the hook recorded them.</summary>
internal sealed class RawHow
{
    [JsonPropertyName("strategy")]
    public required string Strategy { get; init; }

    [JsonPropertyName("components")]
    public required string Components { get; init; }

    [JsonPropertyName("data_and_control_flow")]
    public string? DataAndControlFlow { get; init; }

    [JsonPropertyName("compatibility")]
    public string? Compatibility { get; init; }

    [JsonPropertyName("risks_and_tradeoffs")]
    public required string RisksAndTradeoffs { get; init; }

    [JsonPropertyName("verification_approach")]
    public string? VerificationApproach { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }
}

/// <summary>One record written by the hook process. Contains no command output.</summary>
internal sealed class RawEvent
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ClaudeEvidenceFormat.Version;

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("at")]
    public long AtTicks { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("command_truncated")]
    public bool CommandTruncated { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("interrupted")]
    public bool Interrupted { get; init; }

    [JsonPropertyName("calls")]
    public IReadOnlyList<RawCall>? Calls { get; init; }

    /// <summary>The implementation state captured right after a batch (only for a lone command that looks like a configured check).</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("fingerprint_error")]
    public string? FingerprintError { get; init; }

    [JsonPropertyName("problem")]
    public string? Problem { get; init; }

    /// <summary>The question of a <see cref="ClaudeEvidenceFormat.KindClarification"/> record, as the agent gave it (already bounded by the hook).</summary>
    [JsonPropertyName("question")]
    public string? Question { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The proposal of a <see cref="ClaudeEvidenceFormat.KindWhat"/> record, as the agent gave it (already bounded by the hook).</summary>
    [JsonPropertyName("what")]
    public RawWhat? What { get; init; }

    /// <summary>The proposal of a <see cref="ClaudeEvidenceFormat.KindHow"/> record, as the agent gave it (already bounded by the hook).</summary>
    [JsonPropertyName("how")]
    public RawHow? How { get; init; }

    /// <summary>The report of a <see cref="ClaudeEvidenceFormat.KindReclassification"/> record, as the agent gave it (already bounded by the hook).</summary>
    [JsonPropertyName("reclassification")]
    public RawReclassification? Reclassification { get; init; }

    /// <summary>The report of a <see cref="ClaudeEvidenceFormat.KindReview"/> record, as the reviewer gave it (already bounded by the hook).</summary>
    [JsonPropertyName("review")]
    public RawReview? Review { get; init; }

    /// <summary>The contract of a <see cref="ClaudeEvidenceFormat.KindProveRequest"/> record, as the implementer gave it (already bounded by the hook).</summary>
    [JsonPropertyName("proof_contract")]
    public RawProofContract? ProofContract { get; init; }

    /// <summary>The proof of a <see cref="ClaudeEvidenceFormat.KindProof"/> record, as the prover gave it (already bounded by the hook).</summary>
    [JsonPropertyName("proof")]
    public RawProof? Proof { get; init; }

    /// <summary>How long the handler itself ran for this event (excludes process start-up), for overhead measurement.</summary>
    [JsonPropertyName("handler_ms")]
    public long HandlerMs { get; init; }
}
