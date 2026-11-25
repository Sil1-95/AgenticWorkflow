using System.Collections.Immutable;
using AgenticWorkflow.Core.Authority;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Policy;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Core.Lifecycle;

/// <summary>
/// The authoritative current state of one task, as reconstructed from its event history. Immutable; only the
/// kernel (via event replay) can produce or change one, so every instance satisfies the structural invariants.
/// It is deliberately proportional: a trivial task has an intent, a boundary, one semantic revision and
/// nothing else. Proof, gates, findings and stages do not exist here.
/// </summary>
public sealed record WorkflowTask
{
    public TaskId Id { get; internal init; }

    /// <summary>The user's requested outcome, as given at creation.</summary>
    public string Intent { get; internal init; } = string.Empty;

    public WorkflowState WorkflowState { get; internal init; }

    /// <summary>Why the task is in <see cref="WorkflowState"/> (the reason of the last transition, or creation).</summary>
    public string StateReason { get; internal init; } = string.Empty;

    public CompletionBoundary CompletionBoundary { get; internal init; } = CompletionBoundary.WorkingTree;

    /// <summary>
    /// Whether the human's final ACCEPT is part of how this task completes. Fixed when the task is created; a task whose history predates the
    /// requirement is <see cref="AcceptanceRequirement.HumanRequired"/>.
    /// </summary>
    public AcceptanceRequirement Acceptance { get; internal init; } = AcceptanceRequirement.HumanRequired;

    /// <summary>
    /// True only for a task read from a history recorded before the acceptance requirement existed. Such a task keeps the completion rules it was created
    /// under (a plain recorded COMPLETED is valid); every task created since must complete through its requirement's path.
    /// </summary>
    public bool PredatesAcceptanceRequirement { get; internal init; }

    /// <summary>
    /// True when the human required, at creation, that the task's WHAT be approved before anything is implemented. Fixed in the first event: nothing
    /// later (configuration, the agent) can remove it.
    /// </summary>
    public bool WhatRequired { get; internal init; }

    /// <summary>
    /// True when the human required, at creation, that the task's implementation strategy (HOW) be approved before anything is implemented. Fixed in the
    /// first event, independent of <see cref="WhatRequired"/>: nothing later (configuration, the agent) can remove it.
    /// </summary>
    public bool HowRequired { get; internal init; }

    /// <summary>
    /// True when the human required, at creation, that the verified implementation be independently reviewed before the task completes (or reaches its ACCEPT
    /// gate). Fixed in the first event, independent of <see cref="WhatRequired"/> and <see cref="HowRequired"/>: it implies neither and neither implies it. The agent
    /// can add the same requirement later (<see cref="ReviewRequests"/>); nothing removes it.
    /// </summary>
    public bool ReviewRequired { get; internal init; }

    /// <summary>
    /// True when the human required, at creation, formal PROVE: a Proof Contract the implementer writes and a fresh independent prover judges, claim by claim, before the
    /// task completes (or reaches its ACCEPT gate). Fixed in the first event, independent of WHAT, HOW and REVIEW: it implies none of them and none implies it. The agent can add
    /// the same requirement later by submitting a Proof Contract (<see cref="ProofContracts"/>); nothing removes it.
    /// </summary>
    public bool ProveRequired { get; internal init; }

    public SemanticRevisionId CurrentSemanticRevisionId { get; internal init; }

    /// <summary>Null until the first implementation revision is recorded.</summary>
    public ImplementationRevisionId? CurrentImplementationRevisionId { get; internal init; }

    public DateTimeOffset CreatedAt { get; internal init; }

    /// <summary>All semantic revisions, oldest first (ordinal 1 ... n).</summary>
    public ImmutableArray<SemanticRevision> SemanticRevisions { get; internal init; } = [];

    /// <summary>All implementation revisions, oldest first.</summary>
    public ImmutableArray<ImplementationRevision> ImplementationRevisions { get; internal init; } = [];

