using AgenticWorkflow.Core.Authority;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Policy;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Core.Events;

/// <summary>
/// A fact that happened to a task. The hierarchy is closed (private-protected constructor): only the kernel
/// defines event types, so the replay switch is exhaustive by construction. Payloads carry no task id or
/// timestamp of their own beyond what the records inside them define; those live on <see cref="EventEnvelope"/>.
/// </summary>
public abstract record DomainEvent
{
    private protected DomainEvent()
    {
    }

    /// <summary>Stable machine-readable name of the event type.</summary>
    public abstract string Kind { get; }
}

public static class EventKinds
{
    public const string TaskCreated = "task_created";
    public const string WorkflowStateChanged = "workflow_state_changed";
    public const string SemanticRevisionCreated = "semantic_revision_created";
    public const string ImplementationRevisionRecorded = "implementation_revision_recorded";
    public const string RoleBound = "role_bound";
    public const string AuthorityEnvelopeChanged = "authority_envelope_changed";
    public const string PolicyDecisionRecorded = "policy_decision_recorded";
    public const string VerificationRecorded = "verification_recorded";
    public const string GateOpened = "gate_opened";
    public const string GateDecided = "gate_decided";
    public const string ReclassificationRecorded = "reclassification_recorded";
    public const string ReviewRequested = "review_requested";
    public const string ReviewRecorded = "review_recorded";
    public const string ProofContractRecorded = "proof_contract_recorded";
    public const string ProofRecorded = "proof_recorded";
}

/// <summary>
/// First event of every stream. Creates the task in RUNNING together with its initial semantic revision and the acceptance requirement it will be
/// completed under. A history written before the requirement existed is read by the codec as <see cref="AcceptanceRequirement.HumanRequired"/> with
/// <see cref="PredatesAcceptanceRequirement"/> set: only such a history may still complete by a plain recorded COMPLETED (how every task completed before
/// the ACCEPT gate). A task created now never sets it; for it, a human-required task completes only through an accepted gate.
/// <see cref="WhatRequired"/> records the human's per-run requirement that the WHAT be approved before implementation; it is persisted here, in the
/// authoritative creation, so nothing later can remove it. A history recorded before it existed reads as not required.
/// <see cref="HowRequired"/> is the same for the HOW (implementation strategy) approval; the two requirements are independent.
/// <see cref="ReviewRequired"/> is the same for the independent REVIEW of the verified implementation; it implies neither the WHAT nor the HOW, and neither
/// implies it. A history recorded before it existed reads as not required.
/// <see cref="ProveRequired"/> is the same for formal PROVE (a Proof Contract judged by an independent prover); it implies none of the others and none implies it.
/// </summary>
public sealed record TaskCreated(
    string Intent,
    CompletionBoundary CompletionBoundary,
    SemanticRevision InitialSemanticRevision,
    AcceptanceRequirement Acceptance,
    bool PredatesAcceptanceRequirement = false,
    bool WhatRequired = false,
    bool HowRequired = false,
    bool ReviewRequired = false,
    bool ProveRequired = false) : DomainEvent
{
    public override string Kind => EventKinds.TaskCreated;
}

/// <summary>An explicit, reasoned workflow-state change. <see cref="From"/> is recorded so history is self-checking.</summary>
public sealed record WorkflowStateChanged(WorkflowState From, WorkflowState To, string Reason) : DomainEvent
{
    public override string Kind => EventKinds.WorkflowStateChanged;
}

public sealed record SemanticRevisionCreated(SemanticRevision Revision) : DomainEvent
{
    public override string Kind => EventKinds.SemanticRevisionCreated;
}

public sealed record ImplementationRevisionRecorded(ImplementationRevision Revision) : DomainEvent
{
    public override string Kind => EventKinds.ImplementationRevisionRecorded;
}

/// <summary>A role was explicitly bound (or re-bound) to a profile. Replaces any earlier binding of that role.</summary>
public sealed record RoleBound(RoleBinding Binding) : DomainEvent
{
    public override string Kind => EventKinds.RoleBound;
}

