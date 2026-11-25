using System.Collections.ObjectModel;
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
/// A task's append-only event history together with the current state derived from it.
/// <para>
/// Every command builds an event and appends it through the same path used by <see cref="Replay"/>; if the event
/// violates an invariant the exception is thrown and neither history nor state changes. There is no way to
/// edit or remove an event. Callers pass timestamps explicitly (the kernel never reads the clock) and identities
/// come from the supplied <see cref="IIdSource"/>, so identical inputs yield an identical history.
/// </para>
/// <para>
/// Not thread-safe. Concurrency control is a persistence/application concern.
/// </para>
/// </summary>
public sealed class TaskStream
{
    private readonly IIdSource _ids;
    private readonly List<EventEnvelope> _events = [];
    private readonly ReadOnlyCollection<EventEnvelope> _eventsView;
    private readonly HashSet<EventId> _eventIds = [];
    private WorkflowTask _state;

    private TaskStream(IIdSource ids, EventEnvelope first)
    {
        _ids = Guard.NotNull(ids, nameof(ids));
        _eventsView = _events.AsReadOnly();
        _state = TaskProjection.Apply(null, first);
        _events.Add(first);
        _eventIds.Add(first.Id);
    }

    /// <summary>The state reconstructed from <see cref="Events"/>.</summary>
    public WorkflowTask State => _state;

    /// <summary>The full ordered history. Read-only; the only way to extend it is a command.</summary>
    public IReadOnlyList<EventEnvelope> Events => _eventsView;

    /// <summary>
    /// Creates a task in RUNNING with its initial semantic revision (ordinal 1). The initial authority envelope
    /// is empty and no roles are bound. <paramref name="acceptance"/> is recorded in the first event and fixes how the task can complete:
    /// controller-derived completion, or only through the human's ACCEPT gate. <paramref name="whatRequired"/> is recorded there too: the task then
    /// produces no implementation until a WHAT proposal is approved. <paramref name="howRequired"/> is the same for the implementation strategy, independently. <paramref name="reviewRequired"/> is the same for an independent REVIEW of the verified implementation before the task completes, independently of both. <paramref name="proveRequired"/> is the same for formal PROVE (a Proof Contract judged by an independent prover), independently of all three.
    /// </summary>
    public static TaskStream Create(
        TaskId taskId,
        string intent,
        CompletionBoundary completionBoundary,
        DateTimeOffset at,
        IIdSource ids,
        AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired,
        bool whatRequired = false,
        bool howRequired = false,
        bool reviewRequired = false,
        bool proveRequired = false)
    {
        Guard.NotNull(ids, nameof(ids));

        var revision = new SemanticRevision(
            new SemanticRevisionId(ids.Next()),
            taskId,
            ordinal: 1,
            parentId: null,
            SemanticChangeKind.Initial,
            "Initial task intent.",
            at);

        var first = new EventEnvelope(
            new EventId(ids.Next()),
            taskId,
            sequence: 1,
            at,
            new TaskCreated(intent, completionBoundary, revision, acceptance, WhatRequired: whatRequired, HowRequired: howRequired, ReviewRequired: reviewRequired, ProveRequired: proveRequired));

        return new TaskStream(ids, first);
    }

    /// <summary>
    /// Rebuilds a task from its complete ordered history, validating every event. Fails with
    /// <see cref="InvalidEventHistoryException"/> (or the more specific domain exception) on the first bad event.
    /// <paramref name="ids"/> is only used for events appended after the replay.
    /// </summary>
    public static TaskStream Replay(IEnumerable<EventEnvelope> history, IIdSource ids)
    {
        Guard.NotNull(history, nameof(history));

        TaskStream? stream = null;
        foreach (var envelope in history)
        {
            if (stream is null)
                stream = new TaskStream(ids, envelope);
            else
                stream.Append(envelope);
        }

        return stream ?? throw new InvalidEventHistoryException("A task history must contain at least one event.");
    }

