using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

/// <summary>
/// The body of the hook process Claude Code starts for <c>PostToolUse</c>, <c>PostToolUseFailure</c> and
/// <c>PostToolBatch</c> (one JSON document on stdin, the channel directory as the only argument). It is purely observational:
/// it writes one small record into the controller-owned channel and nothing else. It always exits 0, so it can never approve, deny, block or alter a
/// tool, replace its output, or add anything to the model's context.
/// <para>
/// The one exception to "prints nothing" is the clarification tool: when the session was given it and the agent of THIS session calls it with a
/// well-formed question, the handler records the question and prints the harness's structured "stop this turn" answer
/// (<c>{"continue":false}</c>), so the agent stops where it asked instead of working on. It still decides no permission and alters no tool result.
/// </para>
/// <para>
/// Cost: a record per command-tool event and one per tool batch. The only expensive work is capturing the implementation
/// state, and only for a batch that is exactly one command that looks like a configured check; reads, searches and edits
/// are recorded without touching the repository.
/// </para>
/// </summary>
public static class ClaudeHookHandler
{
    private const int MaxRecordedCommandLength = CommandForm.MaxCommandLength;

    /// <summary>The harness's documented answer that ends the turn cleanly (the transcript stays intact and resumable). <c>stopReason</c> is shown to the person only.</summary>
    private const string StopTurn = "{\"continue\":false,\"stopReason\":\"The agent asked a question; AgenticWorkflow recorded it.\"}";

    private const string StopTurnForWhat = "{\"continue\":false,\"stopReason\":\"The agent proposed a WHAT; AgenticWorkflow recorded it.\"}";

    private const string StopTurnForReclassification = "{\"continue\":false,\"stopReason\":\"The agent reported new evidence; AgenticWorkflow recorded it.\"}";

    private const string StopTurnForReview = "{\"continue\":false,\"stopReason\":\"The reviewer submitted its review; AgenticWorkflow recorded it.\"}";

    private const string StopTurnForProof = "{\"continue\":false,\"stopReason\":\"The prover submitted its proof; AgenticWorkflow recorded it.\"}";

    private const string StopTurnForHow = "{\"continue\":false,\"stopReason\":\"The agent proposed a HOW; AgenticWorkflow recorded it.\"}";

