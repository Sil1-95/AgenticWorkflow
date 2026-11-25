using System.Text.RegularExpressions;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// the acceptance requirement is stored in the task's first event (<c>task_created</c> payload version 2), so replay never depends on configuration or
/// metadata. A version-1 row, written before the requirement existed, replays with the human-required semantics every such task had and is the only
/// history that may complete by a plain recorded state change.
/// </summary>
public sealed partial class AcceptancePersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [GeneratedRegex("""
        ,"acceptance":"[a-z_]+"
        """)]
    private static partial Regex AcceptanceMember();

    private void Damage(string sql, params (string Name, object Value)[] parameters)
        => Assert.True(_db.Execute(sql, parameters) > 0, "The damaging statement must change something, or the test proves nothing.");

    private async Task<SqliteEventStore> SeedAsync(TaskStream stream)
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private static TaskStream Verified(AcceptanceRequirement acceptance)
    {
        var stream = NewTask(acceptance: acceptance);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)],
            At(2));
        return stream;
    }

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired, "not_required")]
    [InlineData(AcceptanceRequirement.HumanRequired, "human_required")]
    public async Task The_requirement_is_stored_in_the_first_event_with_a_stable_name_and_replays_in_a_fresh_store(AcceptanceRequirement acceptance, string name)
    {
        var stream = NewTask(acceptance: acceptance);
        await SeedAsync(stream);

        Assert.Equal("task_created", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 1"));
        Assert.Equal(2, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.Contains($"\"acceptance\":\"{name}\"", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));

        var replayed = await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne);

        Assert.Equal(acceptance, replayed.State.Acceptance);
        Assert.Equal(stream.State, replayed.State);
    }

    [Fact]
    public async Task A_completed_task_without_acceptance_round_trips_with_no_gate_in_its_history()
    {
        var stream = Verified(AcceptanceRequirement.NotRequired);
        stream.CompleteVerified(At(3));
        var store = await SeedAsync(stream);

        var replayed = await LoadAndReplayAsync(store, TaskOne);

        Assert.Equal(WorkflowState.Completed, replayed.State.WorkflowState);
        Assert.Equal(AcceptanceRequirement.NotRequired, replayed.State.Acceptance);
        Assert.Empty(replayed.State.Gates);
        Assert.DoesNotContain(replayed.Events, e => e.Kind is "gate_opened" or "gate_decided");
    }

    // ---- legacy histories ---------------------------------------------------------------------------------------------------

    /// <summary>The stored first event is exactly what a pre-M4B build wrote: payload version 1 and no acceptance member.</summary>
    private void AssertFirstEventIsStoredAsLegacy()
    {
        Assert.Equal(1, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.DoesNotContain("acceptance", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));
    }

    [Fact]
    public async Task A_legacy_version_1_history_replays_with_human_required_semantics_including_its_gate()
    {
        var original = AsLegacy(Verified(AcceptanceRequirement.HumanRequired));
        original.OpenAcceptGate(At(3));
        original.AcceptGate(original.State.PendingGate!.Id, At(4));
        await SeedAsync(original);
        AssertFirstEventIsStoredAsLegacy();

        var replayed = await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne);

        Assert.Equal(AcceptanceRequirement.HumanRequired, replayed.State.Acceptance);
        Assert.True(replayed.State.PredatesAcceptanceRequirement);
        Assert.Equal(WorkflowState.Completed, replayed.State.WorkflowState);
        Assert.Equal(original.State, replayed.State);
    }

    [Fact]
    public async Task A_pre_gate_legacy_history_completed_by_a_plain_state_change_still_replays()
    {
        // before M4A a verified run was completed by a plain recorded state change; a stored version 1 task_created is how that history is recognized
        var original = AsLegacy(Verified(AcceptanceRequirement.HumanRequired));
        original.ChangeWorkflowState(WorkflowState.Completed, "Direct path complete: implementation revision passed lightweight verification.", At(3));
        await SeedAsync(original);
        AssertFirstEventIsStoredAsLegacy();

        var replayed = await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne);

        Assert.Equal(WorkflowState.Completed, replayed.State.WorkflowState);
        Assert.Equal(AcceptanceRequirement.HumanRequired, replayed.State.Acceptance);
        Assert.Empty(replayed.State.Gates);
        Assert.Equal(original.State, replayed.State);
    }

    [Fact]
    public async Task A_stored_version_2_human_required_history_is_never_legacy_so_a_plain_completion_appended_to_it_fails_replay()
    {
        var stream = Verified(AcceptanceRequirement.HumanRequired);
        var store = await SeedAsync(stream);
        Assert.Equal(2, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));

        await store.AppendAsync(
            TaskOne,
            3,
            [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 4, At(3), new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "no gate was accepted"))]);

        var history = await store.LoadAsync(TaskOne);
        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    // ---- damage ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_acceptance_name_is_corruption()
    {
        await SeedAsync(NewTask(acceptance: AcceptanceRequirement.NotRequired));
        Damage("UPDATE task_events SET payload_json = REPLACE(payload_json, 'not_required', 'sometimes') WHERE sequence = 1");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await _db.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_version_2_task_created_without_the_requirement_is_corruption_not_a_default()
    {
        await SeedAsync(NewTask(acceptance: AcceptanceRequirement.NotRequired));
        var json = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1");
        Damage("UPDATE task_events SET payload_json = $json WHERE sequence = 1", ("$json", AcceptanceMember().Replace(json, string.Empty)));

        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await _db.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_version_1_task_created_carrying_a_requirement_is_corruption()
    {
        await SeedAsync(NewTask(acceptance: AcceptanceRequirement.NotRequired));
        Damage("UPDATE task_events SET payload_schema_version = 1 WHERE sequence = 1");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await _db.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unsupported_task_created_version_is_corruption()
    {
        await SeedAsync(NewTask());
        Damage("UPDATE task_events SET payload_schema_version = 3 WHERE sequence = 1");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await _db.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_completion_without_verification_forged_into_a_no_acceptance_history_fails_core_replay()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var store = await SeedAsync(stream);

        // the stored history was legitimate; a forged completion appended to it is stored faithfully but refused by replay
        await store.AppendAsync(
            TaskOne,
            2,
            [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 3, At(2), new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "the agent said so"))]);

        var history = await store.LoadAsync(TaskOne);
        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }
}
