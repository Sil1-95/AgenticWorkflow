using System.Text.Json.Serialization;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Controller;

// The workflow-driving operations of the controller: start a task, answer its question, approve or send back a proposal, accept the result, abort, continue. Every one is a
// named, typed request; none carries a command line or a script, and no text in any of them is ever read as a decision (the decision is the operation, the text is the
// person's own words: an objective, an answer, feedback). A decision names the gate it decides and the exact state it was shown (revision, content hash, implementation and
// verification), so a decision about anything else is refused instead of applied to whatever is open now.
//
// These requests only RECORD what was asked and START the work that follows; they never wait for it. The reply says what was accepted and carries the task as it now stands;
// the agent's work then runs under the controller, and the client follows it by reading the task.

/// <summary>Whether a person is expected to answer the agent in its own session (its native permission prompts). A controller-driven task is attended unless it is told it is not.</summary>
public enum TaskPresence
{
    Attended,
    Unattended,
}

/// <summary>The gate kind a request for changes is about; the controller checks it against the gate it names.</summary>
public enum DecisionGateKind
{
    What,
    How,
    Accept,
}

/// <summary>The proposal an approval is about.</summary>
public enum ProposalKind
{
    What,
    How,
}

/// <summary>Creates a task and starts its first stretch of work. The reply is accepted as soon as the task exists and the work is scheduled.</summary>
public sealed record StartTaskRequest : ControllerRequest
{
    /// <summary>What the person wants done, in their own words.</summary>
    public required string Objective { get; init; }

    /// <summary>The caller's absolute working directory: where a relative <see cref="Workspace"/> or <see cref="Config"/> is resolved, and where the project is discovered when neither is given.</summary>
    public required string WorkingDirectory { get; init; }

    public string? Workspace { get; init; }

    public string? Config { get; init; }

    /// <summary>Overrides the project's acceptance requirement; absent keeps the project's.</summary>
    public AcceptanceRequirement? Acceptance { get; init; }

    public bool RequireWhat { get; init; }

    public bool RequireHow { get; init; }

    public bool RequireReview { get; init; }

    public bool RequireProve { get; init; }

    /// <summary>
    /// The profile each named role runs under, by role id (<c>root</c>, <c>reviewer</c>, <c>prover</c>) and profile id. A role named here uses exactly this profile; a role that is not named
    /// uses the profile the project's configuration binds it to, if it binds one. The controller resolves every binding (the profile exists, its harness can run it, its model and reasoning
    /// are honoured) before it creates the task, records the resolved configuration with the task, and refuses the whole start if any of it fails. Nothing is ever picked because it is available.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Bindings { get; init; }

    public TaskPresence? Presence { get; init; }
}

/// <summary>
/// Binds a role the task needs and has no binding for (see the read model's <c>missingBindings</c>) to a profile the project defines. The profile's resolved execution configuration
/// is recorded with the binding. A role that is already bound to a different profile is refused (<see cref="ControllerErrorCodes.AlreadyBound"/>); binding it to the profile it already
/// has changes nothing. Nothing is started by this request: the client continues the task afterwards (<see cref="ContinueTaskRequest"/>).
/// </summary>
public sealed record BindRoleRequest : ControllerRequest
{
    public required string TaskId { get; init; }

    /// <summary>The role to bind: <c>root</c>, <c>reviewer</c> or <c>prover</c>.</summary>
    public required string Role { get; init; }

    public required string Profile { get; init; }
}

/// <summary>Answers the agent's open CLARIFY question and continues the work. The answer is context for the agent, never an approval.</summary>
public sealed record AnswerClarificationRequest : ControllerRequest
{
    public required string TaskId { get; init; }

    public required string GateId { get; init; }

    /// <summary>The semantic revision the question belongs to, as the read model showed it.</summary>
    public required string SemanticRevisionId { get; init; }

    public required string Answer { get; init; }

    public TaskPresence? Presence { get; init; }
}

/// <summary>Approves the open WHAT or HOW proposal exactly as shown, and continues the work. Nothing is implemented, verified or completed by approving.</summary>
public sealed record ApproveProposalRequest : ControllerRequest
{
    public required ProposalKind Proposal { get; init; }

    public required string TaskId { get; init; }

    public required string GateId { get; init; }

    public required string SemanticRevisionId { get; init; }

    /// <summary>The proposal's content hash as the read model showed it: what exactly the person approves.</summary>
    public required string ContentHash { get; init; }

    public TaskPresence? Presence { get; init; }
}

/// <summary>Sends back an open WHAT, HOW or ACCEPT gate with feedback, and continues the work under a new semantic revision.</summary>
public sealed record RequestTaskChangesRequest : ControllerRequest
{
    public required DecisionGateKind Gate { get; init; }

    public required string TaskId { get; init; }

    public required string GateId { get; init; }

    public required string SemanticRevisionId { get; init; }

    public required string Feedback { get; init; }

    /// <summary>For an ACCEPT gate: the implementation the person reviewed. Required for it, absent for a proposal.</summary>
    public string? ImplementationRevisionId { get; init; }

    /// <summary>For an ACCEPT gate: the verification the person reviewed. Required for it, absent for a proposal.</summary>
    public string? VerificationRunId { get; init; }

    public TaskPresence? Presence { get; init; }
}

/// <summary>Accepts the open ACCEPT gate for exactly the implementation and verification the person reviewed. Completes the task; starts no work.</summary>
public sealed record AcceptTaskRequest : ControllerRequest
{
    public required string TaskId { get; init; }

    public required string GateId { get; init; }

    public required string SemanticRevisionId { get; init; }

    public required string ImplementationRevisionId { get; init; }

    public required string VerificationRunId { get; init; }
}

