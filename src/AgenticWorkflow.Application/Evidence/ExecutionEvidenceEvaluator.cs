using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Evidence;

/// <summary>The controller's decision about one check: reuse the runtime's record of it, or run it. Always carries a stable reason.</summary>
public sealed record EvidenceDecision(
    VerificationCheckId CheckId,
    bool Reusable,
    string Reason,
    AgentObservation? Observation = null)
{
    public static EvidenceDecision Rerun(VerificationCheckId checkId, string reason) => new(checkId, false, reason);
}

/// <summary>
/// Decides, for one declared check, whether the native runtime's record of the agent's own execution may stand in for the
/// controller running the check. The answer is "reuse" only when ALL of this holds, otherwise the controller executes the check
/// exactly as it would without any evidence:
/// <list type="number">
/// <item>the record is trustworthy as a whole and the check expects exit code 0 (the runtime's success event means exit 0);</item>
/// <item>some command is, word for word, the check's executable and arguments (<see cref="CommandForm"/>), in the check's directory;</item>
/// <item>it succeeded, ran alone in its batch (concurrent tools have no order), and its tool-use id appears exactly once;</item>
/// <item>the state captured right after it equals the implementation state being verified now;</item>
/// <item>nothing later in the turn could have changed that state: later activity is read-only, a known read-only command, or
/// another configured check whose own captured state is identical;</item>
/// <item>it did not take longer than the check's own timeout, and no other check already consumed it.</item>
/// </list>
/// This type only reads; it grants nothing the Core invariants and the completion rule do not independently require.
/// </summary>
public sealed class ExecutionEvidenceEvaluator
{
    private enum Stage
    {
        Succeeded,
        ExitProven,
        WorkingDirectory,
        SingleToolBatch,
        UniqueEvent,
        WithinTimeout,
        StateCaptured,
        StateCurrent,
        NothingLaterChangedState,
        NotAlreadyUsed,
    }

    private static readonly IReadOnlyDictionary<Stage, string> StageReasons = new Dictionary<Stage, string>
    {
        [Stage.Succeeded] = "agent_command_failed",
        [Stage.ExitProven] = "exit_unproven",
        [Stage.WorkingDirectory] = "working_directory_mismatch",
        [Stage.SingleToolBatch] = "ambiguous_parallel_batch",
        [Stage.UniqueEvent] = "duplicate_event",
        [Stage.WithinTimeout] = "agent_run_exceeded_check_timeout",
        [Stage.StateCaptured] = "no_state_captured",
        [Stage.StateCurrent] = "state_mismatch",
        [Stage.NothingLaterChangedState] = "state_may_have_changed_later",
        [Stage.NotAlreadyUsed] = "already_used_by_another_check",
    };

    private readonly IReadOnlyList<VerificationCheckSpec> _declaredChecks;

    public ExecutionEvidenceEvaluator(IReadOnlyList<VerificationCheckSpec> declaredChecks)
    {
        ArgumentNullException.ThrowIfNull(declaredChecks);

        _declaredChecks = declaredChecks;
    }

    /// <param name="usedToolUseIds">Activities already accepted for another check in this verification; each is consumed at most once. Updated on reuse.</param>
    public EvidenceDecision Evaluate(
        string workspacePath,
        VerificationCheckSpec check,
        ObservedTurnEvidence? evidence,
        ImplementationFingerprint current,
        ISet<string> usedToolUseIds)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(usedToolUseIds);

        if (evidence is null)
        {
            return EvidenceDecision.Rerun(check.Id, "no_evidence_available");
        }

        if (!evidence.IsTrustworthy)
        {
            return EvidenceDecision.Rerun(check.Id, $"evidence_untrusted: {evidence.Problems[0]}");
        }

        if (check.ExpectedExitCode != 0)
        {
            return EvidenceDecision.Rerun(check.Id, "expected_exit_code_not_zero");
        }

        var expectedDirectory = CommandForm.ExpectedDirectory(workspacePath, check.WorkingDirectory);
        var duplicated = evidence.Activities
            .GroupBy(a => a.ToolUseId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var sameForm = evidence.Activities
            .Where(a => a.Kind == ObservedActivityKind.Command && CommandForm.Matches(check.Executable, check.Arguments, a.Command))
            .OrderByDescending(a => a.Batch)
            .ToList();

        if (sameForm.Count == 0)
        {
            return EvidenceDecision.Rerun(check.Id, "command_mismatch");
        }

        // Each candidate is judged by the first condition it fails; the report is the one that got furthest.
        var furthest = (Stage)(-1);

        foreach (var candidate in sameForm)
        {
            var failed = FirstFailure(candidate, check, expectedDirectory, current, evidence, duplicated, usedToolUseIds);

            if (failed is null)
            {
                var observation = new AgentObservation(
                    evidence.Runtime,
                    candidate.ToolUseId,
                    CommandForm.Identity(check.Executable, check.Arguments),
                    candidate.DurationMilliseconds,
                    candidate.StateAfterBatch!);

                usedToolUseIds.Add(candidate.ToolUseId);

                return new EvidenceDecision(check.Id, true, "agent_observed_current_state", observation);
            }

            if (failed.Value > furthest)
            {
                furthest = failed.Value;
            }
        }

        return EvidenceDecision.Rerun(check.Id, StageReasons[furthest]);
    }

    private Stage? FirstFailure(
        ObservedActivity candidate,
        VerificationCheckSpec check,
        string expectedDirectory,
        ImplementationFingerprint current,
        ObservedTurnEvidence evidence,
        ISet<string> duplicated,
        ISet<string> used)
    {
        if (!candidate.Succeeded)
        {
            // Not reported as failed, but the runtime cannot prove exit 0: unproven, not failed.
            return candidate.ExitUnproven ? Stage.ExitProven : Stage.Succeeded;
        }

        if (!CommandForm.SameDirectory(candidate.WorkingDirectory, expectedDirectory))
        {
            return Stage.WorkingDirectory;
        }

        if (candidate.BatchSize != 1)
        {
            return Stage.SingleToolBatch;
        }

        if (duplicated.Contains(candidate.ToolUseId))
        {
            return Stage.UniqueEvent;
        }

        if (candidate.DurationMilliseconds is { } milliseconds && milliseconds > check.Timeout.TotalMilliseconds)
        {
            return Stage.WithinTimeout;
        }

        if (candidate.StateAfterBatch is null)
        {
            return Stage.StateCaptured;
        }

        if (candidate.StateAfterBatch != current)
        {
            return Stage.StateCurrent;
        }

        if (evidence.Activities.Any(later => later.Batch > candidate.Batch && !IsInertAfter(candidate, later)))
        {
            return Stage.NothingLaterChangedState;
        }

        if (used.Contains(candidate.ToolUseId))
        {
            return Stage.NotAlreadyUsed;
        }

        return null;
    }

    /// <summary>Later activity that provably left the implementation state alone, so it cannot invalidate <paramref name="earlier"/>.</summary>
    private bool IsInertAfter(ObservedActivity earlier, ObservedActivity later)
    {
        switch (later.Kind)
        {
            case ObservedActivityKind.ReadOnly:
                return true;

            case ObservedActivityKind.Command:
                if (CommandForm.IsKnownReadOnly(later.Command))
                {
                    return true;
                }

                // Another configured check, alone in its batch, that captured exactly the same state: it left the state as it found it.
                return later.BatchSize == 1
                       && later.StateAfterBatch is { } after
                       && after == earlier.StateAfterBatch
                       && _declaredChecks.Any(c => CommandForm.Matches(c.Executable, c.Arguments, later.Command));

            default:
                return false;
        }
    }
}
