using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Execution;

// Provider-neutral execution ports. Nothing in this file knows Herdr, Claude, Git, SQLite or a shell; adapters
// implement these and the direct-path runner only ever sees them.

/// <summary>An agent session owned by an <see cref="IAgentRuntime"/>. Operational state; not (yet) workflow history.</summary>
public readonly record struct AgentSessionId
{
    public AgentSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A session id must not be blank.", nameof(value));

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>1-based position of a turn within one session: the first prompt sent is turn 1.</summary>
public readonly record struct AgentTurnNumber
{
    public AgentTurnNumber(int value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Turns are numbered from 1.");

        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// What a runtime reports about a turn, from its structured status (never inferred from output text). <see cref="Done"/>, <see cref="Error"/> and
/// <see cref="Cancelled"/> end the turn; <see cref="AwaitingHuman"/> and <see cref="Resumed"/> are observations about it: the turn is still going.
/// </summary>
public enum AgentTurnStatus
{
    /// <summary>The agent says it finished its turn. This is a signal, not a workflow fact.</summary>
    Done,

    /// <summary>
    /// The agent is waiting for a person to act in its own native interface (a tool permission, a question, a login). The session is alive and
    /// the person answers there; the controller neither sees nor owns the answer. Reported once per interaction, with
    /// <see cref="AgentTurnOutcome.Interaction"/>.
    /// </summary>
    AwaitingHuman,

    /// <summary>
    /// The native interaction ended and the agent is working again. It says nothing about what the person chose (allow once, a session grant,
    /// deny). Reported once, after <see cref="AwaitingHuman"/>.
    /// </summary>
    Resumed,

    /// <summary>The agent or its host reported an error, exited, or the turn timed out.</summary>
    Error,

    /// <summary>The turn ended because the session was stopped or interrupted.</summary>
    Cancelled,
}

/// <summary>A turn's structured outcome and, when not <see cref="AgentTurnStatus.Done"/>, the runtime's reason.</summary>
public sealed record AgentTurnOutcome(AgentTurnStatus Status, string? Reason = null)
{
    public static AgentTurnOutcome Done { get; } = new(AgentTurnStatus.Done);

    public static AgentTurnOutcome Resumed { get; } = new(AgentTurnStatus.Resumed);

    /// <summary>Set exactly when <see cref="Status"/> is <see cref="AgentTurnStatus.AwaitingHuman"/>.</summary>
    public AgentInteraction? Interaction { get; init; }

    public static AgentTurnOutcome AwaitingHuman(AgentInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        return new AgentTurnOutcome(AgentTurnStatus.AwaitingHuman, interaction.Description) { Interaction = interaction };
    }
}

/// <summary>What the agent is waiting for a person to do, as its own harness reports it. The controller never answers it.</summary>
public enum AgentInteractionKind
{
    /// <summary>The harness did not say what it is waiting for.</summary>
    Unspecified,

    /// <summary>A tool permission prompt.</summary>
    Permission,

    /// <summary>A question to the person.</summary>
    Question,

    /// <summary>A login or other authentication.</summary>
    Authentication,
}

/// <summary>
/// Where a person answers a native interaction: the host session (for Herdr, the isolated core's <c>HC_SESSION</c>) and the terminal inside it. Structured
/// and provider-neutral so no consumer has to parse text; it is a pointer, never authority, and nothing here can answer on the person's behalf.
/// </summary>
public sealed record NativeAttachTarget(string HostSession, string Terminal);

/// <summary>
/// A native human interaction the agent is blocked on. <see cref="Description"/> is the harness's own short text (for example which operation is
/// waiting for approval); <see cref="Attach"/> says where the person answers, when the runtime knows. Both are display data, never authority.
/// </summary>
public sealed record AgentInteraction(AgentInteractionKind Kind, string Description, NativeAttachTarget? Attach = null);

/// <summary>
/// What to start. The runtime maps <see cref="Profile"/> to its own launch configuration and must refuse an unknown one.
/// </summary>
public sealed record AgentSessionRequest(ProfileId Profile, string WorkspacePath, string Label)
{
    /// <summary>
    /// What the session is for. A runtime launches a <see cref="AgentSessionPurpose.Review"/> session with the reviewer's own, read-only configuration and offers it
    /// only the review-report capability, even when the reviewer profile is the very profile the implementer uses (the binding is explicit; the configuration is not shared).
    /// A <see cref="AgentSessionPurpose.Prove"/> session is the same for the prover: its own read-only configuration and only the proof-report capability.
    /// </summary>
    public AgentSessionPurpose Purpose { get; init; } = AgentSessionPurpose.Implementation;

    /// <summary>
    /// Optional: the controller's checks, so a runtime that can observe the agent's own executions may report those that
    /// are exactly such a check (see <c>IExecutionEvidenceSource</c>). A runtime without that capability ignores it.
    /// </summary>
    public IReadOnlyList<VerificationCheckSpec> ObservableChecks { get; init; } = [];

    /// <summary>
    /// Set only when a person is attending. A runtime whose agent needs a person while it is still starting (its first-run questions: trust this folder, log in) then keeps
    /// the session alive and reports the wait here instead of failing the start; the person answers it in the agent's own session. Absent, such a startup fails as it always did.
    /// </summary>
    public IAgentStartupInteractions? StartupInteractions { get; init; }
}

/// <summary>
/// Told, while a session is starting, that its agent is waiting for a person in its own interface and when that wait ends. Like every native interaction it is only
/// reported: nothing here ever answers, allows or denies anything.
/// </summary>
public interface IAgentStartupInteractions
{
    /// <summary>The agent is waiting for a person (reported once per wait).</summary>
    Task OpenedAsync(AgentInteraction interaction, CancellationToken cancellationToken);

    /// <summary>The wait is over: the agent is no longer waiting (reported once per opened wait).</summary>
    Task ClosedAsync(CancellationToken cancellationToken);
}

/// <summary>What a requested session is for.</summary>
public enum AgentSessionPurpose
{
    /// <summary>The working agent: edits, repairs, and may raise the workflow signals.</summary>
    Implementation,

    /// <summary>The independent reviewer: a fresh conversation that reads the implementation and submits exactly one structured report.</summary>
    Review,

    /// <summary>The independent prover: a fresh, read-only conversation that judges the Proof Contract claim by claim and submits exactly one structured proof.</summary>
    Prove,
}

/// <summary>An agent runtime failed to start, drive or stop a session (as opposed to the agent reporting an outcome).</summary>
public class AgentRuntimeException : Exception
{
    public AgentRuntimeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The agent could not become ready because something outside the controller's authority is waiting for a person (workspace trust, login,
/// an update prompt). Nothing was answered on its behalf. A typed condition: callers never infer it from message text.
/// </summary>
public sealed class AgentStartupBlockedException : AgentRuntimeException
{
    public AgentStartupBlockedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Minimum port for persistent, multi-turn agent sessions: start one session, then send turns to it and wait for
/// each outcome, then stop it. One prompt is never one process: the same <see cref="AgentSessionId"/> serves
/// every turn.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>Starts a session and returns once it is ready for its first turn. Throws <see cref="AgentRuntimeException"/>.</summary>
    Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Delivers <paramref name="prompt"/> as the next turn and returns that turn's number.</summary>
    Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the structured outcome of <paramref name="turn"/>; a timeout is reported as <see cref="AgentTurnStatus.Error"/>. It may first return
    /// <see cref="AgentTurnStatus.AwaitingHuman"/> and <see cref="AgentTurnStatus.Resumed"/>: observations that the turn is not over, after which the
    /// caller waits again. A native interaction is never answered through this port.
    /// </summary>
    Task<AgentTurnOutcome> WaitForTurnAsync(
        AgentSessionId session,
        AgentTurnNumber turn,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Stops and releases only this session. Never touches any other session.</summary>
    Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default);
}

/// <summary>
/// An exact, read-only capture of a workspace. <see cref="Fingerprint"/> is the opaque identity Core records;
/// <see cref="BaseIdentity"/> is what the changes are relative to (for Git, HEAD); <see cref="PathDigests"/> maps each
/// changed path (relative, '/'-separated) to a digest of its state so two captures can be compared per path.
/// </summary>
public sealed record ImplementationState(
    ImplementationFingerprint Fingerprint,
    string BaseIdentity,
    IReadOnlyDictionary<string, string> PathDigests);

/// <summary>Captures the exact implementation state of a workspace without modifying it.</summary>
public interface IImplementationStateProvider
{
    Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// One configured deterministic check: an executable with an argument list (never a shell string), a time limit
/// and the exit code that means "passed". <see cref="Id"/> is the stable identity recorded in Core.
/// </summary>
public sealed record VerificationCheckSpec
{
    public VerificationCheckSpec(
        VerificationCheckId id,
        string description,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        int expectedExitCode = 0)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(arguments);

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A check needs a description.", nameof(description));

        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("A check needs an executable.", nameof(executable));

        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A check needs a positive timeout.");

        Id = id;
        Description = description;
        Executable = executable;
        Arguments = arguments.ToArray();
        Timeout = timeout;
        WorkingDirectory = workingDirectory;
        ExpectedExitCode = expectedExitCode;
    }

    public VerificationCheckId Id { get; }

    public string Description { get; }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public TimeSpan Timeout { get; }

    /// <summary>Relative to the workspace, or null for the workspace root.</summary>
    public string? WorkingDirectory { get; }

    public int ExpectedExitCode { get; }
}

/// <summary>
/// What running one check produced. <see cref="StdOutTail"/>/<see cref="StdErrTail"/> are bounded diagnostics for
/// the run result and repair feedback; they are never written to workflow history.
/// </summary>
public sealed record CheckExecution(
    int? ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    string StdOutTail,
    string StdErrTail,
    string? StartError = null);

/// <summary>Runs one configured check inside a workspace and reports how it ended. Does not decide pass/fail.</summary>
public interface IVerificationRunner
{
    Task<CheckExecution> RunAsync(string workspacePath, VerificationCheckSpec check, CancellationToken cancellationToken = default);
}
