using System.Collections.Immutable;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Policy;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Core.Events;

/// <summary>
/// The single place where task invariants are enforced. Pure fold: <c>(state, event) -> state</c> or an
/// exception. Both live commands (<see cref="TaskStream"/>) and replay go through <see cref="Apply"/>, so a
/// history that can be produced is exactly a history that can be replayed.
/// </summary>
internal static class TaskProjection
{
    public static WorkflowTask Apply(WorkflowTask? current, EventEnvelope envelope)
        => current is null ? Start(envelope) : Continue(current, envelope);

    private static WorkflowTask Start(EventEnvelope envelope)
    {
        if (envelope.Sequence != 1)
            throw new InvalidEventHistoryException(
                $"A history must start at sequence 1 (first event has sequence {envelope.Sequence}).");

        if (envelope.Payload is not TaskCreated created)
            throw new InvalidEventHistoryException(
                $"The first event must be '{EventKinds.TaskCreated}' (was '{envelope.Kind}').");

        var revision = created.InitialSemanticRevision;
        RequireSameTask(envelope, revision.TaskId, "initial semantic revision");
        RequireStampedAtEnvelope(envelope, revision.CreatedAt, "initial semantic revision");

        if (revision.Ordinal != 1)
            throw new InvalidRevisionLineageException(
                $"A task must be created with semantic revision ordinal 1 (was {revision.Ordinal}).");

        // the legacy marker belongs to histories that predate the requirement, and every one of those was human-required
        if (created.PredatesAcceptanceRequirement && created.Acceptance != AcceptanceRequirement.HumanRequired)
            throw new InvalidEventHistoryException("Only a human-required task can predate the acceptance requirement.");

        return new WorkflowTask
        {
            Id = envelope.TaskId,
            Intent = Guard.NotBlank(created.Intent, nameof(created.Intent)),
            WorkflowState = WorkflowState.Running,
            StateReason = "Task created.",
            CompletionBoundary = Guard.NotNull(created.CompletionBoundary, nameof(created.CompletionBoundary)),
            Acceptance = Enum.IsDefined(created.Acceptance)
                ? created.Acceptance
                : throw new InvalidEventHistoryException($"Unknown acceptance requirement {(int)created.Acceptance}."),
            PredatesAcceptanceRequirement = created.PredatesAcceptanceRequirement,
            WhatRequired = created.WhatRequired,
            HowRequired = created.HowRequired,
            ReviewRequired = created.ReviewRequired,
            ProveRequired = created.ProveRequired,
            CurrentSemanticRevisionId = revision.Id,
            CreatedAt = envelope.Timestamp,
            SemanticRevisions = [revision],
            LastSequence = envelope.Sequence,
            LastEventAt = envelope.Timestamp,
        };
    }

    private static WorkflowTask Continue(WorkflowTask current, EventEnvelope envelope)
    {
        if (envelope.TaskId != current.Id)
            throw new InvalidEventHistoryException(
                $"Event {envelope.Id} belongs to task {envelope.TaskId}, not {current.Id}.");

        if (envelope.Sequence != current.LastSequence + 1)
            throw new InvalidEventHistoryException(
                $"Expected sequence {current.LastSequence + 1} but event {envelope.Id} has {envelope.Sequence}.");

        if (envelope.Timestamp < current.LastEventAt)
            throw new InvalidEventHistoryException(
                $"Event {envelope.Id} is stamped {envelope.Timestamp:O}, before the previous event ({current.LastEventAt:O}).");

        if (current.IsTerminal)
            throw new TaskTerminatedException(current.Id, current.WorkflowState);

        var next = envelope.Payload switch
        {
            TaskCreated => throw new InvalidEventHistoryException(
                $"'{EventKinds.TaskCreated}' is only valid as the first event."),
            WorkflowStateChanged e => ApplyStateChange(current, e),
            SemanticRevisionCreated e => ApplySemanticRevision(current, envelope, e.Revision),
            ImplementationRevisionRecorded e => ApplyImplementationRevision(current, envelope, e.Revision),
            RoleBound e => ApplyRoleBound(current, e),
            AuthorityEnvelopeChanged e => ApplyAuthorityChange(current, e),
            PolicyDecisionRecorded e => ApplyPolicyDecision(current, envelope, e.Decision),
            VerificationRecorded e => ApplyVerification(current, envelope, e.Run),
            GateOpened e => ApplyGateOpened(current, envelope, e.Gate),
            GateDecided e => ApplyGateDecided(current, envelope, e.Decision),
            ReclassificationRecorded e => ApplyReclassification(current, envelope, e.Reclassification, e.Revision),
            ReviewRequested e => ApplyReviewRequested(current, envelope, e.Request),
            ReviewRecorded e => ApplyReviewRecorded(current, envelope, e.Review),
            ProofContractRecorded e => ApplyProofContractRecorded(current, envelope, e.Contract),
            ProofRecorded e => ApplyProofRecorded(current, envelope, e.Proof),
            _ => throw new InvalidEventHistoryException($"Unknown event type '{envelope.Payload.GetType().Name}'."),
        };

        return next with { LastSequence = envelope.Sequence, LastEventAt = envelope.Timestamp };
    }

