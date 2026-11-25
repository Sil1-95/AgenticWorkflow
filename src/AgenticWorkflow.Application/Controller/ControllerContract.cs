using System.Text.Json.Serialization;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Controller;

// The typed contract between a client of the controller and the controller. Nothing here knows how bytes travel (see ControllerWire for the encoding and the
// transport adapters for the carrier). Every operation is a named, typed request; there is no operation that carries a command line, a script or any other string to
// be executed, and no operation that carries free text to be interpreted as a decision. The workflow operations (start, answer, approve, request changes, accept, abort,
// continue; see ControllerWorkflowContract) are further typed requests of this same hierarchy, each naming its gate and the state it was shown.

/// <summary>The operation names of the wire form: the <c>operation</c> discriminator of a request.</summary>
public static class ControllerOperations
{
    public const string Health = "health";
    public const string ListTasks = "list_tasks";
    public const string ReadTask = "read_task";
    public const string Shutdown = "shutdown";

    public const string StartTask = "start_task";
    public const string AnswerClarification = "answer_clarification";

    /// <summary>The wire name of <see cref="ApproveProposalRequest"/>; the proposal it approves is a property of the request.</summary>
    public const string Approve = "approve";

    public const string RequestChanges = "request_changes";
    public const string Accept = "accept";
    public const string Abort = "abort";
    public const string ContinueTask = "continue_task";
    public const string BindRole = "bind_role";

    /// <summary>The names an operation is reported under in a result (an approval says which proposal it approved).</summary>
    public const string ApproveWhat = "approve_what";

    public const string ApproveHow = "approve_how";

    /// <summary>Every operation this build knows, for telling "unsupported operation" from "malformed request".</summary>
    public static IReadOnlyList<string> All { get; } = [Health, ListTasks, ReadTask, Shutdown, StartTask, AnswerClarification, Approve, RequestChanges, Accept, Abort, ContinueTask, BindRole];
}

/// <summary>One request to the controller. The <c>operation</c> property names which.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HealthRequest), ControllerOperations.Health)]
[JsonDerivedType(typeof(ListTasksRequest), ControllerOperations.ListTasks)]
[JsonDerivedType(typeof(ReadTaskRequest), ControllerOperations.ReadTask)]
[JsonDerivedType(typeof(ShutdownRequest), ControllerOperations.Shutdown)]
[JsonDerivedType(typeof(StartTaskRequest), ControllerOperations.StartTask)]
[JsonDerivedType(typeof(AnswerClarificationRequest), ControllerOperations.AnswerClarification)]
[JsonDerivedType(typeof(ApproveProposalRequest), ControllerOperations.Approve)]
[JsonDerivedType(typeof(RequestTaskChangesRequest), ControllerOperations.RequestChanges)]
[JsonDerivedType(typeof(AcceptTaskRequest), ControllerOperations.Accept)]
[JsonDerivedType(typeof(AbortTaskRequest), ControllerOperations.Abort)]
[JsonDerivedType(typeof(ContinueTaskRequest), ControllerOperations.ContinueTask)]
[JsonDerivedType(typeof(BindRoleRequest), ControllerOperations.BindRole)]
public abstract record ControllerRequest;

/// <summary>Asks the controller who it is and whether it is ready. Mutates nothing.</summary>
public sealed record HealthRequest : ControllerRequest;

/// <summary>Lists the most recent tasks, newest first. Mutates nothing.</summary>
public sealed record ListTasksRequest : ControllerRequest
{
    /// <summary>How many to return (1 to <see cref="ControllerProtocol.MaxListLimit"/>); <see cref="ControllerProtocol.DefaultListLimit"/> when absent.</summary>
    public int? Limit { get; init; }
}

/// <summary>Reads one task's <see cref="TaskReadModel"/>. Mutates nothing.</summary>
public sealed record ReadTaskRequest : ControllerRequest
{
    /// <summary>The task id as text (a GUID). The controller validates it; a malformed one is <see cref="ControllerErrorCodes.InvalidTaskId"/>, never an exception.</summary>
    public required string TaskId { get; init; }
}

