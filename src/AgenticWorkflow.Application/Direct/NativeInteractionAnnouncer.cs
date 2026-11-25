using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// One run's view of the optional <see cref="INativeInteractionObserver"/>: tells it a wait opened and closed, exactly once each, and swallows whatever it
/// throws. It is decision-free by construction: it has no access to workflow state and nothing it does can alter the run.
/// </summary>
internal sealed class NativeInteractionAnnouncer(INativeInteractionObserver? observer)
{
    private bool _open;

    public void Opened(AgentInteraction interaction)
    {
        if (observer is null)
            return;

        // A wait that is replaced by another without a "resumed" in between still ends the first one.
        Closed();

        _open = true;

        try
        {
            observer.InteractionOpened(interaction);
        }
        catch (Exception)
        {
            // best effort: visibility must never change the run
        }
    }

    public void Closed()
    {
        if (!_open)
            return;

        _open = false;

        try
        {
            observer?.InteractionClosed();
        }
        catch (Exception)
        {
            // best effort: visibility must never change the run
        }
    }
}
