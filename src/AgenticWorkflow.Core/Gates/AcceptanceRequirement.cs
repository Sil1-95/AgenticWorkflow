namespace AgenticWorkflow.Core.Gates;

/// <summary>
/// Whether a task needs the human's final ACCEPT before it can complete. It is a per-task composition choice, recorded once when the task is created
/// and never changed afterwards, so replay always knows which completion path the task was on. It is chosen explicitly by whoever creates the task;
/// nothing infers it from the task's wording, and no agent selects it.
/// </summary>
public enum AcceptanceRequirement
{
    /// <summary>
    /// No gate. Once the controller has verified the current implementation, the controller itself records COMPLETED. Meant for objective, bounded,
    /// well-proved work, where the controller's checks are the confidence needed.
    /// </summary>
    NotRequired,

    /// <summary>
    /// The workflow opens a durable ACCEPT gate over the verified implementation; only the human's explicit decision completes the task (or sends it back
    /// for changes, or cancels it). Meant for subjective or consequential work. This is also what every task created before the requirement was
    /// recorded is taken to have had.
    /// </summary>
    HumanRequired,
}
