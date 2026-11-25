using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// How a direct run ended. A run completes a task only when the task requires no acceptance (<see cref="Completed"/>, recorded by the controller); a task that
/// requires the human's ACCEPT ends at best at <see cref="AwaitingAcceptance"/>, after which the human's ACCEPT completes it. The runner never marks a task Failed on its own (runtime status is not workflow meaning), and a stale basis or an unknown-outcome write leave the state as it was.
/// A verification that keeps failing (<see cref="VerificationUnresolved"/>) leaves nothing working on the run, so it is recorded BLOCKED (<c>UnresolvedVerificationBlock</c>). When the runtime fails and nothing can work on the run any more
/// (<see cref="Rejected"/>, <see cref="StartupBlocked"/>, <see cref="AgentError"/>, <see cref="AgentCancelled"/>, <see cref="Interrupted"/>,
/// <see cref="ExecutionFailure"/>) the task that was RUNNING is recorded BLOCKED: <c>RuntimeFailureBlock</c> for a first attempt, <c>RecoverableContinuation</c> for a continuation of human
/// input. <see cref="NativeInteractionUnavailable"/> is BLOCKED too, the controller's own record that the agent needed a person in its own interface and the
/// session ended before one could answer.
/// </summary>
public enum DirectRunStatus
{
    /// <summary>
    /// The task requires no acceptance and the controller verified the current implementation, so the controller recorded COMPLETED. No gate was opened and no
    /// human action was needed. The agent's own report played no part.
    /// </summary>
    Completed,

    /// <summary>
    /// The controller verified the current implementation and opened the workflow-owned ACCEPT gate: the task is AWAITING_HUMAN, durably, and needs
    /// no live agent session to be decided. Nothing is complete until the human accepts.
    /// </summary>
    AwaitingAcceptance,

    /// <summary>
    /// The agent deliberately raised a question and the controller opened the workflow-owned CLARIFY gate: the task is AWAITING_HUMAN, durably, and needs no
    /// live agent session to be answered (<c>aw answer</c>). Nothing was verified and no ACCEPT gate was opened; the agent's native conversation was left resumable.
    /// </summary>
    AwaitingClarification,

    /// <summary>
    /// The agent proposed the WHAT of the task and the controller opened the workflow-owned WHAT gate: the task is AWAITING_HUMAN, durably, and needs no live
    /// agent session to be decided (<c>aw approve</c> or <c>aw request-changes</c>). Nothing was implemented, verified or completed; the agent's native conversation
    /// was left resumable.
    /// </summary>
    AwaitingWhat,

    /// <summary>
    /// The WHAT stage was broken: the task needs an approved WHAT before any implementation, and the agent either changed the workspace without one or finished
    /// without proposing one. Nothing was recorded as implemented; the workflow is BLOCKED (<c>WhatStageBlock</c>), and the working tree keeps what the agent did.
    /// </summary>
    WhatStageViolation,

    /// <summary>
    /// The HOW stage was broken: the task needs an approved HOW before any implementation, and the agent either changed the workspace without one or finished
    /// without proposing one. Nothing was recorded as implemented; the workflow is BLOCKED (<c>HowStageBlock</c>), and the working tree keeps what the agent did.
    /// </summary>
    HowStageViolation,

    /// <summary>
    /// The agent proposed the HOW of the task and the controller opened the workflow-owned HOW gate: the task is AWAITING_HUMAN, durably, and needs no live
    /// agent session to be decided (<c>aw approve</c> or <c>aw request-changes</c>). Nothing was implemented, verified or completed; the agent's native conversation
    /// was left resumable.
    /// </summary>
    AwaitingHow,

    /// <summary>Refused before any agent was started (unbound role, profile mismatch, unsupported boundary, ...).</summary>
    Rejected,

    /// <summary>
    /// The agent was waiting for a person to act in its own native interface (a permission, a question, a login) and this run was told nobody is
    /// there to do it. The wait was first recorded as AWAITING_HUMAN while the native interaction was live; once the session was stopped the workflow
    /// became BLOCKED (nothing can answer it any more). Nothing was allowed, denied or answered; the controller never owns that decision.
    /// </summary>
    NativeInteractionUnavailable,

    /// <summary>The agent never became ready because a dialog outside the controller's authority (trust, login, update) is waiting. Nothing was answered.</summary>
    StartupBlocked,

    /// <summary>The agent or its host reported an error. No verification was fabricated.</summary>
    AgentError,

    /// <summary>The agent's turn ended because its session was stopped or interrupted.</summary>
    AgentCancelled,

    /// <summary>Verification still failed after the last permitted turn. The task stays non-terminal.</summary>
    VerificationUnresolved,

    /// <summary>The task moved while the agent worked. Nothing was applied; reload and re-decide.</summary>
    StaleWorkflowState,

