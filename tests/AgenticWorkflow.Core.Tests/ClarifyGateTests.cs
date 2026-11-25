using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>The M5A CLARIFY gate: a durable question the agent raised, bound to the semantic revision only, answered or cancelled once, and replay-safe.</summary>
public sealed class ClarifyGateTests
{
    private static ClarificationRequest Question(string text = "Which dessert?", string reason = "The name is not in the repository.", int turn = 1)
        => new(text, reason, turn);

    private static TaskStream Asking(AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var stream = NewTask(acceptance: acceptance);
        stream.OpenClarifyGate(new GateId(Guid.NewGuid()), Question(), At(1));
        return stream;
    }

    private static List<EventEnvelope> Forge(TaskStream stream, params DomainEvent[] more)
    {
        var events = stream.Events.ToList();

        foreach (var payload in more)
            events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, At(20 + events.Count), payload));

        return events;
    }

    [Fact]
    public void Opening_the_gate_awaits_the_human_and_binds_only_the_semantic_revision()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        var id = new GateId(Guid.NewGuid());

        var gate = stream.OpenClarifyGate(id, Question(turn: 2), At(1));

        var state = stream.State;
        Assert.Equal(WorkflowState.AwaitingHuman, state.WorkflowState);
        Assert.Equal(gate, state.PendingGate);
        Assert.Equal(GateType.Clarify, gate.Type);
        Assert.Equal(id, gate.Id);
        Assert.Equal(state.CurrentSemanticRevisionId, gate.SemanticRevisionId);
        Assert.Null(gate.ImplementationRevisionId);
        Assert.Null(gate.VerificationRunId);
        Assert.Equal(2, gate.Clarification!.Turn);
        Assert.True(state.IsCurrent(gate));
        Assert.Empty(state.ImplementationRevisions);
    }

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired)]
    [InlineData(AcceptanceRequirement.HumanRequired)]
    public void A_question_can_be_asked_whatever_the_acceptance_policy(AcceptanceRequirement acceptance)
        => Assert.Equal(WorkflowState.AwaitingHuman, Asking(acceptance).State.WorkflowState);

    [Fact]
    public void Asking_records_nothing_as_implemented_verified_or_accepted()
    {
        var state = Asking(AcceptanceRequirement.HumanRequired).State;

        Assert.Empty(state.ImplementationRevisions);
        Assert.Null(state.CurrentVerification);
        Assert.Single(state.Gates);
        Assert.Equal(GateType.Clarify, state.Gates[0].Type);
        Assert.False(state.IsTerminal);
    }

    [Fact]
    public void Only_one_gate_is_open_at_a_time_and_a_question_cannot_open_over_another_gate()
    {
        var stream = Asking();

        Assert.ThrowsAny<DomainException>(() => stream.OpenClarifyGate(new GateId(Guid.NewGuid()), Question("Another?"), At(2)));
        Assert.Single(stream.State.Gates);
    }

    [Theory]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.AwaitingHuman)]
    public void A_question_opens_only_on_a_running_task(WorkflowState state)
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.ChangeWorkflowState(state, "waiting", At(1));

        Assert.ThrowsAny<DomainException>(() => stream.OpenClarifyGate(new GateId(Guid.NewGuid()), Question(), At(2)));
    }

    [Fact]
    public void Answering_closes_the_gate_creates_a_human_clarification_revision_and_resumes_in_one_step()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;
        var before = stream.State.CurrentSemanticRevision;
        var eventsBefore = stream.Events.Count;

        var decision = stream.AnswerClarification(gate.Id, "Tiramisu.", At(2));

        var state = stream.State;
        Assert.Equal(GateAction.Answer, decision.Action);
        Assert.Equal("Tiramisu.", decision.Answer);
        Assert.Equal(GateStatus.Answered, state.StatusOf(gate));
        Assert.Equal(WorkflowState.Running, state.WorkflowState);
        Assert.Equal(before.Ordinal + 1, state.CurrentSemanticRevision.Ordinal);
        Assert.Equal(SemanticChangeKind.HumanClarification, state.CurrentSemanticRevision.Kind);
        Assert.Equal(eventsBefore + 2, stream.Events.Count);   // the decision and the revision: nothing in between
        Assert.Null(state.PendingGate);
    }

    [Fact]
    public void Answering_makes_the_answered_gate_stale_because_the_meaning_changed()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;
        stream.AnswerClarification(gate.Id, "Tiramisu.", At(2));

        Assert.False(stream.State.IsCurrent(gate));
    }

    [Fact]
    public void An_answer_is_given_once()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;
        stream.AnswerClarification(gate.Id, "Tiramisu.", At(2));

        Assert.ThrowsAny<DomainException>(() => stream.AnswerClarification(gate.Id, "Panna cotta.", At(3)));
        Assert.Equal(2, stream.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void Cancelling_a_question_cancels_the_task_without_a_new_revision()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;

        stream.CancelGate(gate.Id, At(2));

        Assert.Equal(WorkflowState.Cancelled, stream.State.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, stream.State.StatusOf(gate));
        Assert.Equal(1, stream.State.CurrentSemanticRevision.Ordinal);
    }

    [Fact]
    public void A_clarify_gate_cannot_be_accepted_or_have_changes_requested()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;

        Assert.ThrowsAny<DomainException>(() => stream.AcceptGate(gate.Id, At(2)));
        Assert.ThrowsAny<DomainException>(() => stream.RequestChanges(gate.Id, "no", At(2)));
        Assert.Equal(WorkflowState.AwaitingHuman, stream.State.WorkflowState);
    }

    [Fact]
    public void An_accept_gate_cannot_be_answered()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(2));
        var gate = stream.OpenAcceptGate(At(3));

        Assert.ThrowsAny<DomainException>(() => stream.AnswerClarification(gate.Id, "hello", At(4)));
        Assert.Equal(WorkflowState.AwaitingHuman, stream.State.WorkflowState);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_question_and_a_reason_must_say_something(string blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest(blank, "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest("what", blank, 1));
    }

    [Fact]
    public void A_question_an_answer_and_a_turn_are_bounded()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest(new string('q', ClarificationRequest.MaxQuestionLength + 1), "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest("what", new string('r', ClarificationRequest.MaxReasonLength + 1), 1));
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest("what\u0007", "why", 1));
        Assert.ThrowsAny<ArgumentException>(() => new ClarificationRequest("what", "why", 0));
        Assert.ThrowsAny<ArgumentException>(() => new GateDecision(new GateId(Guid.NewGuid()), GateAction.Answer, null, At(1), new string('a', GateDecision.MaxAnswerLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => new GateDecision(new GateId(Guid.NewGuid()), GateAction.Answer, null, At(1), " "));
    }

    [Fact]
    public void A_clarify_gate_with_an_implementation_binding_or_an_accept_gate_with_a_question_is_invalid()
    {
        var id = new GateId(Guid.NewGuid());
        var semantic = NewTask().State.CurrentSemanticRevisionId;

        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Clarify, semantic, new ImplementationRevisionId(Guid.NewGuid()), null, At(1), Question()));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Clarify, semantic, null, null, At(1)));
        Assert.Throws<InvalidGateException>(() => new Gate(id, TaskOne, GateType.Accept, semantic, new ImplementationRevisionId(Guid.NewGuid()), new VerificationRunId(Guid.NewGuid()), At(1), Question()));
    }

    [Fact]
    public void A_question_and_its_answer_survive_replay()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;
        stream.AnswerClarification(gate.Id, "Tiramisu.", At(2));

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.Equal(stream.State, replayed);
        Assert.Equal("Tiramisu.", replayed.DecisionFor(gate.Id)!.Answer);
        Assert.Equal("Which dessert?", replayed.Gates[0].Clarification!.Question);
    }

    [Fact]
    public void Forged_histories_that_answer_without_the_revision_or_a_stale_question_fail_replay()
    {
        var stream = Asking();
        var gate = stream.State.PendingGate!;

        // an answer to a question that is not the open one
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Answer, null, At(30), "x"))), new GuidIdSource()));

        // an ACCEPT of a question
        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(
            Forge(stream, new GateDecided(new GateDecision(gate.Id, GateAction.Accept, null, At(30)))), new GuidIdSource()));

        // an answer recorded after the meaning already moved on (the question is no longer current)
        var moved = Asking();
        var movedGate = moved.State.PendingGate!;
        moved.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope moved", At(2));
        var history = Forge(moved, new GateDecided(new GateDecision(movedGate.Id, GateAction.Answer, null, At(30), "late")));

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }
}
