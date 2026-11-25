using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>M4B: the acceptance requirement is part of the task's first event, and it decides which completion path Core allows.</summary>
public sealed class AcceptanceRequirementTests
{
    private static VerificationCheckResult Pass() => new(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail() => new(new VerificationCheckId("tests"), VerificationOutcome.Failed, "exit 1", 1);

    private static TaskStream Verified(AcceptanceRequirement acceptance, params VerificationCheckResult[] checks)
    {
        var stream = NewTask(acceptance: acceptance);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, checks.Length == 0 ? [Pass()] : checks, At(2));
        return stream;
    }

    // ---- the requirement is recorded ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired)]
    [InlineData(AcceptanceRequirement.HumanRequired)]
    public void The_requirement_is_recorded_in_the_first_event_and_survives_replay(AcceptanceRequirement acceptance)
    {
        var stream = NewTask(acceptance: acceptance);

        Assert.Equal(acceptance, ((TaskCreated)stream.Events[0].Payload).Acceptance);
        Assert.Equal(acceptance, stream.State.Acceptance);
        Assert.Equal(acceptance, TaskStream.Replay(stream.Events, new SequentialIdSource()).State.Acceptance);
    }

    [Fact]
    public void A_task_created_without_naming_a_requirement_does_not_require_acceptance()
    {
        var stream = TaskStream.Create(TaskOne, "intent", CompletionBoundary.WorkingTree, T0, new SequentialIdSource());

        Assert.Equal(AcceptanceRequirement.NotRequired, stream.State.Acceptance);
    }

