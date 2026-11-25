using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

public sealed class ConcurrencyAndAtomicityTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_stale_writer_is_rejected_with_the_expected_and_actual_versions()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());

        // Two writers both read version 1 and each prepare their own event 2.
        var writerA = await LoadAndReplayAsync(store, TaskOne);
        var writerB = await LoadAndReplayAsync(store, TaskOne);
        writerA.ChangeWorkflowState(WorkflowState.Blocked, "A wins", At(1));
        writerB.ChangeWorkflowState(WorkflowState.AwaitingHuman, "B is late", At(1));

        await store.AppendAsync(TaskOne, 1, NewEvents(writerA, 1));
        var conflict = await Assert.ThrowsAsync<StreamVersionConflictException>(
            () => store.AppendAsync(TaskOne, 1, NewEvents(writerB, 1)));

        Assert.Equal(TaskOne, conflict.TaskId);
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);

        var stored = await LoadAndReplayAsync(store, TaskOne);
        Assert.Equal(WorkflowState.Blocked, stored.State.WorkflowState);
        Assert.Equal("A wins", stored.State.StateReason);
        Assert.Equal(2, stored.Events.Count);
    }

    [Fact]
    public async Task Creating_a_stream_twice_conflicts_the_second_time()
    {
        var store = await _db.OpenStoreAsync();
        var first = NewTask();
        var second = NewTask(intent: "A different creation of the same task id");
        await store.AppendAsync(TaskOne, 0, first.Events);

        var conflict = await Assert.ThrowsAsync<StreamVersionConflictException>(
            () => store.AppendAsync(TaskOne, 0, second.Events));

        Assert.Equal(0, conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);
        Assert.Equal(first.Events, await store.LoadAsync(TaskOne));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task An_append_with_the_wrong_expected_version_is_a_conflict_and_writes_nothing(long wrongVersion)
    {
        var store = await _db.OpenStoreAsync();
        var stream = NewTask();
        await PersistAsync(store, stream);
        stream.ChangeWorkflowState(WorkflowState.Blocked, "x", At(1));
        var next = NewEvents(stream, 1);
        var reNumbered = next.Select((e, i) => Retarget(e, sequence: wrongVersion + 1 + i)).ToList();

        var conflict = await Assert.ThrowsAsync<StreamVersionConflictException>(
            () => store.AppendAsync(TaskOne, wrongVersion, reNumbered));

        Assert.Equal(wrongVersion, conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);
        Assert.Equal(1, await store.GetVersionAsync(TaskOne));
    }

    [Fact]
    public async Task Exactly_one_of_many_simultaneous_writers_wins_the_same_version()
    {
        const int Writers = 8;
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());

        // Every writer has its own store instance (its own connections) and a prepared, different event 2.
        var prepared = new List<(SqliteEventStore Store, IReadOnlyList<EventEnvelope> Events)>();
        for (var i = 0; i < Writers; i++)
        {
            var writerStore = await _db.OpenStoreAsync();
            var stream = await LoadAndReplayAsync(writerStore, TaskOne);
            stream.ChangeWorkflowState(WorkflowState.Blocked, $"writer {i}", At(1));
            prepared.Add((writerStore, NewEvents(stream, 1)));
        }

        using var gate = new ManualResetEventSlim();
        var attempts = prepared
            .Select(p => Task.Run(async () =>
            {
                gate.Wait();
                try
                {
                    return (Version: await p.Store.AppendAsync(TaskOne, 1, p.Events), Error: (Exception?)null);
                }
                catch (Exception e)
                {
                    return (Version: -1L, Error: e);
                }
            }))
            .ToList();
        gate.Set();
        var results = await Task.WhenAll(attempts);

        var winners = results.Where(r => r.Error is null).ToList();
        var losers = results.Where(r => r.Error is not null).ToList();

        Assert.Single(winners);
        Assert.Equal(2, winners[0].Version);
        Assert.Equal(Writers - 1, losers.Count);
        Assert.All(losers, l =>
        {
            var conflict = Assert.IsType<StreamVersionConflictException>(l.Error);
            Assert.Equal(1, conflict.ExpectedVersion);
            Assert.Equal(2, conflict.ActualVersion);
        });

        var stored = await LoadAndReplayAsync(store, TaskOne);
        Assert.Equal(2, stored.Events.Count);
        Assert.StartsWith("writer ", stored.State.StateReason);
    }

    [Fact]
    public async Task Writers_that_retry_after_conflicts_produce_one_gapless_valid_history()
    {
        const int Writers = 4;
        const int AppendsEach = 3;
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());

        using var gate = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            var writerStore = await _db.OpenStoreAsync();
            gate.Wait();
            for (var n = 0; n < AppendsEach; n++)
            {
                while (true)
                {
                    var history = await writerStore.LoadAsync(TaskOne);
                    var stream = TaskStream.Replay(history, new GuidIdSource());
                    stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, $"writer {w} change {n}", At(1 + history.Count));

                    try
                    {
                        await writerStore.AppendAsync(TaskOne, history.Count, NewEvents(stream, history.Count));
                        break;
                    }
                    catch (StreamVersionConflictException)
                    {
                        // Lost the race: reload, rebuild on the winner's history, try again.
                    }
                }
            }
        })).ToList();
        gate.Set();
        await Task.WhenAll(writers);

        var finalState = (await LoadAndReplayAsync(store, TaskOne)).State;

        Assert.Equal(1 + (Writers * AppendsEach), finalState.LastSequence);
        Assert.Equal(1 + (Writers * AppendsEach), finalState.SemanticRevisions.Length);
        Assert.Equal(Enumerable.Range(1, (Writers * AppendsEach) + 1), finalState.SemanticRevisions.Select(r => r.Ordinal));
    }

    [Fact]
    public async Task A_batch_is_all_or_nothing_when_a_later_event_violates_a_store_constraint()
    {
        var store = await _db.OpenStoreAsync();
        var existing = NewTask(TaskTwo);
        await PersistAsync(store, existing);

        var target = NewTask(TaskOne);
        await PersistAsync(store, target);
        target.ChangeWorkflowState(WorkflowState.Blocked, "ok", At(1));
        target.ChangeWorkflowState(WorkflowState.Running, "also ok", At(2));
        var batch = NewEvents(target, 1).ToList();

        // The batch's SECOND event reuses an event id that already exists in another stream: the first insert
        // succeeds inside the transaction, the second hits UNIQUE(event_id).
        batch[1] = Retarget(batch[1], id: existing.Events[0].Id);

        var rejected = await Assert.ThrowsAsync<AppendRejectedException>(() => store.AppendAsync(TaskOne, 1, batch));

        Assert.Contains("nothing was written", rejected.Message);
        Assert.Equal(1, await store.GetVersionAsync(TaskOne));
        Assert.Single(await store.LoadAsync(TaskOne));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM task_events WHERE sequence >= 2"));
        Assert.Single(await store.LoadAsync(TaskTwo));
    }

    [Fact]
    public async Task A_new_stream_batch_that_fails_leaves_no_stream_at_all()
    {
        var store = await _db.OpenStoreAsync();
        var other = NewTask(TaskTwo);
        await PersistAsync(store, other);

        var fresh = NewTask(TaskOne);
        fresh.ChangeWorkflowState(WorkflowState.Blocked, "second", At(1));
        var batch = fresh.Events.ToList();
        batch[1] = Retarget(batch[1], id: other.Events[0].Id);

        await Assert.ThrowsAsync<AppendRejectedException>(() => store.AppendAsync(TaskOne, 0, batch));

        Assert.Equal(0, await store.GetVersionAsync(TaskOne));
        Assert.Empty(await store.LoadAsync(TaskOne));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM task_streams"));
    }

    [Fact]
    public async Task Batches_that_break_the_append_contract_are_refused_before_anything_is_written()
    {
        var store = await _db.OpenStoreAsync();
        var stream = NewTask();
        stream.ChangeWorkflowState(WorkflowState.Blocked, "x", At(1));
        stream.ChangeWorkflowState(WorkflowState.Running, "y", At(2));
        var events = stream.Events;

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(TaskOne, 0, []));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(TaskTwo, 0, events));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(TaskOne, 0, [events[0], events[2]]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(TaskOne, 0, [events[1], events[2]]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(TaskOne, 0, [events[0], Retarget(events[1], id: events[0].Id)]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendAsync(TaskOne, -1, events));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(TaskOne, 0, null!));

        Assert.Equal(0, await store.GetVersionAsync(TaskOne));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM task_events"));
    }

    [Fact]
    public async Task A_cancelled_append_commits_nothing()
    {
        var store = await _db.OpenStoreAsync();
        var stream = RichCompletedTask();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AppendAsync(TaskOne, 0, stream.Events, cancelled.Token));

        Assert.Equal(0, await store.GetVersionAsync(TaskOne));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM task_events"));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM task_streams"));

        // The same batch is still appendable afterwards: cancellation left no lock or half-written state.
        await store.AppendAsync(TaskOne, 0, stream.Events);
        Assert.Equal(stream.Events.Count, await store.GetVersionAsync(TaskOne));
    }

    [Fact]
    public async Task A_cancelled_load_throws_and_changes_nothing()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(TaskOne, cancelled.Token));

        Assert.Equal(1, await store.GetVersionAsync(TaskOne));
    }
}