/// <summary>
/// Asks the controller to stop. It stops accepting connections, finishes the requests in flight and exits. A controller that is supervising work does not stop for a plain
/// request (the reply says so); <see cref="Force"/> stops it anyway, which interrupts that work (the durable task keeps its decision, and the next controller resumes it).
/// </summary>
public sealed record ShutdownRequest : ControllerRequest
{
    public bool Force { get; init; }
}

/// <summary>The result of a successful request. The <c>kind</c> property names which.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HealthResult), "health")]
[JsonDerivedType(typeof(TaskListResult), "task_list")]
[JsonDerivedType(typeof(TaskResult), "task")]
[JsonDerivedType(typeof(ShutdownResult), "shutdown")]
[JsonDerivedType(typeof(WorkflowAcceptedResult), "accepted")]
public abstract record ControllerResult;

public sealed record HealthResult : ControllerResult
{
    public required ControllerStatus Status { get; init; }

    public required ControllerInfo Controller { get; init; }

    /// <summary>What the controller is doing for tasks right now.</summary>
    public SupervisionStatus Supervision { get; init; } = new();
}

/// <summary>The controller's ownership of work, for <see cref="HealthResult"/>. Counts only: no task, path or conversation.</summary>
public sealed record SupervisionStatus
{
    /// <summary>Runs whose agent work the controller is supervising now. While this is above zero the controller never stops for being idle.</summary>
    public int ActiveRuns { get; init; }

    /// <summary>True when this controller can own agent work: it has a workflow, and it could not be killed together with the process that started it (a Windows job that ends its members). Otherwise it refuses every operation that would start work, and clients run their commands themselves.</summary>
    public bool Durable { get; init; } = true;
}

public sealed record TaskListResult : ControllerResult
{
    /// <summary>How many tasks the controller knows in all; the list may hold fewer.</summary>
    public required int Total { get; init; }

    public required IReadOnlyList<TaskSummary> Tasks { get; init; }
}

/// <summary>One task's read model: the same <see cref="TaskReadModel"/>, serialized the same way, as <c>aw status --json</c> writes.</summary>
public sealed record TaskResult : ControllerResult
{
    public required TaskReadModel Task { get; init; }
}

public sealed record ShutdownResult : ControllerResult
{
    /// <summary>True when a controller took the request and is stopping; false when there was no controller to stop (reported by the client).</summary>
    public required bool Accepted { get; init; }

    /// <summary>When a request was not accepted because the controller supervises work: how many runs it supervises. Absent otherwise.</summary>
    public int? ActiveRuns { get; init; }
}

public enum ControllerStatus
{
    Ready,
    ShuttingDown,
}

/// <summary>Who a controller is. Nothing here is secret or sensitive: no path, no task, no conversation reference.</summary>
public sealed record ControllerInfo
{
    /// <summary>Identifies this controller process's life: a client that sees it change knows the controller was restarted.</summary>
    public required Guid InstanceId { get; init; }

    public required int ProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The <c>aw</c> version the controller runs.</summary>
    public required string Version { get; init; }

    /// <summary>The kind of local transport that carries the protocol (for example <c>named_pipe</c> or <c>unix_socket</c>); informational, never an address.</summary>
    public required string Transport { get; init; }

    public required int MinimumProtocolVersion { get; init; }

    public required int MaximumProtocolVersion { get; init; }

    /// <summary>The <see cref="TaskReadModel.SchemaVersion"/> this controller produces.</summary>
    public required int ReadModelSchemaVersion { get; init; }
}

/// <summary>
/// A task as a list shows it: enough to find it and see what it waits for, nothing more. A task whose state cannot be read is listed as such
/// (<see cref="Readable"/> false) instead of failing the list.
/// </summary>
public sealed record TaskSummary
{
    public required Guid TaskId { get; init; }

    public required bool Readable { get; init; }

    /// <summary>Why the task cannot be read; null when it can.</summary>
    public required string? Problem { get; init; }

    /// <summary>The start of the task's intent (bounded); null when unreadable.</summary>
    public required string? Intent { get; init; }

    public required DateTimeOffset? CreatedAt { get; init; }

    public required DateTimeOffset? LastEventAt { get; init; }

    public required WorkflowState? State { get; init; }

    public required WorkflowStage? Stage { get; init; }

    public required StageReason? StageReason { get; init; }

    /// <summary>What a person is asked for now, when anything is.</summary>
    public required HumanRequestKind? PendingRequest { get; init; }

