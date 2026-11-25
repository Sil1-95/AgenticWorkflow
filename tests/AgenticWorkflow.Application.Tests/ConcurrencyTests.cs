namespace AgenticWorkflow.Application.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task A_stale_writer_gets_a_provider_neutral_conflict_and_nothing_wrong_is_appended()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);

        // A and B both loaded version 1.
        var writerA = await service.LoadAsync(Fixture.TaskOne);
        var writerB = await service.LoadAsync(Fixture.TaskOne);
        Assert.Equal(created.Version, writerA.Version);
        Assert.Equal(created.Version, writerB.Version);

        await service.ChangeWorkflowStateAsync(writerA, WorkflowState.Blocked, "A's decision.");

        var conflict = await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.ChangeWorkflowStateAsync(writerB, WorkflowState.AwaitingHuman, "B's stale decision."));

        Assert.Equal(Fixture.TaskOne, conflict.TaskId);
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);

        var history = store.History(Fixture.TaskOne);
        Assert.Equal(2, history.Count);
        Assert.Equal(WorkflowState.Blocked, (await service.LoadAsync(Fixture.TaskOne)).Task.WorkflowState);
    }

    [Fact]
    public async Task A_stale_basis_is_refused_before_the_command_runs()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "A.");
        var appendsBefore = store.AppendCalls;
        var executions = 0;

        await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.MutateAsync(created.Snapshot, (stream, at) =>
            {
                executions++;
                stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "B.", at);
            }, CancellationToken.None));

        Assert.Equal(0, executions);
        Assert.Equal(appendsBefore, store.AppendCalls);
    }

    [Fact]
    public async Task A_conflict_detected_at_append_time_is_not_retried_and_the_command_runs_exactly_once()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        var executions = 0;

        // Another writer wins the race after this call has loaded version 1 but before it appends.
        store.BeforeNextAppend = () => store.Seed(
            Fixture.TaskOne,
            [new EventEnvelope(
                new EventId(Guid.NewGuid()),
                Fixture.TaskOne,
                2,
                Fixture.T0.AddHours(1),
                new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Blocked, "Competing writer."))]);

        var conflict = await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.MutateAsync(created.Snapshot, (stream, at) =>
            {
                executions++;
                stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "My decision.", at);
            }, CancellationToken.None));

        Assert.Equal(1, executions);
        Assert.Equal(1, store.AppendCalls - 1 /* the creation */);
        Assert.Equal(1, conflict.ExpectedVersion);
        Assert.Equal(2, conflict.ActualVersion);
        Assert.IsType<StreamVersionConflictException>(conflict.InnerException);

        var history = store.History(Fixture.TaskOne);
        Assert.Equal(2, history.Count);
        Assert.Equal("Competing writer.", ((WorkflowStateChanged)history[1].Payload).Reason);
    }

    [Fact]
    public async Task After_a_conflict_the_caller_can_reload_and_re_decide_on_fresh_state()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "A.");
        await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.AwaitingHuman, "B."));

        var fresh = await service.LoadAsync(Fixture.TaskOne);
        var change = await service.ChangeWorkflowStateAsync(fresh, WorkflowState.Running, "B re-decided.");

        Assert.Equal(3, change.Version);
        Assert.Equal(WorkflowState.Running, change.Task.WorkflowState);
    }

    [Fact]
    public async Task Creation_race_is_reported_as_task_already_exists_not_as_a_stale_decision()
    {
        var store = new FakeTaskEventStore();
        var winner = Fixture.NewService(store);
        var loser = Fixture.NewService(store, new SequentialIdSource());
        store.BeforeNextAppend = () =>
            store.Seed(Fixture.TaskOne, TaskStream.Create(Fixture.TaskOne, "Other", CompletionBoundary.LocalCommit, Fixture.T0, new SequentialIdSource()).Events.ToList());

        var error = await Assert.ThrowsAsync<TaskAlreadyExistsException>(() => Fixture.CreateAsync(loser));

        Assert.Equal(1, error.ActualVersion);
        Assert.Equal("Other", (await winner.LoadAsync(Fixture.TaskOne)).Task.Intent);
    }
}