    /// <summary>
    /// A workflow mutation failed in a way that leaves its outcome unknown (cancellation, storage failure). The
    /// mutation was NOT repeated; <see cref="DirectRunResult.Task"/> is the freshly reloaded state when that was possible.
    /// </summary>
    Indeterminate,

    /// <summary>The caller cancelled while no workflow mutation was in flight. Workflow state is unchanged by this run after its last confirmed write.</summary>
    Interrupted,

    /// <summary>A port (runtime, workspace inspection, verification runner) failed.</summary>
    ExecutionFailure,

    /// <summary>
    /// An independent REVIEW is required and the Reviewer role has no explicit profile binding, so no review could start. The requirement stays recorded and the work is
    /// left BLOCKED with an actionable reason. A started run's configuration cannot change, so late binding is unsupported (start a new run with a reviewer profile). The implementer is never used as the reviewer.
    /// </summary>
    ReviewerUnbound,

    /// <summary>
    /// The independent reviewer changed the workspace it was only to read. The report was not recorded, no implementation revision was created and nothing was
    /// reverted: the workflow is BLOCKED (<c>ReviewerWorkspaceBlock</c>) and the working tree is preserved exactly as the reviewer left it.
    /// </summary>
    ReviewerModifiedWorkspace,

    /// <summary>The independent review required changes after the last permitted agent turn, so the implementer had no turn left to repair them. The task stays non-terminal.</summary>
    ReviewUnresolved,

    /// <summary>
    /// Formal PROVE is required and the Prover role has no explicit profile binding, so no proof could start. The requirement stays recorded and the work is left BLOCKED
    /// with an actionable reason. Late binding is unsupported (start a new run with a prover profile). Neither the implementer nor the reviewer is ever used as the prover.
    /// </summary>
    ProverUnbound,

    /// <summary>
    /// The independent prover changed the workspace it was only to read. The proof was not recorded, no implementation revision was created and nothing was reverted:
    /// the workflow is BLOCKED (<c>ProverWorkspaceBlock</c>) and the working tree is preserved exactly as the prover left it.
    /// </summary>
    ProverModifiedWorkspace,

    /// <summary>The formal proof did not pass after the last permitted agent turn, so the implementer had no turn left to address it. The task stays non-terminal.</summary>
    ProofUnresolved,

    /// <summary>PROVE is required, the implementer was reminded once and still submitted no Proof Contract, so the work cannot be proved. The task is BLOCKED and the work preserved.</summary>
    ProofContractMissing,
}

/// <summary>
/// Bounded diagnostic for one check of the latest verification. Never persisted in workflow history as such; the
/// <see cref="Observation"/> (set exactly when the runtime, not the controller, executed the check) is what Core records.
/// </summary>
public sealed record CheckDiagnostic(
    VerificationCheckId CheckId,
    VerificationOutcome Outcome,
    string Summary,
    string OutputTail,
    int? ExitCode = null,
    AgentObservation? Observation = null,
    string? EvidenceReason = null);

/// <summary>What happened in one agent turn.</summary>
public sealed record DirectTurnRecord(
    int Turn,
    AgentSessionId Session,
    AgentTurnStatus AgentStatus,
    ImplementationRevisionId? ImplementationRevisionId,
    VerificationOutcome? Verification,
    IReadOnlyList<ScopeViolation> ScopeViolations);

public sealed record DirectRunResult
{
    public required DirectRunStatus Status { get; init; }

    /// <summary>Why the run ended this way, in one line (a stable code, then detail).</summary>
    public required string Reason { get; init; }

    /// <summary>The latest authoritative snapshot the runner holds; after an unknown outcome, the reloaded one (null if reload failed).</summary>
    public TaskSnapshot? Task { get; init; }

    /// <summary>Agent turns used.</summary>
    public int TurnsUsed => Turns.Count;

    /// <summary>The one session used for every turn; null if none was started.</summary>
    public AgentSessionId? Session { get; init; }

    public IReadOnlyList<DirectTurnRecord> Turns { get; init; } = [];

    /// <summary>The check results of the latest verification this run executed (empty if none ran).</summary>
    public IReadOnlyList<CheckDiagnostic> LastVerification { get; init; } = [];

    /// <summary>Why each check of the latest verification was reused from the runtime's record or executed by the controller. Diagnostic only.</summary>
    public IReadOnlyList<EvidenceDecision> EvidenceDecisions { get; init; } = [];

    public DirectCompletionEligibility? Eligibility { get; init; }

    /// <summary>
    /// How the agent's conversation was obtained: new, the earlier native conversation resumed, or a fresh session with the reason it could not be. Null when
    /// the run ended before an agent was involved. Operational provenance, never workflow state.
    /// </summary>
    public AgentContinuation? Continuation { get; init; }

    /// <summary>Operational remarks, e.g. a failure to stop the session.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsCompleted => Status == DirectRunStatus.Completed;

    public bool IsAwaitingAcceptance => Status == DirectRunStatus.AwaitingAcceptance;
}
