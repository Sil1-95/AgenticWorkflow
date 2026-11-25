using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Execution;

// The agent-to-controller signal. A native question is something the agent's own interface asks a person; a workflow signal is something the agent
// deliberately tells the CONTROLLER, through a typed, bounded channel the runtime provides. Nothing here knows Claude, hooks, MCP or Herdr: a runtime
// translates its own mechanism into these values, and the controller never reads terminal text, transcripts or prose to find one.

/// <summary>
/// The agent deliberately asked the workflow for a human-owned answer, because something the task depends on is missing or ambiguous and it cannot be settled
/// from the repository. <see cref="Question"/> is what the person is asked and <see cref="Reason"/> why the answer materially affects the work. Both are
/// bounded by Core when the gate is opened (<c>ClarificationRequest</c>); they are display text, never authority.
/// </summary>
public sealed record AgentClarificationSignal(string Question, string Reason)
{
    /// <summary>
    /// The name of the capability as the agent sees it. Part of the provider-neutral contract: a runtime must expose a tool of exactly this name (under whatever
    /// prefix its own harness adds), taking a <c>question</c> and a <c>reason</c>, and the direct-path instructions refer to it by this name.
    /// </summary>
    public const string ToolName = "request_clarification";
}

/// <summary>
/// The agent deliberately proposed the WHAT of the task: a structured statement of what is to be achieved, which a human approves before anything is
/// implemented. Every member is display text bounded by Core when the gate is opened (<c>WhatProposal</c>); none is authority. It is a distinct signal from
/// <see cref="AgentClarificationSignal"/>: a question asks for missing information, a proposal asks for approval of a contract.
/// </summary>
public sealed record AgentWhatSignal(
    string Objective,
    string RequiredBehavior,
    string Scope,
    string? Constraints,
    string? NonGoals,
    string AcceptanceConditions,
    string Rationale)
{
    /// <summary>
    /// The name of the capability as the agent sees it. Part of the provider-neutral contract: a runtime must expose a tool of exactly this name (under whatever
    /// prefix its own harness adds), taking the members of this record, and the direct-path instructions refer to it by this name.
    /// </summary>
    public const string ToolName = "propose_what";
}

/// <summary>
/// The agent deliberately proposed the HOW of the task: a structured statement of the consequential implementation strategy it intends to use, which a
/// human approves before anything is implemented. Every member is display text bounded by Core when the gate is opened (<c>HowProposal</c>); none is authority.
/// It is a distinct signal from <see cref="AgentWhatSignal"/>: the WHAT is intent, the HOW is strategy.
/// </summary>
public sealed record AgentHowSignal(
    string Strategy,
    string Components,
    string? DataAndControlFlow,
    string? Compatibility,
    string RisksAndTradeoffs,
    string? VerificationApproach,
    string Rationale)
{
    /// <summary>
    /// The name of the capability as the agent sees it. Part of the provider-neutral contract: a runtime must expose a tool of exactly this name (under whatever
    /// prefix its own harness adds), taking the members of this record, and the direct-path instructions refer to it by this name.
    /// </summary>
    public const string ToolName = "propose_how";
}

/// <summary>
/// The agent deliberately reported new evidence that invalidates the assumptions of a stage the workflow already settled, or never opened. It is a typed
/// report, never a transcript or prose to be interpreted: <see cref="Evidence"/> is what was found, <see cref="Stage"/> the earliest stage that must be reconsidered
/// (the WHAT when the intent itself is in question, the HOW when only the strategy is) and <see cref="Reason"/> why that evidence means the stage no longer holds.
/// All three are display text or a closed enum, bounded by Core when the reclassification is recorded; none is authority. It can only add rigor: there is no
/// signal that removes a requirement, and a report that changes nothing (the stage is already being reconsidered) is refused.
/// </summary>
public sealed record AgentReclassificationSignal(string Evidence, ReclassificationStage Stage, string Reason)
{
    /// <summary>
    /// The name of the capability as the agent sees it. Part of the provider-neutral contract: a runtime must expose a tool of exactly this name (under whatever
    /// prefix its own harness adds), taking an <c>evidence</c>, a <c>stage</c> ("what" or "how") and a <c>reason</c>, and the direct-path instructions refer to it by this name.
    /// </summary>
    public const string ToolName = "report_reclassification";
}

