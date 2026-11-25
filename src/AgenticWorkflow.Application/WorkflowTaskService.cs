using AgenticWorkflow.Application.Persistence;
using AgenticWorkflow.Core.Authority;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Policy;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Application.Direct;

namespace AgenticWorkflow.Application;

/// <summary>
/// The application's transaction boundary around one task: <c>load -> replay -> command -> append</c>.
/// <para>
/// Every mutation reloads the stored history, rebuilds state with <see cref="TaskStream.Replay"/> (never by
/// hand), runs one Core command on that stream, and appends exactly the events the stream produced, expecting
/// the version it loaded. Callers cannot supply events; the only events that reach the store are ones
/// <see cref="TaskStream"/> accepted.
/// </para>
/// <para>
/// Mutations take the <see cref="TaskSnapshot"/> the decision was based on. If the task has moved past that
/// version, or moves before the append, the call fails with <see cref="StaleWorkflowStateException"/>. The
/// command is never re-run against newer state: the caller must reload and re-decide. The service holds no
/// state and no transaction across calls; the version check is what makes long gaps between load and append safe.
/// </para>
/// </summary>
public sealed class WorkflowTaskService
{
    private readonly ITaskEventStore _store;
    private readonly IIdSource _ids;
    private readonly IClock _clock;

    public WorkflowTaskService(ITaskEventStore store, IIdSource ids, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _ids = ids;
        _clock = clock;
    }

    /// <summary>
    /// Creates a task through <see cref="TaskStream.Create"/> and appends it at expected version 0. Fails with
    /// <see cref="TaskAlreadyExistsException"/> if the id already has a history; there is no "create or reuse". <paramref name="acceptance"/> is part of the
    /// task's first event, so it is the task's own semantics from then on (it never follows a later configuration change). <paramref name="whatRequired"/> is part of it too: the human's
    /// per-run requirement that the WHAT be approved before anything is implemented; <paramref name="howRequired"/> is the same for the implementation strategy; <paramref name="reviewRequired"/> is the same for an independent REVIEW of the verified implementation; <paramref name="proveRequired"/> is the same for formal PROVE.
    /// </summary>
    public async Task<TaskChange> CreateAsync(
        TaskId taskId,
        string intent,
        CompletionBoundary completionBoundary,
        AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired,
        CancellationToken cancellationToken = default,
        bool whatRequired = false,
        bool howRequired = false,
        bool reviewRequired = false,
        bool proveRequired = false)
    {
        var stream = TaskStream.Create(taskId, intent, completionBoundary, _clock.UtcNow, _ids, acceptance, whatRequired, howRequired, reviewRequired, proveRequired);
        var events = stream.Events.ToList();

        try
        {
            await AppendCheckedAsync(taskId, expectedVersion: 0, events, cancellationToken);
        }
        catch (StreamVersionConflictException e)
        {
            throw new TaskAlreadyExistsException(taskId, e.ActualVersion, e);
        }

        return new TaskChange(new TaskSnapshot(stream.State), events);
    }