    [Fact]
    public void An_undefined_requirement_fails_replay()
    {
        var first = NewTask().Events[0];
        var payload = (TaskCreated)first.Payload with { Acceptance = (AcceptanceRequirement)99 };
        var forged = new EventEnvelope(first.Id, first.TaskId, first.Sequence, first.Timestamp, payload);

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay([forged], new SequentialIdSource()));
    }

    [Fact]
    public void The_requirement_is_part_of_state_equality()
    {
        var human = NewTask(acceptance: AcceptanceRequirement.HumanRequired).State;
        var none = NewTask(acceptance: AcceptanceRequirement.NotRequired).State;

        Assert.NotEqual(human, none);
    }

    // ---- no acceptance required: the controller completes over current passing verification -------------------------------

    [Fact]
    public void A_verified_task_without_acceptance_completes_with_no_gate()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);

        stream.CompleteVerified(At(3));

        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
        Assert.Empty(stream.State.Gates);
        Assert.Empty(stream.State.GateDecisions);
        Assert.Contains(stream.State.CurrentVerification!.Id.ToString(), stream.State.StateReason);
        Assert.Equal(stream.State, TaskStream.Replay(stream.Events, new SequentialIdSource()).State);
    }

    [Fact]
    public void Completion_without_acceptance_needs_a_verification()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(1)));

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(2)));
        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
    }

    [Fact]
    public void A_failed_verification_never_completes_a_task_without_acceptance()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired, Fail());

        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(3)));
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.ChangeWorkflowState(WorkflowState.Completed, "done", At(3)));
        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
    }

    [Fact]
    public void Verification_of_an_older_implementation_does_not_complete_a_newer_one()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));

        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(4)));
    }

    [Fact]
    public void A_verification_made_under_older_semantics_does_not_complete_the_task()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "The requirement changed.", At(3));

        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(4)));
    }

    [Fact]
    public void A_task_without_acceptance_completes_only_from_running()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);
        stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "The agent waits in its own interface.", At(3));

        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(4)));
    }

    [Fact]
    public void A_forged_completion_without_verification_fails_replay()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var forged = new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, 3, At(2),
            new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "the agent said it is done"));

        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(stream.Events.Append(forged), new SequentialIdSource()));
    }

    [Fact]
    public void No_gate_is_ever_opened_for_a_task_without_acceptance()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(3)));
        Assert.Empty(stream.State.Gates);
        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
    }

    // ---- acceptance required: unchanged M4A -----------------------------------------------------------------------------

    [Fact]
    public void A_task_that_requires_acceptance_cannot_be_completed_by_the_controller()
    {
        var stream = Verified(AcceptanceRequirement.HumanRequired);

        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(3)));
        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
    }

    [Fact]
    public void A_task_that_requires_acceptance_still_opens_the_gate_and_completes_through_its_decision()
    {
        var stream = Verified(AcceptanceRequirement.HumanRequired);

        var gate = stream.OpenAcceptGate(At(3));
        Assert.Equal(WorkflowState.AwaitingHuman, stream.State.WorkflowState);

        stream.AcceptGate(gate.Id, At(4));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    // ---- the replay matrix: which histories may contain a COMPLETED, and why --------------------------------------------------

    private static EventEnvelope PlainCompletion(TaskStream stream, int minute)
        => new(
            new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(minute),
            new WorkflowStateChanged(stream.State.WorkflowState, WorkflowState.Completed, "completed by a plain recorded state change"));

    [Fact]
    public void Matrix_A_a_legitimate_no_acceptance_completion_replays_to_COMPLETED()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);
        stream.CompleteVerified(At(3));

        var replayed = TaskStream.Replay(stream.Events, new SequentialIdSource()).State;

        Assert.Equal(WorkflowState.Completed, replayed.WorkflowState);
        Assert.Equal(AcceptanceRequirement.NotRequired, replayed.Acceptance);
        Assert.False(replayed.PredatesAcceptanceRequirement);
    }

    [Fact]
    public void Matrix_B_a_no_acceptance_completion_without_current_passing_verification_fails_replay()
    {
        // no implementation at all
        var none = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(none.Events.Append(PlainCompletion(none, 1)), new SequentialIdSource()));

        // implemented, never verified
        var unverified = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        unverified.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(unverified.Events.Append(PlainCompletion(unverified, 2)), new SequentialIdSource()));

        // verified, but the verification failed
        var failed = Verified(AcceptanceRequirement.NotRequired, Fail());
        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(failed.Events.Append(PlainCompletion(failed, 3)), new SequentialIdSource()));
    }

    [Fact]
    public void Matrix_C_a_new_human_required_task_cannot_be_completed_by_a_plain_event_even_over_passing_verification()
    {
        var stream = Verified(AcceptanceRequirement.HumanRequired);
        Assert.False(stream.State.PredatesAcceptanceRequirement);

        // verified and current, no gate was ever accepted
        var ex = Assert.Throws<InvalidWorkflowTransitionException>(
            () => TaskStream.Replay(stream.Events.Append(PlainCompletion(stream, 3)), new SequentialIdSource()));
        Assert.Contains("accepted gate", ex.Message);

        // the live aggregate refuses it as well
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.ChangeWorkflowState(WorkflowState.Completed, "done", At(3)));

        // an open (undecided) gate is not an acceptance either: only the decision moves the task
        stream.OpenAcceptGate(At(3));
        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(stream.Events.Append(PlainCompletion(stream, 4)), new SequentialIdSource()));

        // while the accepted gate completes it, and that history replays
        stream.AcceptGate(stream.State.PendingGate!.Id, At(4));
        Assert.Equal(WorkflowState.Completed, TaskStream.Replay(stream.Events, new SequentialIdSource()).State.WorkflowState);
    }

    [Fact]
    public void Matrix_C_a_plain_completion_cannot_pass_as_legacy_by_the_absence_of_a_gate()
    {
        // A new history that merely has no gate is not legacy: legacy is a property recorded in the first event, not inferred from what follows.
        var stream = NewTask(acceptance: AcceptanceRequirement.HumanRequired);

        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(stream.Events.Append(PlainCompletion(stream, 1)), new SequentialIdSource()));
    }

    [Fact]
    public void Matrix_D_a_legacy_history_with_a_plain_completion_replays_unchanged()
    {
        // before M4A: verified, then a plain recorded COMPLETED, no gate at all
        var stream = AsLegacy(Verified(AcceptanceRequirement.HumanRequired));
        stream.ChangeWorkflowState(WorkflowState.Completed, "Direct path complete (pre-M4A).", At(3));
        var before = stream.Events.ToList();

        var replayed = TaskStream.Replay(before, new SequentialIdSource()).State;

        Assert.Equal(WorkflowState.Completed, replayed.WorkflowState);
        Assert.Equal(AcceptanceRequirement.HumanRequired, replayed.Acceptance);
        Assert.True(replayed.PredatesAcceptanceRequirement);
        Assert.Empty(replayed.Gates);
        Assert.Equal(before, stream.Events);   // replay rewrote nothing
    }

    [Fact]
    public void Matrix_D_a_legacy_history_that_completed_through_an_M4A_gate_replays()
    {
        var stream = AsLegacy(Verified(AcceptanceRequirement.HumanRequired));
        stream.AcceptGate(stream.OpenAcceptGate(At(3)).Id, At(4));

        Assert.Equal(WorkflowState.Completed, TaskStream.Replay(stream.Events, new SequentialIdSource()).State.WorkflowState);
    }

    [Fact]
    public void The_legacy_marker_is_only_valid_on_a_human_required_task()
    {
        var first = NewTask(acceptance: AcceptanceRequirement.NotRequired).Events[0];
        var payload = (TaskCreated)first.Payload with { PredatesAcceptanceRequirement = true };
        var forged = new EventEnvelope(first.Id, first.TaskId, first.Sequence, first.Timestamp, payload);

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay([forged], new SequentialIdSource()));
    }

    [Fact]
    public void A_task_created_now_is_never_legacy()
    {
        Assert.False(NewTask(acceptance: AcceptanceRequirement.HumanRequired).State.PredatesAcceptanceRequirement);
        Assert.False(NewTask(acceptance: AcceptanceRequirement.NotRequired).State.PredatesAcceptanceRequirement);
        Assert.True(NewLegacyTask().State.PredatesAcceptanceRequirement);
    }
}