    /// <summary>Current explicit binding per role. A role absent here is unbound; there is no fallback.</summary>
    public ImmutableDictionary<RoleId, RoleBinding> RoleBindings { get; internal init; }
        = ImmutableDictionary<RoleId, RoleBinding>.Empty;

    public AuthorityEnvelope Authority { get; internal init; } = AuthorityEnvelope.Empty;

    public ImmutableArray<PolicyDecision> PolicyDecisions { get; internal init; } = [];

    /// <summary>
    /// Every lightweight verification run ever recorded, oldest first. Runs for superseded implementation
    /// revisions stay here as history; they are simply not <see cref="CurrentVerification"/>.
    /// </summary>
    public ImmutableArray<VerificationRun> Verifications { get; internal init; } = [];

    /// <summary>Every workflow-owned gate ever opened, oldest first. Decided gates stay here as history.</summary>
    public ImmutableArray<Gate> Gates { get; internal init; } = [];

    /// <summary>The decision of each decided gate, in the order made. A gate with no entry here is open.</summary>
    public ImmutableArray<GateDecision> GateDecisions { get; internal init; } = [];

    /// <summary>
    /// Every reclassification ever recorded, oldest first: each is new evidence that invalidated the assumptions of a stage. They stay here as history; the
    /// newest one that no later implementation has superseded is <see cref="ActiveReclassification"/>.
    /// </summary>
    public ImmutableArray<Reclassification> Reclassifications { get; internal init; } = [];

    /// <summary>Every request the working agent made that the task be independently reviewed, oldest first. Additive: the first one makes REVIEW required, and nothing lowers it.</summary>
    public ImmutableArray<ReviewRequest> ReviewRequests { get; internal init; } = [];

    /// <summary>Every independent review ever recorded, oldest first. A review that no longer describes the task stays here as history; it is simply not current.</summary>
    public ImmutableArray<Review> Reviews { get; internal init; } = [];

    /// <summary>Every Proof Contract ever submitted, oldest first. One per semantic revision at most; a contract of an earlier revision stays here as history and is simply not current.</summary>
    public ImmutableArray<ProofContract> ProofContracts { get; internal init; } = [];

    /// <summary>Every formal proof ever recorded, oldest first. A proof that no longer describes the task stays here as history; it is simply not current.</summary>
    public ImmutableArray<Proof> Proofs { get; internal init; } = [];

    /// <summary>Sequence of the last event applied.</summary>
    public long LastSequence { get; internal init; }

    public DateTimeOffset LastEventAt { get; internal init; }

    public bool IsTerminal => WorkflowState.IsTerminal();

    public SemanticRevision CurrentSemanticRevision => SemanticRevisions[^1];

    public ImplementationRevision? CurrentImplementationRevision
        => ImplementationRevisions.IsEmpty ? null : ImplementationRevisions[^1];

    /// <summary>
    /// The latest verification run of exactly <paramref name="implementationRevisionId"/>, or null. A run for any
    /// other revision never counts, so evidence cannot carry over to a newer implementation.
    /// </summary>
    public VerificationRun? LatestVerificationFor(ImplementationRevisionId implementationRevisionId)
    {
        for (var i = Verifications.Length - 1; i >= 0; i--)
        {
            if (Verifications[i].ImplementationRevisionId == implementationRevisionId)
                return Verifications[i];
        }

        return null;
    }

    /// <summary>The latest verification of the <em>current</em> implementation revision; null if none or no implementation.</summary>
    public VerificationRun? CurrentVerification
        => CurrentImplementationRevisionId is { } current ? LatestVerificationFor(current) : null;

    /// <summary>The gate awaiting a decision, or null. There is at most one: it is always the latest gate opened.</summary>
    public Gate? PendingGate => Gates.IsEmpty || DecisionFor(Gates[^1].Id) is not null ? null : Gates[^1];

    public GateDecision? DecisionFor(GateId gateId)
        => GateDecisions.FirstOrDefault(d => d.GateId == gateId);

