using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Controller;

/// <summary>
/// Turns a workflow request as it arrived (strings from a peer) into the validated <see cref="WorkflowInstruction"/> the controller's workflow acts on, or into the error to
/// reply with. Identifiers, sizes and shapes are checked here, before the workflow, the run store or the agent runtime is touched, so a malformed request can never change
/// anything. Nothing here reads text as a decision: the request's type is the decision.
/// </summary>
public static class WorkflowRequestValidator
{
    /// <summary>The longest objective a task can be started with. The request as a whole is bounded by <see cref="ControllerProtocol.MaxRequestBytes"/>.</summary>
    public const int MaxObjectiveLength = 16_000;

    /// <summary>The longest path a request carries.</summary>
    public const int MaxPathLength = 1024;

    private const int MaxContentHashLength = 128;

    /// <summary>True for the requests that start or decide work (and so need a workflow); false for the read-only and lifecycle requests.</summary>
    public static bool IsWorkflowRequest(ControllerRequest request)
        => request is StartTaskRequest or AnswerClarificationRequest or ApproveProposalRequest or RequestTaskChangesRequest or AcceptTaskRequest or AbortTaskRequest or ContinueTaskRequest or BindRoleRequest;

    public static (WorkflowInstruction? Instruction, ControllerError? Error) Validate(ControllerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request switch
        {
            StartTaskRequest start => Start(start),
            AnswerClarificationRequest answer => Answer(answer),
            ApproveProposalRequest approve => Approve(approve),
            RequestTaskChangesRequest changes => Changes(changes),
            AcceptTaskRequest accept => Accept(accept),
            AbortTaskRequest abort => Abort(abort),
            ContinueTaskRequest resume => Continue(resume),
            BindRoleRequest bind => Bind(bind),
            _ => (null, new ControllerError(ControllerErrorCodes.UnsupportedOperation, "This controller has no such operation.")),
        };
    }

