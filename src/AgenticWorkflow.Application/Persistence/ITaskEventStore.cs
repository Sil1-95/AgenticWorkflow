using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Persistence;

/// <summary>
/// The application's view of durable, append-only per-task event storage. Implementations store the envelopes
/// Core produced exactly as given and return them in order; they do not interpret or validate workflow rules.
/// <para>
/// The stream's version is the sequence of its last event (0 when the task has no stream), so a loaded history
/// already defines the version an append must expect. There is deliberately no separate version query.
/// </para>
/// </summary>
public interface ITaskEventStore
{
    /// <summary>The task's complete history in sequence order, or an empty list if the task has no stream.</summary>
    Task<IReadOnlyList<EventEnvelope>> LoadAsync(TaskId taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends <paramref name="events"/> to <paramref name="taskId"/>'s stream, but only if the stream
    /// currently ends at <paramref name="expectedVersion"/> (0 for a task that does not exist yet). Otherwise
    /// nothing is written and <see cref="StreamVersionConflictException"/> is thrown. Returns the new version.
    /// </summary>
    Task<long> AppendAsync(
        TaskId taskId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken cancellationToken = default);
}
