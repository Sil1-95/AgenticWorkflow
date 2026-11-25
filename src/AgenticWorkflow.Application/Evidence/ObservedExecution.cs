using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Evidence;

// Provider-neutral execution evidence. A native harness that can report, in a structured and trustworthy way, what its
// agent executed implements IExecutionEvidenceSource. Nothing here knows a hook format, Claude, Git or a file layout. The
// controller (ExecutionEvidenceEvaluator) decides whether any of it is sufficient; a harness without this capability simply
// yields no evidence and the controller runs every check itself, exactly as before.

/// <summary>What kind of thing the agent did, as far as workspace state is concerned.</summary>
public enum ObservedActivityKind
{
    /// <summary>The agent ran a shell command (see <see cref="ObservedActivity.Command"/>).</summary>
    Command,

    /// <summary>A tool known not to change the workspace (reading, searching).</summary>
    ReadOnly,

    /// <summary>A tool known to change workspace files (editing, writing).</summary>
    WorkspaceMutation,

    /// <summary>Anything the adapter cannot classify. Treated as possibly changing state.</summary>
    Unknown,
}

/// <summary>
/// One tool execution the native runtime reported, after it resolved. <see cref="Batch"/> orders resolved tool batches
/// within the turn (0, 1, 2, ...; strictly increasing, one batch resolves before the next begins), and
/// <see cref="BatchSize"/> is how many tools ran in that batch: activities of one batch ran concurrently and have no
/// defined order among themselves.
/// </summary>
public sealed record ObservedActivity
{
    public required string ToolUseId { get; init; }

    public required int Batch { get; init; }

    public required int BatchSize { get; init; }

    public required ObservedActivityKind Kind { get; init; }

    /// <summary>The command text exactly as the agent ran it. Only for <see cref="ObservedActivityKind.Command"/>.</summary>
    public string? Command { get; init; }

    /// <summary>The directory the runtime reported the activity ran in.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// The runtime PROVED the command finished with exit code 0. A runtime that cannot prove that (for example because its harness
    /// reports some non-zero exits as success) reports false. Only meaningful for commands.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// The runtime reported the command as completed without failure, but cannot prove its exit code was 0 (see
    /// <see cref="Succeeded"/>). Diagnostic only: it is never evidence of success, so such a command is still never reusable.
    /// Mutually exclusive with <see cref="Succeeded"/>.
    /// </summary>
    public bool ExitUnproven { get; init; }

    public long? DurationMilliseconds { get; init; }

    /// <summary>
    /// The exact implementation state captured by the runtime's adapter right after the activity's batch resolved. Captured
    /// only for batches that are worth proving (a lone command that looks like a configured check), never for reads or searches.
    /// </summary>
    public ImplementationFingerprint? StateAfterBatch { get; init; }
}

/// <summary>
/// Everything the runtime reported for ONE agent turn of ONE session, in order. <see cref="Problems"/> lists anything that
/// makes the record untrustworthy as a whole (a malformed or foreign event, an event outside its session, out-of-order
/// batches...). Any problem means the controller ignores the whole record.
/// </summary>
public sealed record ObservedTurnEvidence(string Runtime, IReadOnlyList<ObservedActivity> Activities, IReadOnlyList<string> Problems)
{
    public bool IsTrustworthy => Problems.Count == 0;

    public static ObservedTurnEvidence Empty(string runtime) => new(runtime, [], []);
}

/// <summary>
/// Optional capability of an agent runtime: structured, runtime-produced evidence of what the agent executed. It is the
/// runtime's own record, never the agent's words; screen text and transcript prose are not sources.
/// <para>
/// Evidence is scoped to one session and one turn: <see cref="BeginTurnAsync"/> marks the start of a turn window (call it
/// before the prompt is sent) and <see cref="CollectTurnAsync"/> returns only what was observed since. Failure to collect is
/// never fatal to a run; the controller falls back to running its checks.
/// </para>
/// </summary>
public interface IExecutionEvidenceSource
{
    /// <summary>Starts a new turn window for <paramref name="session"/>. Anything observed earlier is never part of a later window.</summary>
    Task BeginTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default);

    /// <summary>
    /// What was observed since the latest <see cref="BeginTurnAsync"/>, or null when this session has no evidence capability
    /// (it was not launched with it, or the runtime cannot provide it).
    /// </summary>
    Task<ObservedTurnEvidence?> CollectTurnAsync(AgentSessionId session, CancellationToken cancellationToken = default);
}