/// <summary>The whole authority envelope was replaced under the stated (current) semantic revision.</summary>
public sealed record AuthorityEnvelopeChanged(
    SemanticRevisionId SemanticRevisionId,
    AuthorityEnvelope Envelope,
    string Reason) : DomainEvent
{
    public override string Kind => EventKinds.AuthorityEnvelopeChanged;
}

public sealed record PolicyDecisionRecorded(PolicyDecision Decision) : DomainEvent
{
    public override string Kind => EventKinds.PolicyDecisionRecorded;
}

/// <summary>A finished lightweight verification of one exact implementation revision.</summary>
public sealed record VerificationRecorded(VerificationRun Run) : DomainEvent
{
    public override string Kind => EventKinds.VerificationRecorded;
}

/// <summary>
/// A workflow-owned human gate was opened over the exact state it binds. Applying it moves the task from RUNNING to AWAITING_HUMAN;
/// from then on only a <see cref="GateDecided"/> can move the task, never a plain state change.
/// </summary>
public sealed record GateOpened(Gate Gate) : DomainEvent
{
    public override string Kind => EventKinds.GateOpened;
}

/// <summary>
/// The one decision that closes the open gate. Applying it moves the task: accept to COMPLETED, request changes to RUNNING,
/// cancel to CANCELLED.
/// </summary>
public sealed record GateDecided(GateDecision Decision) : DomainEvent
{
    public override string Kind => EventKinds.GateDecided;
}

/// <summary>
/// New evidence invalidated the assumptions of a stage. One atomic fact: the <see cref="Reclassification"/> (evidence, stage, the exact gates it invalidated)
/// and the one <see cref="SemanticRevision"/> (kind <c>agent_reclassification</c>) it creates, so a reclassification never exists without its revision. The task stays
/// RUNNING; the stage it targets is unsatisfied until a new proposal is approved. Nothing is deleted: the invalidated gates, implementation and verification remain in
/// history and are simply no longer current.
/// </summary>
public sealed record ReclassificationRecorded(Reclassification Reclassification, SemanticRevision Revision) : DomainEvent
{
    public override string Kind => EventKinds.ReclassificationRecorded;
}

/// <summary>
/// The working agent asked for an independent REVIEW. Additive only: it adds the requirement, creates no semantic revision, invalidates no gate and can never be
/// undone. Like a reclassification it is a recorded fact, never parsed from prose.
/// </summary>
public sealed record ReviewRequested(ReviewRequest Request) : DomainEvent
{
    public override string Kind => EventKinds.ReviewRequested;
}

/// <summary>
/// An independent review of the current verified implementation was recorded: the reviewer's report with the provenance that makes it count. It changes no state
/// but the review history; whether it satisfies the requirement is derived from what it is bound to (see <c>WorkflowTask.ReviewStatus</c>).
/// </summary>
public sealed record ReviewRecorded(Review Review) : DomainEvent
{
    public override string Kind => EventKinds.ReviewRecorded;
}

/// <summary>
/// The working agent submitted its Proof Contract, bound to the semantic revision it was written for. Recording it is also how the agent adds PROVE to a run that did
/// not force it: additive only, creating no semantic revision, invalidating nothing and never undone. Like a reclassification it is a recorded fact, never parsed from prose.
/// </summary>
public sealed record ProofContractRecorded(ProofContract Contract) : DomainEvent
{
    public override string Kind => EventKinds.ProofContractRecorded;
}

/// <summary>
/// A formal proof over the current Proof Contract was recorded: the prover's report with the provenance that makes it count. It changes no state but the proof
/// history; whether it satisfies the requirement is derived from what it is bound to (see <c>WorkflowTask.ProofStatus</c>).
/// </summary>
public sealed record ProofRecorded(Proof Proof) : DomainEvent
{
    public override string Kind => EventKinds.ProofRecorded;
}
