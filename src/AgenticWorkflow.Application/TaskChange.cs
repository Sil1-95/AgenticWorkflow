using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application;

/// <summary>
/// The outcome of a successful application call: the task as it now stands and the events this call appended.
/// A convenience so the caller can continue from <see cref="Snapshot"/> without reloading; the stored history
/// remains the only source of truth. A call that produced no events returns the unchanged snapshot.
/// </summary>
public sealed record TaskChange
{
    internal TaskChange(TaskSnapshot snapshot, IReadOnlyList<EventEnvelope> newEvents)
    {
        Snapshot = snapshot;
        NewEvents = newEvents;
    }

    /// <summary>The current state and version; use it as the basis of the next decision.</summary>
    public TaskSnapshot Snapshot { get; }

    /// <summary>Only the events appended by this call, in order. Empty when nothing changed.</summary>
    public IReadOnlyList<EventEnvelope> NewEvents { get; }

    public WorkflowTask Task => Snapshot.Task;

    public long Version => Snapshot.Version;

    public bool HasChanges => NewEvents.Count > 0;
}
