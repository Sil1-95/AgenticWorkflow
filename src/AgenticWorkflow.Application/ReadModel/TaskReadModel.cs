using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.ReadModel;

/// <summary>
/// The structured, serializable read model of one workflow task: what a controller, a UI or a recovery tool needs to know to show the task and to offer the
/// decisions that are legal for it. It is a projection of the task's durable event stream (<see cref="TaskReadModelProjector"/>) and nothing else: it keeps no
/// state of its own, it is never persisted, and nothing in it can be used to change the task. A decision is made by a typed command against the workflow, never by
/// text found here.
/// <para>
/// Everything outside <see cref="Operational"/> is derived from the event stream alone and is deterministic: the same history always projects to the same model.
/// <see cref="Operational"/> carries facts about the run that are not workflow state (where the run lives, whether a process holds it, the native session it
/// waits in); it is supplied by whoever hosts the run, absent when none was supplied, and it can never change what the rest says.
/// </para>
/// <para>
/// Compatibility: <see cref="SchemaVersion"/> changes only when an existing field is removed, renamed or given a different meaning. Fields and enum values are
/// added without a new version, so a consumer must ignore fields it does not know and treat an enum value it does not know as "something new". Identifiers are
/// GUID strings, enums are snake_case strings and times are ISO 8601 (see <see cref="TaskReadModelJson"/>).
/// </para>
/// </summary>
public sealed record TaskReadModel
{
    /// <summary>The schema this build produces. See the compatibility rule on <see cref="TaskReadModel"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    /// <summary>The task's identity. For a run of the <c>aw</c> CLI it is the run id.</summary>
    public required Guid TaskId { get; init; }

    /// <summary>The outcome the human asked for, as given at creation.</summary>
    public required string Intent { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastEventAt { get; init; }

    /// <summary>The sequence of the last event this model reflects. A command that is meant to apply to exactly this model is refused if the task has moved past it.</summary>
    public required long StreamVersion { get; init; }

    /// <summary>The top-level workflow state.</summary>
    public required WorkflowState State { get; init; }

    /// <summary>The reason recorded with the last state transition (or with creation). Recorded history, bounded and single-purpose; never a state in itself.</summary>
    public required string StateReason { get; init; }

    /// <summary>How far the requested outcome must be carried (<c>working_tree</c> is the only boundary the direct path implements).</summary>
    public required string CompletionBoundary { get; init; }

    /// <summary>Where the workflow stands now. <see cref="WorkflowStage.None"/> once the task is terminal.</summary>
    public required WorkflowStage Stage { get; init; }

    /// <summary>Why <see cref="Stage"/> is the active one.</summary>
    public required StageReason StageReason { get; init; }

    public required RequirementsView Requirements { get; init; }

    /// <summary>The semantic revision the task stands under now.</summary>
    public required SemanticRevisionView SemanticRevision { get; init; }

    /// <summary>Every semantic revision, oldest first.</summary>
    public required IReadOnlyList<SemanticRevisionView> SemanticRevisions { get; init; }

    public required ImplementationView Implementation { get; init; }

    public required VerificationView Verification { get; init; }

    /// <summary>Every gate the workflow ever opened, oldest first, with its binding, currentness, content and decision. At most one is open.</summary>
    public required IReadOnlyList<GateView> Gates { get; init; }

    /// <summary>The decision the workflow is waiting for from a person, or null when it waits for none.</summary>
    public required HumanRequestView? PendingRequest { get; init; }

    /// <summary>Every reclassification the agent reported, oldest first.</summary>
    public required IReadOnlyList<ReclassificationView> Reclassifications { get; init; }

    public required ReviewView Review { get; init; }

    public required ProofView Proof { get; init; }

    public required CompletionView Completion { get; init; }

    /// <summary>Why a BLOCKED task is blocked; null in every other state.</summary>
    public required BlockView? Block { get; init; }

    /// <summary>What can be done about a BLOCKED task; null in every other state.</summary>
    public required RecoveryView? Recovery { get; init; }

    /// <summary>The explicit ROLE to PROFILE bindings, ordered by role. A role that is absent is unbound: there is no fallback.</summary>
    public required IReadOnlyList<RoleBindingView> Bindings { get; init; }

    /// <summary>
    /// The roles the task needs now and has no binding for, each with why it is needed. Empty when every role the task needs is bound. Nothing is launched for a missing
    /// binding and no profile is chosen for it: a client binds the role explicitly (the <c>bind_role</c> operation) and the work then continues.
    /// </summary>
    public IReadOnlyList<MissingBindingView> MissingBindings { get; init; } = [];

    /// <summary>Facts about the run that are not workflow state; null when the host supplied none.</summary>
    public required OperationalView? Operational { get; init; }
}

/// <summary>Where the workflow stands. Derived from the history by the same predicates the controller uses to decide what happens next.</summary>
public enum WorkflowStage
{
    /// <summary>The task is terminal.</summary>
    None,

