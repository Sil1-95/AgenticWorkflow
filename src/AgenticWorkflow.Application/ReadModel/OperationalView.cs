namespace AgenticWorkflow.Application.ReadModel;

/// <summary>Whether a process currently holds the run, so that an agent session of it can still be answered.</summary>
public enum Liveness
{
    /// <summary>A process holds the run now.</summary>
    Live,

    /// <summary>No process holds the run.</summary>
    NotLive,

    /// <summary>The host could not tell.</summary>
    Unknown,
}

/// <summary>Whether the working tree still is exactly the state the pending ACCEPT gate was verified against (checked at the moment the decision is made, too).</summary>
public enum WorkingTreeState
{
    Unchanged,
    Changed,

    /// <summary>It was not checked (no pending ACCEPT gate, an unknown workspace, or a workspace that could not be read).</summary>
    NotChecked,
}

/// <summary>
/// Facts about a run that are not workflow state. The host of the run supplies them (the CLI reads the run's own files; a controller will know them directly);
/// they annotate the model and never change what the workflow facts say. Everything here is optional: a fact that is unknown is null, never guessed.
/// </summary>
public sealed record OperationalView
{
    public required ProjectView? Project { get; init; }

    public required Liveness Liveness { get; init; }

    /// <summary>Where the agent waits for a person in its own interface, when the host recorded it.</summary>
    public required NativeInteractionView? NativeInteraction { get; init; }

    /// <summary>Whether a native conversation of the agent is recorded for resuming. The reference itself is never exposed.</summary>
    public required NativeConversationView? NativeConversation { get; init; }

    /// <summary>How the run's own process ended last, for context; the workflow facts are authoritative where it disagrees.</summary>
    public required LastProcessView? LastProcess { get; init; }

    /// <summary>The state of the working tree against a pending ACCEPT gate.</summary>
    public required WorkingTreeState WorkingTree { get; init; }

    /// <summary>Whether the workspace baseline a recoverable continuation needs is recorded; null when no continuation is pending.</summary>
    public required bool? ContinuationBaselineRecorded { get; init; }

    /// <summary>What the host could not read, one short line each (a missing metadata file, an unreadable record). Nothing was repaired or guessed.</summary>
    public required IReadOnlyList<string> Unavailable { get; init; }
}

/// <summary>The project the run belongs to, as the host recorded it when the run was created. The workflow stream itself records no project identity.</summary>
public sealed record ProjectView
{
    public required string Workspace { get; init; }

    public required string ConfigPath { get; init; }

    public required string ConfigSha256 { get; init; }

    public required DateTimeOffset RunCreatedAt { get; init; }
}

public sealed record NativeInteractionView
{
    /// <summary>The kind of interaction (for example <c>permission</c>).</summary>
    public required string Kind { get; init; }

    public required string? Description { get; init; }

    /// <summary>The Herdr session (core) the agent runs in, when the runtime said where the person answers.</summary>
    public required string? HostSession { get; init; }

    /// <summary>The agent's terminal inside that session.</summary>
    public required string? Terminal { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record NativeConversationView
{
    public required bool Recorded { get; init; }

    /// <summary>The workflow profile the conversation was started under; null when none is recorded.</summary>
    public required string? Profile { get; init; }

    /// <summary>The kind of runtime that issued it (for example <c>claude-code</c>); null when none is recorded.</summary>
    public required string? Runtime { get; init; }
}

public sealed record LastProcessView
{
    public required string Outcome { get; init; }

    public required int ExitCode { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>How the agent's conversation was obtained in that process, when it was a continuation.</summary>
    public required string? Continuation { get; init; }

    /// <summary>False when the recorded result contradicts the workflow state; the workflow state is what counts.</summary>
    public required bool AgreesWithWorkflow { get; init; }
}
