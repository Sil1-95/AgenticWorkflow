using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// Reads, from the run's own files and from the process that holds it, the facts a <see cref="TaskReadModel"/> carries as <see cref="OperationalView"/>: where the run
/// lives, whether a process holds it, where its agent waits for a person, whether a native conversation is recorded, how its process last ended and whether the working
/// tree still matches a pending ACCEPT. None of them is workflow state and none can change it. What cannot be read is reported as unavailable, never guessed or repaired.
/// </summary>
internal sealed class RunOperationalReader(CliServices services, RunStore store)
{
    public async Task<OperationalView> ReadAsync(TaskId runId, WorkflowTask task, CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();

        var (metadata, metadataProblem) = store.ReadMetadata(runId);

        if (metadata is null && metadataProblem is not null)
            unavailable.Add($"run metadata: {metadataProblem}");

        var (outcome, outcomeProblem) = store.ReadOutcome(runId);

        if (outcome is null && outcomeProblem is not null && !outcomeProblem.Contains("missing", StringComparison.Ordinal))
            unavailable.Add($"last run result: {outcomeProblem}");

        var live = store.IsLive(runId);

        return new OperationalView
        {
            Project = metadata is null
                ? null
                : new ProjectView { Workspace = metadata.Workspace, ConfigPath = metadata.ConfigPath, ConfigSha256 = metadata.ConfigSha256, RunCreatedAt = metadata.CreatedAt },
            Liveness = live ? Liveness.Live : Liveness.NotLive,
            NativeInteraction = live && task.WorkflowState == WorkflowState.AwaitingHuman && task.PendingGate is null ? ReadNativeInteraction(runId) : null,
            NativeConversation = ReadConversation(runId, unavailable),
            LastProcess = outcome is null ? null : LastProcess(task, outcome),
            WorkingTree = await ReadWorkingTreeAsync(task, metadata, unavailable, cancellationToken).ConfigureAwait(false),
            ContinuationBaselineRecorded = ReadBaseline(runId, task),
            Unavailable = unavailable,
        };
    }

    private NativeInteractionView? ReadNativeInteraction(TaskId runId)
    {
        var (record, _) = store.ReadNativeInteraction(runId);

        return record is null
            ? null
            : new NativeInteractionView
            {
                Kind = Format.Bounded(record.Kind, 40),
                Description = record.Description is null ? null : Format.Bounded(record.Description, NativeInteractionRecord.MaxDescriptionLength),
                HostSession = record.HostSession,
                Terminal = record.Terminal,
                ObservedAt = record.ObservedAt,
            };
    }

    private NativeConversationView ReadConversation(TaskId runId, List<string> unavailable)
    {
        try
        {
            return new RunConversationStore(store, services.Clock).Find(runId) is { } recorded
                ? new NativeConversationView { Recorded = true, Profile = recorded.Conversation.Profile.Value, Runtime = recorded.Conversation.Runtime }
                : new NativeConversationView { Recorded = false, Profile = null, Runtime = null };
        }
        catch (InvalidAgentConversationException e)
        {
            unavailable.Add($"native conversation: {e.Message}");

            return new NativeConversationView { Recorded = false, Profile = null, Runtime = null };
        }
    }

    private static LastProcessView LastProcess(WorkflowTask task, RunOutcomeRecord recorded) => new()
    {
        Outcome = recorded.Outcome,
        ExitCode = recorded.ExitCode,
        Reason = recorded.Reason,
        EndedAt = recorded.EndedAt,
        Continuation = recorded.Continuation,
        AgreesWithWorkflow = (recorded.Outcome == nameof(RunOutcome.Completed)) == (task.WorkflowState == WorkflowState.Completed),
    };

    /// <summary>The same check <c>aw status</c> prints for a pending ACCEPT gate: whether the working tree is still exactly the state that was verified.</summary>
    private async Task<WorkingTreeState> ReadWorkingTreeAsync(WorkflowTask task, RunMetadata? metadata, List<string> unavailable, CancellationToken cancellationToken)
    {
        if (task.PendingGate is not { Type: GateType.Accept } gate || metadata is null)
            return WorkingTreeState.NotChecked;

        var reviewed = task.ImplementationRevisions.FirstOrDefault(r => r.Id == gate.ImplementationRevisionId);

        if (reviewed is null)
            return WorkingTreeState.NotChecked;

        try
        {
            var now = await services.Workspace.CaptureAsync(metadata.Workspace, cancellationToken).ConfigureAwait(false);

            return now.Fingerprint == reviewed.Fingerprint ? WorkingTreeState.Unchanged : WorkingTreeState.Changed;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            unavailable.Add($"working tree: {e.Message}");

            return WorkingTreeState.NotChecked;
        }
    }

    private bool? ReadBaseline(TaskId runId, WorkflowTask task)
        => RecoverableContinuation.Find(task) is { } pending
            ? new ContinuationBaselineStore(store, services.Clock).Find(runId, pending.BaselineKey).Baseline is not null
            : null;
}