/// <summary>
/// The agent deliberately asked for an independent REVIEW of its verified work. Additive only: it can add the requirement and never remove one, it is not a
/// semantic change and it invalidates nothing. <see cref="Reason"/> is display text bounded by Core when the request is recorded.
/// </summary>
public sealed record AgentReviewRequestSignal(string Reason)
{
    /// <summary>The name of the capability as the agent sees it: a tool of exactly this name taking a <c>reason</c>.</summary>
    public const string ToolName = "request_review";
}

/// <summary>One finding of a submitted review, exactly as the reviewer wrote it. Bounded and validated by Core when the report is built.</summary>
public sealed record AgentReviewFinding(string Title, string Evidence, string Impact, string Recommendation, string? Location);

/// <summary>
/// The independent reviewer's structured report, submitted by its one capability, never read from the terminal or a transcript. <see cref="Verdict"/> is the text
/// the reviewer chose ("PASS" or "CHANGES_REQUIRED"); anything else is a malformed report. Every member is validated and bounded by Core when the report is recorded.
/// </summary>
public sealed record AgentReviewSubmission(string Verdict, string Summary, IReadOnlyList<AgentReviewFinding> Findings, string? Notes)
{
    /// <summary>The name of the capability as the reviewer sees it: a tool of exactly this name taking the members of this record.</summary>
    public const string ToolName = "submit_review";
}

/// <summary>
/// One claim of a submitted Proof Contract, exactly as the agent wrote it. <see cref="Evidence"/> entries are (kind, subject) pairs of the closed vocabulary
/// (<c>verification</c> with a configured check name, <c>review</c> with no subject or <c>current-pass</c>, <c>observation</c> with a short description). Bounded and
/// validated by Core when the contract is built; a malformed contract is rejected whole.
/// </summary>
public sealed record AgentProofClaim(string Id, string Statement, IReadOnlyList<AgentProofEvidenceRequirement> Evidence, string AcceptanceCriterion);

/// <summary>One evidence requirement of a submitted claim: a kind from the closed vocabulary and its subject.</summary>
public sealed record AgentProofEvidenceRequirement(string Kind, string? Subject);

/// <summary>
/// The working agent's structured Proof Contract: the consequential claims its work must make true and the closed-vocabulary evidence each needs. Additive: it can add
/// the PROVE requirement to a run that did not force it and never remove one. It is a typed signal, never read from prose, and it is not itself proof of anything.
/// </summary>
public sealed record AgentProveRequestSignal(string Rationale, IReadOnlyList<AgentProofClaim> Claims)
{
    /// <summary>The name of the capability as the agent sees it: a tool of exactly this name taking the members of this record.</summary>
    public const string ToolName = "request_prove";
}

/// <summary>One claim result of a submitted proof, exactly as the prover wrote it. <see cref="Evidence"/> are typed references (<c>verification:name</c>, <c>review:id</c>, <c>command:n</c>).</summary>
public sealed record AgentProofClaimResult(string ClaimId, string Verdict, IReadOnlyList<string> Evidence, string Summary);

/// <summary>
/// The independent prover's structured report, submitted by its one capability, never read from the terminal or a transcript. The overall verdict is derived from the
/// claim verdicts by Core, never submitted. Every member is validated and bounded by Core when the proof is recorded.
/// </summary>
public sealed record AgentProofSubmission(string Summary, IReadOnlyList<AgentProofClaimResult> Claims)
{
    /// <summary>The name of the capability as the prover sees it: a tool of exactly this name taking the members of this record.</summary>
    public const string ToolName = "submit_proof";
}

/// <summary>
/// Optional capability of an <see cref="IAgentRuntime"/> (the same pattern as <c>IExecutionEvidenceSource</c> and <see cref="IAgentConversationRuntime"/>): it can
/// report the signals an agent raised during one turn of one session. A runtime without it is simply never asked, and an agent run on it cannot raise a
/// workflow question.
/// <para>
/// The runtime is responsible for the signal's identity: only a signal raised by <em>that</em> session during <em>that</em> turn is reported. A signal from
/// another session, an earlier turn, a stale channel or a forged record never appears here.
/// </para>
/// </summary>
public interface IAgentSignalSource
{
    /// <summary>True when this runtime can receive workflow signals from sessions it launches for <paramref name="profile"/>.</summary>
    bool CanSignal(ProfileId profile);