    /// <summary>
    /// Records an explicit, reasoned workflow-state change. This is structural bookkeeping only: moving to
    /// <see cref="WorkflowState.Completed"/> here does NOT establish that completion was earned (proof, gates,
    /// findings); that eligibility is enforced by the application layer, not by this kernel.
    /// </summary>
    public void ChangeWorkflowState(WorkflowState to, string reason, DateTimeOffset at)
        => Commit(new WorkflowStateChanged(_state.WorkflowState, to, reason), at);

    /// <summary>Creates the next semantic revision as a child of the current one and makes it current.</summary>
    public SemanticRevision CreateSemanticRevision(SemanticChangeKind kind, string explanation, DateTimeOffset at)
    {
        var parent = _state.CurrentSemanticRevision;
        var revision = new SemanticRevision(
            new SemanticRevisionId(_ids.Next()),
            _state.Id,
            parent.Ordinal + 1,
            parent.Id,
            kind,
            explanation,
            at);

        Commit(new SemanticRevisionCreated(revision), at);
        return revision;
    }

    /// <summary>
    /// Records the next exact implementation state (child of the current one, if any) and makes it current.
    /// It is bound to the task's current semantic revision.
    /// </summary>
    public ImplementationRevision RecordImplementationRevision(ImplementationFingerprint fingerprint, DateTimeOffset at)
    {
        var revision = new ImplementationRevision(
            new ImplementationRevisionId(_ids.Next()),
            _state.Id,
            _state.CurrentSemanticRevisionId,
            fingerprint,
            _state.CurrentImplementationRevisionId,
            at);

        Commit(new ImplementationRevisionRecorded(revision), at);
        return revision;
    }

    /// <summary>
    /// Explicitly binds (or re-binds) <paramref name="role"/> to <paramref name="profile"/> under the current
    /// semantic revision. Fails if the profile lacks a required capability.
    /// </summary>
    public void BindRole(Role role, Profile profile, DateTimeOffset at, ExecutionConfiguration? execution = null)
        => Commit(new RoleBound(new RoleBinding(role, profile, _state.CurrentSemanticRevisionId, execution)), at);

    /// <summary>Replaces the authority envelope under the current semantic revision.</summary>
    public void ChangeAuthority(AuthorityEnvelope envelope, string reason, DateTimeOffset at)
        => Commit(new AuthorityEnvelopeChanged(_state.CurrentSemanticRevisionId, envelope, reason), at);

    /// <summary>
    /// Records the result of a policy rule. Pass the revisions the decision concerned, or null when not
    /// applicable; nothing is attached implicitly.
    /// </summary>
    public PolicyDecision RecordPolicyDecision(
        PolicyRuleId rule,
        PolicyEffect effect,
        PolicyTrigger trigger,
        string explanation,
        DateTimeOffset at,
        SemanticRevisionId? semanticRevisionId = null,
        ImplementationRevisionId? implementationRevisionId = null)
    {
        var decision = new PolicyDecision(
            _state.Id,
            rule,
            effect,
            trigger,
            explanation,
            at,
            semanticRevisionId,
            implementationRevisionId);

        Commit(new PolicyDecisionRecorded(decision), at);
        return decision;
    }

    /// <summary>
    /// Records a finished lightweight verification of <paramref name="implementationRevisionId"/>. The aggregate
    /// outcome is derived from <paramref name="checks"/> (Passed iff all passed); callers cannot state it. This is
    /// the only normal way to create a <see cref="VerificationRecorded"/> event. Evidence binds to the revision
    /// given, which must already exist; earlier runs stay in history and are simply no longer current once a
    /// newer implementation revision is recorded.
    /// </summary>
    public VerificationRun RecordVerification(
        ImplementationRevisionId implementationRevisionId,
        IEnumerable<VerificationCheckResult> checks,
        DateTimeOffset at)
    {
        Guard.NotNull(checks, nameof(checks));
        var results = checks.ToList();

        var run = new VerificationRun(
            new VerificationRunId(_ids.Next()),
            _state.Id,
            implementationRevisionId,
            VerificationRun.AggregateOf(results),
            results,
            at);

        Commit(new VerificationRecorded(run), at);
        return run;
    }