    /// <summary>The task's authoritative current state, or null if it has no history.</summary>
    public async Task<TaskSnapshot?> FindAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        var stream = await LoadStreamAsync(taskId, cancellationToken);
        return stream is null ? null : new TaskSnapshot(stream.State);
    }

    /// <summary>The task's authoritative current state; <see cref="TaskNotFoundException"/> if it has no history.</summary>
    public async Task<TaskSnapshot> LoadAsync(TaskId taskId, CancellationToken cancellationToken = default)
        => await FindAsync(taskId, cancellationToken) ?? throw new TaskNotFoundException(taskId);

    /// <summary>
    /// A reasoned structural change. It cannot complete a task: completion is either the controller's <see cref="CompleteVerifiedAsync"/> (no acceptance
    /// required) or the human's ACCEPT, <see cref="AcceptGateAsync"/>, so a caller cannot reach COMPLETED by naming it here.
    /// </summary>
    public Task<TaskChange> ChangeWorkflowStateAsync(
        TaskSnapshot basis,
        WorkflowState to,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (to == WorkflowState.Completed)
            throw new ArgumentException("A task completes only through CompleteVerifiedAsync (no acceptance required) or an accepted ACCEPT gate; it cannot be moved to COMPLETED directly.", nameof(to));

        return MutateAsync(basis, (stream, at) => stream.ChangeWorkflowState(to, reason, at), cancellationToken);
    }

    public Task<TaskChange> CreateSemanticRevisionAsync(
        TaskSnapshot basis,
        SemanticChangeKind kind,
        string explanation,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.CreateSemanticRevision(kind, explanation, at), cancellationToken);

    public Task<TaskChange> RecordImplementationRevisionAsync(
        TaskSnapshot basis,
        ImplementationFingerprint fingerprint,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.RecordImplementationRevision(fingerprint, at), cancellationToken);

    public Task<TaskChange> BindRoleAsync(
        TaskSnapshot basis,
        Role role,
        Profile profile,
        CancellationToken cancellationToken = default,
        ExecutionConfiguration? execution = null)
        => MutateAsync(basis, (stream, at) => stream.BindRole(role, profile, at, execution), cancellationToken);

    public Task<TaskChange> ChangeAuthorityEnvelopeAsync(
        TaskSnapshot basis,
        AuthorityEnvelope envelope,
        string reason,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.ChangeAuthority(envelope, reason, at), cancellationToken);

    public Task<TaskChange> RecordPolicyDecisionAsync(
        TaskSnapshot basis,
        PolicyRuleId rule,
        PolicyEffect effect,
        PolicyTrigger trigger,
        string explanation,
        SemanticRevisionId? semanticRevisionId = null,
        ImplementationRevisionId? implementationRevisionId = null,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) => stream.RecordPolicyDecision(
                rule, effect, trigger, explanation, at, semanticRevisionId, implementationRevisionId),
            cancellationToken);

    /// <summary>
    /// Records a lightweight verification of the <em>current</em> implementation revision of <paramref name="basis"/>.
    /// The caller cannot name a revision: the evidence always binds to the revision the decision was based on, so
    /// a stale implementation cannot be verified by accident. Fails with
    /// <see cref="NoCurrentImplementationException"/> when there is none, and with
    /// <see cref="StaleWorkflowStateException"/> if the task moved past <paramref name="basis"/>.
    /// </summary>
    public Task<TaskChange> RecordLightweightVerificationAsync(
        TaskSnapshot basis,
        IReadOnlyList<VerificationCheckResult> checks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(checks);

        var target = basis.Task.CurrentImplementationRevisionId
            ?? throw new NoCurrentImplementationException(basis.Task.Id);

        return MutateAsync(
            basis,
            (stream, at) =>
            {
                // basis.Version == the replayed version here, so this holds; it guards the invariant, not a race.
                if (stream.State.CurrentImplementationRevisionId != target)
                    throw new InvalidOperationException("The verification target is no longer the current implementation revision.");

                stream.RecordVerification(target, checks, at);
            },
            cancellationToken);
    }

    /// <summary>
    /// The completion of a task that does not require acceptance (<see cref="AcceptanceRequirement.NotRequired"/>). Reloads and replays the task, enforces the
    /// stale-basis rule, requires <see cref="DirectPathCompletion.Evaluate"/> to find it eligible (RUNNING, working-tree boundary, current implementation
    /// produced against the current semantic revision, that implementation's latest verification passed) and only then records COMPLETED. No gate is created. A task
    /// that requires the human's ACCEPT is refused (<see cref="AcceptanceRequiredException"/>): it can complete only through its gate. An agent's "done" is never an input.
    /// </summary>
    public Task<TaskChange> CompleteVerifiedAsync(TaskSnapshot basis, CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                if (stream.State.Acceptance != AcceptanceRequirement.NotRequired)
                    throw new AcceptanceRequiredException(stream.State.Id);

                var eligibility = DirectPathCompletion.Evaluate(stream.State);
                if (!eligibility.IsEligible)
                    throw new CompletionNotEligibleException(stream.State.Id, eligibility);

                stream.CompleteVerified(at);
            },
            cancellationToken);

    /// <summary>
    /// Opens the ACCEPT gate over the <em>current</em> implementation revision and its current verification of <paramref name="basis"/>, and
    /// moves the task to AWAITING_HUMAN. Only the controller calls this, after verification passed; the caller names no revision, so a
    /// stale implementation cannot be offered for acceptance. Core refuses it unless the verification passed, the task is RUNNING and no
    /// other gate is open (<see cref="DomainException"/>).
    /// </summary>
    public Task<TaskChange> OpenAcceptGateAsync(TaskSnapshot basis, CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.OpenAcceptGate(at), cancellationToken);

    /// <summary>
    /// The only way a direct-path task completes. Reloads and replays the task, enforces the stale-basis rule, requires
    /// <paramref name="gateId"/> to be the open ACCEPT gate and <see cref="DirectPathCompletion.Evaluate"/> to find the task eligible (current
    /// implementation, current passing verification, current gate), and only then records the human's acceptance, which completes the
    /// task. Otherwise it throws <see cref="CompletionNotEligibleException"/> (or a Core refusal) and writes nothing. An agent's "done" is
    /// never an input; neither is any text.
    /// </summary>
    public Task<TaskChange> AcceptGateAsync(
        TaskSnapshot basis,
        GateId gateId,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);

                var eligibility = DirectPathCompletion.Evaluate(stream.State);
                if (!eligibility.IsEligible)
                    throw new CompletionNotEligibleException(stream.State.Id, eligibility);

                stream.AcceptGate(gateId, at);
            },
            cancellationToken);

    /// <summary>
    /// The human's REQUEST CHANGES at <paramref name="gateId"/>: the gate is closed as not approved (it can never be accepted later), the task
    /// returns to RUNNING and a new semantic revision of kind <c>human_request_changes</c> is created. The feedback is bounded by Core and
    /// recorded in the same atomic append.
    /// </summary>
    public Task<TaskChange> RequestChangesAsync(
        TaskSnapshot basis,
        GateId gateId,
        string feedback,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);
                stream.RequestChanges(gateId, feedback, at);
            },
            cancellationToken);

    /// <summary>A fresh gate id, for a caller that must key state to a gate before the gate is opened (see <see cref="OpenClarifyGateAsync"/>).</summary>
    public GateId NewGateId() => new(_ids.Next());

    /// <summary>A fresh reclassification id, for a caller that must key state (the workspace baseline) to the reclassification before it is recorded (see <see cref="ReclassifyAsync"/>).</summary>
    public ReclassificationId NewReclassificationId() => new(_ids.Next());

    /// <summary>
    /// Records the agent's reclassification <paramref name="id"/> in one atomic append: the evidence, the earliest stage to reconsider and the one semantic revision
    /// it creates. Core derives which approved gates it invalidates and refuses it unless the task is RUNNING with no gate open and the stage can be reconsidered.
    /// The task stays RUNNING, now rerouted: the stage is unsatisfied until it is approved again, and the implementation and verification recorded so far are
    /// historical. The id is chosen by the caller so the workspace baseline can be keyed by it.
    /// </summary>
    public Task<TaskChange> ReclassifyAsync(
        TaskSnapshot basis,
        ReclassificationId id,
        ReclassificationStage stage,
        string evidence,
        string reason,
        int turn,
        string? session,
        ImplementationFingerprint workspace,
        bool provisionalWork,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.Reclassify(id, stage, evidence, reason, turn, session, workspace, provisionalWork, at), cancellationToken);

    /// <summary>A fresh review request id.</summary>
    public ReviewRequestId NewReviewRequestId() => new(_ids.Next());

    /// <summary>A fresh review id.</summary>
    public ReviewId NewReviewId() => new(_ids.Next());

    /// <summary>
    /// Records that the working agent asked for an independent REVIEW. Additive: it adds the requirement and nothing else (no semantic revision, no invalidation, no
    /// gate), and nothing removes it. Core refuses it unless the task is RUNNING, with no decision pending, and REVIEW is not already required.
    /// </summary>
    public Task<TaskChange> RequestReviewAsync(TaskSnapshot basis, string reason, int turn, string? session, CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.RequestReview(NewReviewRequestId(), reason, turn, session, at), cancellationToken);

    /// <summary>
    /// Records an independent review of the current verified implementation in one append. Core binds it to the current semantic revision, implementation revision and
    /// verification run, and refuses one that is stale, duplicated, from the implementer's own conversation or not from the explicitly bound Reviewer profile.
    /// </summary>
    public Task<TaskChange> RecordReviewAsync(
        TaskSnapshot basis,
        ReviewReport report,
        ProfileId reviewerProfile,
        string reviewerSession,
        string implementerSession,
        long reviewerMilliseconds,
        IReadOnlyList<string> reviewerCommands,
        int reviewerCommandCount,
        bool configuredCheckRerun,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) => stream.RecordReview(
                NewReviewId(),
                report,
                reviewerProfile,
                reviewerSession,
                implementerSession,
                reviewerMilliseconds,
                reviewerCommandCount,
                reviewerCommands,
                configuredCheckRerun,
                at),
            cancellationToken);

    /// <summary>A fresh Proof Contract id.</summary>
    public ProofContractId NewProofContractId() => new(_ids.Next());

    /// <summary>A fresh proof id.</summary>
    public ProofId NewProofId() => new(_ids.Next());

    /// <summary>
    /// Records the working agent's Proof Contract for the current semantic revision. It also adds the PROVE requirement to a run that did not force it (additive: no
    /// semantic revision, no invalidation, no gate) and nothing removes it. Core refuses it unless the task is RUNNING, with no decision pending, and the current semantic
    /// revision has no contract yet; a contract is immutable for its revision.
    /// </summary>
    public Task<TaskChange> RecordProofContractAsync(TaskSnapshot basis, string rationale, IEnumerable<ProofClaim> claims, int turn, string? session, CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.RecordProofContract(NewProofContractId(), rationale, claims, turn, session, at), cancellationToken);

    /// <summary>
    /// Records a formal proof of the current verified (and reviewed, when required) implementation over the current Proof Contract in one append. Core binds it to the
    /// contract, semantic revision, implementation revision, verification run and review, and refuses one that is stale, duplicated, from the implementer's or a reviewer's
    /// conversation, not from the explicitly bound Prover profile, or whose report does not resolve against the state.
    /// </summary>
    public Task<TaskChange> RecordProofAsync(
        TaskSnapshot basis,
        ProofReport report,
        ProfileId proverProfile,
        string proverSession,
        string implementerSession,
        long proverMilliseconds,
        IReadOnlyList<string> proverCommands,
        int proverCommandCount,
        bool configuredCheckRerun,
        long duplicatedVerificationMilliseconds,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) => stream.RecordProof(
                NewProofId(),
                report,
                proverProfile,
                proverSession,
                implementerSession,
                proverMilliseconds,
                proverCommandCount,
                proverCommands,
                configuredCheckRerun,
                duplicatedVerificationMilliseconds,
                at),
            cancellationToken);

    /// <summary>
    /// Opens the CLARIFY gate <paramref name="gateId"/> under the <em>current</em> semantic revision of <paramref name="basis"/> and moves the task to
    /// AWAITING_HUMAN. The agent's question is the only input; nothing is claimed about implementation or verification. Core refuses it unless the task is
    /// RUNNING with no other gate open. The id is chosen by the caller so state saved before the gate opens (the workspace baseline) can be keyed by it.
    /// </summary>
    public Task<TaskChange> OpenClarifyGateAsync(
        TaskSnapshot basis,
        GateId gateId,
        ClarificationRequest request,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.OpenClarifyGate(gateId, request, at), cancellationToken);

    /// <summary>
    /// The human's ANSWER to the CLARIFY gate <paramref name="gateId"/>: the gate is closed as answered, the task returns to RUNNING and a new semantic
    /// revision of kind <c>human_clarification</c> is created, in one atomic append. The answer is bounded by Core.
    /// </summary>
    public Task<TaskChange> AnswerClarificationAsync(
        TaskSnapshot basis,
        GateId gateId,
        string answer,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);
                stream.AnswerClarification(gateId, answer, at);
            },
            cancellationToken);

    /// <summary>
    /// Opens the WHAT gate <paramref name="gateId"/> under the <em>current</em> semantic revision of <paramref name="basis"/> carrying
    /// <paramref name="proposal"/>, and moves the task to AWAITING_HUMAN. Nothing is claimed about implementation or verification. Core refuses it unless the task is
    /// RUNNING with no other gate open. The id is chosen by the caller so the pre-proposal workspace baseline can be keyed by it.
    /// </summary>
    public Task<TaskChange> OpenWhatGateAsync(
        TaskSnapshot basis,
        GateId gateId,
        WhatProposal proposal,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.OpenWhatGate(gateId, proposal, at), cancellationToken);

    /// <summary>
    /// The human's APPROVAL of the WHAT gate <paramref name="gateId"/>: the gate is closed as approved, the task returns to RUNNING and a new semantic revision of
    /// kind <c>human_what_approval</c> is created, in one atomic append. It records no implementation, verifies nothing and completes nothing.
    /// </summary>
    public Task<TaskChange> ApproveWhatAsync(
        TaskSnapshot basis,
        GateId gateId,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);
                stream.ApproveWhat(gateId, at);
            },
            cancellationToken);

    /// <summary>
    /// Opens the HOW gate <paramref name="gateId"/> under the <em>current</em> semantic revision of <paramref name="basis"/> carrying
    /// <paramref name="proposal"/>, and moves the task to AWAITING_HUMAN. Core refuses it unless the task is RUNNING with no other gate open and its WHAT stage is
    /// satisfied. The id is chosen by the caller so the pre-proposal workspace baseline can be keyed by it.
    /// </summary>
    public Task<TaskChange> OpenHowGateAsync(
        TaskSnapshot basis,
        GateId gateId,
        HowProposal proposal,
        CancellationToken cancellationToken = default)
        => MutateAsync(basis, (stream, at) => stream.OpenHowGate(gateId, proposal, at), cancellationToken);

    /// <summary>
    /// The human's APPROVAL of the HOW gate <paramref name="gateId"/>: the gate is closed as approved, the task returns to RUNNING and a new semantic revision of
    /// kind <c>human_how_approval</c> is created, in one atomic append. It records no implementation, verifies nothing and completes nothing.
    /// </summary>
    public Task<TaskChange> ApproveHowAsync(
        TaskSnapshot basis,
        GateId gateId,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);
                stream.ApproveHow(gateId, at);
            },
            cancellationToken);

    /// <summary>Closes the open gate without accepting it and cancels the task (the human chose to stop).</summary>
    public Task<TaskChange> CancelGateAsync(
        TaskSnapshot basis,
        GateId gateId,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            basis,
            (stream, at) =>
            {
                RequirePendingGate(stream.State, gateId);
                stream.CancelGate(gateId, at);
            },
            cancellationToken);

    private static void RequirePendingGate(WorkflowTask task, GateId gateId)
    {
        if (task.PendingGate?.Id != gateId)
            throw new InvalidGateException($"Gate {gateId} is not the open gate of task {task.Id}.");
    }

    /// <summary>
    /// The single mutation primitive behind every public mutation. Internal so that no caller can run an
    /// arbitrary delegate against a stream; the public surface is the typed methods above.
    /// </summary>
    internal async Task<TaskChange> MutateAsync(
        TaskSnapshot basis,
        Action<TaskStream, DateTimeOffset> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(command);

        var taskId = basis.Task.Id;

        // load -> replay
        var stream = await LoadStreamAsync(taskId, cancellationToken) ?? throw new TaskNotFoundException(taskId);
        var loadedVersion = stream.Events.Count;

        // The decision was made from basis; if the task has moved, do not even run the command.
        if (loadedVersion != basis.Version)
            throw new StaleWorkflowStateException(taskId, basis.Version, loadedVersion);

        // command (Core invariants may reject it; then nothing is appended)
        command(stream, EffectiveTime(stream.State));

        // Only what TaskStream produced after the loaded history.
        var newEvents = stream.Events.Skip(loadedVersion).ToList();
        if (newEvents.Count == 0)
            return new TaskChange(basis, newEvents);

        // append, expecting exactly the version loaded
        try
        {
            await AppendCheckedAsync(taskId, loadedVersion, newEvents, cancellationToken);
        }
        catch (StreamVersionConflictException e)
        {
            throw new StaleWorkflowStateException(taskId, e.ExpectedVersion, e.ActualVersion, e);
        }

        // Reached only after the store confirmed the append; the in-memory stream is not trusted before this.
        return new TaskChange(new TaskSnapshot(stream.State), newEvents);
    }

    /// <summary>
    /// The timestamp for a mutation of a loaded task: the clock reading, but never earlier than the task's last
    /// event. A wall clock that moved backwards (another process, a clock adjustment) is not a reason to reject
    /// the mutation, yet history must never run backwards; an equal instant is valid and is not nudged.
    /// </summary>
    private DateTimeOffset EffectiveTime(WorkflowTask task)
    {
        var now = _clock.UtcNow;
        return now >= task.LastEventAt ? now : task.LastEventAt;
    }

    private async Task<TaskStream?> LoadStreamAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var history = await _store.LoadAsync(taskId, cancellationToken);
        if (history.Count == 0)
            return null;

        var stream = TaskStream.Replay(history, _ids);
        if (stream.State.Id != taskId)
            throw new InvalidOperationException(
                $"The store returned the history of task {stream.State.Id} when task {taskId} was requested.");

        return stream;
    }

    private async Task AppendCheckedAsync(
        TaskId taskId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken cancellationToken)
    {
        var newVersion = await _store.AppendAsync(taskId, expectedVersion, events, cancellationToken);
        var expectedNewVersion = expectedVersion + events.Count;

        if (newVersion != expectedNewVersion)
            throw new InvalidOperationException(
                $"Task {taskId}: the store reported version {newVersion} after an append that should end at {expectedNewVersion}.");
    }
}
