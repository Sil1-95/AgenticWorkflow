using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The HOW gate's events are stored as versioned payloads with stable names, replay to exactly the state that produced them in a fresh store instance
/// (the durability that lets another process approve later), and a damaged or forged history fails loudly. The forced-HOW marker is written only when it is set, so
/// every history recorded before M5C is stored and replayed exactly as it was (the M5B WHAT marker keeps its own payload version).
/// </summary>
public sealed class HowPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static HowProposal Proposal(string strategy = "Introduce a retry policy object.", int turn = 2)
        => new(strategy, "RetryPolicy decides; the importer asks it.", "The importer calls the policy after each failure.", null, "One more class, but the importer stays testable.", "A fake clock in unit tests.", "Two designs are plausible.", turn);

    /// <summary>1 TaskCreated (maybe forced), 2 GateOpened (how).</summary>
    private static TaskStream Proposing(bool required = false, bool what = false)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, what, required);
        stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(1));
        return stream;
    }

    private async Task<SqliteEventStore> SeedAsync(TaskStream stream)
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private void Damage(string sql)
        => Assert.True(_db.Execute(sql, []) > 0, "The damaging statement must change something, or the test proves nothing.");

    private static Task AppendForgedAsync(SqliteEventStore store, TaskStream stream, DomainEvent forged, int minutes)
        => store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(minutes), forged)]);

    [Fact]
    public async Task The_proposal_and_its_decisions_are_stored_with_stable_kinds_versions_and_names()
    {
        var stream = Proposing(required: true);
        stream.ApproveHow(stream.State.PendingGate!.Id, At(2));
        await SeedAsync(stream);

        Assert.Equal(4, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        var created = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1");
        Assert.Contains("\"how_required\":true", created);
        Assert.Contains("\"what_required\":false", created);

        var opened = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 2");
        Assert.Equal("gate_opened", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 2"));
        Assert.Contains("\"type\":\"how\"", opened);
        Assert.Contains("\"strategy\":\"Introduce a retry policy object.\"", opened);
        Assert.Contains("\"turn\":2", opened);
        Assert.Contains("\"implementation_revision_id\":null", opened);
        Assert.Contains("\"verification_run_id\":null", opened);

        Assert.Contains("\"action\":\"approve\"", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 3"));
        Assert.Contains("human_how_approval", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 4"));
    }

    [Fact]
    public async Task A_task_that_does_not_force_the_HOW_is_stored_exactly_as_before_and_a_WHAT_only_one_keeps_its_own_version()
    {
        await SeedAsync(TaskStream.Create(TaskOne, "Add retry", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired));
        Assert.Equal(2, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.DoesNotContain("how_required", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));

        using var other = new TempDatabase();
        var store = await other.OpenStoreAsync();
        await PersistAsync(store, TaskStream.Create(TaskOne, "Add retry", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true));
        Assert.Equal(3, other.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.DoesNotContain("how_required", other.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));
    }

    [Fact]
    public async Task Both_requirements_round_trip_independently()
    {
        foreach (var (what, how) in new[] { (false, true), (true, false), (true, true) })
        {
            using var db = new TempDatabase();
            var store = await db.OpenStoreAsync();
            await PersistAsync(store, TaskStream.Create(TaskOne, "t", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, what, how));

            var state = (await LoadAndReplayAsync(await db.OpenStoreAsync(), TaskOne)).State;

            Assert.Equal(what, state.WhatRequired);
            Assert.Equal(how, state.HowRequired);
        }
    }

    [Fact]
    public async Task An_open_proposal_replays_in_a_fresh_store_exactly_so_another_process_can_approve_it()
    {
        var original = Proposing(required: true);
        await SeedAsync(original);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(WorkflowState.AwaitingHuman, replayed.WorkflowState);
        Assert.True(replayed.HowRequired);
        Assert.Equal(GateType.How, replayed.PendingGate!.Type);
        Assert.Equal(original.State.PendingGate!.How!.ContentHash, replayed.PendingGate.How!.ContentHash);
        Assert.False(replayed.MayImplement);
    }

    [Fact]
    public async Task An_approved_and_a_changed_proposal_round_trip_with_their_feedback_in_order()
    {
        var stream = Proposing();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Prefer inline.", At(2));
        var second = stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal("Inline.", turn: 3), At(3));
        stream.ApproveHow(second.Id, At(4));
        await SeedAsync(stream);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(stream.State, replayed);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanHowChanges, SemanticChangeKind.HumanHowApproval],
            replayed.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal("Prefer inline.", replayed.DecisionFor(replayed.Gates[0].Id)!.Feedback);
        Assert.True(replayed.HowApproved);
        Assert.True(replayed.MayImplement);
    }

    [Fact]
    public async Task A_combined_WHAT_then_HOW_history_round_trips_in_order()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true, howRequired: true);
        var what = stream.OpenWhatGate(new GateId(Guid.NewGuid()), new WhatProposal("o", "b", "s", null, null, "c", "r", 1), At(1));
        stream.ApproveWhat(what.Id, At(2));
        var how = stream.OpenHowGate(new GateId(Guid.NewGuid()), Proposal(), At(3));
        stream.ApproveHow(how.Id, At(4));
        await SeedAsync(stream);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(stream.State, replayed);
        Assert.Equal([GateType.What, GateType.How], replayed.Gates.Select(g => g.Type).ToArray());
        Assert.True(replayed.MayImplement);
    }

    [Fact]
    public async Task A_how_gate_row_without_its_proposal_is_corruption()
    {
        var store = await SeedAsync(Proposing());
        Damage("UPDATE task_events SET payload_json = json_remove(payload_json, '$.gate.how') WHERE sequence = 2");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_forged_approval_of_a_proposal_that_was_never_made_fails_replay_and_never_becomes_state()
    {
        var stream = Proposing();
        var store = await SeedAsync(stream);

        await AppendForgedAsync(store, stream, new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Approve, null, At(2))), 2);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_second_approval_of_the_same_proposal_fails_replay()
    {
        var stream = Proposing();
        stream.ApproveHow(stream.State.PendingGate!.Id, At(2));
        var store = await SeedAsync(stream);

        await AppendForgedAsync(store, stream, new GateDecided(new GateDecision(stream.State.Gates[0].Id, GateAction.Approve, null, At(3))), 3);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_HOW_gate_opened_while_a_required_WHAT_is_unapproved_fails_replay()
    {
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, whatRequired: true, howRequired: true);
        var store = await SeedAsync(stream);
        var forged = new Gate(new GateId(Guid.NewGuid()), TaskOne, GateType.How, stream.State.CurrentSemanticRevisionId, null, null, At(1), how: Proposal());

        await AppendForgedAsync(store, stream, new GateOpened(forged), 1);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }
}