    /// <summary>
    /// Completes a task that does not require acceptance (<see cref="AcceptanceRequirement.NotRequired"/>) over its current implementation revision and that
    /// revision's passing verification. Replay (and therefore this call) refuses it for a task that is not RUNNING and when the current implementation
    /// is not verified; a task that requires the human's ACCEPT completes through its gate instead.
    /// </summary>
    public void CompleteVerified(DateTimeOffset at)
    {
        if (_state.Acceptance != AcceptanceRequirement.NotRequired)
            throw new InvalidWorkflowTransitionException($"Task {_state.Id} requires the human's ACCEPT; it completes only through its gate.");

        var verification = _state.CurrentVerification
            ?? throw new InvalidWorkflowTransitionException("There is no verification of the current implementation revision to complete over.");

        Commit(
            new WorkflowStateChanged(
                _state.WorkflowState,
                WorkflowState.Completed,
                $"Verified; acceptance is not required: implementation revision {verification.ImplementationRevisionId} passed verification {verification.Id}."),
            at);
    }

    /// <summary>
    /// Opens the ACCEPT gate over the task's current semantic revision, current implementation revision and that implementation's
    /// current verification, and moves the task to AWAITING_HUMAN. Fails with <see cref="InvalidGateException"/> when there is no
    /// verification to bind; replay rejects it when that verification did not pass, the task is not RUNNING or a gate is already open.
    /// </summary>
    public Gate OpenAcceptGate(DateTimeOffset at)
    {
        var verification = _state.CurrentVerification
            ?? throw new InvalidGateException("There is no verification of the current implementation revision to open a gate over.");

        var gate = new Gate(
            new GateId(_ids.Next()),
            _state.Id,
            GateType.Accept,
            _state.CurrentSemanticRevisionId,
            verification.ImplementationRevisionId,
            verification.Id,
            at);

        Commit(new GateOpened(gate), at);
        return gate;
    }

    /// <summary>
    /// Opens a CLARIFY gate under the current semantic revision carrying <paramref name="request"/>, and moves the task to AWAITING_HUMAN. The gate id is
    /// supplied by the caller so that state recorded before the gate opens (the pre-question workspace baseline) can be keyed by it. It records no
    /// implementation and no verification.
    /// </summary>
    public Gate OpenClarifyGate(GateId gateId, ClarificationRequest request, DateTimeOffset at)
    {
        var gate = new Gate(gateId, _state.Id, GateType.Clarify, _state.CurrentSemanticRevisionId, null, null, at, request);

        Commit(new GateOpened(gate), at);
        return gate;
    }

    /// <summary>
    /// Answers the CLARIFY gate and returns the task to RUNNING under a new semantic revision (<see cref="SemanticChangeKind.HumanClarification"/>), in one
    /// atomic step: the answer is never recorded without the revision that makes it part of the task's meaning.
    /// </summary>
    public GateDecision AnswerClarification(GateId gateId, string answer, DateTimeOffset at)
    {
        var decision = Decide(new GateDecision(gateId, GateAction.Answer, null, at, answer), at);
        CreateSemanticRevision(SemanticChangeKind.HumanClarification, $"The human answered the clarification at gate {gateId}.", at);

        return decision;
    }

    /// <summary>
    /// Opens a WHAT gate under the current semantic revision carrying <paramref name="proposal"/>, and moves the task to AWAITING_HUMAN. As for CLARIFY, the
    /// gate id is supplied by the caller so the pre-proposal workspace baseline can be keyed by it. It records no implementation and no verification.
    /// </summary>
    public Gate OpenWhatGate(GateId gateId, WhatProposal proposal, DateTimeOffset at)
    {
        var gate = new Gate(gateId, _state.Id, GateType.What, _state.CurrentSemanticRevisionId, null, null, at, what: proposal);

        Commit(new GateOpened(gate), at);
        return gate;
    }

