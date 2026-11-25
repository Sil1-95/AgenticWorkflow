using System.Globalization;
using System.Text;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw runs</c>: finds the runs in the state root and lists the most recent ones, so a lost run id can be recovered. Each run's state is replayed from its
/// own event stream exactly as <c>aw status</c> does; metadata supplies the workspace only. Read-only: it needs no project, no runtime and no model, creates
/// nothing, and a run that cannot be read is listed as such instead of stopping the list. It prints no configuration, no conversation reference and no
/// recorded process output.
/// </summary>
internal sealed class RunsCommand(CliServices services)
{
    private const int TaskExcerptLength = 80;

    public async Task<int> ExecuteAsync(RunsRequest request, CancellationToken cancellationToken)
    {
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));
        var entries = new List<Entry>();

        foreach (var runId in store.ListRunIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await ReadAsync(store, runId, cancellationToken).ConfigureAwait(false));
        }

        var recent = entries
            .OrderByDescending(e => e.Started ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.RunId.ToString(), StringComparer.Ordinal)
            .Take(request.Limit)
            .ToList();

        services.Out.Write(Render(store, entries.Count, recent));

        return ExitCodes.Success;
    }

    private static async Task<Entry> ReadAsync(RunStore store, TaskId runId, CancellationToken cancellationToken)
    {
        var (metadata, _) = store.ReadMetadata(runId);

        try
        {
            return new Entry(runId, metadata, await store.ReplayAsync(runId, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is RunStateException or IOException or UnauthorizedAccessException)
        {
            // The per-run boundary: a damaged run is reported, not fatal to the list. Persistence and replay failures arrive as RunStateException; the
            // file system errors the store does not translate (a locked or unreadable file) are the other realistic ways one run can fail. Cancellation
            // and anything unexpected are deliberately not caught: they are not a property of one run.
            return new Entry(runId, metadata, null, e is RunStateException ? e.Message : $"The run's files cannot be read: {e.Message}");
        }
    }

    private static string Render(RunStore store, int total, IReadOnlyList<Entry> recent)
    {
        var text = new StringBuilder();

        if (recent.Count == 0)
        {
            text.AppendLine($"No runs in {store.Root}.");
            return text.ToString();
        }

        text.AppendLine($"Recent runs, newest first ({recent.Count} of {total}) in {store.Root}:");

        foreach (var entry in recent)
        {
            text.AppendLine();
            AppendEntry(text, entry);
        }

        text.AppendLine();
        text.AppendLine("Details of one run: aw status <run-id>");

        if (total > recent.Count)
            text.AppendLine($"{total - recent.Count} older run(s) not shown; use --limit <n> to list more.");

        return text.ToString();
    }

    private static void AppendEntry(StringBuilder text, Entry entry)
    {
        if (entry.Task is not { } task)
        {
            text.AppendLine($"{entry.RunId}  UNREADABLE{(entry.Started is { } at ? $"  started {Time(at)}" : string.Empty)}");
            text.AppendLine($"  problem: {Format.Printable(entry.Problem ?? "unknown")}");
            AppendWorkspace(text, entry);
            return;
        }

        text.AppendLine($"{entry.RunId}  {Format.State(task.WorkflowState)}{DescribePending(task)}  started {Time(task.CreatedAt)}  acceptance {AcceptancePolicy.Word(task.Acceptance)}");
        AppendWorkspace(text, entry);
        text.AppendLine($"  task: {Format.Excerpt(task.Intent, TaskExcerptLength)}");
    }

    private static void AppendWorkspace(StringBuilder text, Entry entry)
        => text.AppendLine($"  workspace: {(entry.Metadata is { } metadata && !string.IsNullOrWhiteSpace(metadata.Workspace) ? Format.Printable(metadata.Workspace) : "unknown")}");

    /// <summary>
    /// The annotation of a wait, from the replayed state only. A workflow gate (ACCEPT, CLARIFY, WHAT) keeps its own wording; an AWAITING_HUMAN without one is the agent
    /// waiting for a person in its own interface. Nothing live is consulted: no runtime, no Herdr, no run file beyond the event stream.
    /// </summary>
    private static string DescribePending(WorkflowTask task) => task.PendingGate?.Type switch
    {
        GateType.Clarify => " (question awaits your answer)",
        GateType.What => task.ActiveReclassification is null ? " (WHAT)" : " (WHAT, reclassified)",
        GateType.How => task.ActiveReclassification is null ? " (HOW)" : " (HOW, reclassified)",
        not null => " (awaits your decision)",
        _ => task.WorkflowState == WorkflowState.AwaitingHuman
            ? " (native)"
            : task.WorkflowState == WorkflowState.Running ? DescribeStagePhase(task) : string.Empty,
    };

    /// <summary>
    /// <c>(PROVE)</c> while the work is at the formal proof (the verified, reviewed work is due its proof) and <c>(PROVE REPAIR)</c> while the implementer answers a failed or
    /// inconclusive proof, after the review stage's own annotation. Derived from the durable history only; empty when the run is at neither stage.
    /// </summary>
    private static string DescribeStagePhase(WorkflowTask task)
    {
        var review = DescribeReviewPhase(task);

        if (review.Length > 0 || !task.IsProveRequired)
            return review;

        if (task.IsProveDue)
            return " (PROVE)";

        return task.ProofStatus is ProofStatus.Failed or ProofStatus.Inconclusive ? " (PROVE REPAIR)" : string.Empty;
    }

    /// <summary>
    /// <c>(REVIEW)</c> while the work is at the independent review (the verified implementation is due its review) and <c>(REVIEW REPAIR)</c> while the implementer repairs the
    /// findings of the latest review. Derived from the durable history only; empty when the run is not at that stage.
    /// </summary>
    private static string DescribeReviewPhase(WorkflowTask task)
    {
        if (!task.IsReviewRequired)
            return string.Empty;

        if (task.IsReviewDue)
            return " (REVIEW)";

        return task.ReviewStatus == ReviewStatus.ChangesRequired ? " (REVIEW REPAIR)" : string.Empty;
    }

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private sealed record Entry(TaskId RunId, RunMetadata? Metadata, WorkflowTask? Task, string? Problem)
    {
        /// <summary>
        /// When the run started: from the replayed stream when it can be read, otherwise from the metadata written when the run was created (a metadata
        /// file that parses but carries no timestamp has none).
        /// </summary>
        public DateTimeOffset? Started => Task?.CreatedAt ?? (Metadata is { } metadata && metadata.CreatedAt != default ? metadata.CreatedAt : null);
    }
}
