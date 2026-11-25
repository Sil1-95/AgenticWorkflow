using System.Collections.Immutable;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Core.Verification;

/// <summary>Stable identity of one deterministic check (<c>tests</c>, <c>symbol_new_present</c>, ...).</summary>
public sealed record VerificationCheckId : StableIdentifier
{
    public VerificationCheckId(string value)
        : base(value)
    {
    }
}

/// <summary>
/// The result of a lightweight verification, whole or per check. There is deliberately no pending or stale
/// value: a verification run exists only once it has finished, and staleness is derived by comparing the
/// implementation revision it targets with the task's current one.
/// </summary>
public enum VerificationOutcome
{
    Passed,
    Failed,
}

/// <summary>Who actually executed the check whose result a <see cref="VerificationCheckResult"/> records.</summary>
public enum VerificationEvidenceSource
{
    /// <summary>The controller ran the check itself, through its own verification runner.</summary>
    ControllerExecuted,

    /// <summary>
    /// The native agent runtime executed the check and the controller accepted the runtime's authoritative execution
    /// record of it (see <see cref="AgentObservation"/>). This is NOT "the agent says it passed": the controller still
    /// decides sufficiency, and the observation binds to the exact implementation state it ran against.
    /// </summary>
    AgentObserved,
}

/// <summary>
/// Bounded provenance of a check the controller accepted from the native runtime's structured execution record instead
/// of running it again. Never contains command output. <see cref="StateFingerprint"/> is the implementation state the
/// runtime reported as the state right after that execution, and Core requires it to equal the verified revision's.
/// </summary>
public sealed record AgentObservation
{
    public const int MaxRuntimeLength = 64;
    public const int MaxToolUseIdLength = 128;
    public const int MaxCommandIdentityLength = 500;

    public AgentObservation(
        string runtime,
        string toolUseId,
        string commandIdentity,
        long? durationMilliseconds,
        ImplementationFingerprint stateFingerprint)
    {
        Guard.NotBlank(runtime, nameof(runtime));
        Guard.NotBlank(toolUseId, nameof(toolUseId));
        Guard.NotBlank(commandIdentity, nameof(commandIdentity));

        if (runtime.Length > MaxRuntimeLength)
            throw new ArgumentException($"The runtime name must not exceed {MaxRuntimeLength} characters.", nameof(runtime));

        if (toolUseId.Length > MaxToolUseIdLength)
            throw new ArgumentException($"The tool-use id must not exceed {MaxToolUseIdLength} characters.", nameof(toolUseId));

        if (commandIdentity.Length > MaxCommandIdentityLength)
            throw new ArgumentException($"The command identity must not exceed {MaxCommandIdentityLength} characters.", nameof(commandIdentity));

        if (durationMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds), durationMilliseconds, "A duration cannot be negative.");

        Runtime = runtime;
        ToolUseId = toolUseId;
        CommandIdentity = commandIdentity;
        DurationMilliseconds = durationMilliseconds;
        StateFingerprint = Guard.NotNull(stateFingerprint, nameof(stateFingerprint));
    }

    /// <summary>The harness that executed the check (<c>claude-code</c>).</summary>
    public string Runtime { get; }

    public string ToolUseId { get; }

    /// <summary>The normalized command the runtime executed (executable and arguments, space separated).</summary>
    public string CommandIdentity { get; }

    public long? DurationMilliseconds { get; }

    public ImplementationFingerprint StateFingerprint { get; }
}

/// <summary>
/// The outcome of one executable check. Small by design: a short <see cref="Summary"/> and an optional exit
/// code. Full command output never belongs in workflow history. A result carries an <see cref="Observation"/> exactly
/// when the runtime, not the controller, executed the check; only passing results can be agent-observed.
/// <para>
/// <see cref="EvidenceReason"/> is the controller's own bounded, single-line reason for that choice (reuse the runtime's record, or
/// run the check itself). It is diagnostic only and never changes the outcome; it is null for checks that are not subject to reuse
/// (the scope and workspace-stability sensors) and for results recorded before reasons were kept.
/// </para>
/// </summary>
public sealed record VerificationCheckResult
{
    public const int MaxSummaryLength = 1000;
    public const int MaxEvidenceReasonLength = 300;

    public VerificationCheckResult(
        VerificationCheckId checkId,
        VerificationOutcome outcome,
        string summary,
        int? exitCode = null,
        AgentObservation? observation = null,
        string? evidenceReason = null)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown verification outcome.");