    /// <summary>
    /// Approves the WHAT gate and returns the task to RUNNING under a new semantic revision (<see cref="SemanticChangeKind.HumanWhatApproval"/>) that records the
    /// approved WHAT, in one atomic step. It records no implementation, verifies nothing and completes nothing.
    /// </summary>
    public GateDecision ApproveWhat(GateId gateId, DateTimeOffset at)
    {
        var gate = RequireStage(gateId, GateType.What);
        var decision = Decide(new GateDecision(gateId, GateAction.Approve, null, at), at);
        CreateSemanticRevision(SemanticChangeKind.HumanWhatApproval, $"The human approved the WHAT at gate {gateId} (content {gate?.What?.ContentHash}).", at);

        return decision;
    }

    /// <summary>
    /// Opens a HOW gate under the current semantic revision carrying <paramref name="proposal"/>, and moves the task to AWAITING_HUMAN. As for WHAT, the gate
    /// id is supplied by the caller so the pre-proposal workspace baseline can be keyed by it. Replay refuses it while the WHAT stage is unsatisfied: a strategy
    /// is never proposed on top of an intent that is still to be approved. It records no implementation and no verification.
    /// </summary>
    public Gate OpenHowGate(GateId gateId, HowProposal proposal, DateTimeOffset at)
    {
        var gate = new Gate(gateId, _state.Id, GateType.How, _state.CurrentSemanticRevisionId, null, null, at, how: proposal);

        Commit(new GateOpened(gate), at);
        return gate;
    }

    /// <summary>
    /// Approves the HOW gate and returns the task to RUNNING under a new semantic revision (<see cref="SemanticChangeKind.HumanHowApproval"/>) that records the
    /// approved strategy, in one atomic step. It records no implementation, verifies nothing and completes nothing.
    /// </summary>
    public GateDecision ApproveHow(GateId gateId, DateTimeOffset at)
    {
        var gate = RequireStage(gateId, GateType.How);
        var decision = Decide(new GateDecision(gateId, GateAction.Approve, null, at), at);
        CreateSemanticRevision(SemanticChangeKind.HumanHowApproval, $"The human approved the HOW at gate {gateId} (content {gate?.How?.ContentHash}).", at);

        return decision;
    }

    /// <summary>
    /// Records that new evidence invalidated the assumptions of <paramref name="stage"/> and reroutes the task to it, in one atomic event that also creates the
    /// one semantic revision (<see cref="SemanticChangeKind.AgentReclassification"/>). The approved gates the evidence invalidates are computed here, never supplied:
    /// the approved stage itself and, for the WHAT, the approved HOW derived from it. A stage that was never required is added instead (an additive escalation), and
    /// either way the stage is unsatisfied afterwards, so nothing may be implemented until it is approved again. The task stays RUNNING. There is no counterpart that
    /// removes a requirement. <paramref name="id"/> is supplied by the caller so state recorded before the event (the workspace baseline) can be keyed by it.
    /// Fails with <see cref="InvalidReclassificationException"/> when there is nothing to reconsider (see <see cref="WorkflowTask.ReclassificationRefusal"/>).
    /// </summary>
    public Reclassification Reclassify(
        ReclassificationId id,
        ReclassificationStage stage,
        string evidence,
        string reason,
        int turn,
        string? session,
        ImplementationFingerprint workspace,
        bool provisionalWork,
        DateTimeOffset at)
    {
        var parent = _state.CurrentSemanticRevision;
        var revisionId = new SemanticRevisionId(_ids.Next());

        var reclassification = new Reclassification(
            id,
            stage,
            evidence,
            reason,
            turn,
            session,
            parent.Id,
            revisionId,
            workspace,
            provisionalWork,
            _state.GatesInvalidatedBy(stage));

        var revision = new SemanticRevision(
            revisionId,
            _state.Id,
            parent.Ordinal + 1,
            parent.Id,
            SemanticChangeKind.AgentReclassification,
            $"New evidence invalidated the {reclassification.StageName} (reclassification {id}).",
            at);

        Commit(new ReclassificationRecorded(reclassification, revision), at);
        return reclassification;
    }