    /// <summary>The workspace the run was started in, as the host recorded it; null when unknown.</summary>
    public required string? Workspace { get; init; }

    /// <summary>The task's stream version: a list that shows the same value as before has nothing new to show.</summary>
    public long? StreamVersion { get; init; }

    /// <summary>Why the task is blocked, when it is.</summary>
    public BlockKind? Block { get; init; }

    /// <summary>The roles the task is bound to and what each runs: enough for a list to say which harness and profile work on it. Empty when unreadable.</summary>
    public IReadOnlyList<RoleBindingSummary> Bindings { get; init; } = [];

    /// <summary>The longest intent a summary carries.</summary>
    public const int MaxIntentLength = 200;

    public static TaskSummary From(TaskReadModel model, string? workspace) => new()
    {
        TaskId = model.TaskId,
        Readable = true,
        Problem = null,
        Intent = model.Intent.Length <= MaxIntentLength ? model.Intent : model.Intent[..(MaxIntentLength - 1)] + "…",
        CreatedAt = model.CreatedAt,
        LastEventAt = model.LastEventAt,
        State = model.State,
        Stage = model.Stage,
        StageReason = model.StageReason,
        PendingRequest = model.PendingRequest?.Kind,
        Workspace = workspace,
        StreamVersion = model.StreamVersion,
        Block = model.Block?.Kind,
        Bindings = [.. model.Bindings.Select(b => new RoleBindingSummary(b.Role, b.Profile.Id, b.Configuration?.Harness, b.Configuration?.Model, b.Configuration?.Reasoning))],
    };

    public static TaskSummary Unreadable(Guid taskId, string problem, DateTimeOffset? createdAt, string? workspace) => new()
    {
        TaskId = taskId,
        Readable = false,
        Problem = problem,
        Intent = null,
        CreatedAt = createdAt,
        LastEventAt = null,
        State = null,
        Stage = null,
        StageReason = null,
        PendingRequest = null,
        Workspace = workspace,
    };
}

public sealed record ControllerError(string Code, string Message);

/// <summary>
/// The controller's answer to one request: <see cref="Ok"/> with a <see cref="Result"/>, or not with an <see cref="Error"/> (never both). It states the protocol version
/// it was produced under.
/// </summary>
public sealed record ControllerReply
{
    public required int ProtocolVersion { get; init; }

    public required bool Ok { get; init; }

    public required ControllerResult? Result { get; init; }

    public required ControllerError? Error { get; init; }

    public static ControllerReply Success(ControllerResult result, int protocolVersion = ControllerProtocol.Version)
        => new() { ProtocolVersion = protocolVersion, Ok = true, Result = result, Error = null };

    public static ControllerReply Failure(string code, string message, int protocolVersion = ControllerProtocol.Version)
        => new() { ProtocolVersion = protocolVersion, Ok = false, Result = null, Error = new ControllerError(code, message) };
}

/// <summary>The first message of every connection, sent by the client: who it is and the protocol versions it speaks.</summary>
public sealed record ControllerHello
{
    /// <summary>A short, bounded name of the client (for example <c>aw-ctl</c>); informational, used for the controller's log only.</summary>
    public required string Client { get; init; }

    public required int MinimumProtocolVersion { get; init; }

    public required int MaximumProtocolVersion { get; init; }
}

/// <summary>
/// The controller's answer to the hello. When <see cref="Ok"/>, <see cref="ProtocolVersion"/> is the version both sides now speak and requests may follow; otherwise the
/// connection ends after this message and <see cref="Error"/> says why. It always states the range this controller speaks, so a refused client can report both sides.
/// </summary>
public sealed record ControllerWelcome
{
    public required bool Ok { get; init; }

    public required int? ProtocolVersion { get; init; }

    public required int ServerMinimumProtocolVersion { get; init; }

    public required int ServerMaximumProtocolVersion { get; init; }

    public required ControllerInfo? Controller { get; init; }

    public required ControllerError? Error { get; init; }
}

/// <summary>One role's binding as a list shows it: the profile and, from the configuration recorded with the binding, harness, model and reasoning (null when the profile leaves them to the harness).</summary>
public sealed record RoleBindingSummary(string Role, string Profile, string? Harness, string? Model, string? Reasoning);