    /// <summary>
    /// Where a gate is in its life. An approved gate that is no longer current reads <see cref="GateStatus.Invalidated"/>; its approval stays in
    /// <see cref="GateDecisions"/>, so the lineage (approved, then invalidated) is never lost.
    /// </summary>
    public GateStatus StatusOf(Gate gate)
    {
        var status = DecisionFor(gate.Id)?.ResultingStatus ?? GateStatus.Open;

        return status == GateStatus.Approved && InvalidationOf(gate) is not null ? GateStatus.Invalidated : status;
    }

    /// <summary>
    /// Why an approved <paramref name="gate"/> is no longer current, or null while it is (or when it was never approved). Derived from the history alone:
    /// a reclassification names the gates it invalidated, and an approved HOW is also not current once a WHAT was approved after it (the HOW was derived from the
    /// WHAT that was then superseded). It is never inferred from the mere existence of a later semantic revision.
    /// </summary>
    public GateInvalidation? InvalidationOf(Gate gate)
    {
        if (DecisionFor(gate.Id) is not { Action: GateAction.Approve })
            return null;

        foreach (var reclassification in Reclassifications)
        {
            if (!reclassification.InvalidatedGates.Contains(gate.Id))
                continue;

            var targeted = (gate.Type == GateType.What && reclassification.Stage == ReclassificationStage.What)
                           || (gate.Type == GateType.How && reclassification.Stage == ReclassificationStage.How);

            return targeted
                ? new GateInvalidation(gate, GateInvalidationCause.Reclassification, $"New evidence invalidated the {reclassification.StageName}: {reclassification.Reason}", reclassification.Id, null)
                : new GateInvalidation(
                    gate,
                    GateInvalidationCause.DependsOnInvalidatedWhat,
                    $"The HOW was derived from a WHAT that new evidence invalidated: {reclassification.Reason}",
                    reclassification.Id,
                    null);
        }

        if (gate.Type == GateType.How && SupersedingWhatOf(gate) is { } superseding)
        {
            return new GateInvalidation(
                gate,
                GateInvalidationCause.DependsOnSupersededWhat,
                $"The HOW was derived from a WHAT that a later approved WHAT (gate {superseding.Id}) superseded.",
                null,
                superseding.Id);
        }

        return null;
    }

    /// <summary>The first WHAT gate opened after <paramref name="how"/> that was approved, or null: the WHAT that makes the HOW's basis out of date.</summary>
    private Gate? SupersedingWhatOf(Gate how)
    {
        var position = Gates.IndexOf(how);

        for (var i = position + 1; i < Gates.Length; i++)
        {
            if (Gates[i].Type == GateType.What && DecisionFor(Gates[i].Id) is { Action: GateAction.Approve })
                return Gates[i];
        }

        return null;
    }

    /// <summary>True when a reclassification added or reopened the WHAT stage. Together with <see cref="WhatRequired"/> it can only grow: no event lowers either.</summary>
    public bool WhatEscalated => Reclassifications.Any(r => r.Stage == ReclassificationStage.What);

    /// <summary>True when a reclassification added or reopened the HOW stage. See <see cref="WhatEscalated"/>.</summary>
    public bool HowEscalated => Reclassifications.Any(r => r.Stage == ReclassificationStage.How);

    /// <summary>The most recent WHAT gate opened, whatever its status; null if the WHAT stage never opened.</summary>
    public Gate? LatestWhatGate => Gates.LastOrDefault(g => g.Type == GateType.What);

    /// <summary>True when the most recent WHAT gate was approved. A later request for changes, or a newer proposal, makes it false again until that one is approved.</summary>
    public bool WhatApproved => LatestWhatGate is { } gate && StatusOf(gate) == GateStatus.Approved;

    /// <summary>
    /// True when the WHAT stage asks nothing more before implementation: its latest proposal is approved, or it was neither required nor ever opened. A stage the
    /// agent opened (without the human requiring it) stays unsatisfied until its latest proposal is approved.
    /// </summary>
    public bool WhatSatisfied => WhatApproved || (!WhatRequired && !WhatEscalated && LatestWhatGate is null);