    /// <summary>A CLARIFY question is open.</summary>
    Clarify,

    /// <summary>The WHAT is being proposed, revised or decided.</summary>
    What,

    /// <summary>The HOW is being proposed, revised or decided.</summary>
    How,

    /// <summary>The implementer owes an implementation (a first one, or a repair).</summary>
    Implement,

    /// <summary>The current implementation awaits the controller's verification.</summary>
    Verify,

    /// <summary>The independent review is due.</summary>
    Review,

    /// <summary>The formal PROVE is due, or its Proof Contract is owed.</summary>
    Prove,

    /// <summary>Everything required is satisfied and the human's ACCEPT is what remains.</summary>
    Accept,

    /// <summary>Everything required is satisfied and no acceptance is required: the controller records the completion.</summary>
    Completion,
}

/// <summary>Why a <see cref="WorkflowStage"/> is the active one.</summary>
public enum StageReason
{
    /// <summary>The task is terminal; no stage is active.</summary>
    Terminal,

    /// <summary>A CLARIFY gate is open and waits for the human's answer.</summary>
    AwaitingHumanAnswer,

    /// <summary>A WHAT or HOW gate is open and waits for the human's decision.</summary>
    AwaitingHumanApproval,

    /// <summary>The ACCEPT gate is open and waits for the human's decision.</summary>
    AwaitingHumanAcceptance,

    /// <summary>The stage is required and the agent has not proposed it yet.</summary>
    ProposalNotYetMade,

    /// <summary>The human sent the proposal back; the agent owes a revised one.</summary>
    ProposalRevisionOwed,

    /// <summary>The stage was approved and new evidence (a reclassification, or a later WHAT) invalidated the approval; it must be approved again.</summary>
    ApprovalInvalidated,

    /// <summary>The agent is acting on a human decision (an answer, an approval or requested changes) under the semantic revision it created.</summary>
    HumanInputBeingActedOn,

    /// <summary>No implementation exists yet and nothing prevents one.</summary>
    InitialImplementation,

    /// <summary>The latest implementation targets an earlier semantic revision; a new one is owed.</summary>
    ImplementationStale,

    /// <summary>The current implementation has no verification yet.</summary>
    AwaitingVerification,

    /// <summary>The current implementation's verification failed; the implementer owes a repair.</summary>
    VerificationFailed,

    /// <summary>The independent review of the verified implementation is due.</summary>
    ReviewDue,

    /// <summary>The independent review required changes; the implementer owes a repair.</summary>
    ReviewFindingsOwed,

    /// <summary>PROVE is required and the implementer owes the Proof Contract.</summary>
    ProofContractOwed,

    /// <summary>The independent proof of the verified (and reviewed) implementation is due.</summary>
    ProveDue,

    /// <summary>The independent proof did not pass; the implementer owes a repair.</summary>
    ProofFindingsOwed,

    /// <summary>The work is ready for the human's ACCEPT and the gate has not been opened yet.</summary>
    AcceptGateNotYetOpened,

    /// <summary>The work is verified and no acceptance is required; the controller completes it.</summary>
    EligibleToComplete,
}

/// <summary>Who asked for a requirement. A requirement only ever grows: nothing lowers it.</summary>
public enum RequirementOrigin
{
    /// <summary>Not required.</summary>
    None,

    /// <summary>The human required it when the task was created.</summary>
    Human,

    /// <summary>The agent added it (a reclassification, a voluntary proposal, a review request or a Proof Contract).</summary>
    Agent,
}

/// <summary>What the task requires before it can complete. Each stage is independent: requiring one never implies another.</summary>
public sealed record RequirementsView
{
    public required AcceptanceRequirement Acceptance { get; init; }

    /// <summary>True for a task recorded before the acceptance requirement existed; it keeps the completion rules it was created under.</summary>
    public required bool PredatesAcceptanceRequirement { get; init; }

    public required StageRequirementView What { get; init; }

    public required StageRequirementView How { get; init; }

    public required StageRequirementView Review { get; init; }

