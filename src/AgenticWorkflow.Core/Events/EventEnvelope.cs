using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Events;

/// <summary>
/// One immutable entry of a task's append-only history. <see cref="Sequence"/> is 1-based and gapless within a
/// task. The envelope is the unit a future persistence layer stores; Core never touches storage.
/// </summary>
public sealed record EventEnvelope
{
    public EventEnvelope(EventId id, TaskId taskId, long sequence, DateTimeOffset timestamp, DomainEvent payload)
    {
        if (sequence < 1)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence is 1-based.");

        Id = id;
        TaskId = taskId;
        Sequence = sequence;
        Timestamp = timestamp;
        Payload = Guard.NotNull(payload, nameof(payload));
    }

    public EventId Id { get; }

    public TaskId TaskId { get; }

    public long Sequence { get; }

    public DateTimeOffset Timestamp { get; }

    public DomainEvent Payload { get; }

    public string Kind => Payload.Kind;
}