        Guard.NotBlank(summary, nameof(summary));
        if (summary.Length > MaxSummaryLength)
            throw new ArgumentException($"A check summary must not exceed {MaxSummaryLength} characters.", nameof(summary));

        if (observation is not null && outcome != VerificationOutcome.Passed)
            throw new InvalidVerificationException("Only a passing check can be accepted from an agent observation.");

        if (evidenceReason is not null)
        {
            Guard.NotBlank(evidenceReason, nameof(evidenceReason));

            if (evidenceReason.Length > MaxEvidenceReasonLength)
                throw new ArgumentException($"An evidence reason must not exceed {MaxEvidenceReasonLength} characters.", nameof(evidenceReason));

            if (evidenceReason.Any(char.IsControl))
                throw new ArgumentException("An evidence reason must be a single line without control characters.", nameof(evidenceReason));
        }

        CheckId = Guard.NotNull(checkId, nameof(checkId));
        Outcome = outcome;
        Summary = summary;
        ExitCode = exitCode;
        Observation = observation;
        EvidenceReason = evidenceReason;
    }

    public VerificationCheckId CheckId { get; }

    public VerificationOutcome Outcome { get; }

    public string Summary { get; }

    public int? ExitCode { get; }

    /// <summary>Provenance when the runtime executed the check; null when the controller did.</summary>
    public AgentObservation? Observation { get; }

    /// <summary>Why the check was reused from the runtime or executed by the controller (a stable code, optionally followed by bounded detail); null when none was recorded.</summary>
    public string? EvidenceReason { get; }

    public VerificationEvidenceSource Source
        => Observation is null ? VerificationEvidenceSource.ControllerExecuted : VerificationEvidenceSource.AgentObserved;
}

/// <summary>
/// One finished lightweight verification of an exact <see cref="ImplementationRevisionId"/>. The evidence binds to
/// the implementation revision (which already identifies its semantic revision), never to "whatever is current".
/// <para>
/// Invariants: at least one check, check ids unique within the run, and <see cref="Outcome"/> is
/// <see cref="VerificationOutcome.Passed"/> if and only if every check passed.
/// </para>
/// </summary>
public sealed record VerificationRun
{
    public VerificationRun(
        VerificationRunId id,
        TaskId taskId,
        ImplementationRevisionId implementationRevisionId,
        VerificationOutcome outcome,
        IEnumerable<VerificationCheckResult> checks,
        DateTimeOffset timestamp)
    {
        Guard.NotNull(checks, nameof(checks));

        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown verification outcome.");

        var list = checks.ToImmutableArray();

        if (list.IsEmpty)
            throw new InvalidVerificationException("A verification run needs at least one check.");

        if (list.Any(c => c is null))
            throw new InvalidVerificationException("A verification run cannot contain a null check result.");

        if (list.Select(c => c.CheckId).Distinct().Count() != list.Length)
            throw new InvalidVerificationException("Check ids must be unique within a verification run.");

        var allPassed = list.All(c => c.Outcome == VerificationOutcome.Passed);
        if (allPassed != (outcome == VerificationOutcome.Passed))
            throw new InvalidVerificationException(
                $"Verification outcome '{outcome}' is inconsistent with its checks "
                + $"({list.Count(c => c.Outcome == VerificationOutcome.Failed)} of {list.Length} failed).");

        Id = id;
        TaskId = taskId;
        ImplementationRevisionId = implementationRevisionId;
        Outcome = outcome;
        Checks = list;
        Timestamp = timestamp;
    }

    public VerificationRunId Id { get; }

    public TaskId TaskId { get; }

    public ImplementationRevisionId ImplementationRevisionId { get; }

    public VerificationOutcome Outcome { get; }

    public ImmutableArray<VerificationCheckResult> Checks { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>The aggregate outcome a set of check results implies.</summary>
    public static VerificationOutcome AggregateOf(IEnumerable<VerificationCheckResult> checks)
        => Guard.NotNull(checks, nameof(checks)).All(c => c.Outcome == VerificationOutcome.Passed)
            ? VerificationOutcome.Passed
            : VerificationOutcome.Failed;

    // Value equality across the check list, so replay equality is directly assertable.
    public bool Equals(VerificationRun? other)
        => other is not null
           && Id == other.Id
           && TaskId == other.TaskId
           && ImplementationRevisionId == other.ImplementationRevisionId
           && Outcome == other.Outcome
           && Timestamp == other.Timestamp
           && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(Id, TaskId, ImplementationRevisionId, Outcome);
}