    public required StageRequirementView Prove { get; init; }
}

public sealed record StageRequirementView
{
    public required RequirementOrigin Origin { get; init; }

    /// <summary>True when the requirement asks nothing more now (not required, or its latest decision or judgment is current and positive).</summary>
    public required bool Satisfied { get; init; }

    public bool Required => Origin != RequirementOrigin.None;
}

public sealed record SemanticRevisionView
{
    public required Guid Id { get; init; }

    public required int Ordinal { get; init; }

    /// <summary>Why the revision exists (<c>initial</c>, <c>human_clarification</c>, <c>human_what_approval</c>, <c>agent_reclassification</c>, ...).</summary>
    public required string Kind { get; init; }

    public required string Explanation { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>True for the revision the task stands under now.</summary>
    public required bool IsCurrent { get; init; }
}

/// <summary>Whether an implementation, or the verification of one, still describes the task.</summary>
public enum EvidenceCurrency
{
    /// <summary>None was recorded.</summary>
    None,

    /// <summary>It was produced for the current semantic revision and describes the task now.</summary>
    Current,

    /// <summary>It is historical: it targets a semantic revision the task no longer stands under. It stays in the history and satisfies nothing.</summary>
    Stale,
}

public sealed record ImplementationView
{
    public required EvidenceCurrency Currency { get; init; }

    /// <summary>The latest implementation revision, or null when none was recorded.</summary>
    public required ImplementationRevisionView? Latest { get; init; }

    /// <summary>True when the workspace holds edits made under assumptions a reclassification invalidated and no implementation was recorded for them.</summary>
    public required bool HasProvisionalWork { get; init; }

    /// <summary>Every implementation revision, oldest first.</summary>
    public required IReadOnlyList<ImplementationRevisionView> Revisions { get; init; }
}

public sealed record ImplementationRevisionView
{
    public required Guid Id { get; init; }

    /// <summary>The opaque identity of the exact implementation state, exactly as recorded.</summary>
    public required string Fingerprint { get; init; }

    public required Guid SemanticRevisionId { get; init; }

    /// <summary>The ordinal of the semantic revision this implementation was produced against.</summary>
    public required int SemanticOrdinal { get; init; }

    public required Guid? ParentId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>True when it is the latest implementation revision.</summary>
    public required bool IsLatest { get; init; }

    /// <summary>True when it was produced against the semantic revision the task stands under now.</summary>
    public required bool TargetsCurrentSemantics { get; init; }
}

public sealed record VerificationView
{
    /// <summary>Whether the latest implementation has a verification, and whether that verification still describes the task.</summary>
    public required EvidenceCurrency Currency { get; init; }

    /// <summary>The outcome of the latest implementation's latest verification; null when it has none.</summary>
    public required VerificationOutcome? Outcome { get; init; }

    /// <summary>The run that counts now, or null. It is one of <see cref="Runs"/>.</summary>
    public required Guid? CurrentRunId { get; init; }

    /// <summary>Every verification run ever recorded, oldest first.</summary>
    public required IReadOnlyList<VerificationRunView> Runs { get; init; }
}

public sealed record VerificationRunView
{
    public required Guid Id { get; init; }

    public required Guid ImplementationRevisionId { get; init; }

    public required VerificationOutcome Outcome { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>True when this is the run that counts now: the latest verification of the current implementation.</summary>
    public required bool IsCurrent { get; init; }

    public required IReadOnlyList<VerificationCheckView> Checks { get; init; }
}

public sealed record VerificationCheckView
{
    public required string CheckId { get; init; }

    public required VerificationOutcome Outcome { get; init; }

    /// <summary>The recorded summary of the check. Never full command output.</summary>
    public required string Summary { get; init; }

    public required int? ExitCode { get; init; }

    /// <summary>Who executed the check: the controller itself, or the agent's runtime (accepted from its authoritative execution record).</summary>
    public required string Source { get; init; }

    /// <summary>Why the check was reused from the runtime or executed by the controller; null when none was recorded.</summary>
    public required string? EvidenceReason { get; init; }

    /// <summary>Provenance of a check the runtime executed; null when the controller did.</summary>
    public required AgentObservationView? Observation { get; init; }
}

public sealed record AgentObservationView
{
    public required string Runtime { get; init; }

    public required string ToolUseId { get; init; }

    public required string CommandIdentity { get; init; }

    public required long? DurationMilliseconds { get; init; }
}

/// <summary>What a person is asked for. Always a typed decision at a gate or in the agent's own interface; never free text interpreted as one.</summary>
public enum HumanRequestKind
{
    /// <summary>A CLARIFY question waits for an answer.</summary>
    ClarifyAnswer,

