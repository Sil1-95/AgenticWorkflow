using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Controller;

/// <summary>What the controller knows about tasks, as the controller's operations need it. The host implements it over its own storage; the controller code never sees events or files.</summary>
public interface ITaskCatalog
{
    /// <summary>The most recent <paramref name="limit"/> tasks, newest first, and how many there are in all. A task that cannot be read is listed as unreadable, not left out.</summary>
    Task<TaskListing> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>One task's read model, or why it cannot be given. It never throws for a task that is missing or damaged.</summary>
    Task<TaskReadOutcome> ReadAsync(TaskId taskId, CancellationToken cancellationToken);
}

public sealed record TaskListing(int Total, IReadOnlyList<TaskSummary> Tasks);

/// <summary>Either the model or the reason there is none (<see cref="ControllerErrorCodes.NotFound"/> or <see cref="ControllerErrorCodes.StateUnreadable"/>).</summary>
public sealed record TaskReadOutcome
{
    public TaskReadModel? Model { get; private init; }

    public ControllerError? Error { get; private init; }

    public static TaskReadOutcome Found(TaskReadModel model) => new() { Model = model };

    public static TaskReadOutcome Failed(string code, string message) => new() { Error = new ControllerError(code, message) };
}

/// <summary>What the controller process lets a request do to it. Only <see cref="ShutdownRequest"/> uses it.</summary>
public interface IControllerLifecycle
{
    bool IsShuttingDown { get; }

    void RequestShutdown();
}

/// <summary>
/// The controller's semantics: one typed request in, one reply out. It is the whole of what the controller does for a client and it knows nothing about transports, frames
/// or processes, so it is tested without any of them. It never throws for a request (a failure is a reply with an error code, and nothing is changed by a request that
/// fails); the only exception it lets through is cancellation. Identifiers are validated here, never trusted from the client.
/// </summary>
/// <param name="workflow">What the typed workflow operations act on; without one the controller is read-only and refuses them as unsupported.</param>
/// <param name="durable">False when this process could be killed with whatever started it; it then refuses the operations that would start supervised work.</param>
public sealed class ControllerDispatcher(
    ControllerInfo info,
    ITaskCatalog catalog,
    IControllerLifecycle lifecycle,
    Action<string>? logFailure = null,
    IControllerWorkflow? workflow = null,
    bool durable = true)
{
    public async Task<ControllerReply> HandleAsync(ControllerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (lifecycle.IsShuttingDown && request is not HealthRequest)
            return ControllerReply.Failure(ControllerErrorCodes.ShuttingDown, "The controller is stopping.");

        try
        {
            return request switch
            {
                HealthRequest => ControllerReply.Success(new HealthResult
                {
                    Status = lifecycle.IsShuttingDown ? ControllerStatus.ShuttingDown : ControllerStatus.Ready,
                    Controller = info,
                    Supervision = new SupervisionStatus { ActiveRuns = workflow?.ActiveRuns ?? 0, Durable = durable && workflow is not null },
                }),
                ListTasksRequest list => await ListAsync(list, cancellationToken).ConfigureAwait(false),
                ReadTaskRequest read => await ReadAsync(read, cancellationToken).ConfigureAwait(false),
                ShutdownRequest shutdown => Shutdown(shutdown),
                _ when WorkflowRequestValidator.IsWorkflowRequest(request) => await WorkflowAsync(request, cancellationToken).ConfigureAwait(false),
                _ => ControllerReply.Failure(ControllerErrorCodes.UnsupportedOperation, "This controller has no such operation."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // The detail belongs in the controller's log; the client learns only that the controller, not its request, failed.
            logFailure?.Invoke($"{request.GetType().Name} failed: {e.GetType().Name}");

            return ControllerReply.Failure(ControllerErrorCodes.InternalError, "The controller failed to handle the request.");
        }
    }

    /// <summary>A controller that supervises work does not stop for a plain request: stopping it would interrupt that work. A forced one does, and says so by the work being left for the next controller.</summary>
    private ControllerReply Shutdown(ShutdownRequest request)
    {
        var active = workflow?.ActiveRuns ?? 0;

        return active > 0 && !request.Force
            ? ControllerReply.Success(new ShutdownResult { Accepted = false, ActiveRuns = active })
            : ControllerReply.Success(new ShutdownResult { Accepted = true });
    }

    private async Task<ControllerReply> WorkflowAsync(ControllerRequest request, CancellationToken cancellationToken)
    {
        if (workflow is null)
            return ControllerReply.Failure(ControllerErrorCodes.UnsupportedOperation, "This controller has no such operation.");

        var (instruction, error) = WorkflowRequestValidator.Validate(request);

        if (instruction is null)
            return ControllerReply.Failure(error!.Code, error.Message);

        if (!durable && instruction is not (AcceptInstruction or AbortTaskInstruction))
        {
            return ControllerReply.Failure(
                ControllerErrorCodes.ControllerNotDurable,
                "This controller would end with the process that started it, so it cannot own work. Start it detached ('aw ctl start'), or use the commands directly.");
        }

        var outcome = await workflow.ExecuteAsync(instruction, cancellationToken).ConfigureAwait(false);

        if (outcome.Refusal is { } refusal)
            return ControllerReply.Failure(refusal.Code, refusal.Message);

        var read = await catalog.ReadAsync(outcome.TaskId!.Value, cancellationToken).ConfigureAwait(false);

        // The operation is recorded either way; a task that cannot be read right after is reported as unreadable, and the client reads it again.
        return read.Model is { } model
            ? ControllerReply.Success(new WorkflowAcceptedResult { Operation = instruction.Operation, WorkScheduled = outcome.WorkScheduled, Task = model })
            : ControllerReply.Failure(
                read.Error!.Code,
                $"The '{instruction.Operation}' operation was recorded, but the task cannot be read now: {read.Error.Message}");
    }

    private async Task<ControllerReply> ListAsync(ListTasksRequest request, CancellationToken cancellationToken)
    {
        var limit = request.Limit ?? ControllerProtocol.DefaultListLimit;

        if (limit is < 1 or > ControllerProtocol.MaxListLimit)
            return ControllerReply.Failure(ControllerErrorCodes.InvalidArgument, $"'limit' must be from 1 to {ControllerProtocol.MaxListLimit}.");

        var listing = await catalog.ListAsync(limit, cancellationToken).ConfigureAwait(false);

        return ControllerReply.Success(new TaskListResult { Total = listing.Total, Tasks = listing.Tasks });
    }

    private async Task<ControllerReply> ReadAsync(ReadTaskRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.TaskId, "D", out var guid) || guid == Guid.Empty)
            return ControllerReply.Failure(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID).");

        var outcome = await catalog.ReadAsync(new TaskId(guid), cancellationToken).ConfigureAwait(false);

        return outcome.Model is { } model
            ? ControllerReply.Success(new TaskResult { Task = model })
            : ControllerReply.Failure(outcome.Error!.Code, outcome.Error.Message);
    }
}
