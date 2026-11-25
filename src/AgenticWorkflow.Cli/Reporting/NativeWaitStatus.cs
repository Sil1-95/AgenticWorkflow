using System.Globalization;
using System.Text;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// What <c>aw status</c> says about a LIVE run whose agent waits for a person in its own interface, from the display-only annotation the run's process left
/// (<see cref="NativeInteractionRecord"/>). The workflow state itself (AWAITING_HUMAN) comes from the event stream; this only adds where the person answers.
/// It never offers a command that answers the agent: the decision is made natively, in the agent's Herdr session.
/// </summary>
internal static class NativeWaitStatus
{
    /// <summary>
    /// Appends the native-wait guidance and returns true when the run has a usable annotation; appends nothing and returns false otherwise (an older run, a missing
    /// or unreadable file), so the caller keeps its existing wording. Whether a process holds the run is the caller's question, asked before this.
    /// </summary>
    public static bool TryAppend(StringBuilder text, RunStore store, TaskId runId)
    {
        var (record, _) = store.ReadNativeInteraction(runId);

        if (record is null)
            return false;

        text.AppendLine("Native agent interaction is waiting. Answer it in the live Herdr session.");

        var attach = NativeAttachGuidance.Lines(record.HostSession, record.Terminal);

        if (attach.Count > 0)
        {
            foreach (var line in attach)
                text.AppendLine($"  {line}");
        }
        else
        {
            text.AppendLine("  No attach command could be shown safely; open this run's agent terminal in Herdr.");
        }

        var kind = Format.Bounded(record.Kind, 40);
        var description = Format.Bounded(record.Description, NativeInteractionRecord.MaxDescriptionLength);

        text.AppendLine($"Request: {(description.Length > 0 ? $"{kind}: {description}" : kind)}");
        text.AppendLine($"Waiting since: {record.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
        text.AppendLine("AgenticWorkflow keeps monitoring this run and continues after you answer; it never answers the agent itself.");

        return true;
    }
}