    /// <summary>The most recent HOW gate opened, whatever its status; null if the HOW stage never opened.</summary>
    public Gate? LatestHowGate => Gates.LastOrDefault(g => g.Type == GateType.How);

    /// <summary>True when the most recent HOW gate was approved. A later request for changes, or a newer proposal, makes it false again until that one is approved.</summary>
    public bool HowApproved => LatestHowGate is { } gate && StatusOf(gate) == GateStatus.Approved;

    /// <summary>The HOW stage's counterpart of <see cref="WhatSatisfied"/>.</summary>
    public bool HowSatisfied => HowApproved || (!HowRequired && !HowEscalated && LatestHowGate is null);

    /// <summary>
    /// Whether implementation may exist yet. False while any pre-execution stage is unsatisfied: the WHAT or the HOW was required and not approved, or was opened
    /// (by the agent or the human's requirement) and its latest proposal is not approved. This is the stop-before-implementation invariant: no implementation
    /// revision, and therefore no verification, gate or completion, can be recorded while it is false. The stages are independent: requiring one never
    /// implies the other.
    /// </summary>
    public bool MayImplement => WhatSatisfied && HowSatisfied;

    /// <summary>
    /// True when the current implementation revision exists, was produced against the current semantic revision, and its own latest verification passed.
    /// This is the evidence both completion paths rest on: a gate is opened over it, or (no acceptance required) the task completes over it.
    /// </summary>
    public bool HasCurrentPassingVerification
        => MayImplement
           && CurrentImplementationRevision is { } implementation
           && implementation.SemanticRevisionId == CurrentSemanticRevisionId
           && CurrentVerification is { Outcome: VerificationOutcome.Passed };

    /// <summary>True when REVIEW is required: the human required it at creation, or the working agent requested it. A requirement only ever grows.</summary>
    public bool IsReviewRequired => ReviewRequired || ReviewEscalated;

    /// <summary>True when the working agent requested REVIEW.</summary>
    public bool ReviewEscalated => !ReviewRequests.IsEmpty;

    /// <summary>
    /// True when <paramref name="review"/> still describes the task as it is now: it was made under the current semantic revision, of the current implementation
    /// revision, over the current verification run, which passed. Any change (a reclassification, a repair that changes the workspace, a new verification run even for
    /// an unchanged workspace) makes it historical. A stale review is never deleted and never satisfies anything.
    /// </summary>
    public bool IsReviewCurrent(Review review)
        => HasCurrentPassingVerification
           && review.SemanticRevisionId == CurrentSemanticRevisionId
           && review.ImplementationRevisionId == CurrentImplementationRevisionId
           && CurrentVerification is { } verification
           && verification.Id == review.VerificationRunId;

    /// <summary>The review that describes the task now (whatever its verdict), or null: at most one review is ever recorded over a verification run.</summary>
    public Review? CurrentReview => Reviews.LastOrDefault(IsReviewCurrent);

    /// <summary>True when a review that passed describes the task as it is now.</summary>
    public bool HasCurrentReviewPass => CurrentReview is { Verdict: ReviewVerdict.Pass };

    /// <summary>
    /// True when the REVIEW requirement asks nothing more: it is not required, or a current review passed. Completion and the ACCEPT gate need this in addition to
    /// <see cref="HasCurrentPassingVerification"/>; a review of an earlier state, a review that requires changes and the implementer's own review never satisfy it.
    /// </summary>
    public bool IsReviewSatisfied => !IsReviewRequired || HasCurrentReviewPass;

    /// <summary>The latest review recorded, whatever its state; null if none.</summary>
    public Review? LatestReview => Reviews.IsEmpty ? null : Reviews[^1];

    /// <summary>Where the task stands against REVIEW, from the latest review and whether it is still current. Distinguishes a current pass from a stale one.</summary>
    public ReviewStatus ReviewStatus
    {
        get
        {
            if (LatestReview is not { } latest)
                return ReviewStatus.None;

            var current = IsReviewCurrent(latest);

            return latest.Verdict == ReviewVerdict.Pass
                ? current ? ReviewStatus.PassedCurrent : ReviewStatus.PassedStale
                : current ? ReviewStatus.ChangesRequired : ReviewStatus.ChangesRequiredStale;
        }
    }