    /// <summary>
    /// Records that the working agent asked for an independent REVIEW. Additive: it adds the requirement and nothing else (no semantic revision, no
    /// invalidation), and no counterpart removes it. <paramref name="id"/> is supplied by the caller. Fails with <see cref="InvalidReviewException"/> when REVIEW is
    /// already required or the task is not RUNNING (see <see cref="WorkflowTask.ReviewRequestRefusal"/>).
    /// </summary>
    public ReviewRequest RequestReview(ReviewRequestId id, string reason, int turn, string? session, DateTimeOffset at)
    {
        var request = new ReviewRequest(id, reason, turn, session, _state.CurrentSemanticRevisionId);

        Commit(new ReviewRequested(request), at);
        return request;
    }

    /// <summary>
    /// Records an independent review of the current verified implementation, bound to exactly the semantic revision, implementation revision and verification run
    /// it judged. The round, the report hash and the "current" bindings are computed here, never supplied: a review can only describe the state the task is in. Core
    /// refuses a review that is not required, that came from the implementer's conversation or an earlier reviewer's, or that duplicates one over the same verification.
    /// </summary>
    public Review RecordReview(
        ReviewId id,
        ReviewReport report,
        Roles.ProfileId reviewerProfile,
        string reviewerSession,
        string implementerSession,
        long reviewerMilliseconds,
        int reviewerCommandCount,
        IEnumerable<string> reviewerCommands,
        bool configuredCheckRerun,
        DateTimeOffset at)
    {
        var implementation = _state.CurrentImplementationRevisionId
                             ?? throw new InvalidReviewException("There is no implementation to review.");
        var verification = _state.CurrentVerification
                           ?? throw new InvalidReviewException("The current implementation has no verification to review over.");

        var review = new Review(
            id,
            _state.Reviews.Length + 1,
            report,
            report.ContentHash,
            reviewerProfile,
            reviewerSession,
            implementerSession,
            _state.CurrentSemanticRevisionId,
            implementation,
            verification.Id,
            at,
            reviewerMilliseconds,
            reviewerCommandCount,
            reviewerCommands,
            configuredCheckRerun);

        Commit(new ReviewRecorded(review), at);
        return review;
    }

    /// <summary>
    /// Records the working agent's Proof Contract, bound to the current semantic revision. On a task that did not force PROVE it also adds the requirement (additive: no
    /// semantic revision, no invalidation, and no counterpart removes it). <paramref name="id"/> is supplied by the caller. Fails with <see cref="InvalidProofException"/> when the
    /// task is not RUNNING, a decision is pending or the current semantic revision already has a contract (see <see cref="WorkflowTask.ProofContractRefusal"/>); the contract is
    /// immutable for its revision.
    /// </summary>
    public ProofContract RecordProofContract(ProofContractId id, string rationale, IEnumerable<ProofClaim> claims, int turn, string? session, DateTimeOffset at)
    {
        var contract = new ProofContract(id, _state.CurrentSemanticRevisionId, turn, session, rationale, claims);

        Commit(new ProofContractRecorded(contract), at);
        return contract;
    }

