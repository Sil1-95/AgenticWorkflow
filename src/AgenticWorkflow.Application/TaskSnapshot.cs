using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application;

/// <summary>
/// An immutable, authoritative view of a task at one version, reconstructed through <c>TaskStream.Replay</c>.
/// It is also the "basis" of a decision: mutation methods take the snapshot the decision was made from, and are
/// refused with <see cref="StaleWorkflowStateException"/> if the task has moved since. Only the application
/// service creates snapshots, so a version cannot be invented.
/// </summary>
public sealed record TaskSnapshot
{
    internal TaskSnapshot(WorkflowTask task) => Task = task;

    public WorkflowTask Task { get; }

    /// <summary>The sequence of the task's last event. The version a mutation of this snapshot expects.</summary>
    public long Version => Task.LastSequence;
}