    /// <summary>
    /// True when a fresh independent reviewer is due now: REVIEW is required, the current implementation has a current passing verification, and no review has been
    /// recorded over that verification. Failing verification goes through the implementer first, and a review that required changes is answered by the implementer
    /// (a repair, then a new verification) before the next review is due.
    /// </summary>
    public bool IsReviewDue => IsReviewRequired && HasCurrentPassingVerification && CurrentReview is null;

    /// <summary>The reason a review request is not recorded now, or null when it may be: it needs a RUNNING task and something to add.</summary>
    public string? ReviewRequestRefusal()
    {
        if (WorkflowState != WorkflowState.Running)
            return $"the task is {WorkflowState}; a review is requested by the working agent on a RUNNING task";

        if (IsReviewRequired)
            return "an independent REVIEW is already required";

        return null;
    }

    /// <summary>True when formal PROVE is required: the human required it at creation, or the working agent added it by submitting a Proof Contract. A requirement only ever grows.</summary>
    public bool IsProveRequired => ProveRequired || ProveEscalated;

    /// <summary>True when the working agent added PROVE by submitting a Proof Contract to a run that did not force it.</summary>
    public bool ProveEscalated => !ProveRequired && !ProofContracts.IsEmpty;

    /// <summary>
    /// The Proof Contract that describes the task now, or null: the contract written for the current semantic revision. An implementation repair does not stale it; a
    /// reclassification (a new semantic revision) does, and the old contract is history. At most one contract exists per semantic revision.
    /// </summary>
    public ProofContract? CurrentProofContract => ProofContracts.LastOrDefault(c => c.SemanticRevisionId == CurrentSemanticRevisionId);

    /// <summary>
    /// True when <paramref name="proof"/> still describes the task as it is now: the current Proof Contract, under the current semantic revision, of the current
    /// implementation revision, over the current passing verification and, when REVIEW is required, over the current passing review it was given. Any change makes it
    /// historical. A stale proof is never deleted and never satisfies anything.
    /// </summary>
    public bool IsProofCurrent(Proof proof)
        => HasCurrentPassingVerification
           && proof.SemanticRevisionId == CurrentSemanticRevisionId
           && proof.ImplementationRevisionId == CurrentImplementationRevisionId
           && CurrentVerification is { } verification
           && verification.Id == proof.VerificationRunId
           && CurrentProofContract is { } contract
           && contract.Id == proof.ContractId
           && (IsReviewRequired
               ? CurrentReview is { Verdict: ReviewVerdict.Pass } review && proof.ReviewId == review.Id
               : proof.ReviewId is null);

    /// <summary>The proof that describes the task now (whatever its verdict), or null: at most one proof is ever recorded over a verification run.</summary>
    public Proof? CurrentProof => Proofs.LastOrDefault(IsProofCurrent);

    /// <summary>True when a proof that passed describes the task as it is now.</summary>
    public bool HasCurrentProofPass => CurrentProof is { Verdict: ProofVerdict.Pass };

    /// <summary>
    /// True when the PROVE requirement asks nothing more: it is not required, or a current proof passed. Completion and the ACCEPT gate need this in addition to verification
    /// and REVIEW; a proof of an earlier state, a failed or inconclusive proof and the implementer's own proof never satisfy it.
    /// </summary>
    public bool IsProveSatisfied => !IsProveRequired || HasCurrentProofPass;

    /// <summary>The latest proof recorded, whatever its state; null if none.</summary>
    public Proof? LatestProof => Proofs.IsEmpty ? null : Proofs[^1];

