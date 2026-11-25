using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Persistence;

/// <summary>
/// Raised by an <see cref="ITaskEventStore"/> when an append's expected version is not the stream's current
/// version: another writer got there first. Nothing was written. This is the persistence-level signal;
/// application callers see it as <see cref="StaleWorkflowStateException"/> or <see cref="TaskAlreadyExistsException"/>.
/// </summary>
public sealed class StreamVersionConflictException : Exception
{
    public StreamVersionConflictException(TaskId taskId, long expectedVersion, long actualVersion)
        : base($"Task {taskId}: append expected stream version {expectedVersion} but the stream is at version {actualVersion}.")
    {
        TaskId = taskId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public TaskId TaskId { get; }

    public long ExpectedVersion { get; }

    public long ActualVersion { get; }
}