    /// <summary>
    /// Records a formal proof of the current verified (and, when required, reviewed) implementation over the current Proof Contract, bound to exactly the contract, semantic
    /// revision, implementation revision, verification run and review it judged. The round, the hashes and the "current" bindings are computed here, never supplied: a proof can
    /// only describe the state the task is in. Core refuses a proof that is not required, that came from the implementer's or a reviewer's conversation or an earlier prover's,
    /// that duplicates one over the same verification, or whose report does not resolve against the state.
    /// </summary>
    public Proof RecordProof(
        ProofId id,
        ProofReport report,
        Roles.ProfileId proverProfile,
        string proverSession,
        string implementerSession,
        long proverMilliseconds,
        int proverCommandCount,
        IEnumerable<string> proverCommands,
        bool configuredCheckRerun,
        long duplicatedVerificationMilliseconds,
        DateTimeOffset at)
    {
        var contract = _state.CurrentProofContract
                       ?? throw new InvalidProofException("There is no Proof Contract for the current semantic revision to prove.");
        var implementation = _state.CurrentImplementationRevisionId
                             ?? throw new InvalidProofException("There is no implementation to prove.");
        var verification = _state.CurrentVerification
                           ?? throw new InvalidProofException("The current implementation has no verification to prove over.");

        var proof = new Proof(
            id,
            _state.Proofs.Length + 1,
            contract.Id,
            contract.ContentHash,
            report,
            report.ContentHash,
            proverProfile,
            proverSession,
            implementerSession,
            _state.CurrentSemanticRevisionId,
            implementation,
            verification.Id,
            _state.IsReviewRequired ? _state.CurrentReview?.Id : null,
            at,
            proverMilliseconds,
            proverCommandCount,
            proverCommands,
            configuredCheckRerun,
            duplicatedVerificationMilliseconds);

        Commit(new ProofRecorded(proof), at);
        return proof;
    }

    /// <summary>
    /// The gate being approved, refused up front when it is a gate of another kind: an approval is a decision followed by a stage-specific revision, and a decision
    /// that was recorded for the wrong stage could not be taken back. An unknown id is left to <see cref="Decide"/>, which refuses it as before.
    /// </summary>
    private Gate? RequireStage(GateId gateId, GateType stage)
    {
        var gate = _state.Gates.FirstOrDefault(g => g.Id == gateId);

        if (gate is not null && gate.Type != stage)
            throw new InvalidGateException($"Gate {gateId} is a {gate.Type} gate, not a {stage} gate; it cannot be approved as one.");

        return gate;
    }

    /// <summary>Accepts the gate: the task becomes COMPLETED. Core enforces that it is the open gate and that it is still current.</summary>
    public GateDecision AcceptGate(GateId gateId, DateTimeOffset at)
        => Decide(new GateDecision(gateId, GateAction.Accept, null, at), at);

    /// <summary>
    /// Closes the gate without accepting it and returns the task to RUNNING under a new semantic revision
    /// (<see cref="SemanticChangeKind.HumanRequestChanges"/>), so the closed gate and everything verified under the old meaning are stale.
    /// </summary>
    public GateDecision RequestChanges(GateId gateId, string feedback, DateTimeOffset at)
    {
        var kind = _state.Gates.FirstOrDefault(g => g.Id == gateId)?.Type switch
        {
            GateType.What => SemanticChangeKind.HumanWhatChanges,
            GateType.How => SemanticChangeKind.HumanHowChanges,
            _ => SemanticChangeKind.HumanRequestChanges,
        };
        var decision = Decide(new GateDecision(gateId, GateAction.RequestChanges, feedback, at), at);
        CreateSemanticRevision(kind, $"The human requested changes at gate {gateId}.", at);

        return decision;
    }

    /// <summary>Closes the gate without accepting it and cancels the task.</summary>
    public GateDecision CancelGate(GateId gateId, DateTimeOffset at)
        => Decide(new GateDecision(gateId, GateAction.Cancel, null, at), at);

    private GateDecision Decide(GateDecision decision, DateTimeOffset at)
    {
        Commit(new GateDecided(decision), at);
        return decision;
    }

    private void Commit(DomainEvent payload, DateTimeOffset at)
        => Append(new EventEnvelope(new EventId(_ids.Next()), _state.Id, _state.LastSequence + 1, at, payload));

    private void Append(EventEnvelope envelope)
    {
        if (_eventIds.Contains(envelope.Id))
            throw new InvalidEventHistoryException($"Event id {envelope.Id} appears more than once.");

        var next = TaskProjection.Apply(_state, envelope);

        _state = next;
        _events.Add(envelope);
        _eventIds.Add(envelope.Id);
    }
}