    /// <summary>A WHAT proposal waits for a decision.</summary>
    WhatApproval,

    /// <summary>A HOW proposal waits for a decision.</summary>
    HowApproval,

    /// <summary>The verified result waits for the ACCEPT decision.</summary>
    AcceptDecision,

    /// <summary>The agent waits for a person in its own interface (a permission prompt, for example). The workflow owns no gate for it.</summary>
    NativeAgentInteraction,
}

/// <summary>The typed decisions a person can make. Whether one is legal now is part of the model; whether it takes effect is decided by the workflow when it is made.</summary>
public enum HumanAction
{
    /// <summary>Answer a CLARIFY question (the answer is task context, not an approval).</summary>
    Answer,

    /// <summary>Approve a WHAT or HOW proposal.</summary>
    Approve,

    /// <summary>Send a proposal or the verified result back with feedback.</summary>
    RequestChanges,

    /// <summary>Accept the verified result; the task completes.</summary>
    Accept,

    /// <summary>Stop: close the open gate without approving it, or cancel a blocked or waiting task. The task is cancelled.</summary>
    Abort,
}

public sealed record HumanRequestView
{
    public required HumanRequestKind Kind { get; init; }

    /// <summary>The open gate, or null when the request is not a workflow gate (<see cref="HumanRequestKind.NativeAgentInteraction"/>).</summary>
    public required Guid? GateId { get; init; }

    public required GateType? GateType { get; init; }

    /// <summary>Whether the open gate still describes the task; null without a gate. A gate that is not current can only be aborted.</summary>
    public required bool? IsCurrent { get; init; }

    /// <summary>
    /// The decisions that are legal at the workflow level now. A decision can still be refused when it is made for a reason that is not workflow state (a process
    /// still holds the run; the working tree no longer matches what was verified): see <see cref="TaskReadModel.Operational"/>.
    /// </summary>
    public required IReadOnlyList<HumanAction> AllowedActions { get; init; }
}

public sealed record ReclassificationView
{
    public required Guid Id { get; init; }

    public required ReclassificationStage Stage { get; init; }

    public required string Evidence { get; init; }

    public required string Reason { get; init; }

    public required int Turn { get; init; }

    /// <summary>The agent session that reported it (an opaque runtime identifier); null when the runtime gave none.</summary>
    public required string? Session { get; init; }

    public required int PriorSemanticOrdinal { get; init; }

    /// <summary>The ordinal of the one semantic revision it created.</summary>
    public required int SemanticOrdinal { get; init; }

    /// <summary>True when the workspace held edits that were not recorded as an implementation.</summary>
    public required bool ProvisionalWork { get; init; }

    /// <summary>The exact workspace state when the report was accepted.</summary>
    public required string WorkspaceFingerprint { get; init; }

    /// <summary>The approved gates it invalidated, in the order they were opened.</summary>
    public required IReadOnlyList<Guid> InvalidatedGateIds { get; init; }

    /// <summary>True while the task is still rerouted by it: the targeted stage must be approved again before anything is implemented.</summary>
    public required bool IsActive { get; init; }
}

/// <summary>How the ACCEPT requirement stands.</summary>
public enum AcceptState
{
    /// <summary>The task does not require acceptance; the controller completes it.</summary>
    NotApplicable,

    /// <summary>Acceptance is required and the work is not ready for it yet.</summary>
    NotReady,

    /// <summary>The ACCEPT gate is open and describes the task now.</summary>
    AwaitingDecision,

    /// <summary>The ACCEPT gate is open and no longer describes the task; it cannot be accepted.</summary>
    AwaitingDecisionStale,

    /// <summary>The human accepted; the task is completed.</summary>
    Accepted,

    /// <summary>The task ended (cancelled or failed) without an acceptance.</summary>
    NotAccepted,

    /// <summary>The task completed before the ACCEPT gate existed; no acceptance was recorded.</summary>
    NotRecorded,
}

public sealed record CompletionView
{
    public required AcceptState Accept { get; init; }

    /// <summary>The open ACCEPT gate, or null.</summary>
    public required Guid? AcceptGateId { get; init; }

    /// <summary>Whether the task may complete now under its own policy; null once the task is terminal.</summary>
    public required bool? IsEligible { get; init; }