    /// <summary>Where the task stands against PROVE, from the latest proof and whether it is still current.</summary>
    public ProofStatus ProofStatus
    {
        get
        {
            if (LatestProof is not { } latest)
                return ProofStatus.None;

            var current = IsProofCurrent(latest);

            return latest.Verdict switch
            {
                ProofVerdict.Pass => current ? ProofStatus.PassedCurrent : ProofStatus.PassedStale,
                ProofVerdict.Fail => current ? ProofStatus.Failed : ProofStatus.NotPassedStale,
                _ => current ? ProofStatus.Inconclusive : ProofStatus.NotPassedStale,
            };
        }
    }

    /// <summary>
    /// True when a fresh independent prover is due now: PROVE is required, a Proof Contract exists for the current semantics, the current implementation has a current passing
    /// verification, REVIEW (when required) passed over it, and no proof has been recorded over exactly this state. A failed or inconclusive proof is answered by the implementer
    /// (a repair, then a new verification) before the next proof is due.
    /// </summary>
    public bool IsProveDue => IsProveRequired && CurrentProofContract is not null && HasCurrentPassingVerification && IsReviewSatisfied && CurrentProof is null;

    /// <summary>True when PROVE is required but no Proof Contract exists for the current semantic revision: the implementer owes one before the work is proof-ready.</summary>
    public bool IsProofContractMissing => IsProveRequired && CurrentProofContract is null;

    /// <summary>The reason a Proof Contract is not recorded now, or null when it may be: it needs a RUNNING task and no contract yet for the current semantic revision (a contract is immutable for its revision).</summary>
    public string? ProofContractRefusal()
    {
        if (WorkflowState != WorkflowState.Running)
            return $"the task is {WorkflowState}; a Proof Contract is submitted by the working agent on a RUNNING task";

        if (CurrentProofContract is not null)
            return "a Proof Contract already exists for the current semantic revision and is immutable (a replacement needs new semantics)";

        return null;
    }

    /// <summary>
    /// The reason <paramref name="report"/> cannot be recorded as the proof of the task as it is now, or null when it can: it must cover exactly the claims of the current
    /// Proof Contract, every evidence reference must resolve against the current verification, the current passing review or the prover's own observed commands
    /// (<paramref name="observedCommands"/> of them, numbered from 1), and every PASS claim must cite the evidence its contract requires. A runtime calls this to refuse a report at
    /// its own boundary; Core applies it again on replay.
    /// </summary>
    public string? ProofReportProblem(ProofReport report, int observedCommands)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (CurrentProofContract is not { } contract)
            return "there is no Proof Contract for the current semantic revision";

        var missing = contract.Claims.Where(c => report.Claims.All(r => r.ClaimId != c.Id)).Select(c => c.Id).ToList();
        if (missing.Count > 0)
            return $"the report states no result for claim(s) {string.Join(", ", missing)}";

        var unknown = report.Claims.Where(r => contract.Find(r.ClaimId) is null).Select(r => r.ClaimId).ToList();
        if (unknown.Count > 0)
            return $"the report states result(s) for claim(s) {string.Join(", ", unknown)}, which the Proof Contract does not contain";

        foreach (var result in report.Claims)
        {
            var claim = contract.Find(result.ClaimId)!;

            foreach (var reference in result.Evidence)
            {
                if (EvidenceRefProblem(reference, observedCommands) is { } problem)
                    return $"claim {claim.Id}: {problem}";
            }

            if (result.Verdict != ProofVerdict.Pass)
                continue;

            var observations = claim.Evidence.Count(e => e.Kind == ProofEvidenceKind.Observation);
            var commandRefs = result.Evidence.Count(e => e.Kind == ProofEvidenceRefKind.Command);

            foreach (var requirement in claim.Evidence)
            {
                var cited = requirement.Kind switch
                {
                    ProofEvidenceKind.Verification => result.Evidence.Any(e => e.Kind == ProofEvidenceRefKind.Verification && e.Value == requirement.Subject),
                    ProofEvidenceKind.Review => result.Evidence.Any(e => e.Kind == ProofEvidenceRefKind.Review),
                    _ => true,
                };

                if (!cited)
                    return $"claim {claim.Id} passes without citing the required evidence '{requirement}'";
            }

            if (commandRefs < observations)
                return $"claim {claim.Id} passes with {commandRefs} observed command reference(s) for {observations} required targeted observation(s)";
        }

