using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>The M4A ACCEPT gate: one current gate, bound to what was verified, decided once, and the only way (besides legacy replay) to a completed task.</summary>
public sealed class GateTests
{
    private static VerificationCheckResult Pass(string id = "tests") => new(new VerificationCheckId(id), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail(string id = "tests") => new(new VerificationCheckId(id), VerificationOutcome.Failed, "exit 1", 1);

    private static TaskStream Implemented(string fingerprint = "tree:1", int minute = 1)
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint(fingerprint), At(minute));
        return stream;
    }

    private static void Verify(TaskStream stream, int minute, params VerificationCheckResult[] checks)
        => stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, checks.Length == 0 ? [Pass()] : checks, At(minute));

    private static TaskStream VerifiedWithOpenGate()
    {
        var stream = Implemented();
        Verify(stream, 2);
        stream.OpenAcceptGate(At(3));
        return stream;
    }

    // ---- opening -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Opening_the_gate_makes_the_task_await_the_human_and_binds_what_was_verified()
    {
        var stream = Implemented();
        Verify(stream, 2);

        var gate = stream.OpenAcceptGate(At(3));

        var state = stream.State;
        Assert.Equal(WorkflowState.AwaitingHuman, state.WorkflowState);
        Assert.Equal(gate, state.PendingGate);
        Assert.Equal(GateType.Accept, gate.Type);
        Assert.Equal(state.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Equal(state.CurrentImplementationRevisionId, gate.ImplementationRevisionId);
        Assert.Equal(state.CurrentVerification!.Id, gate.VerificationRunId);
        Assert.Equal(GateStatus.Open, state.StatusOf(gate));
        Assert.True(state.IsCurrent(gate));
        Assert.False(state.IsTerminal);
    }

    [Fact]
    public void Verification_alone_never_completes_the_task()
    {
        var stream = Implemented();
        Verify(stream, 2);

        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
        Assert.Empty(stream.State.Gates);
    }

    [Fact]
    public void A_gate_needs_a_verification_to_bind()
    {
        Assert.Throws<InvalidGateException>(() => Implemented().OpenAcceptGate(At(2)));
        Assert.Throws<InvalidGateException>(() => NewTask().OpenAcceptGate(At(2)));
    }

    [Fact]
    public void A_gate_cannot_open_over_a_failed_verification()
    {
        var stream = Implemented();
        Verify(stream, 2, Pass("a"), Fail("b"));

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(3)));
        Assert.Empty(stream.State.Gates);
    }

    [Fact]
    public void A_new_implementation_revision_leaves_nothing_verified_to_open_a_gate_over()
    {
        var stream = Implemented();
        Verify(stream, 2);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(4)));
    }

    [Fact]
    public void Only_one_gate_is_open_at_a_time()
    {
        var stream = VerifiedWithOpenGate();

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(4)));
        Assert.Single(stream.State.Gates);
    }

    [Theory]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.AwaitingHuman)]
    public void A_gate_opens_only_on_a_running_task(WorkflowState state)
    {
        var stream = Implemented();
        Verify(stream, 2);
        stream.ChangeWorkflowState(state, "waiting", At(3));

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(4)));
    }

    // ---- accepting ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Accepting_the_current_gate_completes_the_task()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;

        var decision = stream.AcceptGate(gate.Id, At(4));

        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
        Assert.True(stream.State.IsTerminal);
        Assert.Equal(GateAction.Accept, decision.Action);
        Assert.Equal(GateStatus.Accepted, stream.State.StatusOf(gate));
        Assert.Null(stream.State.PendingGate);
    }

    [Fact]
    public void A_gate_can_only_be_decided_once()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        stream.AcceptGate(gate.Id, At(4));

        Assert.ThrowsAny<DomainException>(() => stream.AcceptGate(gate.Id, At(5)));
        Assert.ThrowsAny<DomainException>(() => stream.CancelGate(gate.Id, At(5)));
        Assert.Single(stream.State.GateDecisions);
    }

    [Fact]
    public void A_gate_unknown_to_the_task_cannot_be_decided()
    {
        var stream = VerifiedWithOpenGate();

        Assert.Throws<InvalidGateException>(() => stream.AcceptGate(new GateId(Guid.NewGuid()), At(4)));
        Assert.Equal(WorkflowState.AwaitingHuman, stream.State.WorkflowState);
    }

    [Fact]
    public void A_gate_cannot_be_accepted_after_the_implementation_changed()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(4));

        Assert.False(stream.State.IsCurrent(gate));
        Assert.Throws<StaleGateException>(() => stream.AcceptGate(gate.Id, At(5)));
        Assert.NotEqual(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void A_gate_cannot_be_accepted_after_the_meaning_changed()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "Also rename the docs.", At(4));

        Assert.False(stream.State.IsCurrent(gate));
        Assert.Throws<StaleGateException>(() => stream.AcceptGate(gate.Id, At(5)));
    }

    [Fact]
    public void A_gate_cannot_be_accepted_after_a_newer_verification_of_the_same_implementation()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        Verify(stream, 4);   // the gate binds the verification the human reviewed, not a later one

        Assert.False(stream.State.IsCurrent(gate));
        Assert.Throws<StaleGateException>(() => stream.AcceptGate(gate.Id, At(5)));
    }

    [Fact]
    public void The_generic_state_change_cannot_move_a_task_off_its_pending_gate()
    {
        var stream = VerifiedWithOpenGate();

        Assert.Throws<InvalidGateException>(() => stream.ChangeWorkflowState(WorkflowState.Running, "carry on", At(4)));
        Assert.Throws<InvalidGateException>(() => stream.ChangeWorkflowState(WorkflowState.Cancelled, "stop", At(4)));
        Assert.NotNull(stream.State.PendingGate);
    }

    // ---- requesting changes ------------------------------------------------------------------------------------------

    [Fact]
    public void Requesting_changes_closes_the_gate_as_not_approved_and_returns_to_running_under_a_new_semantic_revision()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        var before = stream.State.CurrentSemanticRevision;

        stream.RequestChanges(gate.Id, "Please add a test.", At(4));

        var state = stream.State;
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(GateStatus.ChangesRequested, state.StatusOf(gate));
        Assert.Equal("Please add a test.", state.DecisionFor(gate.Id)!.Feedback);
        Assert.Null(state.PendingGate);
        Assert.Equal(before.Ordinal + 1, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, state.CurrentSemanticRevision.Kind);
        Assert.False(state.IsCurrent(gate));
    }

    [Fact]
    public void After_requested_changes_the_old_implementation_targets_stale_semantics_and_a_new_gate_needs_new_work()
    {
        var stream = VerifiedWithOpenGate();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Again.", At(4));

        // the old implementation and its verification are bound to the previous meaning: nothing to open a gate over
        Assert.Throws<InvalidRevisionReferenceException>(() => stream.OpenAcceptGate(At(5)));

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(5));
        Verify(stream, 6);
        var second = stream.OpenAcceptGate(At(7));

        Assert.Equal(2, stream.State.Gates.Length);
        Assert.Equal(second, stream.State.PendingGate);
        Assert.True(stream.State.IsCurrent(second));
    }

    [Fact]
    public void A_gate_closed_by_requested_changes_can_never_be_accepted_later()
    {
        var stream = VerifiedWithOpenGate();
        var first = stream.State.PendingGate!;
        stream.RequestChanges(first.Id, "Again.", At(4));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(5));
        Verify(stream, 6);
        stream.OpenAcceptGate(At(7));

        Assert.ThrowsAny<DomainException>(() => stream.AcceptGate(first.Id, At(8)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Requested_changes_need_feedback(string? feedback)
        => Assert.ThrowsAny<ArgumentException>(() => new GateDecision(new GateId(Guid.NewGuid()), GateAction.RequestChanges, feedback, At(1)));

    [Fact]
    public void Feedback_is_bounded()
    {
        var gateId = new GateId(Guid.NewGuid());

        Assert.NotNull(new GateDecision(gateId, GateAction.RequestChanges, new string('x', GateDecision.MaxFeedbackLength), At(1)));
        Assert.ThrowsAny<ArgumentException>(() => new GateDecision(gateId, GateAction.RequestChanges, new string('x', GateDecision.MaxFeedbackLength + 1), At(1)));
    }

    [Theory]
    [InlineData(GateAction.Accept)]
    [InlineData(GateAction.Cancel)]
    public void Only_requested_changes_carry_feedback(GateAction action)
        => Assert.Throws<InvalidGateException>(() => new GateDecision(new GateId(Guid.NewGuid()), action, "because", At(1)));

    // ---- cancelling --------------------------------------------------------------------------------------------------

    [Fact]
    public void Cancelling_closes_the_gate_without_approval_and_cancels_the_task()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;

        stream.CancelGate(gate.Id, At(4));

        Assert.Equal(WorkflowState.Cancelled, stream.State.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, stream.State.StatusOf(gate));
        Assert.Null(stream.State.PendingGate);
    }

    // ---- replay ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gate_history_replays_to_exactly_the_state_that_produced_it()
    {
        var stream = VerifiedWithOpenGate();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Again.", At(4));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(5));
        Verify(stream, 6);
        stream.OpenAcceptGate(At(7));
        stream.AcceptGate(stream.State.PendingGate!.Id, At(8));

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource());

        Assert.Equal(stream.State, replayed.State);
        Assert.Equal(WorkflowState.Completed, replayed.State.WorkflowState);
        Assert.Equal(2, replayed.State.Gates.Length);
        Assert.Equal(2, replayed.State.GateDecisions.Length);
    }

    [Fact]
    public void A_replayed_pending_gate_is_still_pending_and_current()
    {
        var stream = VerifiedWithOpenGate();

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.Equal(stream.State.PendingGate, replayed.PendingGate);
        Assert.True(replayed.IsCurrent(replayed.PendingGate!));
    }

    [Fact]
    public void A_history_with_an_accept_over_a_stale_gate_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(4));
        stream.CancelGate(gate.Id, At(5));

        var forged = stream.Events.ToList();
        var cancel = forged[^1];
        forged[^1] = new EventEnvelope(cancel.Id, cancel.TaskId, cancel.Sequence, cancel.Timestamp, new GateDecided(new GateDecision(gate.Id, GateAction.Accept, null, cancel.Timestamp)));

        Assert.Throws<StaleGateException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void A_history_that_decides_the_same_gate_twice_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;
        stream.RequestChanges(gate.Id, "Again.", At(4));

        var forged = stream.Events.ToList();
        var decided = forged.Single(e => e.Payload is GateDecided);
        forged.Add(new EventEnvelope(new EventId(Guid.NewGuid()), decided.TaskId, forged.Count + 1, At(9), new GateDecided(new GateDecision(gate.Id, GateAction.Cancel, null, At(9)))));

        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void A_history_that_decides_an_unknown_gate_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        var forged = stream.Events.ToList();
        forged.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, forged.Count + 1, At(9), new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Accept, null, At(9)))));

        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void A_history_whose_gate_binds_a_revision_that_is_not_current_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        var gate = stream.State.PendingGate!;

        var forged = stream.Events.Where(e => e.Payload is not GateOpened).ToList();
        var other = new Gate(gate.Id, gate.TaskId, gate.Type, gate.SemanticRevisionId, new ImplementationRevisionId(Guid.NewGuid()), gate.VerificationRunId, At(3));
        forged.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, forged.Count + 1, At(3), new GateOpened(other)));

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void A_history_that_opens_a_gate_over_a_failed_verification_fails_replay()
    {
        var stream = Implemented();
        Verify(stream, 2, Fail());
        var verification = stream.State.CurrentVerification!;

        var forged = stream.Events.ToList();
        var gate = new Gate(new GateId(Guid.NewGuid()), TaskOne, GateType.Accept, stream.State.CurrentSemanticRevisionId, verification.ImplementationRevisionId, verification.Id, At(3));
        forged.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, forged.Count + 1, At(3), new GateOpened(gate)));

        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void Legacy_histories_that_completed_without_a_gate_still_replay()
    {
        var stream = AsLegacy(Implemented());
        Verify(stream, 2);
        stream.ChangeWorkflowState(WorkflowState.Completed, "done (pre-M4A)", At(3));

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.Equal(WorkflowState.Completed, replayed.WorkflowState);
        Assert.Empty(replayed.Gates);
    }
}
