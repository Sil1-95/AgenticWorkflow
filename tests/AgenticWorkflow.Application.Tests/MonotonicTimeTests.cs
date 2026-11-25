namespace AgenticWorkflow.Application.Tests;

/// <summary>A wall clock regression is not invalid workflow history; event time never decreases.</summary>
public sealed class MonotonicTimeTests
{
    private sealed class SettableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private static async Task<(WorkflowTaskService Service, SettableClock Clock, TaskChange Created, FakeTaskEventStore Store)> StartAsync()
    {
        var store = new FakeTaskEventStore();
        var clock = new SettableClock { UtcNow = Fixture.T0 };
        var service = Fixture.NewService(store, clock: clock);
        var created = await Fixture.CreateAsync(service);
        return (service, clock, created, store);
    }

    [Fact]
    public async Task A_clock_later_than_the_last_event_is_used()
    {
        var (service, clock, created, _) = await StartAsync();
        clock.UtcNow = Fixture.T0.AddMinutes(5);

        var change = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        Assert.Equal(Fixture.T0.AddMinutes(5), change.NewEvents[0].Timestamp);
        Assert.Equal(Fixture.T0.AddMinutes(5), change.Task.LastEventAt);
    }

    [Fact]
    public async Task A_clock_equal_to_the_last_event_is_allowed_without_adjustment()
    {
        var (service, _, created, _) = await StartAsync();

        var change = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        Assert.Equal(Fixture.T0, change.NewEvents[0].Timestamp);
    }

    [Fact]
    public async Task A_clock_earlier_than_the_last_event_uses_the_last_event_instant()
    {
        var (service, clock, created, _) = await StartAsync();
        clock.UtcNow = Fixture.T0.AddMinutes(10);
        var second = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        clock.UtcNow = Fixture.T0.AddMinutes(-30);
        var third = await service.ChangeWorkflowStateAsync(second.Snapshot, WorkflowState.Running, "Unblocked.");

        Assert.Equal(Fixture.T0.AddMinutes(10), third.NewEvents[0].Timestamp);
    }

    [Fact]
    public async Task Comparison_is_by_instant_not_by_offset()
    {
        var (service, clock, created, _) = await StartAsync();
        clock.UtcNow = Fixture.T0.AddMinutes(10);
        var second = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        // Same instant, expressed in another offset: equal, so the clock reading is used as is.
        clock.UtcNow = Fixture.T0.AddMinutes(10).ToOffset(TimeSpan.FromHours(5));
        var third = await service.ChangeWorkflowStateAsync(second.Snapshot, WorkflowState.Running, "Unblocked.");

        Assert.Equal(Fixture.T0.AddMinutes(10), third.NewEvents[0].Timestamp);
    }

    [Fact]
    public async Task The_behavior_survives_reload_with_a_new_service_instance_and_an_earlier_clock()
    {
        var (service, clock, created, store) = await StartAsync();
        clock.UtcNow = Fixture.T0.AddMinutes(10);
        await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting.");

        // A new "process" whose wall clock is earlier than the persisted final event.
        var restarted = Fixture.NewService(store, new SequentialIdSource(), new SettableClock { UtcNow = Fixture.T0.AddMinutes(-60) });
        var loaded = await restarted.LoadAsync(Fixture.TaskOne);
        var change = await restarted.ChangeWorkflowStateAsync(loaded, WorkflowState.Running, "Unblocked.");

        Assert.Equal(Fixture.T0.AddMinutes(10), change.NewEvents[0].Timestamp);
        var replayed = TaskStream.Replay(store.History(Fixture.TaskOne), new SequentialIdSource()).State;
        Assert.Equal(change.Task, replayed);
    }

    [Fact]
    public async Task Creation_uses_the_current_clock_normally()
    {
        var store = new FakeTaskEventStore();
        var created = await Fixture.NewService(store, clock: new SettableClock { UtcNow = Fixture.T0.AddHours(3) })
            .CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.WorkingTree);

        Assert.Equal(Fixture.T0.AddHours(3), created.NewEvents[0].Timestamp);
    }
}
