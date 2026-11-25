using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>
/// new evidence invalidates the assumptions of the earliest stage it affects. Core derives currentness (which approvals, which implementation, which
/// verification still serve the task) from the history alone, keeps everything that was recorded, reroutes to the earliest invalid stage, can only add rigor, and
/// refuses to replay a history that says otherwise.
/// </summary>
public sealed class ReclassificationTests
{
    private static TaskStream NewStream(bool what = false, bool how = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
        => TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), acceptance, what, how);

    private static WhatProposal What(string objective = "Retries on 429.")
        => new(objective, "A failed import is retried.", "The importer only.", null, null, "A 429 is retried.", "Two policies are plausible.", 1);

    private static HowProposal How(string strategy = "Introduce a retry policy object.")
        => new(strategy, "RetryPolicy decides; the importer asks it.", null, null, "One more class.", null, "Inline and policy are both plausible.", 1);

    private static Gate ApproveWhat(TaskStream stream, int at, string objective = "Retries on 429.")
    {
        var gate = stream.OpenWhatGate(new GateId(Guid.NewGuid()), What(objective), At(at));
        stream.ApproveWhat(gate.Id, At(at + 1));
        return gate;
    }

    private static Gate ApproveHow(TaskStream stream, int at, string strategy = "Introduce a retry policy object.")
    {
        var gate = stream.OpenHowGate(new GateId(Guid.NewGuid()), How(strategy), At(at));
        stream.ApproveHow(gate.Id, At(at + 1));
        return gate;
    }

    private static void Implement(TaskStream stream, int at, string fingerprint = "tree:1", VerificationOutcome outcome = VerificationOutcome.Passed)
    {
        stream.RecordImplementationRevision(new ImplementationFingerprint(fingerprint), At(at));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), outcome, "ok", 0)], At(at + 1));
    }

    private static Reclassification Reclassify(TaskStream stream, ReclassificationStage stage, int at, bool provisional = false, string workspace = "ws:1", string evidence = "The retry budget cannot be met by a policy object.")
        => stream.Reclassify(new ReclassificationId(Guid.NewGuid()), stage, evidence, "The approved assumption does not hold.", 3, "session-1", new ImplementationFingerprint(workspace), provisional, At(at));

    /// <summary>WHAT and HOW approved, implemented and verified: the state new evidence arrives in.</summary>
    private static (TaskStream Stream, Gate What, Gate How) ApprovedAndImplemented(bool what = true, bool how = true)
    {
        var stream = NewStream(what: what, how: how);
        var w = ApproveWhat(stream, 1);
        var h = ApproveHow(stream, 3);
        Implement(stream, 5);
        return (stream, w, h);
    }

    private static List<EventEnvelope> Forge(TaskStream stream, params DomainEvent[] more)
    {
        var events = stream.Events.ToList();

        foreach (var payload in more)
            events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, At(50 + events.Count), payload));

        return events;
    }

    // ---- HOW invalidation preserves the WHAT ---------------------------------------------------------------------------------------

    [Fact]
    public void A_HOW_reclassification_preserves_the_WHAT_and_invalidates_only_the_HOW()
    {
        var (stream, what, how) = ApprovedAndImplemented();
        var before = stream.State.CurrentSemanticRevision;
        var eventsBefore = stream.Events.Count;

        var reclassification = Reclassify(stream, ReclassificationStage.How, 8);

        var state = stream.State;
        Assert.Equal(eventsBefore + 1, stream.Events.Count);   // one atomic event
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(before.Ordinal + 1, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(SemanticChangeKind.AgentReclassification, state.CurrentSemanticRevision.Kind);
        Assert.Equal(before.Id, state.CurrentSemanticRevision.ParentId);
        Assert.Equal(before.Id, reclassification.PriorSemanticRevisionId);
        Assert.Equal(state.CurrentSemanticRevisionId, reclassification.SemanticRevisionId);
        Assert.Equal([how.Id], reclassification.InvalidatedGates.ToArray());

        Assert.True(state.WhatApproved);
        Assert.Equal(GateStatus.Approved, state.StatusOf(what));
        Assert.Null(state.InvalidationOf(what));
        Assert.Equal(GateStatus.Invalidated, state.StatusOf(how));
        Assert.False(state.HowSatisfied);
        Assert.False(state.MayImplement);
        Assert.Equal(ReclassificationStage.How, state.ActiveReclassification!.Stage);
    }

    // ---- WHAT invalidation is transitive ------------------------------------------------------------------------------------------

    [Fact]
    public void A_WHAT_reclassification_invalidates_the_approved_WHAT_and_the_HOW_derived_from_it()
    {
        var (stream, what, how) = ApprovedAndImplemented();

        var reclassification = Reclassify(stream, ReclassificationStage.What, 8);

        var state = stream.State;
        Assert.Equal([what.Id, how.Id], reclassification.InvalidatedGates.ToArray());
        Assert.Equal(GateStatus.Invalidated, state.StatusOf(what));
        Assert.Equal(GateStatus.Invalidated, state.StatusOf(how));
        Assert.Equal(GateInvalidationCause.Reclassification, state.InvalidationOf(what)!.Cause);
        Assert.Equal(GateInvalidationCause.DependsOnInvalidatedWhat, state.InvalidationOf(how)!.Cause);
        Assert.All(state.GateDecisions, d => Assert.Equal(GateAction.Approve, d.Action));   // the approvals are still there
        Assert.False(state.WhatSatisfied);
        Assert.False(state.HowSatisfied);
        Assert.False(state.MayImplement);
    }

    [Fact]
    public void A_WHAT_reclassification_with_no_HOW_does_not_invent_one()
    {
        var stream = NewStream(what: true);
        var what = ApproveWhat(stream, 1);
        Implement(stream, 3);

        var reclassification = Reclassify(stream, ReclassificationStage.What, 6);

        var state = stream.State;
        Assert.Equal([what.Id], reclassification.InvalidatedGates.ToArray());
        Assert.Null(state.LatestHowGate);
        Assert.False(state.HowRequired);
        Assert.True(state.HowSatisfied);   // never required, never opened
        Assert.False(state.WhatSatisfied);
    }

    [Fact]
    public void A_HOW_that_was_required_is_still_required_after_the_WHAT_is_revisited()
    {
        var (stream, _, how) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.What, 8);

        ApproveWhat(stream, 10, "Retries on 429 and 503.");

        var state = stream.State;
        Assert.True(state.WhatSatisfied);
        Assert.False(state.HowSatisfied);   // the HOW of the old WHAT is invalidated, not carried over
        Assert.Equal(how.Id, state.LatestHowGate!.Id);
        Assert.False(state.MayImplement);
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(12)));

        var newHow = ApproveHow(stream, 13, "Inline retry loop.");

        Assert.True(state.HowSatisfied is false);   // the earlier snapshot is immutable
        Assert.True(stream.State.HowSatisfied);
        Assert.True(stream.State.MayImplement);
        Assert.NotEqual(how.Id, newHow.Id);
        Assert.Equal(GateStatus.Invalidated, stream.State.StatusOf(how));
    }

    // ---- currentness -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_implementation_and_its_verification_become_historical_not_deleted()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        var implementation = stream.State.CurrentImplementationRevision!;
        var verification = stream.State.CurrentVerification!;
        Assert.True(stream.State.IsImplementationCurrent);
        Assert.False(stream.State.IsVerificationStale);

        Reclassify(stream, ReclassificationStage.How, 8);

        var state = stream.State;
        Assert.False(state.IsImplementationCurrent);
        Assert.True(state.IsVerificationStale);
        Assert.False(state.HasCurrentPassingVerification);
        Assert.Equal(implementation, state.ImplementationRevisions.Single());
        Assert.Equal(verification, Assert.Single(state.Verifications));
        Assert.Equal(VerificationOutcome.Passed, state.CurrentVerification!.Outcome);   // still a PASS, but of a stale implementation
        Assert.Equal(implementation.SemanticRevisionId, state.SemanticRevisions[^2].Id);
    }

    [Fact]
    public void Stale_verification_cannot_complete_the_run_by_any_path()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.How, 8);
        ApproveHow(stream, 10, "Inline retry loop.");   // every stage is satisfied again, but nothing was re-implemented

        Assert.True(stream.State.MayImplement);
        Assert.False(stream.State.HasCurrentPassingVerification);
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(12)));
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.ChangeWorkflowState(WorkflowState.Completed, "done", At(12)));

        var accepted = ApprovedAndImplemented(what: true, how: true).Stream;
        var ids = accepted.State;
        Assert.NotNull(ids);

        // an ACCEPT gate cannot be opened over the stale verification either
        var needsAccept = NewStream(what: true, how: true, acceptance: AcceptanceRequirement.HumanRequired);
        ApproveWhat(needsAccept, 1);
        ApproveHow(needsAccept, 3);
        Implement(needsAccept, 5);
        Reclassify(needsAccept, ReclassificationStage.How, 8);
        ApproveHow(needsAccept, 10, "Inline retry loop.");

        Assert.ThrowsAny<DomainException>(() => needsAccept.OpenAcceptGate(At(12)));

        // after a fresh implementation and verification, completion is possible again
        Implement(stream, 12, "tree:2");
        stream.CompleteVerified(At(14));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void An_invalidated_gate_is_never_current_and_never_approved()
    {
        var (stream, what, how) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.What, 8);

        var state = stream.State;
        Assert.False(state.IsCurrent(what));
        Assert.False(state.IsCurrent(how));
        Assert.False(state.WhatApproved);
        Assert.False(state.HowApproved);
        Assert.NotEqual(GateStatus.Approved, state.StatusOf(what));
        Assert.Equal(GateAction.Approve, state.DecisionFor(what.Id)!.Action);   // the approval is history, and visible as one
    }

    [Fact]
    public void History_stays_queryable_after_reclassification()
    {
        var (stream, what, how) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.What, 8, evidence: "Stakeholders also need 503.");

        var state = stream.State;
        Assert.Equal([what.Id, how.Id], state.Gates.Select(g => g.Id));
        Assert.Equal(2, state.GateDecisions.Count(d => d.Action == GateAction.Approve));
        Assert.Single(state.ImplementationRevisions);
        Assert.Single(state.Verifications);
        var reclassification = Assert.Single(state.Reclassifications);
        Assert.Equal("Stakeholders also need 503.", reclassification.Evidence);
        Assert.Equal(3, reclassification.Turn);
        Assert.Equal("session-1", reclassification.Session);
        Assert.Equal(4, state.SemanticRevisions.Length);   // initial, WHAT approval, HOW approval, reclassification
    }

    // ---- provisional work ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_mid_turn_reclassification_records_the_workspace_as_provisional_work_without_an_implementation()
    {
        var stream = NewStream(how: false);   // a direct path: nothing approved, nothing implemented yet

        var reclassification = Reclassify(stream, ReclassificationStage.How, 2, provisional: true, workspace: "ws:edited");

        var state = stream.State;
        Assert.True(state.HasProvisionalWork);
        Assert.Equal("ws:edited", state.ActiveReclassification!.Workspace.Value);
        Assert.Empty(state.ImplementationRevisions);   // the edits are not absorbed as an implementation
        Assert.Empty(reclassification.InvalidatedGates.ToArray());
    }

    [Fact]
    public void Provisional_work_stops_being_active_once_an_implementation_is_recorded_after_the_new_approvals()
    {
        var stream = NewStream();
        Reclassify(stream, ReclassificationStage.How, 2, provisional: true);
        ApproveHow(stream, 4);

        Assert.True(stream.State.HasProvisionalWork);   // approved, but nothing implemented yet

        Implement(stream, 6);

        Assert.False(stream.State.HasProvisionalWork);
        Assert.Null(stream.State.ActiveReclassification);
        Assert.Single(stream.State.Reclassifications);
    }

    // ---- additive escalation -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReclassificationStage.What)]
    [InlineData(ReclassificationStage.How)]
    public void A_direct_path_can_be_escalated_to_a_stage_it_never_required(ReclassificationStage stage)
    {
        var stream = NewStream();
        Implement(stream, 1);   // the direct path had nothing to wait for
        Assert.True(stream.State.MayImplement);

        Reclassify(stream, stage, 4);

        var state = stream.State;
        Assert.False(state.WhatRequired);
        Assert.False(state.HowRequired);
        Assert.False(state.MayImplement);   // persisted by the reclassification itself
        Assert.Equal(stage == ReclassificationStage.What, state.WhatEscalated);
        Assert.Equal(stage == ReclassificationStage.How, state.HowEscalated);
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(6)));

        if (stage == ReclassificationStage.What)
            ApproveWhat(stream, 7);
        else
            ApproveHow(stream, 7);

        Assert.True(stream.State.MayImplement);
    }

    [Fact]
    public void An_escalated_requirement_survives_replay_from_the_events_alone()
    {
        var stream = NewStream();
        Reclassify(stream, ReclassificationStage.How, 2);

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.False(replayed.HowSatisfied);
        Assert.True(replayed.HowEscalated);
        Assert.Equal(stream.State, replayed);
    }

    // ---- the agent cannot lower rigor ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_in_Core_lets_a_reclassification_lower_a_requirement()
    {
        var stream = NewStream(what: true, how: true);
        ApproveWhat(stream, 1);
        ApproveHow(stream, 3);
        Implement(stream, 5);
        Reclassify(stream, ReclassificationStage.How, 8);

        // the targeted stage is unsatisfied, and saying more about it (or about a stage above it) is refused, so it cannot be "reclassified away"
        Assert.Throws<InvalidReclassificationException>(() => Reclassify(stream, ReclassificationStage.How, 9));
        Assert.True(stream.State.WhatRequired);
        Assert.True(stream.State.HowRequired);
        Assert.True(stream.State.HowEscalated);

        // approving the HOW again is the only way it becomes satisfied; the requirements are untouched afterwards
        ApproveHow(stream, 10, "Inline retry loop.");
        Assert.True(stream.State.WhatRequired);
        Assert.True(stream.State.HowRequired);
        Assert.True(stream.State.HowEscalated);
    }

    [Fact]
    public void A_stage_that_is_already_unsatisfied_cannot_be_reclassified()
    {
        var forcedWhat = NewStream(what: true);   // WHAT required and never proposed
        Assert.Throws<InvalidReclassificationException>(() => Reclassify(forcedWhat, ReclassificationStage.What, 1));
        Assert.Throws<InvalidReclassificationException>(() => Reclassify(forcedWhat, ReclassificationStage.How, 1));

        var forcedHow = NewStream(how: true);
        Assert.Throws<InvalidReclassificationException>(() => Reclassify(forcedHow, ReclassificationStage.How, 1));
        Reclassify(forcedHow, ReclassificationStage.What, 2);   // the WHAT is satisfied (vacuously): that stage can be added
        Assert.Equal(2, forcedHow.Events.Count);
    }

    [Fact]
    public void A_reclassification_is_refused_while_a_gate_is_open_and_on_a_task_that_is_not_running()
    {
        var stream = NewStream();
        stream.OpenWhatGate(new GateId(Guid.NewGuid()), What(), At(1));
        Assert.ThrowsAny<DomainException>(() => Reclassify(stream, ReclassificationStage.How, 2));

        var blocked = NewStream();
        blocked.ChangeWorkflowState(WorkflowState.Blocked, "stuck", At(1));
        Assert.Throws<InvalidReclassificationException>(() => Reclassify(blocked, ReclassificationStage.How, 2));
    }

    [Fact]
    public void Bounded_text_is_enforced_and_nothing_is_recorded_on_refusal()
    {
        var stream = NewStream();
        var events = stream.Events.Count;

        Assert.Throws<ArgumentException>(() => Reclassify(stream, ReclassificationStage.How, 1, evidence: new string('x', Reclassification.MaxEvidenceLength + 1)));
        Assert.Throws<ArgumentException>(() => Reclassify(stream, ReclassificationStage.How, 1, evidence: " "));
        Assert.Throws<ArgumentException>(() => stream.Reclassify(new ReclassificationId(Guid.NewGuid()), ReclassificationStage.How, "e", new string('y', Reclassification.MaxReasonLength + 1), 1, null, new ImplementationFingerprint("w"), false, At(1)));
        Assert.Equal(events, stream.Events.Count);
    }

    // ---- WHAT -> HOW dependency through the explicit WHAT paths --------------------------------------------------------------------------

    [Fact]
    public void A_later_approved_WHAT_makes_an_earlier_approved_HOW_no_longer_current()
    {
        var stream = NewStream();
        var how = ApproveHow(stream, 1);   // the agent chose a HOW on its own, with no WHAT stage at all
        Assert.True(stream.State.HowApproved);

        var what = stream.OpenWhatGate(new GateId(Guid.NewGuid()), What("A revised statement of the intent."), At(3));
        Assert.True(stream.State.HowApproved);   // the superseding WHAT is only proposed so far
        Assert.False(stream.State.MayImplement);   // the WHAT stage is unsatisfied meanwhile

        stream.ApproveWhat(what.Id, At(4));

        var state = stream.State;
        Assert.False(state.HowApproved);
        Assert.False(state.HowSatisfied);
        Assert.Equal(GateStatus.Invalidated, state.StatusOf(how));
        var invalidation = state.InvalidationOf(how)!;
        Assert.Equal(GateInvalidationCause.DependsOnSupersededWhat, invalidation.Cause);
        Assert.Equal(what.Id, invalidation.SupersededBy);
        Assert.False(state.IsCurrent(how));
        Assert.False(state.MayImplement);
    }

    [Fact]
    public void Revising_the_WHAT_by_request_changes_keeps_the_approved_HOW_until_a_WHAT_is_approved_again()
    {
        var stream = NewStream();
        var how = ApproveHow(stream, 1);
        var what = stream.OpenWhatGate(new GateId(Guid.NewGuid()), What(), At(3));

        stream.RequestChanges(what.Id, "Say more about 503.", At(4));

        Assert.True(stream.State.HowApproved);   // nothing was approved, so nothing superseded it
        Assert.Equal(GateStatus.Approved, stream.State.StatusOf(how));
        Assert.False(stream.State.MayImplement);
    }

    [Fact]
    public void A_HOW_revision_never_invalidates_the_WHAT()
    {
        var stream = NewStream(what: true, how: true);
        var what = ApproveWhat(stream, 1);
        var how = stream.OpenHowGate(new GateId(Guid.NewGuid()), How(), At(3));
        stream.RequestChanges(how.Id, "Use a loop instead.", At(4));
        ApproveHow(stream, 5, "Inline retry loop.");

        Assert.True(stream.State.WhatApproved);
        Assert.Null(stream.State.InvalidationOf(what));
        Assert.True(stream.State.MayImplement);
    }

    [Fact]
    public void A_clarification_answer_is_a_semantic_revision_but_invalidates_no_approved_stage()
    {
        var stream = NewStream(what: true, how: true);
        var what = ApproveWhat(stream, 1);
        var how = ApproveHow(stream, 3);
        var question = stream.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which API?", "It matters.", 2), At(5));
        stream.AnswerClarification(question.Id, "The v2 API.", At(6));

        Assert.True(stream.State.WhatApproved);
        Assert.True(stream.State.HowApproved);
        Assert.Null(stream.State.InvalidationOf(what));
        Assert.Null(stream.State.InvalidationOf(how));
        Assert.True(stream.State.MayImplement);
    }

    // ---- replay refuses forged currentness --------------------------------------------------------------------------------------------

    private static Reclassification ForgedReclassification(TaskStream stream, ReclassificationStage stage, IEnumerable<GateId> invalidated, SemanticRevisionId revisionId, SemanticRevisionId? prior = null, ReclassificationId? id = null)
        => new(
            id ?? new ReclassificationId(Guid.NewGuid()),
            stage,
            "evidence",
            "reason",
            1,
            null,
            prior ?? stream.State.CurrentSemanticRevisionId,
            revisionId,
            new ImplementationFingerprint("ws"),
            false,
            invalidated);

    private static SemanticRevision ForgedRevision(TaskStream stream, int at, SemanticChangeKind? kind = null)
    {
        var parent = stream.State.CurrentSemanticRevision;
        return new SemanticRevision(new SemanticRevisionId(Guid.NewGuid()), TaskOne, parent.Ordinal + 1, parent.Id, kind ?? SemanticChangeKind.AgentReclassification, "forged", At(at));
    }

    [Fact]
    public void Replay_refuses_an_invalidation_of_a_gate_that_was_never_approved()
    {
        var stream = NewStream();
        var stranger = new GateId(Guid.NewGuid());
        var revision = ForgedRevision(stream, 50 + 2);
        var forged = Forge(stream, new ReclassificationRecorded(ForgedReclassification(stream, ReclassificationStage.How, [stranger], revision.Id), revision));
        forged[^1] = Retarget(forged[^1], at: revision.CreatedAt);

        Assert.Throws<InvalidReclassificationException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_reclassification_that_leaves_an_approved_dependent_HOW_current()
    {
        var (stream, what, _) = ApprovedAndImplemented();
        var revision = ForgedRevision(stream, 0);
        var events = stream.Events.ToList();
        var at = events[^1].Timestamp.AddMinutes(1);
        revision = new SemanticRevision(revision.Id, revision.TaskId, revision.Ordinal, revision.ParentId, revision.Kind, revision.Explanation, at);
        events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, at, new ReclassificationRecorded(ForgedReclassification(stream, ReclassificationStage.What, [what.Id], revision.Id), revision)));

        // the WHAT alone is named; the HOW derived from it would stay approved
        Assert.Throws<InvalidReclassificationException>(() => TaskStream.Replay(events, new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_reclassification_revision_outside_a_reclassification_event()
    {
        var stream = NewStream();
        var revision = ForgedRevision(stream, 0);
        var at = stream.Events[^1].Timestamp.AddMinutes(1);
        revision = new SemanticRevision(revision.Id, revision.TaskId, revision.Ordinal, revision.ParentId, revision.Kind, revision.Explanation, at);
        var events = stream.Events.ToList();
        events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, at, new SemanticRevisionCreated(revision)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(events, new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_reclassification_bound_to_the_wrong_revision_a_duplicate_id_and_a_pending_gate()
    {
        var stream = NewStream();
        Reclassify(stream, ReclassificationStage.How, 2);
        var first = stream.State.Reclassifications[0];

        // duplicate id (after the HOW is approved again, so the stage is reclassifiable)
        ApproveHow(stream, 4);
        var dupRevision = NewRevisionAfter(stream, 8);
        var dup = Forge(stream, new ReclassificationRecorded(ForgedReclassification(stream, ReclassificationStage.How, [stream.State.LatestHowGate!.Id], dupRevision.Id, id: first.Id), dupRevision));
        dup[^1] = Retarget(dup[^1], at: dupRevision.CreatedAt);
        Assert.Throws<InvalidReclassificationException>(() => TaskStream.Replay(dup, new GuidIdSource()));

        // wrong prior revision
        var wrongRevision = NewRevisionAfter(stream, 9);
        var wrong = Forge(stream, new ReclassificationRecorded(ForgedReclassification(stream, ReclassificationStage.How, [stream.State.LatestHowGate!.Id], wrongRevision.Id, prior: stream.State.SemanticRevisions[0].Id), wrongRevision));
        wrong[^1] = Retarget(wrong[^1], at: wrongRevision.CreatedAt);
        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(wrong, new GuidIdSource()));

        // while a gate is open
        var gated = NewStream();
        gated.OpenWhatGate(new GateId(Guid.NewGuid()), What(), At(1));
        var gateRevision = NewRevisionAfter(gated, 8);
        var pending = Forge(gated, new ReclassificationRecorded(ForgedReclassification(gated, ReclassificationStage.How, [], gateRevision.Id), gateRevision));
        pending[^1] = Retarget(pending[^1], at: gateRevision.CreatedAt);
        Assert.Throws<InvalidReclassificationException>(() => TaskStream.Replay(pending, new GuidIdSource()));
    }

    private static SemanticRevision NewRevisionAfter(TaskStream stream, int _)
    {
        var parent = stream.State.CurrentSemanticRevision;
        var at = stream.Events[^1].Timestamp.AddMinutes(1);
        return new SemanticRevision(new SemanticRevisionId(Guid.NewGuid()), TaskOne, parent.Ordinal + 1, parent.Id, SemanticChangeKind.AgentReclassification, "forged", at);
    }

    [Fact]
    public void Replay_refuses_an_implementation_recorded_while_the_rerouted_stage_is_unsatisfied()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.How, 8);
        var revision = stream.State.CurrentSemanticRevisionId;
        var at = stream.Events[^1].Timestamp.AddMinutes(1);
        var forgedImplementation = new ImplementationRevision(new ImplementationRevisionId(Guid.NewGuid()), TaskOne, revision, new ImplementationFingerprint("tree:2"), stream.State.CurrentImplementationRevisionId, at);
        var events = stream.Events.ToList();
        events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, at, new ImplementationRevisionRecorded(forgedImplementation)));

        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(events, new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_completion_over_stale_verification()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.How, 8);
        ApproveHow(stream, 10, "Inline retry loop.");
        var events = Forge(stream, new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "forged completion"));

        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(events, new GuidIdSource()));
    }

    [Fact]
    public void A_legacy_history_cannot_use_its_plain_completion_after_a_reclassification()
    {
        var stream = NewLegacyTask();
        Reclassify(stream, ReclassificationStage.How, 2);
        ApproveHow(stream, 4);
        var events = Forge(stream, new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "legacy completion"));

        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(events, new GuidIdSource()));
    }

    [Fact]
    public void Replaying_a_reclassified_history_yields_exactly_the_live_state()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.What, 8, provisional: true);
        ApproveWhat(stream, 10, "Retries on 429 and 503.");
        ApproveHow(stream, 12, "Inline retry loop.");
        Implement(stream, 14, "tree:2");

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.Equal(stream.State, replayed);
        Assert.True(replayed.HasCurrentPassingVerification);
        Assert.False(replayed.HasProvisionalWork);
    }

    [Fact]
    public void A_reclassification_of_a_task_created_without_acceptance_still_completes_only_over_fresh_verification()
    {
        var (stream, _, _) = ApprovedAndImplemented();
        Reclassify(stream, ReclassificationStage.How, 8);
        ApproveHow(stream, 10, "Inline retry loop.");
        Implement(stream, 12, "tree:2");

        stream.CompleteVerified(At(14));

        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }
}
