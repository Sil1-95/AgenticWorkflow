using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>
/// The M5B WHAT gate: a durable, bounded requirements proposal bound to the semantic revision only, approved or sent back once, replay-safe, and the
/// stop-before-implementation invariant that keeps a task from producing an implementation while its WHAT stage is unsatisfied.
/// </summary>
public sealed class WhatGateTests
{
    private static WhatProposal Proposal(string objective = "Retries on 429 and 503 with a bounded backoff.", int turn = 1)
        => new(
            objective,
            "A failed import is retried at most three times.",
            "The importer only.",
            "No new dependency.",
            "No change to the exporter.",
            "The importer's tests pass and a 429 is retried.",
            "Two retry policies are plausible and a person should choose.",
            turn);

    private static TaskStream Proposing(bool required = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), acceptance, required);
        stream.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal(), At(1));
        return stream;
    }

    private static List<EventEnvelope> Forge(TaskStream stream, params DomainEvent[] more)
    {
        var events = stream.Events.ToList();

        foreach (var payload in more)
            events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, At(20 + events.Count), payload));

        return events;
    }

    private static void RecordPassingImplementation(TaskStream stream, int at)
    {
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(at));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(at + 1));
    }

    // ---- 1-10: the gate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Opening_a_WHAT_gate_awaits_the_human_and_binds_only_the_semantic_revision_and_the_artifact()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, T0, new SequentialIdSource());
        var id = new GateId(Guid.NewGuid());

        var gate = stream.OpenWhatGate(id, Proposal(turn: 2), At(1));

        var state = stream.State;
        Assert.Equal(WorkflowState.AwaitingHuman, state.WorkflowState);
        Assert.Equal(gate, state.PendingGate);
        Assert.Equal(GateType.What, gate.Type);
        Assert.Equal(id, gate.Id);
        Assert.Equal(state.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Null(gate.ImplementationRevisionId);
        Assert.Null(gate.VerificationRunId);
        Assert.Null(gate.Clarification);
        Assert.Equal(2, gate.What!.Turn);
        Assert.True(state.IsCurrent(gate));
        Assert.Empty(state.ImplementationRevisions);
        Assert.Null(state.CurrentVerification);
    }

    [Fact]
    public void Only_one_gate_is_open_at_a_time_and_a_WHAT_cannot_open_over_another_gate()
    {
        var stream = Proposing();

        Assert.ThrowsAny<DomainException>(() => stream.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal("Another."), At(2)));
        Assert.Single(stream.State.Gates);

        var clarify = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        clarify.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which?", "Why.", 1), At(1));
        Assert.ThrowsAny<DomainException>(() => clarify.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal(), At(2)));
    }

    [Theory]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.AwaitingHuman)]
    public void A_WHAT_opens_only_on_a_running_task(WorkflowState state)
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.ChangeWorkflowState(state, "waiting", At(1));

        Assert.ThrowsAny<DomainException>(() => stream.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal(), At(2)));
    }

    [Fact]
    public void Approving_closes_the_gate_creates_exactly_one_approval_revision_and_resumes_in_one_step()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;
        var before = stream.State.CurrentSemanticRevision;
        var eventsBefore = stream.Events.Count;

        var decision = stream.ApproveWhat(gate.Id, At(2));

        var state = stream.State;
        Assert.Equal(GateAction.Approve, decision.Action);
        Assert.Equal(GateStatus.Approved, state.StatusOf(gate));
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(before.Ordinal + 1, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(SemanticChangeKind.HumanWhatApproval, state.CurrentSemanticRevision.Kind);
        Assert.Contains(gate.What!.ContentHash, state.CurrentSemanticRevision.Explanation);
        Assert.Equal(eventsBefore + 2, stream.Events.Count);   // the decision and the revision: nothing in between
        Assert.Null(state.PendingGate);

        // approval implements, verifies and completes nothing
        Assert.Empty(state.ImplementationRevisions);
        Assert.Null(state.CurrentVerification);
        Assert.False(state.IsTerminal);
        Assert.True(state.WhatApproved);
    }

    [Fact]
    public void Requesting_changes_closes_the_gate_creates_exactly_one_changes_revision_and_keeps_the_stage_unsatisfied()
    {
        var stream = Proposing(required: true);
        var gate = stream.State.PendingGate!;
        var eventsBefore = stream.Events.Count;

        var decision = stream.RequestChanges(gate.Id, "Cap retries at two.", At(2));

        var state = stream.State;
        Assert.Equal("Cap retries at two.", decision.Feedback);
        Assert.Equal(GateStatus.ChangesRequested, state.StatusOf(gate));
        Assert.Equal(SemanticChangeKind.HumanWhatChanges, state.CurrentSemanticRevision.Kind);
        Assert.Equal(2, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(eventsBefore + 2, stream.Events.Count);
        Assert.False(state.WhatApproved);
        Assert.False(state.MayImplement);   // unsatisfied until a later approval
    }

    [Fact]
    public void A_stale_WHAT_cannot_be_approved_because_the_meaning_moved_on()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;
        stream.CancelGate(gate.Id, At(2));

        var moved = Proposing();
        var movedGate = moved.State.PendingGate!;
        moved.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope moved", At(2));

        Assert.False(moved.State.IsCurrent(movedGate));
        Assert.ThrowsAny<DomainException>(() => moved.ApproveWhat(movedGate.Id, At(3)));
        Assert.Equal(2, moved.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void A_decision_is_made_once_and_a_second_approval_or_change_request_records_nothing()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;
        stream.ApproveWhat(gate.Id, At(2));
        var eventsAfter = stream.Events.Count;

        Assert.ThrowsAny<DomainException>(() => stream.ApproveWhat(gate.Id, At(3)));
        Assert.ThrowsAny<DomainException>(() => stream.RequestChanges(gate.Id, "no", At(3)));
        Assert.Equal(eventsAfter, stream.Events.Count);
        Assert.Equal(2, stream.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void Forged_histories_that_approve_without_the_revision_or_the_right_gate_fail_replay()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;

        // an approval of a gate that is not the open one
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Approve, null, At(30)))), new GuidIdSource()));

        // an approval with no approval revision after it: the decision alone must not satisfy the stage
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(gate.Id, GateAction.Approve, null, At(30)))), new GuidIdSource()));

        // an approval whose revision is of another kind
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(
                stream,
                new GateDecided(new GateDecision(gate.Id, GateAction.Approve, null, At(30))),
                new SemanticRevisionCreated(new SemanticRevision(new SemanticRevisionId(Guid.NewGuid()), TaskOne, 2, stream.State.CurrentSemanticRevisionId, SemanticChangeKind.RequirementChange, "sneaky", At(31)))),
            new GuidIdSource()));

        // an approval of an ACCEPT gate, and an ACCEPT of a WHAT gate
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(gate.Id, GateAction.Accept, null, At(30)))), new GuidIdSource()));

        // an approval recorded after the meaning already moved on
        var moved = Proposing();
        var movedGate = moved.State.PendingGate!;
        moved.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope moved", At(2));
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(moved, new GateDecided(new GateDecision(movedGate.Id, GateAction.Approve, null, At(30)))), new GuidIdSource()));
    }

    [Fact]
    public void ACCEPT_and_CLARIFY_gates_are_unchanged_and_cannot_be_approved()
    {
        var accept = NewTask();
        RecordPassingImplementation(accept, 1);
        var acceptGate = accept.OpenAcceptGate(At(3));

        Assert.ThrowsAny<DomainException>(() => accept.ApproveWhat(acceptGate.Id, At(4)));
        accept.AcceptGate(acceptGate.Id, At(5));
        Assert.Equal(GateStatus.Accepted, accept.State.StatusOf(acceptGate));
        Assert.Equal(WorkflowState.Completed, accept.State.WorkflowState);

        var clarify = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        var question = clarify.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which?", "Why.", 1), At(1));

        Assert.ThrowsAny<DomainException>(() => clarify.ApproveWhat(question.Id, At(2)));
        clarify.AnswerClarification(question.Id, "This one.", At(3));
        Assert.Equal(SemanticChangeKind.HumanClarification, clarify.State.CurrentSemanticRevision.Kind);

        // and a WHAT gate cannot be accepted or answered
        var what = Proposing();
        Assert.ThrowsAny<DomainException>(() => what.AcceptGate(what.State.PendingGate!.Id, At(2)));
        Assert.ThrowsAny<DomainException>(() => what.AnswerClarification(what.State.PendingGate!.Id, "x", At(2)));
        Assert.Equal(WorkflowState.AwaitingHuman, what.State.WorkflowState);
    }

    [Fact]
    public void The_artifact_is_hashable_immutable_bounded_and_replayable()
    {
        var one = Proposal();
        Assert.Equal(one.ContentHash, Proposal().ContentHash);                                 // deterministic
        Assert.NotEqual(one.ContentHash, Proposal("A different objective.").ContentHash);      // content-bound
        Assert.Equal(one.ContentHash, Proposal(turn: 5).ContentHash);                          // the turn is provenance, not content
        Assert.Equal(64, one.ContentHash.Length);

        // sections are named: moving text between sections changes the hash
        var swapped = new WhatProposal("B", "A", "S", null, null, "C", "R", 1);
        var straight = new WhatProposal("A", "B", "S", null, null, "C", "R", 1);
        Assert.NotEqual(straight.ContentHash, swapped.ContentHash);

        // bounds, no summarization: an oversized section, a control character or a total over the bound is refused, never cut
        var big = new string('x', WhatProposal.MaxSectionLength + 1);
        Assert.ThrowsAny<ArgumentException>(() => new WhatProposal(big, "b", "s", null, null, "c", "r", 1));
        Assert.ThrowsAny<ArgumentException>(() => new WhatProposal("o", "b", "s", null, null, "c", new string('r', WhatProposal.MaxRationaleLength + 1), 1));
        Assert.ThrowsAny<ArgumentException>(() => new WhatProposal("o\u0007", "b", "s", null, null, "c", "r", 1));
        Assert.ThrowsAny<ArgumentException>(() => new WhatProposal(" ", "b", "s", null, null, "c", "r", 1));
        var section = new string('y', WhatProposal.MaxSectionLength);
        Assert.ThrowsAny<ArgumentException>(() => new WhatProposal(section, section, section, section, section, section, "r", 1));

        // replay yields the same artifact
        var stream = Proposing();
        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;
        Assert.Equal(stream.State, replayed);
        Assert.Equal(one.ContentHash, replayed.Gates[0].What!.ContentHash);
        Assert.Equal("Retries on 429 and 503 with a bounded backoff.", replayed.Gates[0].What!.Objective);
    }

    [Fact]
    public void A_gate_shape_that_mixes_the_WHAT_with_an_implementation_or_a_question_is_invalid()
    {
        var id = new GateId(Guid.NewGuid());
        var semantic = NewTask().State.CurrentSemanticRevisionId;

        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.What, semantic, new ImplementationRevisionId(Guid.NewGuid()), null, At(1), what: Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.What, semantic, null, null, At(1)));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.What, semantic, null, null, At(1), new ClarificationRequest("q", "r", 1), Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Clarify, semantic, null, null, At(1), new ClarificationRequest("q", "r", 1), Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Accept, semantic, new ImplementationRevisionId(Guid.NewGuid()), new VerificationRunId(Guid.NewGuid()), At(1), what: Proposal()));
    }

    // ---- 11-15: the stage a person can require ---------------------------------------------------------------------------------------

    [Fact]
    public void A_required_stage_is_recorded_in_the_first_event_and_survives_replay_and_every_later_change()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true);

        Assert.True(((TaskCreated)stream.Events[0].Payload).WhatRequired);
        Assert.True(stream.State.WhatRequired);

        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "moved", At(1));
        stream.ChangeWorkflowState(WorkflowState.Blocked, "x", At(2));

        Assert.True(stream.State.WhatRequired);
        Assert.True(TaskStream.Replay(stream.Events, new GuidIdSource()).State.WhatRequired);
        Assert.False(NewTask().State.WhatRequired);
    }

    [Fact]
    public void A_required_stage_refuses_any_implementation_before_the_WHAT_is_approved()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true);

        Assert.False(stream.State.MayImplement);
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1)));

        // with a proposal pending, and after changes were requested: still refused
        var gateId = new GateId(Guid.NewGuid());
        stream.OpenWhatGate(gateId, Proposal(), At(2));
        stream.RequestChanges(gateId, "Narrower.", At(3));
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(4)));
        Assert.Empty(stream.State.ImplementationRevisions);
    }

    [Fact]
    public void A_required_stage_cannot_complete_until_a_WHAT_is_approved_and_then_completes_as_before()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true);

        Assert.ThrowsAny<DomainException>(() => stream.ChangeWorkflowState(WorkflowState.Completed, "controller completes", At(1)));

        var gateId = new GateId(Guid.NewGuid());
        stream.OpenWhatGate(gateId, Proposal(), At(2));
        stream.ApproveWhat(gateId, At(3));

        RecordPassingImplementation(stream, 4);
        stream.ChangeWorkflowState(WorkflowState.Completed, "controller completes", At(6));

        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
        Assert.Single(stream.State.ImplementationRevisions);
    }

    [Fact]
    public void A_stage_the_agent_chose_binds_the_same_way_as_a_required_one()
    {
        var stream = Proposing(required: false);

        Assert.False(stream.State.WhatRequired);
        Assert.False(stream.State.MayImplement);   // a proposal is open: nothing may be implemented until it is approved
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2)));

        stream.ApproveWhat(stream.State.PendingGate!.Id, At(3));
        Assert.True(stream.State.MayImplement);

        // a task the stage never touched pays nothing
        Assert.True(NewTask().State.MayImplement);
    }

    [Fact]
    public void Feedback_history_is_kept_in_order_across_rounds_and_each_proposal_stays_historical()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true);
        var first = new GateId(Guid.NewGuid());
        var second = new GateId(Guid.NewGuid());

        stream.OpenWhatGate(first, Proposal("v1"), At(1));
        stream.RequestChanges(first, "First feedback.", At(2));
        stream.OpenWhatGate(second, Proposal("v2", turn: 2), At(3));

        var state = stream.State;
        Assert.False(state.IsCurrent(state.Gates[0]));          // v1 is history: bound to an earlier revision
        Assert.True(state.IsCurrent(state.Gates[1]));           // v2 is the current proposal
        Assert.Equal(second, state.LatestWhatGate!.Id);
        Assert.Equal("First feedback.", state.DecisionFor(first)!.Feedback);

        stream.ApproveWhat(second, At(4));

        var kinds = stream.State.SemanticRevisions.Select(r => r.Kind).ToArray();
        Assert.Equal([SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatChanges, SemanticChangeKind.HumanWhatApproval], kinds);
        Assert.Equal("v1", stream.State.Gates[0].What!.Objective);
        Assert.Equal("v2", stream.State.Gates[1].What!.Objective);
    }
}