    /// <summary>The stable codes (<c>DirectCompletionReasons</c>) of what is missing. Empty when eligible and once terminal.</summary>
    public required IReadOnlyList<string> BlockingReasons { get; init; }
}

/// <summary>Why a task is BLOCKED, classified from the recorded reason.</summary>
public enum BlockKind
{
    /// <summary>The agent's runtime failed during a human-requested continuation; the work can be continued.</summary>
    RecoverableContinuation,

    /// <summary>The agent's runtime failed during its first attempt; nothing is recoverable in place.</summary>
    RuntimeFailure,

    /// <summary>The controller's verification was still failing after the permitted turns.</summary>
    UnresolvedVerification,

    /// <summary>The WHAT stage was broken: work began without an approved WHAT.</summary>
    WhatStageViolated,

    /// <summary>The HOW stage was broken: work began without an approved HOW.</summary>
    HowStageViolated,

    /// <summary>The independent reviewer changed the workspace it was only to read.</summary>
    ReviewerModifiedWorkspace,

    /// <summary>The independent review still required changes after the last permitted turn.</summary>
    ReviewUnresolved,

    /// <summary>The independent prover changed the workspace it was only to read.</summary>
    ProverModifiedWorkspace,

    /// <summary>The formal proof was not established after the last permitted turn.</summary>
    ProofUnresolved,

    /// <summary>The agent needed a person in its own interface and its session ended before anyone answered.</summary>
    NativeInteractionUnavailable,

    /// <summary>Recorded for a reason this model does not classify (an older or newer history).</summary>
    Other,
}

public sealed record BlockView
{
    public required BlockKind Kind { get; init; }

    /// <summary>The recorded reason, exactly as stored.</summary>
    public required string Reason { get; init; }
}

public enum RecoveryAction
{
    /// <summary>Continue the work a failed continuation stranded (no new decision, revision or gate is recorded).</summary>
    Continue,

    /// <summary>Cancel the blocked task.</summary>
    Abort,
}

public sealed record RecoveryView
{
    /// <summary>The workflow-level actions available for the blocked task. <see cref="RecoveryAction.Continue"/> also needs the run's operational records (see <see cref="OperationalView"/>).</summary>
    public required IReadOnlyList<RecoveryAction> Actions { get; init; }

    /// <summary>The work a recoverable block stranded; null when the block is not recoverable.</summary>
    public required PendingWorkView? PendingWork { get; init; }
}

/// <summary>The work a recoverable continuation would resume, read from the history. The words of the decision itself are on the gate (<see cref="GateView.Decision"/>).</summary>
public sealed record PendingWorkView
{
    public required string Kind { get; init; }

    public required int SemanticOrdinal { get; init; }

    /// <summary>The gate whose decision carries the work; null for work the agent or the controller owes (a reclassification, a review, a proof).</summary>
    public required Guid? DecisionGateId { get; init; }

    public required Guid? ReclassificationId { get; init; }

    public required Guid? ReviewId { get; init; }

    public required Guid? ProofId { get; init; }
}

public sealed record RoleBindingView
{
    public required string Role { get; init; }

    public required ProfileView Profile { get; init; }

    /// <summary>The ordinal of the semantic revision the binding was made under.</summary>
    public required int BoundAtSemanticOrdinal { get; init; }

    /// <summary>
    /// The harness, model and reasoning the profile runs with, as recorded when the role was bound. Null for a binding recorded before execution configuration existed
    /// (the run keeps launching exactly as it always did); a consumer must not infer one from the profile's name.
    /// </summary>
    public required ExecutionConfigurationView? Configuration { get; init; }
}

/// <summary>A role the task needs and has no binding for.</summary>
public sealed record MissingBindingView
{
    public required string Role { get; init; }

    /// <summary>Why the role is needed now: <c>work_requires_root</c>, <c>review_required</c> or <c>prove_required</c>. An open set of stable codes.</summary>
    public required string Reason { get; init; }

    /// <summary>The capabilities any profile bound to this role must provide.</summary>
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }
}

public sealed record ProfileView
{
    public required string Id { get; init; }

    public required string? Description { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>The exact execution configuration of a binding. Every field is an open string: none of them is a closed list of known values.</summary>
public sealed record ExecutionConfigurationView
{
    public required string? Harness { get; init; }

    public required string? Model { get; init; }

    public required string? Reasoning { get; init; }

    /// <summary>Identifies the profile definition these values came from when the role was bound (a hash of the values). Lets a client tell that a project's profile has since changed; it is not a secret.</summary>
    public string? Fingerprint { get; init; }
}
