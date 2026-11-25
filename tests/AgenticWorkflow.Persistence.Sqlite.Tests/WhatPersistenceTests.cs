using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The WHAT gate's events are stored as versioned payloads with stable names, replay to exactly the state that produced them in a fresh store instance
/// (the durability that lets another process approve later, from a fresh process), and a damaged or forged history fails loudly. The forced-stage marker is
/// written only when it is set, so every history recorded before M5B is stored and replayed exactly as it was.
/// </summary>
public sealed class WhatPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static WhatProposal Proposal(string objective = "Retries on 429 and 503 with a bounded backoff.", int turn = 2)
        => new(objective, "A failed import is retried at most three times.", "The importer only.", "No new dependency.", null, "The importer's tests pass.", "Two policies are plausible.", turn);

    /// <summary>1 TaskCreated (maybe forced), 2 GateOpened (what).</summary>
    private static TaskStream Proposing(bool required = false)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, required);
        stream.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal(), At(1));
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
        stream.ApproveWhat(stream.State.PendingGate!.Id, At(2));
        await SeedAsync(stream);

        Assert.Equal(3, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.Contains("\"what_required\":true", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));

        var opened = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 2");
        Assert.Equal("gate_opened", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 2"));
        Assert.Contains("\"type\":\"what\"", opened);
        Assert.Contains("\"objective\":\"Retries on 429 and 503 with a bounded backoff.\"", opened);
        Assert.Contains("\"turn\":2", opened);
        Assert.Contains("\"implementation_revision_id\":null", opened);   // nothing is claimed about implementation or verification
        Assert.Contains("\"verification_run_id\":null", opened);

        Assert.Contains("\"action\":\"approve\"", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 3"));
        Assert.Contains("human_what_approval", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 4"));
    }

    [Fact]
    public async Task A_task_that_does_not_force_the_stage_is_stored_exactly_as_before_M5B()
    {
        await SeedAsync(TaskStream.Create(TaskOne, "Add retry", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired));

        Assert.Equal(2, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.DoesNotContain("what_required", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));
    }

    [Fact]
    public async Task An_open_proposal_replays_in_a_fresh_store_exactly_so_another_process_can_approve_it()
    {
        var original = Proposing(required: true);
        await SeedAsync(original);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(WorkflowState.AwaitingHuman, replayed.WorkflowState);
        Assert.True(replayed.WhatRequired);
        Assert.Equal(GateType.What, replayed.PendingGate!.Type);
        Assert.Equal(original.State.PendingGate!.What!.ContentHash, replayed.PendingGate.What!.ContentHash);
        Assert.False(replayed.MayImplement);
    }

    [Fact]
    public async Task An_approved_and_a_changed_proposal_round_trip_with_their_feedback_in_order()
    {
        var stream = Proposing();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Cap the scope.", At(2));
        var second = stream.OpenWhatGate(new GateId(Guid.NewGuid()), Proposal("Capped.", turn: 3), At(3));
        stream.ApproveWhat(second.Id, At(4));
        await SeedAsync(stream);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(stream.State, replayed);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatChanges, SemanticChangeKind.HumanWhatApproval],
            replayed.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal("Cap the scope.", replayed.DecisionFor(replayed.Gates[0].Id)!.Feedback);
        Assert.True(replayed.WhatApproved);
        Assert.True(replayed.MayImplement);
    }

    [Fact]
    public async Task A_what_gate_row_without_its_proposal_is_corruption()
    {
        var store = await SeedAsync(Proposing());
        Damage("UPDATE task_events SET payload_json = json_remove(payload_json, '$.gate.what') WHERE sequence = 2");

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
        stream.ApproveWhat(stream.State.PendingGate!.Id, At(2));
        var store = await SeedAsync(stream);

        await AppendForgedAsync(store, stream, new GateDecided(new GateDecision(stream.State.Gates[0].Id, GateAction.Approve, null, At(3))), 3);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task An_approval_decision_on_a_clarification_gate_fails_replay_so_it_cannot_stand_in_for_an_answer()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which?", "Why.", 1), At(1));
        var store = await SeedAsync(stream);

        await AppendForgedAsync(store, stream, new GateDecided(new GateDecision(stream.State.PendingGate!.Id, GateAction.Approve, null, At(2))), 2);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }
}
