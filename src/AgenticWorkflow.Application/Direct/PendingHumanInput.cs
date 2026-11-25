using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Direct;

/// <summary>The human decisions that send a run back to work under a new semantic revision.</summary>
public enum HumanInputKind
{
    /// <summary>The person requested changes at the ACCEPT gate (<c>aw request-changes</c>).</summary>
    RequestedChanges,

    /// <summary>The person answered the agent's CLARIFY question (<c>aw answer</c>).</summary>
    Clarification,

    /// <summary>The person approved the agent's WHAT proposal (<c>aw approve</c>): the implementation may begin.</summary>
    WhatApproved,

    /// <summary>The person sent the agent's WHAT proposal back with feedback (<c>aw request-changes</c> at a WHAT gate): the proposal is revised, not implemented.</summary>
    WhatChangesRequested,

    /// <summary>The person approved the agent's HOW proposal (<c>aw approve</c>): the strategy is settled and, once every required stage is satisfied, implementation may begin.</summary>
    HowApproved,

    /// <summary>The person sent the agent's HOW proposal back with feedback (<c>aw request-changes</c> at a HOW gate): the strategy is revised, not implemented.</summary>
    HowChangesRequested,

    /// <summary>
    /// The agent reported new evidence that invalidated a stage and the controller recorded it. Not a human decision: the pending work is the revised proposal the
    /// agent must now make (the stage's approval is invalidated and nothing may be implemented until a person approves it again).
    /// </summary>
    Reclassification,

    /// <summary>
    /// The independent REVIEW of the current verified implementation is due and has not reported: the reviewer was never started, or died before a durable report.
    /// Not human input: the pending work is a fresh reviewer session over the same deterministic packet. The implementer is not involved.
    /// </summary>
    ReviewPending,

    /// <summary>
    /// The independent REVIEW required changes and nothing has been reviewed since: the pending work is the implementer repairing the findings, in its own
    /// original conversation, followed by ordinary verification and a NEW fresh review.
    /// </summary>
    ReviewFindings,

    /// <summary>
    /// The formal PROVE of the current verified (and reviewed, when required) implementation is due and has not reported: the prover was never started, or died before
    /// a durable proof. Not human input: the pending work is a fresh prover session over the same deterministic packet. The implementer is not involved.
    /// </summary>
    ProvePending,

    /// <summary>
    /// The formal PROVE did not pass (FAIL or INCONCLUSIVE) and nothing has been verified since: the pending work is the implementer addressing it, in its own original
    /// conversation, followed by ordinary verification, REVIEW first when required, and a NEW fresh prover.
    /// </summary>
    ProofFindings,
}

/// <summary>
/// Human input the work has not finished acting on, read from the authoritative history alone: the decision that carries it and the semantic revision that
/// decision created (one atomic append). This is the single definition shared by the runner (what to tell the agent), the recoverable continuation (what is
/// stranded when the runtime fails) and the status display.
/// </summary>
public sealed record PendingHumanInput(HumanInputKind Kind, GateDecision? Decision, SemanticRevision Revision, ClarificationRequest? Question, WhatProposal? What = null, HowProposal? How = null, Reclassification? Reclassification = null, Review? Review = null, Guid? ReviewBaselineId = null, Proof? Proof = null, Guid? ProofBaselineId = null)
{
    /// <summary>
    /// The id the workspace baseline of this input is kept under: the decision's gate for human input, and for a reclassification (which has no gate) its own id.
    /// The baseline store is keyed by an opaque identifier, so the two can never collide: both are generated GUIDs.
    /// </summary>
    public GateId BaselineKey => Reclassification is { } reclassified ? new GateId(reclassified.Id.Value) : ReviewBaselineId is { } review ? new GateId(review) : ProofBaselineId is { } proof ? new GateId(proof) : Decision!.GateId;

