using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

public sealed class ReplayTests
{
    [Fact]
    public void Full_history_replays_to_an_equivalent_state()
    {
        var original = RichHistory();

        var replayed = TaskStream.Replay(original.Events, new SequentialIdSource());

        Assert.Equal(original.State, replayed.State);
        Assert.Equal(WorkflowState.AwaitingHuman, replayed.State.WorkflowState);
        Assert.Equal(2, replayed.State.SemanticRevisions.Length);
        Assert.Single(replayed.State.ImplementationRevisions);
        Assert.Single(replayed.State.RoleBindings);
        Assert.Single(replayed.State.PolicyDecisions);
        Assert.Equal(AuthorityClassification.ReservedForHuman, replayed.State.Authority.Classify(new ActionId("deployment")));
        Assert.Equal(original.Events.Count, replayed.Events.Count);
        Assert.Equal(original.Events, replayed.Events);
    }

    [Fact]
    public void Identical_inputs_and_id_sources_produce_identical_histories()
    {
        var first = RichHistory(new SequentialIdSource());
        var second = RichHistory(new SequentialIdSource());

        Assert.Equal(first.Events, second.Events);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Replay_of_a_prefix_yields_the_state_at_that_point()
    {
        var original = RichHistory();

        var atSequence3 = TaskStream.Replay(original.Events.Take(3), new SequentialIdSource());

        Assert.Equal(3, atSequence3.State.LastSequence);
        Assert.Equal(WorkflowState.Running, atSequence3.State.WorkflowState);
        Assert.Single(atSequence3.State.RoleBindings);
        Assert.Equal(AuthorityClassification.ReservedForHuman, atSequence3.State.Authority.Classify(new ActionId("deployment")));
        Assert.Single(atSequence3.State.SemanticRevisions);
    }

    [Fact]
    public void Replayed_stream_can_continue_and_matches_the_stream_that_continued_live()
    {
        var live = RichHistory();
        var replayed = TaskStream.Replay(live.Events, new SequentialIdSource());

        live.ChangeWorkflowState(WorkflowState.Running, "signed off", At(7));
        replayed.ChangeWorkflowState(WorkflowState.Running, "signed off", At(7));

        Assert.Equal(live.State, replayed.State);
        Assert.Equal(8, replayed.State.LastSequence);
    }

    [Fact]
    public void Envelopes_carry_id_task_gapless_sequence_timestamp_and_kind()
    {
        var stream = RichHistory();

        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), stream.Events.Select(e => e.Sequence));
        Assert.All(stream.Events, e => Assert.Equal(TaskOne, e.TaskId));
        Assert.Equal(stream.Events.Count, stream.Events.Select(e => e.Id).Distinct().Count());
        Assert.Equal(
            [
                EventKinds.TaskCreated, EventKinds.RoleBound, EventKinds.AuthorityEnvelopeChanged,
                EventKinds.SemanticRevisionCreated, EventKinds.ImplementationRevisionRecorded,
                EventKinds.PolicyDecisionRecorded, EventKinds.WorkflowStateChanged,
            ],
            stream.Events.Select(e => e.Kind));
        Assert.Equal(Enumerable.Range(0, 7).Select(At), stream.Events.Select(e => e.Timestamp));
    }

    [Fact]
    public void History_is_append_only_from_the_outside()
    {
        var stream = NewTask();

        var view = Assert.IsAssignableFrom<IReadOnlyList<EventEnvelope>>(stream.Events);

        Assert.False(view is IList<EventEnvelope> { IsReadOnly: false });
        stream.ChangeWorkflowState(WorkflowState.Blocked, "x", At(1));
        Assert.Equal(2, view.Count);
    }

    // ---- invalid histories -------------------------------------------------------------------------------

    [Fact]
    public void Empty_history_is_rejected()
        => Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay([], new SequentialIdSource()));

    [Fact]
    public void History_must_start_with_task_created()
    {
        var history = RichHistory().Events.Skip(1).Select(e => Retarget(e, sequence: e.Sequence - 1)).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void History_must_start_at_sequence_one()
    {
        var history = RichHistory().Events.Select(e => Retarget(e, sequence: e.Sequence + 1)).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Gap_in_sequence_is_rejected()
    {
        var events = RichHistory().Events;
        var history = events.Take(3).Concat(events.Skip(4)).ToList();

        var ex = Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
        Assert.Contains("Expected sequence 4", ex.Message);
    }

    [Fact]
    public void Duplicate_sequence_is_rejected()
    {
        var events = RichHistory().Events;
        var history = events.Take(3).Append(Retarget(events[3], sequence: 3, id: new EventId(Guid.NewGuid()))).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replayed_or_out_of_order_events_are_rejected()
    {
        var events = RichHistory().Events;

        Assert.Throws<InvalidEventHistoryException>(
            () => TaskStream.Replay(events.Take(3).Append(events[2]), new SequentialIdSource()));
        Assert.Throws<InvalidEventHistoryException>(
            () => TaskStream.Replay([events[0], events[2], events[1]], new SequentialIdSource()));
    }

    [Fact]
    public void Duplicate_event_id_at_a_valid_sequence_is_rejected()
    {
        var events = RichHistory().Events;
        var history = events.Take(2).Append(Retarget(events[2], id: events[1].Id)).ToList();

        var ex = Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Event_from_another_task_is_rejected()
    {
        var events = RichHistory().Events;
        var history = events.Take(2).Append(Retarget(events[2], task: TaskTwo)).ToList();

        var ex = Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
        Assert.Contains(TaskTwo.ToString(), ex.Message);
    }

    [Fact]
    public void Interleaving_two_tasks_is_rejected()
    {
        var a = RichHistory(new SequentialIdSource());
        var b = NewTask(new SequentialIdSource(), TaskTwo);
        var mixed = new[] { a.Events[0], b.Events[0] };

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(mixed, new SequentialIdSource()));
    }

    [Fact]
    public void Timestamps_may_not_go_backwards()
    {
        var events = RichHistory().Events;
        var history = events.Take(3).Append(Retarget(events[3], at: At(0))).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Task_created_is_only_valid_as_the_first_event()
    {
        var events = NewTask().Events;
        var history = new[] { events[0], Retarget(events[0], sequence: 2, id: new EventId(Guid.NewGuid())) };

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Transition_whose_from_state_disagrees_with_history_is_rejected()
    {
        var history = NewTask().Events.ToList();
        history.Add(new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, 2, At(1),
            new WorkflowStateChanged(WorkflowState.Blocked, WorkflowState.Running, "forged")));

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Theory]
    [InlineData(WorkflowState.Completed)]
    [InlineData(WorkflowState.Cancelled)]
    [InlineData(WorkflowState.Failed)]
    public void Terminal_state_invariant_survives_replay(WorkflowState terminal)
    {
        var stream = AsLegacy(RichHistory());   // structure test: a plain COMPLETED is valid only in a legacy history
        stream.ChangeWorkflowState(terminal, "finished", At(7));

        // The legitimate history replays...
        var replayed = TaskStream.Replay(stream.Events, new SequentialIdSource());
        Assert.Equal(terminal, replayed.State.WorkflowState);
        Assert.True(replayed.State.IsTerminal);

        // ...but a forged tail event after the terminal one does not, whatever its kind.
        var forgedTransition = new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, 9, At(8),
            new WorkflowStateChanged(terminal, WorkflowState.Running, "resurrected"));
        var forgedBinding = new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, 9, At(8),
            new RoleBound(new RoleBinding(Role("late"), Profile("p"), replayed.State.CurrentSemanticRevisionId)));

        Assert.Throws<TaskTerminatedException>(
            () => TaskStream.Replay(stream.Events.Append(forgedTransition), new SequentialIdSource()));
        Assert.Throws<TaskTerminatedException>(
            () => TaskStream.Replay(stream.Events.Append(forgedBinding), new SequentialIdSource()));
    }

    [Fact]
    public void State_equality_detects_real_differences()
    {
        var a = RichHistory();
        var b = TaskStream.Replay(a.Events, new SequentialIdSource());
        Assert.Equal(a.State, b.State);

        b.ChangeAuthority(Envelope([], []), "different", At(7));
        Assert.NotEqual(a.State, b.State);
    }
}