    private static WorkflowTask ApplyStateChange(WorkflowTask current, WorkflowStateChanged e)
    {
        if (!Enum.IsDefined(e.To))
            throw new InvalidWorkflowTransitionException($"Unknown workflow state {(int)e.To}.");

        if (e.From != current.WorkflowState)
            throw new InvalidEventHistoryException(
                $"Transition claims to leave {e.From} but the task is {current.WorkflowState}.");

        if (e.To == e.From)
            throw new InvalidWorkflowTransitionException($"Task is already {e.To}; a no-op transition is not recorded.");

        // A gate is a human decision: nothing but that decision may move the task while one is open.
        if (current.PendingGate is { } pending)
            throw new InvalidGateException(
                $"Gate {pending.Id} is open; the task leaves {current.WorkflowState} only by a decision on it, not by a plain change to {e.To}.");

        if (string.IsNullOrWhiteSpace(e.Reason))
            throw new InvalidWorkflowTransitionException(
                $"A reason is required for every transition ({e.From} -> {e.To}).");

        // A task that needs no acceptance is completed by the controller, and only over current passing verification: the same evidence a gate would
        // have bound. A task that requires acceptance completes only through its accepted gate (GateDecided), never by a plain state change; the one
        // exception is a history recorded before the requirement existed, which completed by a plain state change and must keep replaying unchanged.
        if (e.To == WorkflowState.Completed && current.Acceptance == AcceptanceRequirement.HumanRequired && (!current.PredatesAcceptanceRequirement || !current.Reclassifications.IsEmpty))
            throw new InvalidWorkflowTransitionException(
                "A task that requires acceptance completes only through an accepted gate, not by a plain state change.");

        if (e.To == WorkflowState.Completed && current.Acceptance == AcceptanceRequirement.NotRequired)
        {
            if (current.WorkflowState != WorkflowState.Running)
                throw new InvalidWorkflowTransitionException($"A task completes without acceptance only from RUNNING (the task is {current.WorkflowState}).");

            if (!current.HasCurrentPassingVerification)
                throw new InvalidWorkflowTransitionException(
                    "A task completes without acceptance only over a current implementation revision, produced against the current semantic revision, whose latest verification passed.");

            // a task that requires REVIEW completes only after a review of exactly this state passed. A stale pass, a review that required changes and a review
            // from the implementer's own conversation never count (the last cannot even be recorded).
            if (!current.IsReviewSatisfied)
                throw new InvalidWorkflowTransitionException(
                    "A task that requires an independent REVIEW completes only over a current review that passed: one of the current implementation revision, over its current verification, under the current semantic revision.");

            // a task that requires formal PROVE completes only after a proof of exactly this state, over the current Proof Contract, passed. A stale pass, a failed or
            // inconclusive proof and a missing contract never count.
            if (!current.IsProveSatisfied)
                throw new InvalidWorkflowTransitionException(
                    "A task that requires formal PROVE completes only over a current proof that passed: one of the current Proof Contract and implementation revision, over its current verification (and review), under the current semantic revision.");
        }

        return current with { WorkflowState = e.To, StateReason = e.Reason };
    }

    private static WorkflowTask ApplySemanticRevision(WorkflowTask current, EventEnvelope envelope, SemanticRevision revision)
    {
        var next = AppendRevision(current, envelope, revision);

        // A reclassification revision exists only as part of its reclassification_recorded event, which carries the evidence and the invalidations.
        if (revision.Kind == SemanticChangeKind.AgentReclassification)
            throw new InvalidRevisionLineageException(
                $"Semantic revision {revision.Id} of kind '{revision.Kind}' exists only inside a '{EventKinds.ReclassificationRecorded}' event.");

        RequireStageDecisionProvenance(current, revision);

        return next;
    }

