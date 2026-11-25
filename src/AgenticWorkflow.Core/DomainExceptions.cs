using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Core;

/// <summary>
/// Base type for every violation of a domain invariant. Malformed arguments (blank text, empty identities)
/// are reported with <see cref="ArgumentException"/> instead; a <see cref="DomainException"/> always means
/// "well-formed input that the current history does not allow".
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }
}

/// <summary>An operation or event arrived for a task that is already COMPLETED, CANCELLED or FAILED.</summary>
public sealed class TaskTerminatedException : DomainException
{
    public TaskTerminatedException(TaskId taskId, WorkflowState state)
        : base($"Task {taskId} is {state}, which is terminal; its history is closed.")
    {
        TaskId = taskId;
        State = state;
    }

    public TaskId TaskId { get; }

    public WorkflowState State { get; }
}

/// <summary>A workflow-state change that is structurally not allowed (no-op, or missing reason).</summary>
public sealed class InvalidWorkflowTransitionException : DomainException
{
    public InvalidWorkflowTransitionException(string message)
        : base(message)
    {
    }
}

/// <summary>A revision record whose own shape or place in its lineage is inconsistent.</summary>
public sealed class InvalidRevisionLineageException : DomainException
{
    public InvalidRevisionLineageException(string message)
        : base(message)
    {
    }
}

/// <summary>A record points at a semantic or implementation revision that is unknown or not the current one.</summary>
public sealed class InvalidRevisionReferenceException : DomainException
{
    public InvalidRevisionReferenceException(string message)
        : base(message)
    {
    }
}

/// <summary>A verification run whose own shape is inconsistent (no checks, duplicate checks, outcome not matching its checks).</summary>
public sealed class InvalidVerificationException : DomainException
{
    public InvalidVerificationException(string message)
        : base(message)
    {
    }
}

/// <summary>A role was bound to a profile that does not provide the role's required capabilities.</summary>
public sealed class RoleBindingException : DomainException
{
    public RoleBindingException(string message)
        : base(message)
    {
    }
}

/// <summary>A role was resolved but has no explicit binding. There is deliberately no fallback.</summary>
public sealed class UnboundRoleException : DomainException
{
    public UnboundRoleException(RoleId role)
        : base($"Role '{role}' has no explicit profile binding.")
    {
        Role = role;
    }

    public RoleId Role { get; }
}

/// <summary>An event history that is not a valid, ordered, single-task stream.</summary>
public sealed class InvalidEventHistoryException : DomainException
{
    public InvalidEventHistoryException(string message)
        : base(message)
    {
    }
}

/// <summary>A gate event that the history does not allow: opened in the wrong state or twice, decided when unknown or already decided, or a workflow change that bypasses an open gate.</summary>
public sealed class InvalidGateException : DomainException
{
    public InvalidGateException(string message)
        : base(message)
    {
    }
}

/// <summary>A gate decision that would accept a state which is no longer the task's current one. A stale gate never authorizes anything.</summary>
public sealed class StaleGateException : DomainException
{
    public StaleGateException(string message)
        : base(message)
    {
    }
}

/// <summary>A reclassification the history does not allow: nothing to reconsider, not raised on a RUNNING task, or one that does not invalidate exactly what it must.</summary>
public sealed class InvalidReclassificationException : DomainException
{
    public InvalidReclassificationException(string message)
        : base(message)
    {
    }
}

/// <summary>A review request or review the history does not allow: not required, not over the current verified implementation, from the implementer's own conversation, a duplicate, malformed or tampered.</summary>
public sealed class InvalidReviewException : DomainException
{
    public InvalidReviewException(string message)
        : base(message)
    {
    }
}

/// <summary>A Proof Contract or proof the history does not allow: not required, not over the current verified (and reviewed) implementation, from the implementer's or a reviewer's own conversation, a duplicate, an unresolvable evidence reference, malformed or tampered.</summary>
public sealed class InvalidProofException : DomainException
{
    public InvalidProofException(string message)
        : base(message)
    {
    }
}
