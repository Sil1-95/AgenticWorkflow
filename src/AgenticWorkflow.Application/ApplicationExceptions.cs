using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application;

/// <summary>
/// A decision was made from workflow state that is no longer current: the task's history has moved past the
/// version the caller based the decision on. Nothing was written and the command was not executed against the
/// newer state. The caller must reload the task and re-decide; the same command must never be blindly resent.
/// </summary>
public sealed class StaleWorkflowStateException : Exception
{
    public StaleWorkflowStateException(TaskId taskId, long expectedVersion, long actualVersion, Exception? inner = null)
        : base(
            $"Task {taskId}: the decision was based on version {expectedVersion} but the task is at version {actualVersion}. Reload and re-decide.",
            inner)
    {
        TaskId = taskId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public TaskId TaskId { get; }

    /// <summary>The version the caller's decision was based on.</summary>
    public long ExpectedVersion { get; }

    /// <summary>The version the task's history is actually at.</summary>
    public long ActualVersion { get; }
}

/// <summary>A task was created with an id that already has a history. Creation is never idempotent.</summary>
public sealed class TaskAlreadyExistsException : Exception
{
    public TaskAlreadyExistsException(TaskId taskId, long actualVersion, Exception? inner = null)
        : base($"Task {taskId} already exists (at version {actualVersion}).", inner)
    {
        TaskId = taskId;
        ActualVersion = actualVersion;
    }

    public TaskId TaskId { get; }

    public long ActualVersion { get; }
}

/// <summary>An operation needed a task that has no history.</summary>
public sealed class TaskNotFoundException : Exception
{
    public TaskNotFoundException(TaskId taskId)
        : base($"Task {taskId} does not exist.")
    {
        TaskId = taskId;
    }

    public TaskId TaskId { get; }
}

/// <summary>A verification was requested for a task that has no implementation revision to verify.</summary>
public sealed class NoCurrentImplementationException : Exception
{
    public NoCurrentImplementationException(TaskId taskId)
        : base($"Task {taskId} has no implementation revision; there is nothing to verify.")
    {
        TaskId = taskId;
    }

    public TaskId TaskId { get; }
}

/// <summary>
/// Completion through the verified direct path was refused because the deterministic direct-path conditions are not met.
/// Nothing was written. <see cref="Eligibility"/> lists every unmet condition.
/// </summary>
public sealed class CompletionNotEligibleException : Exception
{
    public CompletionNotEligibleException(TaskId taskId, Direct.DirectCompletionEligibility eligibility)
        : base($"Task {taskId} is not eligible to complete: {string.Join(", ", eligibility.Reasons)}.")
    {
        TaskId = taskId;
        Eligibility = eligibility;
    }

    public TaskId TaskId { get; }

    public Direct.DirectCompletionEligibility Eligibility { get; }
}

/// <summary>
/// The controller tried to complete a task that requires the human's ACCEPT (<see cref="Core.Gates.AcceptanceRequirement.HumanRequired"/>). Such a task completes
/// only through its gate. Nothing was written.
/// </summary>
public sealed class AcceptanceRequiredException : Exception
{
    public AcceptanceRequiredException(TaskId taskId)
        : base($"Task {taskId} requires the human's ACCEPT; it completes only through its gate, not by the controller.")
    {
        TaskId = taskId;
    }

    public TaskId TaskId { get; }
}
