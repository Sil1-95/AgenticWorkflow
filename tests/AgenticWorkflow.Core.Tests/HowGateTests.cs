using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>
/// The M5C HOW gate: a durable, bounded implementation-strategy proposal bound to the semantic revision only, approved or sent back once, replay-safe, ordered
/// after the WHAT when both are required, and the stop-before-implementation invariant that keeps a task from producing an implementation while a required HOW is
/// unsatisfied.
/// </summary>
public sealed class HowGateTests
{
    private static HowProposal Proposal(string strategy = "Introduce a retry policy object and inject it into the importer.", int turn = 1)
        => new(
            strategy,
            "RetryPolicy decides; the importer asks it.",
            "The importer calls the policy after each failure.",
            "No persistence change.",
            "A policy object costs one class but keeps the importer testable.",
            "A fake clock drives the policy in unit tests.",
            "A retry placed inline and one behind a policy are both plausible.",
            turn);

    private static TaskStream NewStream(bool what = false, bool how = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
        => TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), acceptance, what, how);

    private static TaskStream Proposing(bool required = false)
    {
        var stream = NewStream(how: required);
        stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(1));
        return stream;
    }

    private static WhatProposal What()
        => new("Retries on 429.", "A failed import is retried.", "The importer only.", null, null, "A 429 is retried.", "Two policies are plausible.", 1);

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

    // ---- the gate ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Opening_a_HOW_gate_awaits_the_human_and_binds_only_the_semantic_revision_and_the_artifact()
    {
        var stream = NewStream();
        var id = new GateId(Guid.NewGuid());

        var gate = stream.OpenHowGate(id, Proposal(turn: 2), At(1));

        var state = stream.State;
        Assert.Equal(WorkflowState.AwaitingHuman, state.WorkflowState);
        Assert.Equal(gate, state.PendingGate);
        Assert.Equal(GateType.How, gate.Type);
        Assert.Equal(id, gate.Id);
        Assert.Equal(state.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Null(gate.ImplementationRevisionId);
        Assert.Null(gate.VerificationRunId);
        Assert.Null(gate.Clarification);
        Assert.Null(gate.What);
        Assert.Equal(2, gate.How!.Turn);
        Assert.True(state.IsCurrent(gate));
        Assert.Empty(state.ImplementationRevisions);
        Assert.Null(state.CurrentVerification);
    }

    [Fact]
    public void Only_one_gate_is_open_at_a_time_across_every_kind()
    {
        var stream = Proposing();

        Assert.ThrowsAny<DomainException>(() => stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal("Another."), At(2)));
        Assert.ThrowsAny<DomainException>(() => stream.OpenWhatGate(new GateId(Guid.NewGuid()), What(), At(2)));
        Assert.Single(stream.State.Gates);

        var clarify = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        clarify.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which?", "Why.", 1), At(1));
        Assert.ThrowsAny<DomainException>(() => clarify.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(2)));
    }

    [Theory]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.AwaitingHuman)]
    public void A_HOW_opens_only_on_a_running_task(WorkflowState state)
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.ChangeWorkflowState(state, "waiting", At(1));

        Assert.ThrowsAny<DomainException>(() => stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(2)));
    }

    [Fact]
    public void Approving_closes_the_gate_creates_exactly_one_HOW_approval_revision_and_resumes_in_one_step()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;
        var before = stream.State.CurrentSemanticRevision;
        var eventsBefore = stream.Events.Count;

        var decision = stream.ApproveHow(gate.Id, At(2));

        var state = stream.State;
        Assert.Equal(GateAction.Approve, decision.Action);
        Assert.Equal(GateStatus.Approved, state.StatusOf(gate));
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(before.Ordinal + 1, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(SemanticChangeKind.HumanHowApproval, state.CurrentSemanticRevision.Kind);
        Assert.Contains(gate.How!.ContentHash, state.CurrentSemanticRevision.Explanation);
        Assert.Equal(eventsBefore + 2, stream.Events.Count);
        Assert.Null(state.PendingGate);

        Assert.Empty(state.ImplementationRevisions);
        Assert.Null(state.CurrentVerification);
        Assert.False(state.IsTerminal);
        Assert.True(state.HowApproved);
    }

    [Fact]
    public void Requesting_changes_closes_the_gate_creates_exactly_one_HOW_changes_revision_and_keeps_the_stage_unsatisfied()
    {
        var stream = Proposing(required: true);
        var gate = stream.State.PendingGate!;
        var eventsBefore = stream.Events.Count;

        var decision = stream.RequestChanges(gate.Id, "Inline it; no policy class.", At(2));

        var state = stream.State;
        Assert.Equal("Inline it; no policy class.", decision.Feedback);
        Assert.Equal(GateStatus.ChangesRequested, state.StatusOf(gate));
        Assert.Equal(SemanticChangeKind.HumanHowChanges, state.CurrentSemanticRevision.Kind);
        Assert.Equal(2, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(eventsBefore + 2, stream.Events.Count);
        Assert.False(state.HowApproved);
        Assert.False(state.MayImplement);
    }

    [Fact]
    public void A_stale_HOW_cannot_be_approved_because_the_meaning_moved_on()
    {
        var moved = Proposing();
        var movedGate = moved.State.PendingGate!;
        moved.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope moved", At(2));

        Assert.False(moved.State.IsCurrent(movedGate));
        Assert.ThrowsAny<DomainException>(() => moved.ApproveHow(movedGate.Id, At(3)));
        Assert.Equal(2, moved.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void A_decision_is_made_once_and_a_second_approval_or_change_request_records_nothing()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;
        stream.ApproveHow(gate.Id, At(2));
        var eventsAfter = stream.Events.Count;

        Assert.ThrowsAny<DomainException>(() => stream.ApproveHow(gate.Id, At(3)));
        Assert.ThrowsAny<DomainException>(() => stream.RequestChanges(gate.Id, "no", At(3)));
        Assert.Equal(eventsAfter, stream.Events.Count);
        Assert.Equal(2, stream.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void Forged_histories_that_approve_without_the_revision_or_the_right_gate_fail_replay()
    {
        var stream = Proposing();
        var gate = stream.State.PendingGate!;

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Approve, null, At(30)))), new GuidIdSource()));

        // the decision alone must not satisfy the stage
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(gate.Id, GateAction.Approve, null, At(30)))), new GuidIdSource()));

        // an approval whose revision is of another kind, and one whose revision is the WHAT's
        foreach (var kind in new[] { SemanticChangeKind.RequirementChange, SemanticChangeKind.HumanWhatApproval })
        {
            Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
                Forge(
                    stream,
                    new GateDecided(new GateDecision(gate.Id, GateAction.Approve, null, At(30))),
                    new SemanticRevisionCreated(new SemanticRevision(new SemanticRevisionId(Guid.NewGuid()), TaskOne, 2, stream.State.CurrentSemanticRevisionId, kind, "sneaky", At(31)))),
                new GuidIdSource()));
        }

        // an ACCEPT of a HOW gate
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
    public void Other_gates_are_unchanged_and_cannot_be_approved_as_a_HOW_while_a_HOW_cannot_be_accepted_or_answered()
    {
        var accept = NewTask();
        RecordPassingImplementation(accept, 1);
        var acceptGate = accept.OpenAcceptGate(At(3));
        Assert.ThrowsAny<DomainException>(() => accept.ApproveHow(acceptGate.Id, At(4)));
        accept.AcceptGate(acceptGate.Id, At(5));
        Assert.Equal(WorkflowState.Completed, accept.State.WorkflowState);

        var what = NewStream();
        what.OpenWhatGate(new GateId(Guid.NewGuid()), What(), At(1));
        Assert.ThrowsAny<DomainException>(() => what.ApproveHow(what.State.PendingGate!.Id, At(2)));

        var how = Proposing();
        Assert.ThrowsAny<DomainException>(() => how.ApproveWhat(how.State.PendingGate!.Id, At(2)));
        Assert.ThrowsAny<DomainException>(() => how.AcceptGate(how.State.PendingGate!.Id, At(2)));
        Assert.ThrowsAny<DomainException>(() => how.AnswerClarification(how.State.PendingGate!.Id, "x", At(2)));
        Assert.Equal(WorkflowState.AwaitingHuman, how.State.WorkflowState);
    }

    [Fact]
    public void The_artifact_is_hashable_immutable_bounded_and_replayable()
    {
        var one = Proposal();
        Assert.Equal(one.ContentHash, Proposal().ContentHash);
        Assert.NotEqual(one.ContentHash, Proposal("A different strategy.").ContentHash);
        Assert.Equal(one.ContentHash, Proposal(turn: 5).ContentHash);   // the turn is provenance, not content
        Assert.Equal(64, one.ContentHash.Length);

        // sections are named: moving text between sections changes the hash
        var swapped = new HowProposal("B", "A", null, null, "R", null, "why", 1);
        var straight = new HowProposal("A", "B", null, null, "R", null, "why", 1);
        Assert.NotEqual(straight.ContentHash, swapped.ContentHash);

        // an optional section left out is not the same artifact as one given
        Assert.NotEqual(straight.ContentHash, new HowProposal("A", "B", "flow", null, "R", null, "why", 1).ContentHash);

        // bounds, no summarization: an oversized section, a control character or a total over the bound is refused, never cut
        var big = new string('x', HowProposal.MaxSectionLength + 1);
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal(big, "c", null, null, "r", null, "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal("s", "c", null, null, "r", null, new string('r', HowProposal.MaxRationaleLength + 1), 1));
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal("s\u0007", "c", null, null, "r", null, "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal(" ", "c", null, null, "r", null, "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal("s", "c", null, null, " ", null, "why", 1));
        var section = new string('y', HowProposal.MaxSectionLength);
        Assert.ThrowsAny<ArgumentException>(() => new HowProposal(section, section, section, section, section, section, "r", 1));

        Assert.Equal(
            ["Strategy", "Components and responsibilities", "Risks and tradeoffs"],
            new HowProposal("s", "c", null, null, "r", null, "why", 1).Sections().Select(s => s.Name).ToArray());

        var stream = Proposing();
        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;
        Assert.Equal(stream.State, replayed);
        Assert.Equal(one.ContentHash, replayed.Gates[0].How!.ContentHash);
    }

    [Fact]
    public void A_gate_shape_that_mixes_the_HOW_with_an_implementation_a_question_or_a_WHAT_is_invalid()
    {
        var id = new GateId(Guid.NewGuid());
        var semantic = NewTask().State.CurrentSemanticRevisionId;

        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.How, semantic, new ImplementationRevisionId(Guid.NewGuid()), null, At(1), how: Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.How, semantic, null, null, At(1)));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.How, semantic, null, null, At(1), what: What(), how: Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.What, semantic, null, null, At(1), what: What(), how: Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.How, semantic, null, null, At(1), what: What()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Clarify, semantic, null, null, At(1), new ClarificationRequest("q", "r", 1), how: Proposal()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Accept, semantic, new ImplementationRevisionId(Guid.NewGuid()), new VerificationRunId(Guid.NewGuid()), At(1), how: Proposal()));
    }

    // ---- the stage a person can require ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_required_stage_is_recorded_in_the_first_event_and_survives_replay_and_every_later_change()
    {
        var stream = NewStream(how: true);

        var created = (TaskCreated)stream.Events[0].Payload;
        Assert.True(created.HowRequired);
        Assert.False(created.WhatRequired);   // HOW does not imply WHAT
        Assert.True(stream.State.HowRequired);
        Assert.False(stream.State.WhatRequired);

        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "moved", At(1));
        stream.ChangeWorkflowState(WorkflowState.Blocked, "x", At(2));

        Assert.True(stream.State.HowRequired);
        Assert.True(TaskStream.Replay(stream.Events, new GuidIdSource()).State.HowRequired);
        Assert.False(NewTask().State.HowRequired);

        var whatOnly = NewStream(what: true);
        Assert.True(whatOnly.State.WhatRequired);
        Assert.False(whatOnly.State.HowRequired);   // WHAT does not imply HOW
        Assert.True(whatOnly.State.HowSatisfied);
    }

    [Fact]
    public void A_required_stage_refuses_any_implementation_before_the_HOW_is_approved()
    {
        var stream = NewStream(how: true);

        Assert.False(stream.State.MayImplement);
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1)));

        var gateId = new GateId(Guid.NewGuid());
        stream.OpenHowGate(gateId, Proposal(), At(2));
        stream.RequestChanges(gateId, "Narrower.", At(3));
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(4)));
        Assert.Empty(stream.State.ImplementationRevisions);
    }

    [Fact]
    public void A_required_stage_cannot_complete_until_a_HOW_is_approved_and_then_completes_as_before()
    {
        var stream = NewStream(how: true);

        Assert.ThrowsAny<DomainException>(() => stream.ChangeWorkflowState(WorkflowState.Completed, "controller completes", At(1)));

        var gateId = new GateId(Guid.NewGuid());
        stream.OpenHowGate(gateId, Proposal(), At(2));
        stream.ApproveHow(gateId, At(3));

        RecordPassingImplementation(stream, 4);
        stream.ChangeWorkflowState(WorkflowState.Completed, "controller completes", At(6));

        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
        Assert.Single(stream.State.ImplementationRevisions);
    }

    [Fact]
    public void A_stage_the_agent_chose_binds_the_same_way_as_a_required_one_and_an_untouched_task_pays_nothing()
    {
        var stream = Proposing(required: false);

        Assert.False(stream.State.HowRequired);
        Assert.False(stream.State.MayImplement);
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2)));

        stream.ApproveHow(stream.State.PendingGate!.Id, At(3));
        Assert.True(stream.State.MayImplement);

        Assert.True(NewTask().State.MayImplement);
    }

    // ---- WHAT before HOW ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void With_both_required_a_HOW_cannot_open_before_the_WHAT_is_approved_and_implementation_needs_both()
    {
        var stream = NewStream(what: true, how: true);

        Assert.False(stream.State.WhatSatisfied);
        Assert.ThrowsAny<DomainException>(() => stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(1)));
        Assert.Empty(stream.State.Gates);

        var whatId = new GateId(Guid.NewGuid());
        stream.OpenWhatGate(whatId, What(), At(2));
        stream.ApproveWhat(whatId, At(3));

        Assert.True(stream.State.WhatSatisfied);
        Assert.False(stream.State.MayImplement);   // the WHAT is approved; the HOW still stands in the way
        Assert.Throws<InvalidGateException>(() => stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(4)));

        var howId = new GateId(Guid.NewGuid());
        var how = stream.OpenHowGate(howId, Proposal(), At(5));
        Assert.Equal(stream.State.CurrentSemanticRevisionId, how.SemanticRevisionId);   // bound to the revision that carries the approved WHAT

        stream.ApproveHow(howId, At(6));

        Assert.True(stream.State.MayImplement);
        RecordPassingImplementation(stream, 7);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatApproval, SemanticChangeKind.HumanHowApproval],
            stream.State.SemanticRevisions.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void A_HOW_proposed_on_a_basis_that_a_later_WHAT_change_replaced_is_stale_and_cannot_become_current()
    {
        var stream = NewStream(what: true, how: true);
        var whatId = new GateId(Guid.NewGuid());
        stream.OpenWhatGate(whatId, What(), At(1));
        stream.ApproveWhat(whatId, At(2));
        var howId = new GateId(Guid.NewGuid());
        var how = stream.OpenHowGate(howId, Proposal(), At(3));

        // the meaning moves (as a later requirement change would): the HOW no longer describes the run
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope moved", At(4));

        Assert.False(stream.State.IsCurrent(how));
        Assert.ThrowsAny<DomainException>(() => stream.ApproveHow(howId, At(5)));
        Assert.False(stream.State.HowApproved);
    }

    [Fact]
    public void Feedback_history_is_kept_in_order_across_rounds_and_each_proposal_stays_historical()
    {
        var stream = NewStream(how: true);
        var first = new GateId(Guid.NewGuid());
        var second = new GateId(Guid.NewGuid());

        stream.OpenHowGate(first, Proposal("v1"), At(1));
        stream.RequestChanges(first, "First feedback.", At(2));
        stream.OpenHowGate(second, Proposal("v2", turn: 2), At(3));

        var state = stream.State;
        Assert.False(state.IsCurrent(state.Gates[0]));
        Assert.True(state.IsCurrent(state.Gates[1]));
        Assert.Equal(second, state.LatestHowGate!.Id);
        Assert.Equal("First feedback.", state.DecisionFor(first)!.Feedback);

        stream.ApproveHow(second, At(4));

        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanHowChanges, SemanticChangeKind.HumanHowApproval],
            stream.State.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal("v1", stream.State.Gates[0].How!.Strategy);
        Assert.Equal("v2", stream.State.Gates[1].How!.Strategy);
    }
}