    /// <returns>Always 0: the handler must never fail a tool.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader stdin,
        IImplementationStateProvider state,
        CancellationToken cancellationToken = default,
        TextWriter? stdout = null)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            if (args.Count < 1 || !Directory.Exists(args[0]))
            {
                return 0; // nowhere to record; the controller will simply see no evidence
            }

            var directory = args[0];
            var config = ReadConfig(directory);

            if (config is null)
            {
                return 0;
            }

            var raw = await stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var record = await BuildRecordAsync(config, raw, state, clock, cancellationToken).ConfigureAwait(false);

            Write(directory, record);

            // Only after the record is durable: a stop for a question nobody can read would lose it.
            if (record.Kind == ClaudeEvidenceFormat.KindClarification)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurn).ConfigureAwait(false);
            else if (record.Kind == ClaudeEvidenceFormat.KindWhat)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurnForWhat).ConfigureAwait(false);
            else if (record.Kind == ClaudeEvidenceFormat.KindHow)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurnForHow).ConfigureAwait(false);
            else if (record.Kind == ClaudeEvidenceFormat.KindReclassification)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurnForReclassification).ConfigureAwait(false);
            else if (record.Kind == ClaudeEvidenceFormat.KindReview)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurnForReview).ConfigureAwait(false);
            else if (record.Kind == ClaudeEvidenceFormat.KindProof)
                await (stdout ?? Console.Out).WriteLineAsync(StopTurnForProof).ConfigureAwait(false);

            // A review REQUEST and a Proof Contract are deliberately not here: they are additive and the agent keeps working.
        }
        catch (Exception)
        {
            // Telemetry must not disturb the agent. A missing record only means no reuse; the controller reruns its checks.
        }

        return 0;
    }

    private static async Task<RawEvent> BuildRecordAsync(
        ChannelConfig config,
        string raw,
        IImplementationStateProvider state,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException e)
        {
            return Problem(config, $"malformed_hook_payload: {e.GetType().Name}", clock);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Problem(config, "malformed_hook_payload: not a JSON object", clock);
            }

            var sessionId = Text(root, "session_id");

            if (!string.Equals(sessionId, config.SessionId, StringComparison.Ordinal))
            {
                return Problem(config, $"foreign_session: event belongs to session '{Truncate(sessionId, 80)}', not the owned one", clock);
            }

            var eventName = Text(root, "hook_event_name");

            return eventName switch
            {
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ClarificationToolFullName => ClarificationRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.WhatToolFullName => WhatRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.HowToolFullName => HowRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ReclassificationToolFullName => ReclassificationRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ReviewRequestToolFullName => ReviewRequestRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ReviewToolFullName => ReviewRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ProveRequestToolFullName => ProveRequestRecord(config, root, clock),
                "PostToolUse" when Text(root, "tool_name") == ClaudeEvidenceFormat.ProofToolFullName => ProofRecord(config, root, clock),
                "PostToolUse" => ToolRecord(config, root, ClaudeEvidenceFormat.KindToolSuccess, clock),
                "PostToolUseFailure" => ToolRecord(config, root, ClaudeEvidenceFormat.KindToolFailure, clock),
                "PostToolBatch" => await BatchRecordAsync(config, root, state, clock, cancellationToken).ConfigureAwait(false),
                _ => Problem(config, $"unexpected_hook_event: '{Truncate(eventName, 40)}'", clock),
            };
        }
    }

    /// <summary>
    /// The record of the agent's question. Written only for a session that was given the tool, by the session that owns the channel (the session check above),
    /// and only for a well-formed, bounded question: anything else becomes a problem record, which the controller never reads as a question and which does not
    /// stop the turn.
    /// </summary>
    private static RawEvent ClarificationRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.Clarification)
            return Problem(config, "unexpected_clarification: this session was not given the clarification tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryRead(input, out var question, out var reason, out var problem))
            return Problem(config, $"malformed_clarification: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindClarification,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ClarificationToolFullName,
            Question = question,
            Reason = reason,
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the agent's WHAT proposal, under the same rules as <see cref="ClarificationRecord"/>: written only for a session that was given the tool,
    /// by the session that owns the channel, and only for a well-formed, bounded proposal (the very bounds Core enforces). Anything else becomes a problem record,
    /// which the controller never reads as a proposal and which does not stop the turn.
    /// </summary>
    private static RawEvent WhatRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.What)
            return Problem(config, "unexpected_what: this session was not given the WHAT tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadWhat(input, out var signal, out _))
            return Problem(config, $"malformed_what: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindWhat,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.WhatToolFullName,
            What = new RawWhat
            {
                Objective = signal.Objective,
                RequiredBehavior = signal.RequiredBehavior,
                Scope = signal.Scope,
                Constraints = signal.Constraints,
                NonGoals = signal.NonGoals,
                AcceptanceConditions = signal.AcceptanceConditions,
                Rationale = signal.Rationale,
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the agent's HOW proposal, under the same rules as <see cref="WhatRecord"/>: written only for a session that was given the tool, by the
    /// session that owns the channel, and only for a well-formed, bounded proposal (the very bounds Core enforces). Anything else becomes a problem record, which the
    /// controller never reads as a proposal and which does not stop the turn.
    /// </summary>
    private static RawEvent HowRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.How)
            return Problem(config, "unexpected_how: this session was not given the HOW tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadHow(input, out var signal, out _))
            return Problem(config, $"malformed_how: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindHow,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.HowToolFullName,
            How = new RawHow
            {
                Strategy = signal.Strategy,
                Components = signal.Components,
                DataAndControlFlow = signal.DataAndControlFlow,
                Compatibility = signal.Compatibility,
                RisksAndTradeoffs = signal.RisksAndTradeoffs,
                VerificationApproach = signal.VerificationApproach,
                Rationale = signal.Rationale,
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the agent's reclassification report, under the same rules as <see cref="HowRecord"/>: written only for a session that was given the tool, by the
    /// session that owns the channel, and only for a well-formed report within the bounds Core enforces. Anything else becomes a problem record, which the controller
    /// never reads as a report and which does not stop the turn.
    /// </summary>
    private static RawEvent ReclassificationRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.Reclassification)
            return Problem(config, "unexpected_reclassification: this session was not given the reclassification tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadReclassification(input, out var signal, out _))
            return Problem(config, $"malformed_reclassification: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindReclassification,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ReclassificationToolFullName,
            Reclassification = new RawReclassification
            {
                Evidence = signal.Evidence,
                Stage = signal.Stage == ReclassificationStage.What ? "what" : "how",
                Reason = signal.Reason,
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the agent's request for an independent review: written only for a session that was given the tool, by the session that owns the channel, and
    /// only for a well-formed, bounded reason. Anything else becomes a problem record. It never stops the turn.
    /// </summary>
    private static RawEvent ReviewRequestRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.ReviewRequest)
            return Problem(config, "unexpected_review_request: this session was not given the review-request tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadReviewRequest(input, out var reason, out _))
            return Problem(config, $"malformed_review_request: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindReviewRequest,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ReviewRequestToolFullName,
            Reason = reason,
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the independent reviewer's report: written only for a session that was given the review tool, by the session that owns the channel, and only
    /// for a well-formed report within the bounds Core enforces. Anything else becomes a problem record, which the controller never reads as a report and which does not
    /// stop the turn (so the reviewer can correct it).
    /// </summary>
    private static RawEvent ReviewRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.Review)
            return Problem(config, "unexpected_review: this session was not given the review tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadReview(input, out var submission, out _))
            return Problem(config, $"malformed_review: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindReview,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ReviewToolFullName,
            Review = new RawReview
            {
                Verdict = submission.Verdict,
                Summary = submission.Summary,
                Findings = [.. submission.Findings.Select(f => new RawReviewFinding { Title = f.Title, Evidence = f.Evidence, Impact = f.Impact, Recommendation = f.Recommendation, Location = f.Location })],
                Notes = submission.Notes,
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the implementer's Proof Contract: written only for a session that was given the tool, by the session that owns the channel, and only for a
    /// well-formed contract within the bounds Core enforces. Anything else becomes a problem record. It never stops the turn.
    /// </summary>
    private static RawEvent ProveRequestRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.ProveRequest)
            return Problem(config, "unexpected_prove_request: this session was not given the Proof Contract tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadProveRequest(input, out var contract, out _))
            return Problem(config, $"malformed_prove_request: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindProveRequest,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ProveRequestToolFullName,
            ProofContract = new RawProofContract
            {
                Rationale = contract.Rationale,
                Claims =
                [
                    .. contract.Claims.Select(c => new RawProofClaim
                    {
                        Id = c.Id,
                        Statement = c.Statement,
                        AcceptanceCriterion = c.AcceptanceCriterion,
                        Evidence = [.. c.Evidence.Select(e => new RawProofEvidenceRequirement { Kind = e.Kind, Subject = e.Subject })],
                    }),
                ],
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The record of the independent prover's proof: written only for a session that was given the proof tool, by the session that owns the channel, and only for a
    /// well-formed proof within the bounds Core enforces. Anything else becomes a problem record, which the controller never reads as a proof and which does not stop the
    /// turn (so the prover can correct it).
    /// </summary>
    private static RawEvent ProofRecord(ChannelConfig config, JsonElement root, Stopwatch clock)
    {
        if (!config.Prove)
            return Problem(config, "unexpected_proof: this session was not given the proof tool", clock);

        if (!root.TryGetProperty("tool_input", out var input) || !ClaudeClarificationServer.TryReadProof(input, out var proof, out _))
            return Problem(config, $"malformed_proof: {Truncate(root.TryGetProperty("tool_input", out _) ? "invalid tool input" : "no tool input", 80)}", clock);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindProof,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = Text(root, "tool_use_id"),
            ToolName = ClaudeEvidenceFormat.ProofToolFullName,
            Proof = new RawProof
            {
                Summary = proof.Summary,
                Claims = [.. proof.Claims.Select(c => new RawProofClaimResult { ClaimId = c.ClaimId, Verdict = c.Verdict, Evidence = [.. c.Evidence], Summary = c.Summary })],
            },
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    private static RawEvent ToolRecord(ChannelConfig config, JsonElement root, string kind, Stopwatch clock)
    {
        var toolUseId = Text(root, "tool_use_id");

        if (string.IsNullOrWhiteSpace(toolUseId))
        {
            return Problem(config, "malformed_hook_payload: tool event without tool_use_id", clock);
        }

        var (command, truncated) = CommandOf(root);

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = kind,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            Cwd = Text(root, "cwd"),
            AgentId = Text(root, "agent_id"),
            ToolUseId = toolUseId,
            ToolName = Text(root, "tool_name"),
            Command = command,
            CommandTruncated = truncated,
            DurationMs = Number(root, "duration_ms"),
            Interrupted = Flag(root, "tool_response", "interrupted") || Flag(root, "is_interrupt"),
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    private static async Task<RawEvent> BatchRecordAsync(
        ChannelConfig config,
        JsonElement root,
        IImplementationStateProvider state,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("tool_calls", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Problem(config, "malformed_hook_payload: batch without tool_calls", clock);
        }

        var calls = new List<RawCall>();

        foreach (var call in array.EnumerateArray())
        {
            var (command, truncated) = CommandOf(call);

            calls.Add(new RawCall
            {
                ToolUseId = Text(call, "tool_use_id"),
                ToolName = Text(call, "tool_name"),
                Command = command,
                CommandTruncated = truncated,
                AgentId = Text(call, "agent_id"),
            });
        }

        var cwd = Text(root, "cwd");
        string? fingerprint = null;
        string? fingerprintError = null;

        if (calls.Count == 1 && IsProvable(config, calls[0], cwd))
        {
            try
            {
                var captured = await state.CaptureAsync(config.Workspace, cancellationToken).ConfigureAwait(false);
                fingerprint = captured.Fingerprint.Value;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                fingerprintError = $"{e.GetType().Name}: {Truncate(e.Message, 200)}";
            }
        }

        return new RawEvent
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindBatch,
            AtTicks = DateTime.UtcNow.Ticks,
            SessionId = config.SessionId,
            PromptId = Text(root, "prompt_id"),
            Cwd = cwd,
            AgentId = Text(root, "agent_id"),
            Calls = calls,
            Fingerprint = fingerprint,
            FingerprintError = fingerprintError,
            HandlerMs = clock.ElapsedMilliseconds,
        };
    }

    /// <summary>A lone command that is, word for word, a configured check in that check's directory: the only thing worth fingerprinting.</summary>
    private static bool IsProvable(ChannelConfig config, RawCall call, string? cwd)
        => ClaudeEvidenceFormat.IsCommandTool(call.ToolName)
           && !call.CommandTruncated
           && config.Candidates.Any(candidate =>
               CommandForm.Matches(candidate.Executable, candidate.Arguments, call.Command)
               && CommandForm.SameDirectory(cwd, CommandForm.ExpectedDirectory(config.Workspace, candidate.WorkingDirectory)));

    private static RawEvent Problem(ChannelConfig config, string problem, Stopwatch clock)
        => new()
        {
            RunId = config.RunId,
            Kind = ClaudeEvidenceFormat.KindProblem,
            AtTicks = DateTime.UtcNow.Ticks,
            Problem = problem,
            HandlerMs = clock.ElapsedMilliseconds,
        };

    private static ChannelConfig? ReadConfig(string directory)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(directory, ClaudeEvidenceFormat.ConfigFile));
            return JsonSerializer.Deserialize<ChannelConfig>(text, ClaudeEvidenceFormat.Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written under a temporary name and renamed, so the controller never reads a half-written record.</summary>
    private static void Write(string directory, RawEvent record)
    {
        var finalPath = Path.Combine(directory, ClaudeEvidenceFormat.EventFileName(record.AtTicks));
        var temporaryPath = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}");

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, ClaudeEvidenceFormat.Json));
        File.Move(temporaryPath, finalPath);
    }

    private static (string? Command, bool Truncated) CommandOf(JsonElement element)
    {
        if (!element.TryGetProperty("tool_input", out var input)
            || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("command", out var command)
            || command.ValueKind != JsonValueKind.String)
        {
            return (null, false);
        }

        var text = command.GetString();

        return text is { Length: > MaxRecordedCommandLength } ? (null, true) : (text, false);
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
            ? number
            : null;

    private static bool Flag(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return false;
            }
        }

        return current.ValueKind == JsonValueKind.True;
    }

    private static string Truncate(string? text, int length)
        => text is null ? "(none)" : text.Length <= length ? text : text[..length];
}
