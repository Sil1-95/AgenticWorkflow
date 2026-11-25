using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

/// <summary>
/// Turns the hook records of one turn window into provider-neutral <see cref="ObservedTurnEvidence"/>. It classifies Claude's tool
/// names, orders activities by the harness's own batch boundaries (never by guessing among concurrent tools), and reports as
/// <see cref="ObservedTurnEvidence.Problems"/> everything that makes the record unsafe as a whole. It decides nothing about
/// reuse: that is the controller's evaluator.
/// </summary>
internal static class ClaudeEvidenceTranslator
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "Read", "Grep", "Glob", "LS", "NotebookRead", "TodoWrite", "ToolSearch",
    };

    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    /// <summary>
    /// Executables whose exit status Claude Code reports literally: a <c>PostToolUse</c> (as opposed to <c>PostToolUseFailure</c>)
    /// means exit code 0. Claude Code gives other commands "semantic" exit codes (for example <c>git grep</c>, <c>grep</c>, <c>diff</c>
    /// and <c>test</c> exiting 1 is reported as a success), and the hook payload carries no exit code, so for those a
    /// <c>PostToolUse</c> does NOT prove exit 0 and must never stand in for a controller run. Extend this set only after verifying,
    /// live, that a non-zero exit of that executable arrives as <c>PostToolUseFailure</c>.
    /// </summary>
    internal static readonly IReadOnlySet<string> LiteralExitStatusExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dotnet" };

    internal static bool ProvesExitZero(string? command)
        => CommandForm.Tokenize(command) is { } tokens && LiteralExitStatusExecutables.Contains(tokens[0]);

    /// <param name="controllerTurns">How many turns the controller sent inside this window: one prompt per turn is expected, so more prompts than turns is a window that mixed in something else.</param>
    public static ObservedTurnEvidence Translate(
        string runId,
        string sessionId,
        IReadOnlyList<RawEvent> events,
        IReadOnlyList<string> readProblems,
        int controllerTurns = 1)
    {
        var problems = new List<string>(readProblems);

        foreach (var evt in events)
        {
            if (!string.Equals(evt.RunId, runId, StringComparison.Ordinal))
            {
                problems.Add("foreign_run: a record does not belong to this evidence channel");
            }

            if (evt.Kind == ClaudeEvidenceFormat.KindProblem)
            {
                problems.Add(evt.Problem ?? "unspecified hook problem");
            }
            else if (!string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal))
            {
                problems.Add("foreign_session: a record does not belong to the owned session");
            }
        }

        var tracked = events.Where(e => e.Kind is not (ClaudeEvidenceFormat.KindProblem or ClaudeEvidenceFormat.KindClarification or ClaudeEvidenceFormat.KindWhat or ClaudeEvidenceFormat.KindHow or ClaudeEvidenceFormat.KindReclassification or ClaudeEvidenceFormat.KindReviewRequest or ClaudeEvidenceFormat.KindReview or ClaudeEvidenceFormat.KindProveRequest or ClaudeEvidenceFormat.KindProof)).ToList();

        if (tracked.Select(e => e.PromptId).Where(p => p is not null).Distinct().Count() > Math.Max(1, controllerTurns))
        {
            problems.Add("multiple_prompts_in_turn: records of more than one prompt fall into this turn window");
        }

        var toolEvents = tracked.Where(e => e.Kind is ClaudeEvidenceFormat.KindToolSuccess or ClaudeEvidenceFormat.KindToolFailure).ToList();
        var batches = tracked.Where(e => e.Kind == ClaudeEvidenceFormat.KindBatch).OrderBy(e => e.AtTicks).ToList();

        var referenced = new HashSet<RawEvent>(ReferenceEqualityComparer.Instance);
        var activities = new List<ObservedActivity>();

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var calls = batch.Calls ?? [];
            var fingerprint = batch.Fingerprint is { Length: > 0 } text ? new ImplementationFingerprint(text) : null;

            for (var callIndex = 0; callIndex < calls.Count; callIndex++)
            {
                var call = calls[callIndex];
                var id = string.IsNullOrWhiteSpace(call.ToolUseId) ? $"unidentified-{batchIndex}-{callIndex}" : call.ToolUseId;

                if (!ClaudeEvidenceFormat.IsCommandTool(call.ToolName))
                {
                    activities.Add(new ObservedActivity
                    {
                        ToolUseId = id,
                        Batch = batchIndex,
                        BatchSize = calls.Count,
                        Kind = call.AgentId is null ? ClassifyOther(call.ToolName) : ObservedActivityKind.Unknown,
                        StateAfterBatch = fingerprint,
                    });
                    continue;
                }

                var matching = toolEvents.Where(e => string.Equals(e.ToolUseId, call.ToolUseId, StringComparison.Ordinal)).ToList();

                if (matching.Count == 0)
                {
                    // The batch says it ran but no outcome was recorded: it did not demonstrably succeed.
                    activities.Add(CommandActivity(id, batchIndex, calls.Count, call, null, batch, fingerprint));
                    continue;
                }

                foreach (var evt in matching)
                {
                    referenced.Add(evt);

                    if (evt.AtTicks > batch.AtTicks)
                    {
                        problems.Add($"out_of_order: the outcome of tool use {id} was recorded after its batch resolved");
                    }

                    if (evt.Command is not null && call.Command is not null && !string.Equals(evt.Command, call.Command, StringComparison.Ordinal))
                    {
                        problems.Add($"command_disagreement: tool use {id} was recorded with two different commands");
                    }

                    activities.Add(CommandActivity(id, batchIndex, calls.Count, call, evt, batch, fingerprint));
                }
            }
        }

        foreach (var evt in toolEvents.Where(e => !referenced.Contains(e)))
        {
            problems.Add($"tool_event_without_batch: tool use {evt.ToolUseId} has no resolved batch");
        }

        return new ObservedTurnEvidence(ClaudeEvidenceFormat.RuntimeName, activities, problems.Distinct().ToList());
    }

    private static ObservedActivity CommandActivity(
        string id,
        int batchIndex,
        int batchSize,
        RawCall call,
        RawEvent? outcome,
        RawEvent batch,
        ImplementationFingerprint? fingerprint)
    {
        var delegated = call.AgentId is not null || outcome?.AgentId is not null;
        var reportedSuccess = !delegated && outcome is { Kind: ClaudeEvidenceFormat.KindToolSuccess, Interrupted: false };
        var provesExitZero = ProvesExitZero(outcome?.Command ?? call.Command);

        return new ObservedActivity
        {
            ToolUseId = id,
            Batch = batchIndex,
            BatchSize = batchSize,
            Kind = delegated ? ObservedActivityKind.Unknown : ObservedActivityKind.Command,
            Command = delegated ? null : outcome?.Command ?? call.Command,
            WorkingDirectory = outcome?.Cwd ?? batch.Cwd,
            Succeeded = reportedSuccess && provesExitZero,
            ExitUnproven = reportedSuccess && !provesExitZero,
            DurationMilliseconds = outcome?.DurationMs,
            StateAfterBatch = fingerprint,
        };
    }

    private static ObservedActivityKind ClassifyOther(string? toolName)
    {
        if (toolName is not null && ReadOnlyTools.Contains(toolName))
        {
            return ObservedActivityKind.ReadOnly;
        }

        return toolName is not null && MutatingTools.Contains(toolName)
            ? ObservedActivityKind.WorkspaceMutation
            : ObservedActivityKind.Unknown;
    }
}