        return null;
    }

    private string? EvidenceRefProblem(ProofEvidenceRef reference, int observedCommands)
    {
        switch (reference.Kind)
        {
            case ProofEvidenceRefKind.Verification:
                if (!HasCurrentPassingVerification || CurrentVerification is not { } verification)
                    return $"'{reference}' does not resolve: there is no current passing verification";

                return verification.Checks.Any(c => c.CheckId.Value == reference.Value)
                    ? null
                    : $"'{reference}' does not resolve: the current verification has no check '{reference.Value}'";

            case ProofEvidenceRefKind.Review:
                return CurrentReview is { Verdict: ReviewVerdict.Pass } review && review.Id.ToString() == reference.Value
                    ? null
                    : $"'{reference}' does not resolve to the current passing review";

            default:
                return reference.Ordinal <= observedCommands
                    ? null
                    : $"'{reference}' does not resolve: the prover's session observed {observedCommands} command(s)";
        }
    }

    /// <summary>
    /// True when <paramref name="gate"/> still describes the task as it is now. An ACCEPT gate: the current semantic revision, the current implementation
    /// revision, and that implementation's latest verification, which passed. A CLARIFY, WHAT or HOW gate: the current semantic revision (it binds nothing else, a WHAT
    /// or HOW gate also its immutable proposal). A gate for anything older is stale.
    /// </summary>
    public bool IsCurrent(Gate gate)
        => gate.SemanticRevisionId == CurrentSemanticRevisionId
           && InvalidationOf(gate) is null
           && (gate.Type is GateType.Clarify or GateType.What or GateType.How
               || (gate.ImplementationRevisionId == CurrentImplementationRevisionId
                   && CurrentVerification is { Outcome: VerificationOutcome.Passed } verification
                   && verification.Id == gate.VerificationRunId
                   && IsReviewSatisfied
                   && IsProveSatisfied));

    /// <summary>
    /// True when the latest implementation revision still serves the task as it now is: it exists and was produced against the <em>current</em> semantic revision.
    /// An implementation recorded under an earlier revision (a reclassification, a request for changes, a clarification) is historical: it stays in
    /// <see cref="ImplementationRevisions"/>, the workspace may still contain it, and it is simply not current.
    /// </summary>
    public bool IsImplementationCurrent
        => CurrentImplementationRevision is { } implementation && implementation.SemanticRevisionId == CurrentSemanticRevisionId;

    /// <summary>True when the latest implementation has a verification but is not current, so that verification (pass or fail) cannot satisfy the workflow: historical evidence only.</summary>
    public bool IsVerificationStale
        => CurrentVerification is not null && !IsImplementationCurrent;

    /// <summary>
    /// The reclassification the task is still rerouted by: the newest one, until an implementation is recorded after it. While it is active the targeted stage
    /// must be approved again before anything may be implemented, and its workspace is the baseline that may not change meanwhile.
    /// </summary>
    public Reclassification? ActiveReclassification
    {
        get
        {
            if (Reclassifications.IsEmpty)
                return null;

            var latest = Reclassifications[^1];

            if (CurrentImplementationRevision is { } implementation
                && OrdinalOf(implementation.SemanticRevisionId) > OrdinalOf(latest.SemanticRevisionId))
                return null;

            return latest;
        }
    }

    /// <summary>
    /// True when the workspace holds edits made under assumptions a reclassification has since invalidated and no implementation was recorded for them: provisional
    /// work, preserved, not approved and not an implementation revision.
    /// </summary>
    public bool HasProvisionalWork => ActiveReclassification is { ProvisionalWork: true };

    private int OrdinalOf(SemanticRevisionId id)
        => SemanticRevisions.First(r => r.Id == id).Ordinal;

    /// <summary>
    /// Whether new evidence may reclassify the task to <paramref name="stage"/> now, with the reason when it may not (null means it may). A reclassification needs
    /// something to reconsider or to add: the stage must currently be satisfied (an approval to invalidate, or a stage that was never required, to add), and the HOW
    /// cannot be reconsidered while the WHAT it rests on is itself unsatisfied. A stage that is already unsatisfied is already being worked; saying so again changes
    /// nothing and is refused rather than recorded.
    /// </summary>
    public string? ReclassificationRefusal(ReclassificationStage stage)
    {
        if (WorkflowState != WorkflowState.Running)
            return $"the task is {WorkflowState}; a reclassification is raised by the working agent on a RUNNING task";

        if (!WhatSatisfied)
            return "the WHAT stage is already unsatisfied; it is being worked and there is no approved assumption to invalidate";

        if (stage == ReclassificationStage.How && !HowSatisfied)
            return "the HOW stage is already unsatisfied; it is being worked and there is no approved assumption to invalidate";

        return null;
    }

    /// <summary>
    /// The approved gates a reclassification to <paramref name="stage"/> invalidates now, in the order they were opened: for a WHAT, the approved WHAT and the approved
    /// HOW derived from it; for a HOW, only the approved HOW. Empty when there is nothing approved to invalidate (an additive escalation).
    /// </summary>
    public ImmutableArray<GateId> GatesInvalidatedBy(ReclassificationStage stage)
    {
        var invalidated = new List<Gate>();

        if (stage == ReclassificationStage.What && WhatApproved && LatestWhatGate is { } what)
            invalidated.Add(what);

        if (HowApproved && LatestHowGate is { } how)
            invalidated.Add(how);

        return [.. invalidated.OrderBy(g => Gates.IndexOf(g)).Select(g => g.Id)];
    }

    /// <summary>Returns the explicit binding for <paramref name="role"/>; throws if none. Never substitutes.</summary>
    public RoleBinding GetBinding(RoleId role)
        => RoleBindings.TryGetValue(Guard.NotNull(role, nameof(role)), out var binding)
            ? binding
            : throw new UnboundRoleException(role);

    // Value equality across collections, so "replay yields the same state" is directly assertable.
    public bool Equals(WorkflowTask? other)
        => other is not null
           && Id == other.Id
           && Intent == other.Intent
           && WorkflowState == other.WorkflowState
           && StateReason == other.StateReason
           && CompletionBoundary == other.CompletionBoundary
           && Acceptance == other.Acceptance
           && PredatesAcceptanceRequirement == other.PredatesAcceptanceRequirement
           && WhatRequired == other.WhatRequired
           && HowRequired == other.HowRequired
           && ReviewRequired == other.ReviewRequired
           && ProveRequired == other.ProveRequired
           && CurrentSemanticRevisionId == other.CurrentSemanticRevisionId
           && CurrentImplementationRevisionId == other.CurrentImplementationRevisionId
           && CreatedAt == other.CreatedAt
           && LastSequence == other.LastSequence
           && LastEventAt == other.LastEventAt
           && Authority == other.Authority
           && SemanticRevisions.SequenceEqual(other.SemanticRevisions)
           && ImplementationRevisions.SequenceEqual(other.ImplementationRevisions)
           && PolicyDecisions.SequenceEqual(other.PolicyDecisions)
           && Verifications.SequenceEqual(other.Verifications)
           && Gates.SequenceEqual(other.Gates)
           && GateDecisions.SequenceEqual(other.GateDecisions)
           && Reclassifications.SequenceEqual(other.Reclassifications)
           && ReviewRequests.SequenceEqual(other.ReviewRequests)
           && Reviews.SequenceEqual(other.Reviews)
           && ProofContracts.SequenceEqual(other.ProofContracts)
           && Proofs.SequenceEqual(other.Proofs)
           && RoleBindings.Count == other.RoleBindings.Count
           && RoleBindings.All(pair => other.RoleBindings.TryGetValue(pair.Key, out var binding) && binding == pair.Value);

    public override int GetHashCode() => HashCode.Combine(Id, LastSequence);
}
