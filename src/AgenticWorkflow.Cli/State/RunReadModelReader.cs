using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// The one way a run becomes a <see cref="TaskReadModel"/>: the run's event stream is replayed (read-only) and projected together with the run's operational facts.
/// <c>aw status --json</c> and the controller both use it, so they cannot disagree about what a run looks like.
/// </summary>
internal sealed class RunReadModelReader(CliServices services, RunStore store)
{
    /// <summary>Replays the run and projects it. Throws <see cref="RunStateException"/> when the run does not exist or cannot be trusted.</summary>
    public async Task<TaskReadModel> ReadAsync(TaskId runId, CancellationToken cancellationToken)
        => await ProjectAsync(runId, await store.ReplayAsync(runId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Projects a run that has already been replayed.</summary>
    public async Task<TaskReadModel> ProjectAsync(TaskId runId, WorkflowTask task, CancellationToken cancellationToken)
    {
        var operational = await new RunOperationalReader(services, store).ReadAsync(runId, task, cancellationToken).ConfigureAwait(false);

        return TaskReadModelProjector.Project(task, operational);
    }
}
