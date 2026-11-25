namespace AgenticWorkflow.Core.Lifecycle;

/// <summary>
/// The six top-level Workflow V1.1 states.
/// <para>
/// The kernel enforces only the <em>structural</em> lifecycle: terminal states are final and every change is explicit
/// and reasoned. Whether a task is <em>entitled</em> to reach <see cref="Completed"/> (current proof,
/// satisfied boundary, no open findings, ...) is NOT known to this kernel. Moving a task to
/// <see cref="Completed"/> here is bookkeeping, never evidence that completion was earned; the application layer
/// must gate that transition.
/// </para>
/// </summary>
public enum WorkflowState
{
    /// <summary>Work can proceed.</summary>
    Running,

    /// <summary>Known required human information or authority is missing.</summary>
    AwaitingHuman,

    /// <summary>Progress requires an external condition to change.</summary>
    Blocked,

    /// <summary>Terminal. The requested completion boundary has been reached.</summary>
    Completed,

    /// <summary>Terminal. Authoritative, intentional termination.</summary>
    Cancelled,

    /// <summary>Terminal. The requested outcome cannot be achieved.</summary>
    Failed,
}

public static class WorkflowStateExtensions
{
    public static bool IsTerminal(this WorkflowState state)
        => state is WorkflowState.Completed or WorkflowState.Cancelled or WorkflowState.Failed;
}