    private static (WorkflowInstruction?, ControllerError?) Start(StartTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Objective))
            return Invalid("'objective' is required.");

        if (request.Objective.Length > MaxObjectiveLength)
            return Invalid($"'objective' must not exceed {MaxObjectiveLength} characters.");

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || request.WorkingDirectory.Length > MaxPathLength || !Path.IsPathFullyQualified(request.WorkingDirectory))
            return Invalid("'workingDirectory' must be an absolute path.");

        if (request.Workspace is { } workspace && (string.IsNullOrWhiteSpace(workspace) || workspace.Length > MaxPathLength))
            return Invalid("'workspace' must be a path of at most " + MaxPathLength + " characters.");

        if (request.Config is { } config && (string.IsNullOrWhiteSpace(config) || config.Length > MaxPathLength))
            return Invalid("'config' must be a path of at most " + MaxPathLength + " characters.");

        if (request.Acceptance is { } acceptance && !Enum.IsDefined(acceptance))
            return Invalid("'acceptance' is not a known requirement.");

        var (bindings, bindingProblem) = Bindings(request.Bindings);

        if (bindingProblem is not null)
            return Invalid(bindingProblem);

        return (new StartTaskInstruction(
            request.Objective.Trim(),
            request.WorkingDirectory,
            request.Workspace,
            request.Config,
            request.Acceptance,
            request.RequireWhat,
            request.RequireHow,
            request.RequireReview,
            request.RequireProve,
            Presence(request.Presence),
            bindings), null);
    }

    /// <summary>The most role-to-profile choices a start can carry: there are three bindable roles, so more than that names a role twice or a role that is not there.</summary>
    private const int MaxBindings = 8;

    private static (IReadOnlyDictionary<RoleId, ProfileId> Bindings, string? Problem) Bindings(IReadOnlyDictionary<string, string>? requested)
    {
        var bindings = new Dictionary<RoleId, ProfileId>();

        if (requested is null)
            return (bindings, null);

        if (requested.Count > MaxBindings)
            return (bindings, $"'bindings' names too many roles (at most {MaxBindings}).");

        foreach (var (role, profile) in requested)
        {
            if (BindableRoles.Find(role) is null)
                return (bindings, $"'bindings' names role '{Shorten(role)}', which cannot be bound (known: {string.Join(", ", BindableRoles.All)}).");

            if (!IsProfileId(profile))
                return (bindings, $"The profile for role '{role}' is not a profile id (lowercase letters, digits, '_', '.', ':' and '-', at most 64 characters).");

            bindings[new RoleId(role)] = new ProfileId(profile);
        }

        return (bindings, null);
    }

    private static (WorkflowInstruction?, ControllerError?) Bind(BindRoleRequest request)
    {
        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (BindableRoles.Find(request.Role) is null)
            return Invalid($"'role' must be one of: {string.Join(", ", BindableRoles.All)}.");

        if (!IsProfileId(request.Profile))
            return Invalid("'profile' is not a profile id (lowercase letters, digits, '_', '.', ':' and '-', at most 64 characters).");

        return (new BindRoleInstruction(task, new RoleId(request.Role), new ProfileId(request.Profile)), null);
    }

    private static bool IsProfileId(string? text)
        => !string.IsNullOrEmpty(text) && text.Length <= 64 && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or ':' or '-');

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "...";

    private static (WorkflowInstruction?, ControllerError?) Answer(AnswerClarificationRequest request)
    {
        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (Gate(request.GateId) is not { } gate)
            return InvalidGate();

        if (Revision(request.SemanticRevisionId) is not { } revision)
            return Invalid("'semanticRevisionId' must be a GUID.");

        if (string.IsNullOrWhiteSpace(request.Answer) || request.Answer.Length > GateDecision.MaxAnswerLength)
            return Invalid($"'answer' is required and must not exceed {GateDecision.MaxAnswerLength} characters.");

        return (new AnswerClarificationInstruction(task, gate, revision, request.Answer.Trim(), Presence(request.Presence)), null);
    }

    private static (WorkflowInstruction?, ControllerError?) Approve(ApproveProposalRequest request)
    {
        if (!Enum.IsDefined(request.Proposal))
            return Invalid("'proposal' must be 'what' or 'how'.");

        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (Gate(request.GateId) is not { } gate)
            return InvalidGate();

        if (Revision(request.SemanticRevisionId) is not { } revision)
            return Invalid("'semanticRevisionId' must be a GUID.");

        if (string.IsNullOrWhiteSpace(request.ContentHash) || request.ContentHash.Length > MaxContentHashLength)
            return Invalid("'contentHash' is required: it names exactly what is approved.");

        return (new ApproveProposalInstruction(request.Proposal, task, gate, revision, request.ContentHash.Trim(), Presence(request.Presence)), null);
    }

    private static (WorkflowInstruction?, ControllerError?) Changes(RequestTaskChangesRequest request)
    {
        if (!Enum.IsDefined(request.Gate))
            return Invalid("'gate' must be 'what', 'how' or 'accept'.");

        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (Gate(request.GateId) is not { } gate)
            return InvalidGate();

        if (Revision(request.SemanticRevisionId) is not { } revision)
            return Invalid("'semanticRevisionId' must be a GUID.");

        if (string.IsNullOrWhiteSpace(request.Feedback) || request.Feedback.Length > GateDecision.MaxFeedbackLength)
            return Invalid($"'feedback' is required and must not exceed {GateDecision.MaxFeedbackLength} characters.");

        Guid? implementation = null;
        Guid? verification = null;

        if (request.Gate == DecisionGateKind.Accept)
        {
            implementation = Revision(request.ImplementationRevisionId);
            verification = Revision(request.VerificationRunId);

            if (implementation is null || verification is null)
                return Invalid("Changes to an ACCEPT gate name the implementation and the verification that were reviewed ('implementationRevisionId', 'verificationRunId').");
        }
        else if (request.ImplementationRevisionId is not null || request.VerificationRunId is not null)
        {
            return Invalid("A proposal has no implementation or verification to name.");
        }

        return (new RequestChangesInstruction(request.Gate, task, gate, revision, request.Feedback.Trim(), implementation, verification, Presence(request.Presence)), null);
    }

    private static (WorkflowInstruction?, ControllerError?) Accept(AcceptTaskRequest request)
    {
        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (Gate(request.GateId) is not { } gate)
            return InvalidGate();

        if (Revision(request.SemanticRevisionId) is not { } revision
            || Revision(request.ImplementationRevisionId) is not { } implementation
            || Revision(request.VerificationRunId) is not { } verification)
        {
            return Invalid("An acceptance names the semantic revision, the implementation and the verification that were reviewed, each a GUID.");
        }

        return (new AcceptInstruction(task, gate, revision, implementation, verification), null);
    }

    private static (WorkflowInstruction?, ControllerError?) Abort(AbortTaskRequest request)
    {
        if (Task(request.TaskId) is not { } task)
            return (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

        if (request.GateId is null)
            return (new AbortTaskInstruction(task, null), null);

        return Gate(request.GateId) is { } gate ? (new AbortTaskInstruction(task, gate), null) : InvalidGate();
    }

    private static (WorkflowInstruction?, ControllerError?) Continue(ContinueTaskRequest request)
        => Task(request.TaskId) is { } task
            ? (new ContinueTaskInstruction(task, Presence(request.Presence)), null)
            : (null, new ControllerError(ControllerErrorCodes.InvalidTaskId, "The task id is not a task id (expected a GUID)."));

    private static TaskPresence Presence(TaskPresence? presence) => presence ?? TaskPresence.Attended;

    private static TaskId? Task(string? text) => Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty ? new TaskId(guid) : null;

    private static GateId? Gate(string? text) => Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty ? new GateId(guid) : null;

    private static Guid? Revision(string? text) => Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty ? guid : null;

    private static (WorkflowInstruction?, ControllerError?) Invalid(string message) => (null, new ControllerError(ControllerErrorCodes.InvalidArgument, message));

    private static (WorkflowInstruction?, ControllerError?) InvalidGate()
        => (null, new ControllerError(ControllerErrorCodes.InvalidGateId, "The gate id is not a gate id (expected a GUID)."));
}