    /// <summary>
    /// The clarification signals the agent raised in <paramref name="turn"/> of <paramref name="session"/>, in the order they arrived. Normally zero or one: the
    /// runtime stops the turn at the first. More than one is reported as such, never merged, so the controller can reject the conflict.
    /// </summary>
    Task<IReadOnlyList<AgentClarificationSignal>> CollectClarificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// The WHAT proposals the agent raised in <paramref name="turn"/> of <paramref name="session"/>, under the same identity rules as
    /// <see cref="CollectClarificationsAsync"/>: only this session and this turn, in arrival order, never merged. A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentWhatSignal>> CollectWhatProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentWhatSignal>>([]);

    /// <summary>
    /// The HOW proposals the agent raised in <paramref name="turn"/> of <paramref name="session"/>, under the same identity rules as
    /// <see cref="CollectClarificationsAsync"/>: only this session and this turn, in arrival order, never merged. A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentHowSignal>> CollectHowProposalsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentHowSignal>>([]);

    /// <summary>
    /// The reclassification reports the agent raised in <paramref name="turn"/> of <paramref name="session"/>, under the same identity rules as
    /// <see cref="CollectClarificationsAsync"/>: only this session and this turn, in arrival order, never merged. A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentReclassificationSignal>> CollectReclassificationsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReclassificationSignal>>([]);

    /// <summary>
    /// The review requests the agent raised in <paramref name="turn"/> of <paramref name="session"/>, under the same identity rules as
    /// <see cref="CollectClarificationsAsync"/>. A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentReviewRequestSignal>> CollectReviewRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReviewRequestSignal>>([]);

    /// <summary>
    /// The review reports the reviewer session submitted in <paramref name="turn"/>, under the same identity rules: only this session and this turn, in arrival order,
    /// never merged (more than one is reported as such so the controller can reject the duplicate). A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentReviewSubmission>> CollectReviewSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentReviewSubmission>>([]);

    /// <summary>
    /// The Proof Contracts the working agent submitted in <paramref name="turn"/> of <paramref name="session"/>, under the same identity rules as
    /// <see cref="CollectClarificationsAsync"/>: only this session and this turn, in arrival order, never merged (more than one is reported as such so the controller can reject the duplicate).
    /// A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentProveRequestSignal>> CollectProveRequestsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentProveRequestSignal>>([]);

    /// <summary>
    /// The proof reports the prover session submitted in <paramref name="turn"/>, under the same identity rules: only this session and this turn, in arrival order,
    /// never merged (more than one is reported as such so the controller can reject the duplicate). A runtime that has no such signal reports none.
    /// </summary>
    Task<IReadOnlyList<AgentProofSubmission>> CollectProofSubmissionsAsync(AgentSessionId session, AgentTurnNumber turn, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AgentProofSubmission>>([]);
}

/// <summary>
/// Where the controller keeps the workspace baseline of a stretch of work across the process boundary of a CLARIFY wait. The baseline is saved <em>before</em>
/// the question is opened, keyed by the question's gate id, so the stretch that continues after the answer is judged against the workspace as it was before the
/// agent asked (the continuation baseline, reused: the same record serves a requested-changes continuation, keyed by its gate).
/// </summary>
public interface IContinuationBaselineSink
{
    /// <summary>Durably records <paramref name="baseline"/> for <paramref name="gate"/> of <paramref name="task"/>. Throws when it could not be kept.</summary>
    Task SaveAsync(TaskId task, GateId gate, ImplementationState baseline, CancellationToken cancellationToken = default);
}

/// <summary>The optional agent-signal capabilities bundled for the runner (the CLARIFY question, the WHAT proposal and the HOW proposal): how a signal arrives, and where the baseline of the wait is kept.</summary>
public sealed record AgentClarificationSupport(IAgentSignalSource Signals, IContinuationBaselineSink Baselines);