/// <summary>Cancels a task that is waiting for a person or blocked. A task whose work the controller is supervising is not aborted by this request.</summary>
public sealed record AbortTaskRequest : ControllerRequest
{
    public required string TaskId { get; init; }

    /// <summary>When the task is waiting at a gate, the gate the person means to abandon; absent when it is blocked without one.</summary>
    public string? GateId { get; init; }
}

/// <summary>Retries work a person already asked for and that was interrupted: no new decision, no new revision. Only the states the controller can prove safe are accepted.</summary>
public sealed record ContinueTaskRequest : ControllerRequest
{
    public required string TaskId { get; init; }

    public TaskPresence? Presence { get; init; }
}

/// <summary>What the controller did with a workflow operation: it recorded it, and either started work or had none to start.</summary>
public sealed record WorkflowAcceptedResult : ControllerResult
{
    /// <summary>The operation that was accepted (one of <see cref="ControllerOperations"/>).</summary>
    public required string Operation { get; init; }

    /// <summary>True when the controller now supervises a stretch of agent work for this task, in the background; false when the operation was complete in itself (an acceptance, an abort).</summary>
    public required bool WorkScheduled { get; init; }

    /// <summary>The task as it stands after the operation. Follow it by reading the task again.</summary>
    public required TaskReadModel Task { get; init; }
}

// ---- the validated form: what the controller's workflow acts on -------------------------------------------------------------------

/// <summary>A workflow operation after its identifiers and text were validated. It is what <see cref="IControllerWorkflow"/> receives; no wire string reaches it.</summary>
public abstract record WorkflowInstruction(string Operation);

/// <summary>The roles that can be bound by name.</summary>
public static class BindableRoles
{
    public static IReadOnlyList<string> All { get; } = [DirectPathRoles.RootRoleId.Value, DirectPathRoles.ReviewerRoleId.Value, DirectPathRoles.ProverRoleId.Value];

    public static Role? Find(string? role)
        => role == DirectPathRoles.RootRoleId.Value ? DirectPathRoles.Root
            : role == DirectPathRoles.ReviewerRoleId.Value ? DirectPathRoles.Reviewer
            : role == DirectPathRoles.ProverRoleId.Value ? DirectPathRoles.Prover
            : null;
}

public sealed record StartTaskInstruction(
    string Objective,
    string WorkingDirectory,
    string? Workspace,
    string? Config,
    AcceptanceRequirement? Acceptance,
    bool RequireWhat,
    bool RequireHow,
    bool RequireReview,
    bool RequireProve,
    TaskPresence Presence,
    IReadOnlyDictionary<RoleId, ProfileId> Bindings) : WorkflowInstruction(ControllerOperations.StartTask);

public sealed record BindRoleInstruction(TaskId TaskId, RoleId Role, ProfileId Profile) : WorkflowInstruction(ControllerOperations.BindRole);

public sealed record AnswerClarificationInstruction(TaskId TaskId, GateId GateId, Guid SemanticRevisionId, string Answer, TaskPresence Presence)
    : WorkflowInstruction(ControllerOperations.AnswerClarification);

public sealed record ApproveProposalInstruction(ProposalKind Proposal, TaskId TaskId, GateId GateId, Guid SemanticRevisionId, string ContentHash, TaskPresence Presence)
    : WorkflowInstruction(Proposal == ProposalKind.What ? ControllerOperations.ApproveWhat : ControllerOperations.ApproveHow);

public sealed record RequestChangesInstruction(
    DecisionGateKind Gate,
    TaskId TaskId,
    GateId GateId,
    Guid SemanticRevisionId,
    string Feedback,
    Guid? ImplementationRevisionId,
    Guid? VerificationRunId,
    TaskPresence Presence) : WorkflowInstruction(ControllerOperations.RequestChanges);

public sealed record AcceptInstruction(TaskId TaskId, GateId GateId, Guid SemanticRevisionId, Guid ImplementationRevisionId, Guid VerificationRunId)
    : WorkflowInstruction(ControllerOperations.Accept);

public sealed record AbortTaskInstruction(TaskId TaskId, GateId? GateId) : WorkflowInstruction(ControllerOperations.Abort);

public sealed record ContinueTaskInstruction(TaskId TaskId, TaskPresence Presence) : WorkflowInstruction(ControllerOperations.ContinueTask);

/// <summary>What the controller's workflow did: the task it concerns and whether work was scheduled, or the reason nothing was recorded.</summary>
public sealed record WorkflowOutcome
{
    public TaskId? TaskId { get; private init; }

    public bool WorkScheduled { get; private init; }

    public ControllerError? Refusal { get; private init; }

    public static WorkflowOutcome Recorded(TaskId taskId, bool workScheduled) => new() { TaskId = taskId, WorkScheduled = workScheduled };

    /// <summary>Nothing was recorded, and the controller says why. <paramref name="code"/> is one of <see cref="ControllerErrorCodes"/>.</summary>
    public static WorkflowOutcome Refused(string code, string message) => new() { Refusal = new ControllerError(code, message) };
}

/// <summary>
/// The controller's ownership of workflows, as the typed operations need it. The host implements it over the run store, the agent runtime and the verification runner; the
/// contract knows none of them. An implementation validates against the run's durable state BEFORE it records anything, records at most once, and then owns the work the
/// operation started until it ends. It must not throw for a refused operation (that is a <see cref="WorkflowOutcome.Refused"/>).
/// </summary>
public interface IControllerWorkflow
{
    /// <summary>How many runs the controller is supervising right now (a stretch of agent work, scheduled or running). Zero means the controller owns no work.</summary>
    int ActiveRuns { get; }

    Task<WorkflowOutcome> ExecuteAsync(WorkflowInstruction instruction, CancellationToken cancellationToken);
}