    /// <summary>
    /// A reclassification is accepted only on a RUNNING task with something to reconsider, bound to the revision it descends from, creating exactly one
    /// semantic revision, and invalidating exactly the approved gates Core derives for it: a history that names other gates (or none where some are approved) does not
    /// replay. It changes no state but the revision lineage, the reclassification record and the status reason; the stage it targets becomes unsatisfied
    /// because its approval is now invalidated (or, for an escalation, because it is now required), never by editing a flag.
    /// </summary>
    private static WorkflowTask ApplyReclassification(WorkflowTask current, EventEnvelope envelope, Reclassification reclassification, SemanticRevision revision)
    {
        if (current.Reclassifications.Any(r => r.Id == reclassification.Id))
            throw new InvalidReclassificationException($"Reclassification id {reclassification.Id} is already used.");

        if (current.PendingGate is { } pending)
            throw new InvalidReclassificationException($"Gate {pending.Id} is open; a reclassification is raised by the working agent, not while a decision is pending.");

        if (current.ReclassificationRefusal(reclassification.Stage) is { } refusal)
            throw new InvalidReclassificationException($"Reclassification {reclassification.Id} refused: {refusal}.");

        if (reclassification.PriorSemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Reclassification {reclassification.Id} descends from semantic revision {reclassification.PriorSemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        if (reclassification.SemanticRevisionId != revision.Id)
            throw new InvalidRevisionLineageException($"Reclassification {reclassification.Id} names semantic revision {reclassification.SemanticRevisionId}, but its event carries {revision.Id}.");

        if (revision.Kind != SemanticChangeKind.AgentReclassification)
            throw new InvalidRevisionLineageException($"The semantic revision of a reclassification must be of kind '{SemanticChangeKind.AgentReclassification}' (was '{revision.Kind}').");

        var expected = current.GatesInvalidatedBy(reclassification.Stage);
        if (!reclassification.InvalidatedGates.SequenceEqual(expected))
            throw new InvalidReclassificationException(
                $"Reclassification {reclassification.Id} must invalidate exactly the approved gates that depend on the {reclassification.StageName} ({(expected.IsEmpty ? "none" : string.Join(", ", expected))}), "
                + $"but it names {(reclassification.InvalidatedGates.IsEmpty ? "none" : string.Join(", ", reclassification.InvalidatedGates))}.");

        var withRevision = AppendRevision(current, envelope, revision);

        return withRevision with
        {
            Reclassifications = current.Reclassifications.Add(reclassification),
            StateReason = $"Reclassified: new evidence invalidated the {reclassification.StageName}; it must be approved again before anything is implemented.",
        };
    }

    /// <summary>
    /// A review request is accepted only on a RUNNING task that does not already require REVIEW, with no decision pending, bound to the current semantic
    /// revision. It is additive: it creates no semantic revision and changes nothing but the requirement, so it never invalidates a gate, an implementation or a
    /// verification, and nothing ever removes it. It may be made at any point of the work, including before the WHAT or HOW is settled.
    /// </summary>
    private static WorkflowTask ApplyReviewRequested(WorkflowTask current, EventEnvelope envelope, ReviewRequest request)
    {
        if (current.ReviewRequests.Any(r => r.Id == request.Id))
            throw new InvalidReviewException($"Review request id {request.Id} is already used.");

        if (current.PendingGate is { } pending)
            throw new InvalidReviewException($"Gate {pending.Id} is open; a review is requested by the working agent, not while a decision is pending.");

        if (current.ReviewRequestRefusal() is { } refusal)
            throw new InvalidReviewException($"Review request {request.Id} refused: {refusal}.");

        if (request.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Review request {request.Id} was made under semantic revision {request.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        return current with
        {
            ReviewRequests = current.ReviewRequests.Add(request),
            StateReason = "An independent REVIEW is required: the agent asked for one; the verified implementation is reviewed before the task completes.",
        };
    }

    /// <summary>
    /// A review is accepted only on a RUNNING task that requires REVIEW, of exactly the implementation the task now has, over exactly its current passing
    /// verification, under the current semantic revision: a history claiming a review of another implementation, an earlier verification or an earlier meaning does not
    /// replay. It must come from a conversation of its own (never the implementer's, never an earlier reviewer's), from the profile the Reviewer role is explicitly bound
    /// to, be the next round and the only review over its verification. (The report hash is checked when the review is constructed.)
    /// </summary>
    private static WorkflowTask ApplyReviewRecorded(WorkflowTask current, EventEnvelope envelope, Review review)
    {
        if (current.Reviews.Any(r => r.Id == review.Id))
            throw new InvalidReviewException($"Review id {review.Id} is already used.");

        if (current.WorkflowState != WorkflowState.Running)
            throw new InvalidReviewException($"A review is recorded on a RUNNING task (the task is {current.WorkflowState}).");

        if (!current.IsReviewRequired)
            throw new InvalidReviewException($"Review {review.Id} refused: the task does not require an independent REVIEW.");

        if (review.Round != current.Reviews.Length + 1)
            throw new InvalidReviewException($"Review {review.Id} claims round {review.Round}, but the next review of the task is round {current.Reviews.Length + 1}.");

        if (review.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidReviewException(
                $"Review {review.Id} judged semantic revision {review.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}; it is stale.");

        if (current.CurrentImplementationRevisionId != review.ImplementationRevisionId)
            throw new InvalidReviewException(
                $"Review {review.Id} judged implementation revision {review.ImplementationRevisionId}, which is not the current one ({current.CurrentImplementationRevisionId?.ToString() ?? "none"}); it is stale.");

        if (!current.HasCurrentPassingVerification || current.CurrentVerification!.Id != review.VerificationRunId)
            throw new InvalidReviewException(
                $"Review {review.Id} is bound to verification run {review.VerificationRunId}, which is not the current passing verification of the current implementation revision.");

        if (current.CurrentReview is not null)
            throw new InvalidReviewException($"Review {review.Id} is a duplicate: verification run {review.VerificationRunId} was already reviewed.");

        if (string.Equals(review.ReviewerSession, review.ImplementerSession, StringComparison.Ordinal))
            throw new InvalidReviewException($"Review {review.Id} came from the implementer's own conversation; an independent review is a conversation of its own.");

        if (current.Reviews.Any(r => string.Equals(r.ReviewerSession, review.ReviewerSession, StringComparison.Ordinal)))
            throw new InvalidReviewException($"Review {review.Id} reuses the conversation of an earlier review; every review is a fresh conversation.");

        if (current.Proofs.Any(p => string.Equals(p.ProverSession, review.ReviewerSession, StringComparison.Ordinal)))
            throw new InvalidReviewException($"Review {review.Id} came from a prover's conversation; the reviewer is a different conversation.");

        if (!current.RoleBindings.TryGetValue(WellKnownRoles.Reviewer, out var binding) || binding.Profile.Id != review.ReviewerProfile)
            throw new InvalidReviewException(
                $"Review {review.Id} names reviewer profile '{review.ReviewerProfile}', but the Reviewer role is not explicitly bound to it.");

        RequireStampedAtEnvelope(envelope, review.RecordedAt, "review");

        return current with
        {
            Reviews = current.Reviews.Add(review),
            StateReason = review.Verdict == ReviewVerdict.Pass
                ? $"Independent review round {review.Round} passed implementation revision {review.ImplementationRevisionId}."
                : $"Independent review round {review.Round} requires changes ({review.Report.Findings.Length} finding(s)); the implementer addresses them before the next review.",
        };
    }

    /// <summary>
    /// A Proof Contract is accepted only on a RUNNING task with no decision pending, bound to the current semantic revision, and only when that revision has no contract
    /// yet: a contract is immutable for its revision, so a failed proof cannot be answered by weakening it (a replacement needs new semantics, normally through a
    /// reclassification). Recording one on a task that did not force PROVE adds the requirement (additive: no semantic revision, no invalidation, never removed). It may be
    /// recorded at any point of the work, including before the WHAT or HOW is settled (a settled stage is a new semantic revision and so makes it history).
    /// </summary>
    private static WorkflowTask ApplyProofContractRecorded(WorkflowTask current, EventEnvelope envelope, ProofContract contract)
    {
        if (current.ProofContracts.Any(c => c.Id == contract.Id))
            throw new InvalidProofException($"Proof Contract id {contract.Id} is already used.");

        if (current.PendingGate is { } pending)
            throw new InvalidProofException($"Gate {pending.Id} is open; a Proof Contract is submitted by the working agent, not while a decision is pending.");

        if (current.ProofContractRefusal() is { } refusal)
            throw new InvalidProofException($"Proof Contract {contract.Id} refused: {refusal}.");

        if (contract.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Proof Contract {contract.Id} was written under semantic revision {contract.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        return current with
        {
            ProofContracts = current.ProofContracts.Add(contract),
            StateReason = current.ProveRequired
                ? "The Proof Contract for the current semantics was recorded; formal PROVE evaluates it after verification (and REVIEW, when required)."
                : "Formal PROVE is required: the agent submitted a Proof Contract; an independent prover evaluates it after verification (and REVIEW, when required).",
        };
    }

    /// <summary>
    /// A formal proof is accepted only on a RUNNING task that requires PROVE, over the current Proof Contract, of exactly the implementation the task now has, over
    /// exactly its current passing verification and (when REVIEW is required) its current passing review, under the current semantic revision: a history claiming a proof of
    /// another contract, implementation, earlier verification, stale review or earlier meaning does not replay. It must come from a conversation of its own (never the
    /// implementer's, never a reviewer's, never an earlier prover's), from the profile the Prover role is explicitly bound to, be the next round and the only proof over its
    /// verification, and its report must resolve against the state it describes (every claim, every evidence reference, every PASS's required evidence). The report hash
    /// is checked when the proof is constructed.
    /// </summary>
    private static WorkflowTask ApplyProofRecorded(WorkflowTask current, EventEnvelope envelope, Proof proof)
    {
        if (current.Proofs.Any(p => p.Id == proof.Id))
            throw new InvalidProofException($"Proof id {proof.Id} is already used.");

        if (current.WorkflowState != WorkflowState.Running)
            throw new InvalidProofException($"A proof is recorded on a RUNNING task (the task is {current.WorkflowState}).");

        if (!current.IsProveRequired)
            throw new InvalidProofException($"Proof {proof.Id} refused: the task does not require formal PROVE.");

        if (proof.Round != current.Proofs.Length + 1)
            throw new InvalidProofException($"Proof {proof.Id} claims round {proof.Round}, but the next proof of the task is round {current.Proofs.Length + 1}.");

        if (current.CurrentProofContract is not { } contract)
            throw new InvalidProofException($"Proof {proof.Id} refused: there is no Proof Contract for the current semantic revision.");

        if (proof.ContractId != contract.Id || !string.Equals(proof.ContractHash, contract.ContentHash, StringComparison.Ordinal))
            throw new InvalidProofException(
                $"Proof {proof.Id} evaluated Proof Contract {proof.ContractId}, which is not the current contract ({contract.Id}); it is stale.");

        if (proof.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidProofException(
                $"Proof {proof.Id} judged semantic revision {proof.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}; it is stale.");

        if (current.CurrentImplementationRevisionId != proof.ImplementationRevisionId)
            throw new InvalidProofException(
                $"Proof {proof.Id} judged implementation revision {proof.ImplementationRevisionId}, which is not the current one ({current.CurrentImplementationRevisionId?.ToString() ?? "none"}); it is stale.");

        if (!current.HasCurrentPassingVerification || current.CurrentVerification!.Id != proof.VerificationRunId)
            throw new InvalidProofException(
                $"Proof {proof.Id} is bound to verification run {proof.VerificationRunId}, which is not the current passing verification of the current implementation revision.");

        if (!current.IsReviewSatisfied)
            throw new InvalidProofException($"Proof {proof.Id} refused: the task requires an independent REVIEW and no current review of this state passed, so the proof cannot be evaluated yet.");

        var expectedReview = current.IsReviewRequired ? current.CurrentReview?.Id : null;
        if (proof.ReviewId != expectedReview)
            throw new InvalidProofException(
                $"Proof {proof.Id} is bound to review {proof.ReviewId?.ToString() ?? "none"}, but the current review is {expectedReview?.ToString() ?? "none"}; it is stale.");

        if (current.CurrentProof is not null)
            throw new InvalidProofException($"Proof {proof.Id} is a duplicate: verification run {proof.VerificationRunId} was already proved.");

        if (string.Equals(proof.ProverSession, proof.ImplementerSession, StringComparison.Ordinal))
            throw new InvalidProofException($"Proof {proof.Id} came from the implementer's own conversation; an independent proof is a conversation of its own.");

        if (current.Reviews.Any(r => string.Equals(r.ReviewerSession, proof.ProverSession, StringComparison.Ordinal)))
            throw new InvalidProofException($"Proof {proof.Id} came from a reviewer's conversation; the prover is a different conversation.");

        if (current.Proofs.Any(p => string.Equals(p.ProverSession, proof.ProverSession, StringComparison.Ordinal)))
            throw new InvalidProofException($"Proof {proof.Id} reuses the conversation of an earlier proof; every proof is a fresh conversation.");

        if (!current.RoleBindings.TryGetValue(WellKnownRoles.Prover, out var binding) || binding.Profile.Id != proof.ProverProfile)
            throw new InvalidProofException(
                $"Proof {proof.Id} names prover profile '{proof.ProverProfile}', but the Prover role is not explicitly bound to it.");

        if (current.ProofReportProblem(proof.Report, proof.ProverCommands.Length) is { } problem)
            throw new InvalidProofException($"Proof {proof.Id} refused: {problem}.");

        RequireStampedAtEnvelope(envelope, proof.RecordedAt, "proof");

        return current with
        {
            Proofs = current.Proofs.Add(proof),
            StateReason = proof.Verdict == ProofVerdict.Pass
                ? $"Formal proof round {proof.Round} passed implementation revision {proof.ImplementationRevisionId}."
                : $"Formal proof round {proof.Round} did not establish every claim ({ProofReport.VerdictName(proof.Verdict)}); the implementer addresses it before the next proof.",
        };
    }

    private static WorkflowTask AppendRevision(WorkflowTask current, EventEnvelope envelope, SemanticRevision revision)
    {
        var parent = current.CurrentSemanticRevision;

        if (revision.TaskId != current.Id)
            throw new InvalidRevisionLineageException(
                $"Semantic revision {revision.Id} belongs to task {revision.TaskId}, not {current.Id}.");

        if (revision.ParentId != parent.Id)
            throw new InvalidRevisionLineageException(
                $"Semantic revision {revision.Id} must descend from the current revision {parent.Id} (parent was {revision.ParentId?.ToString() ?? "none"}).");

        if (revision.Ordinal != parent.Ordinal + 1)
            throw new InvalidRevisionLineageException(
                $"Semantic revision ordinal must be {parent.Ordinal + 1} (was {revision.Ordinal}).");

        if (current.SemanticRevisions.Any(r => r.Id == revision.Id))
            throw new InvalidRevisionLineageException($"Semantic revision id {revision.Id} is already used.");

        RequireStampedAtEnvelope(envelope, revision.CreatedAt, "semantic revision");

        return current with
        {
            CurrentSemanticRevisionId = revision.Id,
            SemanticRevisions = current.SemanticRevisions.Add(revision),
        };
    }

    private static WorkflowTask ApplyImplementationRevision(WorkflowTask current, EventEnvelope envelope, ImplementationRevision revision)
    {
        if (revision.TaskId != current.Id)
            throw new InvalidRevisionLineageException(
                $"Implementation revision {revision.Id} belongs to task {revision.TaskId}, not {current.Id}.");

        if (revision.ParentId != current.CurrentImplementationRevisionId)
            throw new InvalidRevisionLineageException(
                $"Implementation revision {revision.Id} must descend from the current revision "
                + $"({current.CurrentImplementationRevisionId?.ToString() ?? "none"}); parent was {revision.ParentId?.ToString() ?? "none"}.");

        if (current.ImplementationRevisions.Any(r => r.Id == revision.Id))
            throw new InvalidRevisionLineageException($"Implementation revision id {revision.Id} is already used.");

        // Semantic revisions in the state all belong to this task, so membership proves "same task" and
        // "already exists at this point in history" at once.
        if (current.SemanticRevisions.All(r => r.Id != revision.SemanticRevisionId))
            throw new InvalidRevisionReferenceException(
                $"Implementation revision {revision.Id} references unknown semantic revision {revision.SemanticRevisionId}.");

        RequireStampedAtEnvelope(envelope, revision.CreatedAt, "implementation revision");

        // Stop before implementation: while the WHAT or the HOW stage is unsatisfied (required and not approved, or opened and its latest proposal
        // not approved) no implementation exists, so nothing downstream (verification, an ACCEPT gate, completion) can be recorded either.
        if (!current.WhatSatisfied)
            throw new InvalidGateException(
                $"Implementation revision {revision.Id} cannot be recorded: the task's WHAT is {(current.LatestWhatGate is null ? "required but was never proposed" : "not approved")}.");

        if (!current.HowSatisfied)
            throw new InvalidGateException(
                $"Implementation revision {revision.Id} cannot be recorded: the task's HOW is {(current.LatestHowGate is null ? "required but was never proposed" : "not approved")}.");

        return current with
        {
            CurrentImplementationRevisionId = revision.Id,
            ImplementationRevisions = current.ImplementationRevisions.Add(revision),
        };
    }

    private static WorkflowTask ApplyRoleBound(WorkflowTask current, RoleBound e)
    {
        RequireCurrentSemanticRevision(current, e.Binding.BoundAt, $"binding of role '{e.Binding.Role.Id}'");

        return current with { RoleBindings = current.RoleBindings.SetItem(e.Binding.Role.Id, e.Binding) };
    }

    private static WorkflowTask ApplyAuthorityChange(WorkflowTask current, AuthorityEnvelopeChanged e)
    {
        RequireCurrentSemanticRevision(current, e.SemanticRevisionId, "authority change");
        Guard.NotBlank(e.Reason, nameof(e.Reason));

        return current with { Authority = Guard.NotNull(e.Envelope, nameof(e.Envelope)) };
    }

    private static WorkflowTask ApplyPolicyDecision(WorkflowTask current, EventEnvelope envelope, PolicyDecision decision)
    {
        if (decision.TaskId != current.Id)
            throw new InvalidEventHistoryException(
                $"Policy decision belongs to task {decision.TaskId}, not {current.Id}.");

        if (decision.SemanticRevisionId is { } semantic && current.SemanticRevisions.All(r => r.Id != semantic))
            throw new InvalidRevisionReferenceException($"Policy decision references unknown semantic revision {semantic}.");

        if (decision.ImplementationRevisionId is { } implementation
            && current.ImplementationRevisions.All(r => r.Id != implementation))
            throw new InvalidRevisionReferenceException(
                $"Policy decision references unknown implementation revision {implementation}.");

        RequireStampedAtEnvelope(envelope, decision.DecidedAt, "policy decision");

        return current with { PolicyDecisions = current.PolicyDecisions.Add(decision) };
    }

    private static WorkflowTask ApplyVerification(WorkflowTask current, EventEnvelope envelope, VerificationRun run)
    {
        if (run.TaskId != current.Id)
            throw new InvalidEventHistoryException($"Verification run {run.Id} belongs to task {run.TaskId}, not {current.Id}.");

        // Implementation revisions in the state all belong to this task, so membership proves both "exists at
        // this point in history" and "same task".
        var revision = current.ImplementationRevisions.FirstOrDefault(r => r.Id == run.ImplementationRevisionId)
            ?? throw new InvalidRevisionReferenceException(
                $"Verification run {run.Id} references unknown implementation revision {run.ImplementationRevisionId}.");

        // Evidence accepted from the runtime is only valid for the exact state it observed.
        foreach (var check in run.Checks)
        {
            if (check.Observation is { } observation && observation.StateFingerprint != revision.Fingerprint)
                throw new InvalidVerificationException(
                    $"Check '{check.CheckId}' was observed against a different implementation state than revision {revision.Id} records.");
        }

        if (current.Verifications.Any(v => v.Id == run.Id))
            throw new InvalidVerificationException($"Verification run id {run.Id} is already used.");

        RequireStampedAtEnvelope(envelope, run.Timestamp, "verification run");

        return current with { Verifications = current.Verifications.Add(run) };
    }

    private static WorkflowTask ApplyGateOpened(WorkflowTask current, EventEnvelope envelope, Gate gate)
    {
        if (gate.TaskId != current.Id)
            throw new InvalidGateException($"Gate {gate.Id} belongs to task {gate.TaskId}, not {current.Id}.");

        if (current.Gates.Any(g => g.Id == gate.Id))
            throw new InvalidGateException($"Gate id {gate.Id} is already used.");

        if (current.PendingGate is { } pending)
            throw new InvalidGateException($"Gate {pending.Id} is still open; there is only one current gate.");

        if (current.WorkflowState != WorkflowState.Running)
            throw new InvalidGateException($"A gate opens only on a RUNNING task (the task is {current.WorkflowState}).");

        if (gate.Type == GateType.Clarify)
            return ApplyClarifyGateOpened(current, envelope, gate);

        if (gate.Type == GateType.What)
            return ApplyWhatGateOpened(current, envelope, gate);

        if (gate.Type == GateType.How)
            return ApplyHowGateOpened(current, envelope, gate);

        // No acceptance required means no ACCEPT gate: one is never synthesized for such a task.
        if (current.Acceptance != AcceptanceRequirement.HumanRequired)
            throw new InvalidGateException($"Task {current.Id} does not require acceptance; it has no ACCEPT gate.");

        // The gate binds exactly the state that exists now, so it can only describe what was really verified.
        if (gate.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds semantic revision {gate.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        var implementation = current.CurrentImplementationRevision;
        if (implementation is null || implementation.Id != gate.ImplementationRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds implementation revision {gate.ImplementationRevisionId}, which is not the current one ({current.CurrentImplementationRevisionId?.ToString() ?? "none"}).");

        if (implementation.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} cannot bind implementation revision {implementation.Id}: it was produced against an earlier semantic revision.");

        var verification = current.LatestVerificationFor(implementation.Id);
        if (verification is null || verification.Id != gate.VerificationRunId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds verification run {gate.VerificationRunId}, which is not the current verification of implementation revision {implementation.Id}.");

        if (verification.Outcome != VerificationOutcome.Passed)
            throw new InvalidGateException($"Gate {gate.Id} cannot open over verification run {verification.Id}: it did not pass.");

        // the human is asked to accept only what the independent reviewer passed. A REVIEW pass is never the ACCEPT; it is the precondition for asking.
        if (!current.IsReviewSatisfied)
            throw new InvalidGateException(
                $"Gate {gate.Id} cannot open: the task requires an independent REVIEW and no current review of implementation revision {implementation.Id} passed.");

        // likewise for formal PROVE. A proof PASS is never the ACCEPT; it is the precondition for asking.
        if (!current.IsProveSatisfied)
            throw new InvalidGateException(
                $"Gate {gate.Id} cannot open: the task requires formal PROVE and no current proof of implementation revision {implementation.Id} passed.");

        RequireStampedAtEnvelope(envelope, gate.OpenedAt, "gate");

        return current with
        {
            WorkflowState = WorkflowState.AwaitingHuman,
            StateReason = $"{gate.Type.ToString().ToUpperInvariant()} gate {gate.Id} opened over implementation revision {gate.ImplementationRevisionId} (verification {gate.VerificationRunId} passed); awaiting the human decision.",
            Gates = current.Gates.Add(gate),
        };
    }

    /// <summary>
    /// A CLARIFY gate may open on any RUNNING task, whatever its acceptance policy (the agent's question is independent of how the work will be accepted), and
    /// binds only the semantic revision it was asked under. Nothing was verified, so no implementation or verification is bound or recorded.
    /// </summary>
    private static WorkflowTask ApplyClarifyGateOpened(WorkflowTask current, EventEnvelope envelope, Gate gate)
    {
        if (gate.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds semantic revision {gate.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        RequireStampedAtEnvelope(envelope, gate.OpenedAt, "gate");

        return current with
        {
            WorkflowState = WorkflowState.AwaitingHuman,
            StateReason = $"CLARIFY gate {gate.Id} opened under semantic revision {current.CurrentSemanticRevision.Ordinal}; awaiting the human's answer.",
            Gates = current.Gates.Add(gate),
        };
    }

    /// <summary>
    /// A WHAT gate may open on any RUNNING task (the agent may choose it, or the human required it) and binds only the semantic revision it was proposed under
    /// and its proposal. Opening one makes the WHAT stage unsatisfied until it is approved (see <c>WorkflowTask.MayImplement</c>).
    /// </summary>
    private static WorkflowTask ApplyWhatGateOpened(WorkflowTask current, EventEnvelope envelope, Gate gate)
    {
        if (gate.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds semantic revision {gate.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        RequireStampedAtEnvelope(envelope, gate.OpenedAt, "gate");

        return current with
        {
            WorkflowState = WorkflowState.AwaitingHuman,
            StateReason = $"WHAT gate {gate.Id} opened under semantic revision {current.CurrentSemanticRevision.Ordinal}; awaiting the human's decision on the proposal.",
            Gates = current.Gates.Add(gate),
        };
    }

    /// <summary>
    /// A HOW gate may open on any RUNNING task whose WHAT stage is satisfied: the strategy is proposed over an intent that is already settled (approved, or never
    /// subject to approval), never over one still awaiting its decision or its revision. It binds only the semantic revision it was proposed under and its
    /// proposal, so a proposal made before the WHAT was approved cannot become current afterwards. Opening one makes the HOW stage unsatisfied until it is approved.
    /// </summary>
    private static WorkflowTask ApplyHowGateOpened(WorkflowTask current, EventEnvelope envelope, Gate gate)
    {
        if (gate.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"Gate {gate.Id} binds semantic revision {gate.SemanticRevisionId}, but the current revision is {current.CurrentSemanticRevisionId}.");

        if (!current.WhatSatisfied)
            throw new InvalidGateException(
                $"Gate {gate.Id} cannot open: the HOW is proposed only once the WHAT stage is satisfied, and the task's WHAT is {(current.LatestWhatGate is null ? "required but was never proposed" : "not approved")}.");

        RequireStampedAtEnvelope(envelope, gate.OpenedAt, "gate");

        return current with
        {
            WorkflowState = WorkflowState.AwaitingHuman,
            StateReason = $"HOW gate {gate.Id} opened under semantic revision {current.CurrentSemanticRevision.Ordinal}; awaiting the human's decision on the strategy.",
            Gates = current.Gates.Add(gate),
        };
    }

    /// <summary>
    /// A semantic revision of kind WHAT/HOW approval or WHAT/HOW changes exists only as the consequence of exactly that decision on a gate of that stage, bound to
    /// the revision it descends from. A history that claims an approved stage without the human's decision, or twice for one decision, does not replay.
    /// </summary>
    private static void RequireStageDecisionProvenance(WorkflowTask current, SemanticRevision revision)
    {
        var required = StageDecisionOf(revision.Kind);

        if (required is not { } expected)
            return;

        var last = current.GateDecisions.IsEmpty ? null : current.GateDecisions[^1];
        var gate = last is null ? null : current.Gates.First(g => g.Id == last.GateId);

        if (last is null || gate is null || gate.Type != expected.Gate || last.Action != expected.Action || gate.SemanticRevisionId != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionLineageException(
                $"Semantic revision {revision.Id} of kind '{revision.Kind}' must follow the human's '{expected.Action}' decision on a {expected.Gate.ToString().ToUpperInvariant()} gate bound to the revision it descends from.");
    }

    /// <summary>The decision (and gate kind) a stage-decision semantic revision exists to record; null for every other kind.</summary>
    private static (GateType Gate, GateAction Action)? StageDecisionOf(SemanticChangeKind kind)
    {
        if (kind == SemanticChangeKind.HumanWhatApproval)
            return (GateType.What, GateAction.Approve);

        if (kind == SemanticChangeKind.HumanWhatChanges)
            return (GateType.What, GateAction.RequestChanges);

        if (kind == SemanticChangeKind.HumanHowApproval)
            return (GateType.How, GateAction.Approve);

        if (kind == SemanticChangeKind.HumanHowChanges)
            return (GateType.How, GateAction.RequestChanges);

        return null;
    }

    private static WorkflowTask ApplyGateDecided(WorkflowTask current, EventEnvelope envelope, GateDecision decision)
    {
        var gate = current.Gates.FirstOrDefault(g => g.Id == decision.GateId)
            ?? throw new InvalidGateException($"Gate {decision.GateId} is unknown to task {current.Id}.");

        if (current.DecisionFor(gate.Id) is { } earlier)
            throw new InvalidGateException($"Gate {gate.Id} was already decided ({earlier.Action}); a gate is decided once.");

        RequireStampedAtEnvelope(envelope, decision.DecidedAt, "gate decision");

        // A decision belongs to its gate's kind: an answer only closes a CLARIFY gate, an approval only a WHAT or HOW gate, accepting only an ACCEPT gate, and a
        // request for changes an ACCEPT, WHAT or HOW gate. Cancelling closes any.
        var applies = decision.Action switch
        {
            GateAction.Cancel => true,
            GateAction.Answer => gate.Type == GateType.Clarify,
            GateAction.Approve => gate.Type is GateType.What or GateType.How,
            GateAction.Accept => gate.Type == GateType.Accept,
            _ => gate.Type is GateType.Accept or GateType.What or GateType.How,
        };

        if (!applies)
            throw new InvalidGateException($"A '{decision.Action}' decision does not apply to a {gate.Type.ToString().ToUpperInvariant()} gate (gate {gate.Id}).");

        var decided = current with { GateDecisions = current.GateDecisions.Add(decision) };

        switch (decision.Action)
        {
            case GateAction.Accept:
                // Acceptance authorizes exactly the state the gate binds, and only while that is still the task's state.
                if (!current.IsCurrent(gate))
                    throw new StaleGateException(
                        $"Gate {gate.Id} no longer describes the task (its semantic revision, implementation revision or verification is not current); it cannot be accepted.");

                return decided with
                {
                    WorkflowState = WorkflowState.Completed,
                    StateReason = $"Accepted by the human at gate {gate.Id}: implementation revision {gate.ImplementationRevisionId} passed verification {gate.VerificationRunId}.",
                };

            case GateAction.Answer:
                // An answer is about the question as it was asked: only while the task still stands under the semantic revision the question was asked under.
                if (!current.IsCurrent(gate))
                    throw new StaleGateException($"Gate {gate.Id} was asked under an earlier semantic revision; it can no longer be answered.");

                return decided with
                {
                    WorkflowState = WorkflowState.Running,
                    StateReason = $"The human answered the clarification at gate {gate.Id}; the work continues.",
                };

            case GateAction.Approve:
                // An approval is of the proposal as it was made: only while the task still stands under the semantic revision the proposal was made under.
                if (!current.IsCurrent(gate))
                    throw new StaleGateException($"Gate {gate.Id} was proposed under an earlier semantic revision; it can no longer be approved.");

                return decided with
                {
                    WorkflowState = WorkflowState.Running,
                    StateReason = gate.Type == GateType.How
                        ? $"The human approved the HOW at gate {gate.Id}; the strategy is settled."
                        : $"The human approved the WHAT at gate {gate.Id}; the implementation may begin.",
                };

            case GateAction.RequestChanges:
                return decided with
                {
                    WorkflowState = WorkflowState.Running,
                    StateReason = gate.Type switch
                    {
                        GateType.What => $"The human requested changes to the WHAT at gate {gate.Id}; the proposal is revised before any implementation.",
                        GateType.How => $"The human requested changes to the HOW at gate {gate.Id}; the strategy is revised before any implementation.",
                        _ => $"The human requested changes at gate {gate.Id}; the work continues.",
                    },
                };

            default:
                return decided with
                {
                    WorkflowState = WorkflowState.Cancelled,
                    StateReason = $"Cancelled by the human at gate {gate.Id}.",
                };
        }
    }

    private static void RequireCurrentSemanticRevision(WorkflowTask current, SemanticRevisionId referenced, string what)
    {
        if (referenced != current.CurrentSemanticRevisionId)
            throw new InvalidRevisionReferenceException(
                $"The {what} was made under semantic revision {referenced}, but the current revision is {current.CurrentSemanticRevisionId}.");
    }

    private static void RequireSameTask(EventEnvelope envelope, TaskId actual, string what)
    {
        if (actual != envelope.TaskId)
            throw new InvalidEventHistoryException($"The {what} belongs to task {actual}, not {envelope.TaskId}.");
    }

    private static void RequireStampedAtEnvelope(EventEnvelope envelope, DateTimeOffset stamped, string what)
    {
        if (stamped != envelope.Timestamp)
            throw new InvalidEventHistoryException(
                $"The {what} is stamped {stamped:O} but its event is stamped {envelope.Timestamp:O}.");
    }
}