    /// <summary>The person's own words: the feedback of a request for changes, or the answer to the question. An approval carries none.</summary>
    public string Text => Kind switch
    {
        HumanInputKind.RequestedChanges or HumanInputKind.WhatChangesRequested or HumanInputKind.HowChangesRequested => Decision?.Feedback ?? string.Empty,
        HumanInputKind.Clarification => Decision?.Answer ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// The human input the task is currently under: the latest gate decision is a request for changes or an answer, no gate has been opened since, and the
    /// task still stands under the semantic revision that decision created. Null otherwise. It says nothing about whether implementation work followed.
    /// </summary>
    public static PendingHumanInput? Find(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.PendingGate is not null)
            return null;

        // a recorded reclassification that nothing has acted on yet: no gate decision and no other semantic change since, so the agent still owes the revised proposal.
        if (task.ActiveReclassification is { } reclassified && reclassified.SemanticRevisionId == task.CurrentSemanticRevisionId)
            return new PendingHumanInput(HumanInputKind.Reclassification, null, task.CurrentSemanticRevision, null, Reclassification: reclassified);

        // the independent review owns the work that follows a verified implementation. It is read before the gate decisions because a review creates none: the
        // WHAT approval that preceded the implementation is still the latest decision, yet it is no longer what is pending.
        if (task.IsReviewRequired && task.PendingGate is null)
        {
            if (task.IsReviewDue)
                return new PendingHumanInput(HumanInputKind.ReviewPending, null, task.CurrentSemanticRevision, null, ReviewBaselineId: task.CurrentVerification!.Id.Value);

            if (task.LatestReview is { Verdict: ReviewVerdict.ChangesRequired } findings && findings.SemanticRevisionId == task.CurrentSemanticRevisionId)
                return new PendingHumanInput(HumanInputKind.ReviewFindings, null, task.CurrentSemanticRevision, null, Review: findings, ReviewBaselineId: findings.Id.Value);
        }

        // the formal proof follows the review (a due proof implies a satisfied review) and owns the same kind of pending work: a proof that is due, or the repair of one
        // that did not pass.
        if (task.IsProveRequired && task.PendingGate is null)
        {
            if (task.IsProveDue)
                return new PendingHumanInput(HumanInputKind.ProvePending, null, task.CurrentSemanticRevision, null, ProofBaselineId: task.CurrentVerification!.Id.Value);

            if (task.ProofStatus is ProofStatus.Failed or ProofStatus.Inconclusive && task.LatestProof is { } notPassed)
                return new PendingHumanInput(HumanInputKind.ProofFindings, null, task.CurrentSemanticRevision, null, Proof: notPassed, ProofBaselineId: notPassed.Id.Value);
        }

        if (task.GateDecisions.IsEmpty)
            return null;

        var decision = task.GateDecisions[^1];
        var revision = task.CurrentSemanticRevision;
        var decided = task.Gates.FirstOrDefault(g => g.Id == decision.GateId);

        if (decision.Action == GateAction.Approve && revision.Kind == SemanticChangeKind.HumanWhatApproval && decided?.What is { } approved)
            return new PendingHumanInput(HumanInputKind.WhatApproved, decision, revision, null, approved);

        if (decision.Action == GateAction.RequestChanges && revision.Kind == SemanticChangeKind.HumanWhatChanges && decided?.What is { } revised)
            return new PendingHumanInput(HumanInputKind.WhatChangesRequested, decision, revision, null, revised);

        if (decision.Action == GateAction.Approve && revision.Kind == SemanticChangeKind.HumanHowApproval && decided?.How is { } approvedHow)
            return new PendingHumanInput(HumanInputKind.HowApproved, decision, revision, null, null, approvedHow);

        if (decision.Action == GateAction.RequestChanges && revision.Kind == SemanticChangeKind.HumanHowChanges && decided?.How is { } revisedHow)
            return new PendingHumanInput(HumanInputKind.HowChangesRequested, decision, revision, null, null, revisedHow);

        // The decision and its semantic revision are one atomic append; any later semantic change means the input is no longer what is current.
        if (decision.Action == GateAction.RequestChanges && revision.Kind == SemanticChangeKind.HumanRequestChanges)
            return new PendingHumanInput(HumanInputKind.RequestedChanges, decision, revision, null);

        if (decision.Action == GateAction.Answer
            && revision.Kind == SemanticChangeKind.HumanClarification
            && task.Gates.FirstOrDefault(g => g.Id == decision.GateId)?.Clarification is { } question)
            return new PendingHumanInput(HumanInputKind.Clarification, decision, revision, question);

        return null;
    }
}
