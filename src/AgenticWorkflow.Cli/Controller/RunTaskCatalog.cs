using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// What the controller knows about tasks: the runs in the state root, read through the same replay and projection as <c>aw status --json</c>. It only reads. A run is
/// opened read-only for the moment it is read; nothing is cached, so what a client sees is always the run's durable state, and nothing here can take a run's lock or
/// append to its stream.
/// </summary>
internal sealed class RunTaskCatalog(CliServices services, RunStore store) : ITaskCatalog
{
    private readonly RunReadModelReader _reader = new(services, store);

    public async Task<TaskListing> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var all = store.ListRunIds()
            .Select(id => (Id: id, Metadata: store.ReadMetadata(id).Metadata))
            .ToList();

        var recent = all
            .OrderByDescending(run => run.Metadata?.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(run => run.Id.ToString(), StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var summaries = new List<TaskSummary>(recent.Count);

        foreach (var (id, metadata) in recent)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var model = await _reader.ReadAsync(id, cancellationToken).ConfigureAwait(false);

                summaries.Add(TaskSummary.From(model, metadata?.Workspace));
            }
            catch (RunStateException e)
            {
                summaries.Add(TaskSummary.Unreadable(id.Value, e.Message, metadata?.CreatedAt, metadata?.Workspace));
            }
        }

        return new TaskListing(all.Count, summaries);
    }

    public async Task<TaskReadOutcome> ReadAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        try
        {
            return TaskReadOutcome.Found(await _reader.ReadAsync(taskId, cancellationToken).ConfigureAwait(false));
        }
        catch (RunStateException e)
        {
            return TaskReadOutcome.Failed(
                e.Problem == RunStateProblem.NotFound ? ControllerErrorCodes.NotFound : ControllerErrorCodes.StateUnreadable,
                e.Message);
        }
    }
}
