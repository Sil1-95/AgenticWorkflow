using System.Reflection;

namespace AgenticWorkflow.Application.Tests;

public sealed class MutationTests
{
    [Fact]
    public async Task A_mutation_appends_only_the_new_event_and_returns_the_current_state_and_version()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);

        var change = await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting on CI.");

        Assert.Equal(WorkflowState.Blocked, change.Task.WorkflowState);
        Assert.Equal(2, change.Version);
        var appended = Assert.Single(change.NewEvents);
        Assert.Equal(EventKinds.WorkflowStateChanged, appended.Kind);
        Assert.Equal(2, appended.Sequence);

        Assert.Equal(2, store.AcceptedBatches.Count);
        Assert.Same(appended, Assert.Single(store.AcceptedBatches[1]));
        Assert.Equal(2, store.History(Fixture.TaskOne).Count);
        Assert.Equal(change.Task, (await service.LoadAsync(Fixture.TaskOne)).Task);
    }

    [Fact]
    public async Task Every_mutation_reloads_and_replays_instead_of_trusting_the_callers_snapshot()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        var loadsBefore = store.LoadCalls;

        await service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Waiting on CI.");

        Assert.Equal(loadsBefore + 1, store.LoadCalls);
    }

    [Fact]
    public async Task Sequential_mutations_increment_the_version_and_stay_replayable()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);

        var final = await Fixture.RunStandardSequenceAsync(service);

        Assert.Equal(7, final.Version);
        var history = store.History(Fixture.TaskOne);
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), history.Select(e => e.Sequence));
        Assert.Equal(
            [
                EventKinds.TaskCreated,
                EventKinds.RoleBound,
                EventKinds.AuthorityEnvelopeChanged,
                EventKinds.SemanticRevisionCreated,
                EventKinds.ImplementationRevisionRecorded,
                EventKinds.PolicyDecisionRecorded,
                EventKinds.WorkflowStateChanged,
            ],
            history.Select(e => e.Kind));

        Assert.Equal(final.Task, TaskStream.Replay(history, new SequentialIdSource()).State);
        Assert.Equal(final.Task, (await service.LoadAsync(Fixture.TaskOne)).Task);
    }

    [Fact]
    public async Task The_same_calls_with_the_same_ids_and_clock_produce_an_identical_history()
    {
        var firstStore = new FakeTaskEventStore();
        var secondStore = new FakeTaskEventStore();

        var first = await Fixture.RunStandardSequenceAsync(Fixture.NewService(firstStore));
        var second = await Fixture.RunStandardSequenceAsync(Fixture.NewService(secondStore));

        Assert.Equal(first.Task, second.Task);
        Assert.Equal(
            firstStore.History(Fixture.TaskOne).Select(e => (e.Id, e.Sequence, e.Timestamp, e.Kind)),
            secondStore.History(Fixture.TaskOne).Select(e => (e.Id, e.Sequence, e.Timestamp, e.Kind)));
    }

    [Fact]
    public async Task Events_are_timestamped_by_the_injected_clock_not_the_wall_clock()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);

        await Fixture.RunStandardSequenceAsync(service);

        Assert.Equal(
            Enumerable.Range(0, 7).Select(i => Fixture.T0.AddMinutes(i)),
            store.History(Fixture.TaskOne).Select(e => e.Timestamp));
    }

    [Fact]
    public async Task Implementation_revisions_record_the_semantic_revision_they_target_and_nothing_is_created_implicitly()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var change = await Fixture.CreateAsync(service);
        change = await service.RecordImplementationRevisionAsync(change.Snapshot, new ImplementationFingerprint("tree:one"));
        var originalSemanticId = change.Task.CurrentSemanticRevisionId;

        change = await service.CreateSemanticRevisionAsync(change.Snapshot, SemanticChangeKind.RequirementChange, "Scope changed.");

        // M2A neither decides what a stale implementation means nor creates a new implementation revision.
        var implementation = change.Task.CurrentImplementationRevision!;
        Assert.Equal(originalSemanticId, implementation.SemanticRevisionId);
        Assert.NotEqual(change.Task.CurrentSemanticRevisionId, implementation.SemanticRevisionId);
        Assert.Single(change.NewEvents);
        Assert.Single(change.Task.ImplementationRevisions);
    }

    [Fact]
    public async Task Changing_authority_or_roles_does_not_create_a_semantic_revision()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var change = await Fixture.CreateAsync(service);

        change = await service.ChangeAuthorityEnvelopeAsync(change.Snapshot, Fixture.Envelope(["a"], ["b"]), "delegation");
        Assert.Equal(EventKinds.AuthorityEnvelopeChanged, Assert.Single(change.NewEvents).Kind);

        change = await service.BindRoleAsync(change.Snapshot, Fixture.Role("implementer"), Fixture.Profile("p"));
        Assert.Equal(EventKinds.RoleBound, Assert.Single(change.NewEvents).Kind);

        Assert.Single(change.Task.SemanticRevisions);
    }

    [Fact]
    public async Task A_mutation_that_produces_no_events_does_not_append_and_returns_the_unchanged_snapshot()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        var appendsBefore = store.AppendCalls;

        var change = await service.MutateAsync(created.Snapshot, (_, _) => { }, CancellationToken.None);

        Assert.Equal(appendsBefore, store.AppendCalls);
        Assert.False(change.HasChanges);
        Assert.Empty(change.NewEvents);
        Assert.Equal(1, change.Version);
        Assert.Equal(created.Task, change.Task);
        Assert.Single(store.History(Fixture.TaskOne));
    }

    [Fact]
    public async Task Mutating_a_task_that_has_no_history_fails_with_task_not_found()
    {
        var elsewhere = Fixture.NewService(new FakeTaskEventStore());
        var snapshot = (await Fixture.CreateAsync(elsewhere)).Snapshot;
        var service = Fixture.NewService(new FakeTaskEventStore());

        await Assert.ThrowsAsync<TaskNotFoundException>(
            () => service.ChangeWorkflowStateAsync(snapshot, WorkflowState.Blocked, "x"));
    }

    [Fact]
    public void The_public_surface_accepts_no_events_streams_or_delegates()
    {
        var forbidden = new[] { typeof(EventEnvelope), typeof(TaskStream), typeof(DomainEvent), typeof(Delegate) };

        var offenders = typeof(WorkflowTaskService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => (Method: m.Name, p.ParameterType)))
            .Where(x => forbidden.Any(f => f.IsAssignableFrom(x.ParameterType))
                        || x.ParameterType.IsGenericType
                        && x.ParameterType.GetGenericArguments().Any(g => forbidden.Any(f => f.IsAssignableFrom(g))))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_public_surface_exposes_no_mutable_task_stream()
    {
        var publicTypes = new[] { typeof(WorkflowTaskService), typeof(TaskSnapshot), typeof(TaskChange) };

        var offenders = publicTypes
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m is MethodInfo { ReturnType: var r } && r.ToString().Contains(nameof(TaskStream))
                        || m is PropertyInfo { PropertyType: var p } && p.ToString().Contains(nameof(TaskStream)))
            .ToList();

        Assert.Empty(offenders);
    }
}
