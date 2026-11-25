using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// Makes a native agent interaction impossible to miss for the person supervising an attended run, and says how to attach. Told by the runner only after the
/// workflow durably recorded AWAITING_HUMAN: it prints one notice on the console, leaves a small display-only annotation in the run directory for a fresh
/// <c>aw status</c>, and (when opted in) asks the notifier for one signal outside the terminal.
/// <para>
/// It only informs. It never answers, allows or denies anything, and nothing it does can change the run: every failure here (the console, the annotation file,
/// the notifier) is swallowed, at most reported as one short console line. The annotation is removed when the wait ends and again when the observer is disposed.
/// </para>
/// </summary>
internal sealed class CliNativeInteractionObserver(
    TaskId runId,
    RunProgress progress,
    RunStore store,
    IClock clock,
    IHumanAttentionNotifier notifier) : INativeInteractionObserver, IAsyncDisposable
{
    public const int DescriptionLimit = NativeInteractionRecord.MaxDescriptionLength;

    private const int ToastDescriptionLimit = 160;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _notifications = [];

    public void InteractionOpened(AgentInteraction interaction)
    {
        var kind = interaction.Kind.ToString().ToLowerInvariant();
        var description = Format.Bounded(interaction.Description, DescriptionLimit);

        progress.Block(NoticeLines(kind, description, interaction.Attach));
        Annotate(kind, description, interaction.Attach);

        // After the console notice, and never waited for here: a slow or failing desktop signal must not hold up the run.
        var notice = ToastNotice(kind, description, interaction.Attach);
        _notifications.Add(Task.Run(() => NotifyAsync(notice)));
    }

    public void InteractionClosed() => ClearAnnotation();

    public async ValueTask DisposeAsync()
    {
        ClearAnnotation();

        // A notification still in flight is no longer worth waiting for; cancelling kills its child. It never faults, so awaiting it is safe.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_notifications).ConfigureAwait(false);

        _lifetime.Dispose();
    }

    /// <summary>The console notice. The attach block comes from the one shared renderer, so it is the same text <c>aw status</c> gives.</summary>
    internal static IReadOnlyList<string> NoticeLines(string kind, string description, NativeAttachTarget? attach)
    {
        var lines = new List<string>
        {
            "NATIVE INTERACTION REQUIRED",
            description.Length > 0 ? $"{kind}: {description}" : kind,
            "The agent is waiting for input in Herdr. Answer it there; AgenticWorkflow never answers it.",
        };

        var attachLines = NativeAttachGuidance.Lines(attach);

        if (attachLines.Count > 0)
            lines.AddRange(attachLines);
        else
            lines.Add("No attach command could be shown safely; open this run's agent terminal in Herdr.");

        lines.Add("AgenticWorkflow keeps monitoring this run and continues after you answer.");

        return lines;
    }

    private HumanAttentionNotice ToastNotice(string kind, string description, NativeAttachTarget? attach)
    {
        var what = description.Length > 0 ? $"{kind}: {Format.Bounded(description, ToastDescriptionLimit)}" : kind;
        var where = NativeAttachGuidance.PowerShell(attach?.HostSession, attach?.Terminal) ?? "Open this run's agent terminal in Herdr.";

        return new HumanAttentionNotice(
            "AgenticWorkflow: your agent is waiting for you",
            $"Run {Format.ShortRunId(runId)}: {what}\n{where}");
    }

    private void Annotate(string kind, string description, NativeAttachTarget? attach)
    {
        try
        {
            store.WriteNativeInteraction(
                runId,
                new NativeInteractionRecord(
                    NativeInteractionRecord.CurrentVersion,
                    runId.ToString(),
                    kind,
                    description.Length > 0 ? description : null,
                    attach?.HostSession,
                    attach?.Terminal,
                    clock.UtcNow));
        }
        catch (Exception)
        {
            // an annotation only helps a later `aw status`; failing to write it must not fail the run
        }
    }

    private void ClearAnnotation()
    {
        try
        {
            store.ClearNativeInteraction(runId);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private async Task NotifyAsync(HumanAttentionNotice notice)
    {
        string? problem;

        try
        {
            problem = await notifier.NotifyAsync(notice, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            problem = $"unexpected {e.GetType().Name}";
        }

        if (problem is null)
            return;

        try
        {
            progress.Line($"desktop notification unavailable ({Format.Bounded(problem, 80)})");
        }
        catch (Exception)
        {
            // the console is gone; nothing else to do
        }
    }
}
