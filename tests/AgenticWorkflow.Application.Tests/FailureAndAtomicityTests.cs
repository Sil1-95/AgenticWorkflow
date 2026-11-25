using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

public sealed class FailureAndAtomicityTests
{
    [Theory]
    [InlineData(WorkflowState.Completed)]
    [InlineData(WorkflowState.Cancelled)]
    [InlineData(WorkflowState.Failed)]
    public async Task A_terminal_task_rejects_every_further_mutation_through_core_and_appends_nothing(WorkflowState terminal)
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        // COMPLETED is only reachable through an accepted gate (here: a task that requires acceptance), which the working-tree boundary alone supports
        var created = terminal == WorkflowState.Completed
            ? await service.CreateAsync(Fixture.TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, AcceptanceRequirement.HumanRequired)
            : await Fixture.CreateAsync(service);
        var closed = terminal == WorkflowState.Completed
            ? await AcceptAsync(service, created.Snapshot)
            : await service.ChangeWorkflowStateAsync(created.Snapshot, terminal, "Done.");
        var appendsBefore = store.AppendCalls;
        var historyBefore = store.History(Fixture.TaskOne);

        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.ChangeWorkflowStateAsync(closed.Snapshot, WorkflowState.Running, "Reopen."));
        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.CreateSemanticRevisionAsync(closed.Snapshot, SemanticChangeKind.RequirementChange, "More."));
        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.RecordImplementationRevisionAsync(closed.Snapshot, new ImplementationFingerprint("tree:x")));
        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.BindRoleAsync(closed.Snapshot, Fixture.Role("implementer"), Fixture.Profile("p")));
        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.ChangeAuthorityEnvelopeAsync(closed.Snapshot, Fixture.Envelope(["a"], []), "x"));
        await Assert.ThrowsAsync<TaskTerminatedException>(
            () => service.RecordPolicyDecisionAsync(closed.Snapshot, new PolicyRuleId("r"), PolicyEffect.Notify, Fixture.Trigger(), "x"));

        Assert.Equal(appendsBefore, store.AppendCalls);
        Assert.Equal(historyBefore, store.History(Fixture.TaskOne));
        Assert.True((await service.LoadAsync(Fixture.TaskOne)).Task.IsTerminal);
    }

    [Fact]
    public async Task A_command_rejected_by_core_appends_nothing_and_preserves_history()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        var appendsBefore = store.AppendCalls;
        var historyBefore = store.History(Fixture.TaskOne);

        // Running -> Running is not a transition.
        await Assert.ThrowsAsync<InvalidWorkflowTransitionException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Running, "No-op."));
        // The profile lacks the role's required capability.
        await Assert.ThrowsAsync<RoleBindingException>(
            () => service.BindRoleAsync(created.Snapshot, Fixture.Role("implementer", "code_edit"), Fixture.Profile("p")));

        Assert.Equal(appendsBefore, store.AppendCalls);
        Assert.Equal(historyBefore, store.History(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_command_that_adds_events_and_then_fails_appends_none_of_them()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        var appendsBefore = store.AppendCalls;

        await Assert.ThrowsAsync<InvalidWorkflowTransitionException>(
            () => service.MutateAsync(created.Snapshot, (stream, at) =>
            {
                stream.ChangeWorkflowState(WorkflowState.Blocked, "First part works.", at);
                stream.ChangeWorkflowState(WorkflowState.Blocked, "Second part is rejected.", at);
            }, CancellationToken.None));

        Assert.Equal(appendsBefore, store.AppendCalls);
        Assert.Single(store.History(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_store_failure_during_append_is_not_reported_as_success_and_the_stored_history_stays_authoritative()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        store.FailNextAppend = new IOException("disk on fire");

        var error = await Assert.ThrowsAsync<IOException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "Lost."));

        Assert.Equal("disk on fire", error.Message);
        Assert.Single(store.History(Fixture.TaskOne));

        // Nothing was remembered from the failed attempt: the task is still at version 1 and the same basis works.
        var reloaded = await service.LoadAsync(Fixture.TaskOne);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(WorkflowState.Running, reloaded.Task.WorkflowState);

        var retried = await service.ChangeWorkflowStateAsync(reloaded, WorkflowState.Blocked, "Second attempt.");
        Assert.Equal(2, retried.Version);
    }

    [Fact]
    public async Task A_store_failure_during_creation_is_not_reported_as_success()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        store.FailNextAppend = new IOException("disk on fire");

        await Assert.ThrowsAsync<IOException>(() => Fixture.CreateAsync(service));

        Assert.Null(await service.FindAsync(Fixture.TaskOne));
    }

    [Fact]
    public async Task A_store_that_reports_the_wrong_version_is_not_trusted()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        store.NextReportedVersion = 99;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "x"));
    }

    [Fact]
    public async Task A_token_cancelled_up_front_stops_before_anything_is_loaded_or_appended()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "x", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateAsync(Fixture.TaskTwo, "intent", CompletionBoundary.LocalCommit, cancellationToken: cts.Token));

        Assert.Single(store.History(Fixture.TaskOne));
        Assert.Empty(store.History(Fixture.TaskTwo));
    }

    [Fact]
    public async Task Cancellation_during_the_append_propagates_and_is_never_reported_as_success()
    {
        var store = new FakeTaskEventStore();
        var service = Fixture.NewService(store);
        var created = await Fixture.CreateAsync(service);
        using var cts = new CancellationTokenSource();
        store.BeforeNextAppend = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ChangeWorkflowStateAsync(created.Snapshot, WorkflowState.Blocked, "x", cts.Token));

        Assert.Single(store.History(Fixture.TaskOne));
        Assert.Equal(1, (await service.LoadAsync(Fixture.TaskOne)).Version);
    }

    /// <summary>The only way to COMPLETED since M4A: verified implementation, open ACCEPT gate, explicit accept.</summary>
    private static async Task<TaskChange> AcceptAsync(WorkflowTaskService service, TaskSnapshot basis)
    {
        var revised = await service.RecordImplementationRevisionAsync(basis, new ImplementationFingerprint("tree:accepted"));
        var verified = await service.RecordLightweightVerificationAsync(
            revised.Snapshot,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)]);
        var gated = await service.OpenAcceptGateAsync(verified.Snapshot);

        return await service.AcceptGateAsync(gated.Snapshot, gated.Snapshot.Task.PendingGate!.Id);
    }
}
