using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

public sealed class ReplayAndDurabilityTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Persist_close_reopen_load_replay_yields_the_identical_state()
    {
        var original = RichCompletedTask();

        // Writer scope: the store (and every connection it opened) is gone after this block.
        {
            var writer = await _db.OpenStoreAsync();
            var version = await writer.AppendAsync(TaskOne, 0, original.Events);
            Assert.Equal(original.Events.Count, version);
        }

        var reader = await _db.OpenStoreAsync();
        var history = await reader.LoadAsync(TaskOne);
        var replayed = TaskStream.Replay(history, new GuidIdSource());

        Assert.Equal(original.Events, history);
        Assert.Equal(original.State, replayed.State);
        Assert.Equal(original.Events.Count, replayed.State.LastSequence);
        Assert.Equal(original.Events.Count, await reader.GetVersionAsync(TaskOne));
    }

    [Fact]
    public async Task A_stream_can_be_extended_across_sessions_in_several_appends()
    {
        var stream = NewTask();
        var store = await _db.OpenStoreAsync();
        var version = await store.AppendAsync(TaskOne, 0, stream.Events);

        stream.ChangeWorkflowState(WorkflowState.Blocked, "waiting", At(1));
        version = await (await _db.OpenStoreAsync()).AppendAsync(TaskOne, version, NewEvents(stream, version));

        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "changed", At(2));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(3));
        version = await (await _db.OpenStoreAsync()).AppendAsync(TaskOne, version, NewEvents(stream, version));

        var replayed = await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne);

        Assert.Equal(4, version);
        Assert.Equal(stream.State, replayed.State);
        Assert.Equal(stream.Events, replayed.Events);
    }

    [Fact]
    public async Task Replayed_stream_keeps_accepting_valid_commands_that_persist_at_the_right_version()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());

        var replayed = await LoadAndReplayAsync(store, TaskOne);
        replayed.ChangeWorkflowState(WorkflowState.Blocked, "waiting", At(1));
        await store.AppendAsync(TaskOne, 1, NewEvents(replayed, 1));

        Assert.Equal(WorkflowState.Blocked, (await LoadAndReplayAsync(store, TaskOne)).State.WorkflowState);
    }

    [Fact]
    public async Task Independent_task_streams_do_not_mix()
    {
        var store = await _db.OpenStoreAsync();
        var one = RichCompletedTask(TaskOne);
        var two = NewTask(TaskTwo, intent: "Another task");
        two.ChangeWorkflowState(WorkflowState.Cancelled, "No longer needed.", At(1));

        await PersistAsync(store, one);
        await PersistAsync(store, two);

        var loadedOne = await store.LoadAsync(TaskOne);
        var loadedTwo = await store.LoadAsync(TaskTwo);

        Assert.Equal(one.Events, loadedOne);
        Assert.Equal(two.Events, loadedTwo);
        Assert.All(loadedOne, e => Assert.Equal(TaskOne, e.TaskId));
        Assert.All(loadedTwo, e => Assert.Equal(TaskTwo, e.TaskId));
        Assert.Equal(one.State, (await LoadAndReplayAsync(store, TaskOne)).State);
        Assert.Equal(two.State, (await LoadAndReplayAsync(store, TaskTwo)).State);
        Assert.Equal(2, _db.Scalar<int>("SELECT COUNT(*) FROM task_streams"));
    }

    [Fact]
    public async Task Interleaved_appends_to_different_tasks_keep_each_stream_ordered_and_independent()
    {
        var store = await _db.OpenStoreAsync();
        var one = NewTask(TaskOne);
        var two = NewTask(TaskTwo);
        await store.AppendAsync(TaskOne, 0, one.Events);
        await store.AppendAsync(TaskTwo, 0, two.Events);

        one.ChangeWorkflowState(WorkflowState.Blocked, "a", At(1));
        two.ChangeWorkflowState(WorkflowState.AwaitingHuman, "b", At(1));
        await store.AppendAsync(TaskTwo, 1, NewEvents(two, 1));
        await store.AppendAsync(TaskOne, 1, NewEvents(one, 1));

        Assert.Equal(one.State, (await LoadAndReplayAsync(store, TaskOne)).State);
        Assert.Equal(two.State, (await LoadAndReplayAsync(store, TaskTwo)).State);
    }

    [Fact]
    public async Task An_unknown_task_has_an_empty_history_and_version_zero()
    {
        var store = await _db.OpenStoreAsync();

        Assert.Empty(await store.LoadAsync(TaskTwo));
        Assert.Equal(0, await store.GetVersionAsync(TaskTwo));
    }

    [Fact]
    public async Task A_terminal_task_stays_closed_after_a_round_trip()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, RichCompletedTask());

        var replayed = await LoadAndReplayAsync(store, TaskOne);

        Assert.True(replayed.State.IsTerminal);
        Assert.Throws<TaskTerminatedException>(() => replayed.ChangeWorkflowState(WorkflowState.Running, "reopen", At(99)));
        Assert.Throws<TaskTerminatedException>(() => replayed.RecordImplementationRevision(new ImplementationFingerprint("late"), At(99)));
    }

    [Fact]
    public async Task A_forged_event_after_the_terminal_state_is_stored_faithfully_but_rejected_by_core_replay()
    {
        // The store does not judge domain validity; the kernel does. Storing is not accepting.
        var completed = RichCompletedTask();
        var other = NewTask(TaskTwo);
        other.ChangeWorkflowState(WorkflowState.Blocked, "x", At(100));
        var forged = new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, completed.Events.Count + 1, At(100), other.Events[1].Payload);

        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, completed);
        await store.AppendAsync(TaskOne, completed.Events.Count, [forged]);

        var history = await store.LoadAsync(TaskOne);

        Assert.Equal(forged, history[^1]);
        Assert.Throws<TaskTerminatedException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public async Task A_forged_implementation_event_with_an_unknown_semantic_revision_persists_but_fails_replay()
    {
        var stream = NewTask();
        var forgedRevision = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()),
            TaskOne,
            new SemanticRevisionId(Guid.NewGuid()),
            new ImplementationFingerprint("tree:forged"),
            null,
            At(1));
        var forged = new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new ImplementationRevisionRecorded(forgedRevision));

        var store = await _db.OpenStoreAsync();
        await store.AppendAsync(TaskOne, 0, [stream.Events[0], forged]);

        var history = await store.LoadAsync(TaskOne);

        Assert.Equal(forgedRevision, ((ImplementationRevisionRecorded)history[1].Payload).Revision);
        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public async Task Rows_have_no_global_ordering_column()
    {
        await _db.OpenStoreAsync();

        // WITHOUT ROWID tables: no insertion counter exists that could be mistaken for workflow ordering.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _db.Scalar<long>("SELECT rowid FROM task_events LIMIT 1"));
    }
}
