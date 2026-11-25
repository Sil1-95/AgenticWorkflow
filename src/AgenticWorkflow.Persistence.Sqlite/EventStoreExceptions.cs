using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Persistence.Sqlite;

/// <summary>
/// Base type for failures of the durable store itself. Domain-invalid histories are NOT reported with this
/// type: once stored events decode, the Core kernel (<c>TaskStream.Replay</c>) is the judge of their validity
/// and throws its own <c>DomainException</c>s.
/// </summary>
public abstract class EventStoreException : Exception
{
    protected EventStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Stored data cannot be trusted or understood: unknown event kind, unsupported payload version, malformed or
/// incomplete payload, invalid identifier, or a broken sequence. Never normalized into something valid-looking.
/// </summary>
public sealed class EventStoreCorruptionException : EventStoreException
{
    public EventStoreCorruptionException(string message, TaskId? taskId = null, long? sequence = null, Exception? inner = null)
        : base(Describe(message, taskId, sequence), inner)
    {
        TaskId = taskId;
        Sequence = sequence;
    }

    public TaskId? TaskId { get; }

    public long? Sequence { get; }

    private static string Describe(string message, TaskId? taskId, long? sequence)
        => taskId is null ? message : $"Task {taskId}{(sequence is null ? string.Empty : $" event #{sequence}")}: {message}";
}

/// <summary>The database file has a schema version (or shape) this build does not support. It is left untouched.</summary>
public sealed class UnsupportedSchemaException : EventStoreException
{
    public UnsupportedSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>The store's own constraints refused an append (for example an event id that already exists). Nothing was written.</summary>
public sealed class AppendRejectedException : EventStoreException
{
    public AppendRejectedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
