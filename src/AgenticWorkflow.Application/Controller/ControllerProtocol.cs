using AgenticWorkflow.Application.ReadModel;

namespace AgenticWorkflow.Application.Controller;

/// <summary>
/// The version and the limits of the controller protocol. The protocol is what a client and the controller agree on before anything else is exchanged; it is
/// independent of the operating-system transport that carries it.
/// <para>
/// Versioning: <see cref="Version"/> is the newest protocol this build speaks and <see cref="MinimumVersion"/> the oldest it still accepts. A client states the range it
/// speaks in its hello; the controller picks the newest version both speak, or refuses the connection (<see cref="ControllerErrorCodes.IncompatibleProtocol"/>) before any
/// request is read. Adding an operation, or a property of a result, does not change the version; removing or renaming one, or changing what a request or a result means,
/// does. A request carries no property the controller does not know (it is refused, not ignored), so an old controller never half-understands a new client.
/// </para>
/// </summary>
public static class ControllerProtocol
{
    /// <summary>The newest protocol version this build speaks.</summary>
    public const int Version = 1;

    /// <summary>The oldest protocol version this build still speaks.</summary>
    public const int MinimumVersion = 1;

    /// <summary>The largest request or hello a controller reads. Requests are small and typed; anything larger is a protocol violation, not a request.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>The largest reply or welcome a client reads (a read model with every review and proof round is the largest thing sent).</summary>
    public const int MaxReplyBytes = 16 * 1024 * 1024;

    /// <summary>How many tasks <see cref="ListTasksRequest"/> returns unless told otherwise.</summary>
    public const int DefaultListLimit = 20;

    /// <summary>The most tasks one <see cref="ListTasksRequest"/> can ask for.</summary>
    public const int MaxListLimit = 200;

    /// <summary>The newest version both sides speak, or null when their ranges do not overlap.</summary>
    public static int? Negotiate(int clientMinimum, int clientMaximum)
    {
        var highest = Math.Min(clientMaximum, Version);
        var lowest = Math.Max(clientMinimum, MinimumVersion);

        return clientMinimum <= clientMaximum && highest >= lowest ? highest : null;
    }
}

/// <summary>Stable machine-readable codes of a failed controller exchange.</summary>
public static class ControllerErrorCodes
{
    /// <summary>The hello or request is not a well-formed message of the contract. Nothing was done.</summary>
    public const string MalformedRequest = "malformed_request";

    /// <summary>The request names an operation this controller does not have.</summary>
    public const string UnsupportedOperation = "unsupported_operation";

    /// <summary>The client and the controller speak no protocol version in common.</summary>
    public const string IncompatibleProtocol = "incompatible_protocol";

    /// <summary>An argument is outside what the operation accepts (for example a list limit of 0).</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>The identifier is not a task id. Same code as a failed <c>aw status --json</c>.</summary>
    public const string InvalidTaskId = TaskReadFailureCodes.InvalidTaskId;

    /// <summary>There is no such task. Same code as a failed <c>aw status --json</c>.</summary>
    public const string NotFound = TaskReadFailureCodes.NotFound;

    /// <summary>The task exists but its durable state cannot be read. Same code as a failed <c>aw status --json</c>.</summary>
    public const string StateUnreadable = TaskReadFailureCodes.StateUnreadable;

    /// <summary>The controller is stopping and takes no new work.</summary>
    public const string ShuttingDown = "shutting_down";

    // ---- workflow operations: why a decision or a start was refused. Nothing was recorded in any of these cases. ----

    /// <summary>The identifier is not a gate id (a GUID).</summary>
    public const string InvalidGateId = "invalid_gate_id";

    /// <summary>The task has no such gate.</summary>
    public const string NoSuchGate = "no_such_gate";

    /// <summary>The gate was decided before; a gate is decided once.</summary>
    public const string AlreadyDecided = "already_decided";

    /// <summary>The gate no longer describes the task (a later revision, an invalidated approval, a newer implementation or verification).</summary>
    public const string StaleGate = "stale_gate";

    /// <summary>The state the client says it was shown (revision, content, implementation, verification) is not the gate's: the client decided about something else.</summary>
    public const string StaleBinding = "stale_binding";

    /// <summary>The gate is of another kind than the operation decides.</summary>
    public const string WrongGateKind = "wrong_gate_kind";

    /// <summary>The task's state does not allow the operation (it has ended, is not waiting, is not blocked, needs no acceptance).</summary>
    public const string IllegalState = "illegal_state";

    /// <summary>Something else holds the task: a stretch of work in progress, here or in another process. Nothing was recorded.</summary>
    public const string RunOwned = "run_owned";

    /// <summary>The working tree is not the state that was verified and shown to the person.</summary>
    public const string WorkspaceChanged = "workspace_changed";

    /// <summary>The task changed while the operation was being recorded; nothing was.</summary>
    public const string StateMoved = "state_moved";

    /// <summary>Something the work needs is missing or cannot be trusted (configuration, a prerequisite, the workspace, the recorded conversation or baseline). The message says which.</summary>
    public const string PreconditionFailed = "precondition_failed";

    // ---- role bindings: a start or a binding was refused because of how a role would be run. Nothing was recorded and nothing was launched. ----

    /// <summary>A role the work needs has no binding, and none was chosen for it: the client must bind it explicitly.</summary>
    public const string MissingBinding = "missing_binding";

    /// <summary>The named profile is not defined by the project (and is not a built-in one). There is no fallback to another profile.</summary>
    public const string UnknownProfile = "unknown_profile";

    /// <summary>The profile names a harness this build cannot run (one it knows only by name, or none at all). There is no fallback to another harness.</summary>
    public const string UnsupportedHarness = "unsupported_harness";

    /// <summary>The harness cannot honour the profile's model or reasoning. The message names the profile and the value; nothing was dropped or defaulted.</summary>
    public const string UnsupportedConfiguration = "unsupported_configuration";

    /// <summary>The role already has a binding that is not this one. A binding is never replaced.</summary>
    public const string AlreadyBound = "already_bound";

    /// <summary>This controller could be killed together with the process that started it, so it must not take ownership of work.</summary>
    public const string ControllerNotDurable = "controller_not_durable";

    /// <summary>The controller failed in a way that is not the request's fault. The detail is in the controller's own log, not in the reply.</summary>
    public const string InternalError = "internal_error";

    // Client-side codes: produced by the client, never by a controller, so a failure to reach one looks like every other failure.

    /// <summary>No controller could be reached, and none could be started.</summary>
    public const string ControllerUnavailable = "controller_unavailable";

    /// <summary>A controller holds the endpoint but did not answer the handshake in time.</summary>
    public const string ControllerUnresponsive = "controller_unresponsive";
}
