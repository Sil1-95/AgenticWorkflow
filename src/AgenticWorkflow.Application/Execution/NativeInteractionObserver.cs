namespace AgenticWorkflow.Application.Execution;

/// <summary>
/// Optional, provider-neutral visibility port for native human interactions (an agent waiting for a person in its own interface). The runner calls it only
/// to LET SOMEONE KNOW: it never answers, allows, denies, translates or records anything about the person's choice, and nothing an observer does (or throws)
/// can change the workflow state, the turn or the run result. Every call is best-effort.
/// </summary>
public interface INativeInteractionObserver
{
    /// <summary>
    /// A person is attending and the agent is waiting for them. Called once per interaction, only after the workflow durably recorded AWAITING_HUMAN. It is
    /// never called when nobody is attending (the run then stops instead of waiting).
    /// </summary>
    void InteractionOpened(AgentInteraction interaction);

    /// <summary>The wait is over for this run: the workflow returned to RUNNING (the person acted, or the turn ended) or the run ended. Called once per opened interaction.</summary>
    void InteractionClosed();
}
