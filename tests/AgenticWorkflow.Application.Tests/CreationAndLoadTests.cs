namespace AgenticWorkflow.Application.Tests;

public sealed class CreationAndLoadTests
{
    [Fact]
    public async Task Create_appends_TaskCreated_at_expected_version_zero_and_load_returns_equivalent_state()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);

        var created = await Fixture.CreateAsync(service);

        var stored = Assert.Single(store.History(Fixture.TaskOne));
        Assert.Equal(EventKinds.TaskCreated, stored.Kind);
        Assert.Equal(1, stored.Sequence);
        Assert.Equal(Fixture.T0, stored.Timestamp);
        Assert.Equal(1, created.Version);
        Assert.Same(stored, Assert.Single(created.NewEvents));
        Assert.True(created.HasChanges);
        Assert.Equal(WorkflowState.Running, created.Task.WorkflowState);

        var loaded = await service.LoadAsync(Fixture.TaskOne);
        Assert.Equal(created.Task, loaded.Task);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Create_uses_the_injected_clock_and_id_source()
    {
        var store = new FakeTaskEventStore();
        var at = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var service = Fixture.NewService(store, new SequentialIdSource(), new SteppingClock(at));

        var created = await Fixture.CreateAsync(service);

        Assert.Equal(at, created.Task.CreatedAt);
        Assert.Equal(new SemanticRevisionId(new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), created.Task.CurrentSemanticRevisionId);
    }

    [Fact]
    public async Task Creating_an_existing_task_fails_explicitly_and_leaves_history_untouched()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        await Fixture.CreateAsync(service);
        var before = store.History(Fixture.TaskOne);

        var error = await Assert.ThrowsAsync<TaskAlreadyExistsException>(() => Fixture.CreateAsync(service));

        Assert.Equal(Fixture.TaskOne, error.TaskId);
        Assert.Equal(1, error.ActualVersion);
        Assert.IsType<StreamVersionConflictException>(error.InnerException);
        Assert.Equal(before, store.History(Fixture.TaskOne));
        Assert.Single(store.AcceptedBatches);
    }

    [Fact]
    public async Task Create_does_not_call_load_it_relies_on_expected_version_zero()
    {
        var store = new FakeTaskEventStore();

        await Fixture.CreateAsync(Fixture.NewService(store));

        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(1, store.AppendCalls);
    }

    [Fact]
    public async Task A_task_that_does_not_exist_is_distinguishable_from_one_with_events()
    {
        var service = Fixture.NewService(new FakeTaskEventStore());

        Assert.Null(await service.FindAsync(Fixture.TaskOne));
        var error = await Assert.ThrowsAsync<TaskNotFoundException>(() => service.LoadAsync(Fixture.TaskOne));
        Assert.Equal(Fixture.TaskOne, error.TaskId);

        await Fixture.CreateAsync(service);

        Assert.NotNull(await service.FindAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task Tasks_are_isolated_from_each_other()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);

        await Fixture.CreateAsync(service, Fixture.TaskOne);
        var two = await Fixture.CreateAsync(service, Fixture.TaskTwo);

        Assert.Equal(Fixture.TaskTwo, two.Task.Id);
        Assert.Single(store.History(Fixture.TaskOne));
        Assert.Single(store.History(Fixture.TaskTwo));
    }

    [Fact]
    public async Task Load_rebuilds_state_with_replay_so_a_corrupt_history_is_rejected_by_core()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        await Fixture.CreateAsync(service);
        var first = store.History(Fixture.TaskOne)[0];

        // A second TaskCreated is not a valid continuation; Core's Replay must refuse it.
        store.Seed(Fixture.TaskOne, [new EventEnvelope(new EventId(Guid.NewGuid()), Fixture.TaskOne, 2, Fixture.T0, first.Payload)]);

        await Assert.ThrowsAsync<InvalidEventHistoryException>(() => service.LoadAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_store_returning_another_tasks_history_is_refused()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        await Fixture.CreateAsync(service, Fixture.TaskTwo);
        store.NextLoadResult = store.History(Fixture.TaskTwo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync(Fixture.TaskOne));
    }
}
