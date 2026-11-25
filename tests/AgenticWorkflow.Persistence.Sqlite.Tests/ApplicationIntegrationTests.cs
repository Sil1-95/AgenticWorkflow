namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// Application -> ITaskEventStore -> SqliteEventStore, against a real temp-file database. These prove the port
/// semantics hold end to end; the adapter's own behavior is covered by the rest of this project.
/// </summary>
public sealed class ApplicationIntegrationTests
{
    private static WorkflowTaskService NewService(ITaskEventStore store, int startMinutes = 0)
        => new(store, new GuidIdSource(), new StepClock(Fixture.At(startMinutes)));

    [Fact]
    public void The_sqlite_store_implements_the_application_port()
        => Assert.True(typeof(ITaskEventStore).IsAssignableFrom(typeof(SqliteEventStore)));

    [Fact]
    public async Task Create_load_mutate_close_reopen_and_continue_reconstructs_the_same_state()
    {
        using var db = new TempDatabase();

        TaskChange change;
        {
            var service = NewService(await db.OpenStoreAsync());
            change = await service.CreateAsync(Fixture.TaskOne, "Add retry to the importer", CompletionBoundary.LocalCommit);
            change = await service.BindRoleAsync(change.Snapshot, Fixture.Role("implementer", "code_edit"), Fixture.Profile("p-impl", null, "code_edit"));
            change = await service.CreateSemanticRevisionAsync(change.Snapshot, SemanticChangeKind.RequirementChange, "Also retry on 429.");
            change = await service.RecordImplementationRevisionAsync(change.Snapshot, new ImplementationFingerprint("tree:abc"));
            change = await service.ChangeWorkflowStateAsync(change.Snapshot, WorkflowState.Blocked, "Waiting on CI.");
            Assert.Equal(5, change.Version);
        }

        // "Close": no store instance holds anything. "Reopen": a brand-new instance on the same file.
        // (Core rejects a timestamp earlier than the last stored event, so the new clock must start later.)
        var reopened = NewService(await db.OpenStoreAsync(), startMinutes: 60);
        var loaded = await reopened.LoadAsync(Fixture.TaskOne);
        Assert.Equal(change.Task, loaded.Task);
        Assert.Equal(5, loaded.Version);

        var continued = await reopened.ChangeWorkflowStateAsync(loaded, WorkflowState.Running, "CI is green.");
        Assert.Equal(6, continued.Version);
        Assert.Equal(WorkflowState.Running, continued.Task.WorkflowState);

        var finalStore = await db.OpenStoreAsync();
        var history = await finalStore.LoadAsync(Fixture.TaskOne);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), history.Select(e => e.Sequence));
        Assert.Equal(continued.Task, TaskStream.Replay(history, new GuidIdSource()).State);
        Assert.Equal(6, await finalStore.GetVersionAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_new_process_with_an_earlier_wall_clock_still_continues_the_persisted_task()
    {
        using var db = new TempDatabase();
        var first = NewService(await db.OpenStoreAsync(), startMinutes: 120);
        var created = await first.CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.WorkingTree);
        var last = await first.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        // Brand-new store and service whose clock is hours behind the stored history.
        var restarted = NewService(await db.OpenStoreAsync(), startMinutes: 0);
        var resumed = await restarted.ChangeWorkflowStateAsync(
            await restarted.LoadAsync(Fixture.TaskOne), WorkflowState.Running, "Unblocked.");

        Assert.Equal(last.Task.LastEventAt, resumed.NewEvents[0].Timestamp);
        var reloaded = await NewService(await db.OpenStoreAsync()).LoadAsync(Fixture.TaskOne);
        Assert.Equal(resumed.Task, reloaded.Task);
    }

    [Fact]
    public async Task Creating_a_task_that_already_exists_fails_explicitly()
    {
        using var db = new TempDatabase();
        var store = await db.OpenStoreAsync();
        var service = NewService(store);
        await service.CreateAsync(Fixture.TaskOne, "first", CompletionBoundary.LocalCommit);

        var error = await Assert.ThrowsAsync<TaskAlreadyExistsException>(
            () => service.CreateAsync(Fixture.TaskOne, "second", CompletionBoundary.LocalCommit));

        Assert.Equal(1, error.ActualVersion);
        Assert.Equal("first", (await service.LoadAsync(Fixture.TaskOne)).Task.Intent);
        Assert.Equal(1, await store.GetVersionAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_missing_task_is_not_found()
    {
        using var db = new TempDatabase();
        var service = NewService(await db.OpenStoreAsync());

        Assert.Null(await service.FindAsync(Fixture.TaskOne));
        await Assert.ThrowsAsync<TaskNotFoundException>(() => service.LoadAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_stale_writer_gets_the_application_conflict_from_a_real_sqlite_stream()
    {
        using var db = new TempDatabase();
        var store = await db.OpenStoreAsync();
        var service = NewService(store);
        await service.CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.LocalCommit);
        var writerA = await service.LoadAsync(Fixture.TaskOne);
        var writerB = await service.LoadAsync(Fixture.TaskOne);

        await service.ChangeWorkflowStateAsync(writerA, WorkflowState.Blocked, "A's decision.");
        var conflict = await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.ChangeWorkflowStateAsync(writerB, WorkflowState.AwaitingHuman, "B's stale decision."));

        Assert.Equal(Fixture.TaskOne, conflict.TaskId);
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);
        Assert.Equal(2, await store.GetVersionAsync(Fixture.TaskOne));
        Assert.Equal(WorkflowState.Blocked, (await service.LoadAsync(Fixture.TaskOne)).Task.WorkflowState);
    }

    [Fact]
    public async Task A_write_that_loses_the_race_inside_the_call_is_translated_from_the_real_sqlite_conflict()
    {
        using var db = new TempDatabase();
        var store = await db.OpenStoreAsync();
        var racing = new RaceAfterLoadStore(store);
        var service = NewService(racing);
        var competitor = NewService(store);
        var created = await service.CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.LocalCommit);

        // Between this call's load and its append, another writer moves the real stream to version 2.
        racing.AfterNextLoad = async () => await competitor.ChangeWorkflowStateAsync(
            await competitor.LoadAsync(Fixture.TaskOne), WorkflowState.Blocked, "Competing writer.");

        var conflict = await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.AwaitingHuman, "My decision."));

        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);
        Assert.IsType<StreamVersionConflictException>(conflict.InnerException);

        var history = await store.LoadAsync(Fixture.TaskOne);
        Assert.Equal(2, history.Count);
        Assert.Equal("Competing writer.", ((WorkflowStateChanged)history[1].Payload).Reason);
    }

    [Fact]
    public async Task A_terminal_task_stays_closed_and_nothing_is_appended()
    {
        using var db = new TempDatabase();
        var store = await db.OpenStoreAsync();
        var service = NewService(store);
        var created = await service.CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.LocalCommit);
        var closed = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Cancelled, "Not needed.");

        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.ChangeWorkflowStateAsync(closed.Snapshot, WorkflowState.Running, "Reopen."));

        Assert.Equal(2, await store.GetVersionAsync(Fixture.TaskOne));
    }

    private sealed class StepClock : IClock
    {
        private DateTimeOffset _now;

        public StepClock(DateTimeOffset start) => _now = start;

        public DateTimeOffset UtcNow
        {
            get
            {
                var current = _now;
                _now = _now.AddMinutes(1);
                return current;
            }
        }
    }

    /// <summary>Wraps a real store and runs an action once, right after the next load, to simulate another writer.</summary>
    private sealed class RaceAfterLoadStore : ITaskEventStore
    {
        private readonly ITaskEventStore _inner;

        public RaceAfterLoadStore(ITaskEventStore inner) => _inner = inner;

        public Func<Task>? AfterNextLoad { get; set; }

        public async Task<IReadOnlyList<EventEnvelope>> LoadAsync(TaskId taskId, CancellationToken cancellationToken = default)
        {
            var history = await _inner.LoadAsync(taskId, cancellationToken);

            if (AfterNextLoad is { } race)
            {
                AfterNextLoad = null;
                await race();
            }

            return history;
        }

        public Task<long> AppendAsync(
            TaskId taskId,
            long expectedVersion,
            IReadOnlyList<EventEnvelope> events,
            CancellationToken cancellationToken = default)
            => _inner.AppendAsync(taskId, expectedVersion, events, cancellationToken);
    }
}
