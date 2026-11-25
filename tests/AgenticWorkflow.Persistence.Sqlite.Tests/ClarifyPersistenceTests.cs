using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The CLARIFY gate's events are stored as versioned payloads with stable names, replay to exactly the state that produced them in a fresh store
/// instance (the durability that lets another process answer later), and a damaged or forged history fails loudly. Existing ACCEPT rows are unchanged.
/// </summary>
public sealed class ClarifyPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>1 TaskCreated, 2 GateOpened (clarify).</summary>
    private static TaskStream Asking()
    {
        var stream = NewTask(acceptance: AcceptanceRequirement.NotRequired);
        stream.OpenClarifyGate(new GateId(Guid.NewGuid()), new ClarificationRequest("Which dessert?", "Not in the repository.", 2), At(1));
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

    [Fact]
    public async Task The_question_and_the_answer_are_stored_with_stable_kinds_versions_and_names()
    {
        var stream = Asking();
        stream.AnswerClarification(stream.State.PendingGate!.Id, "Tiramisu.", At(2));
        await SeedAsync(stream);

        var opened = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 2");
        Assert.Equal("gate_opened", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 2"));
        Assert.Contains("\"type\":\"clarify\"", opened);
        Assert.Contains("\"question\":\"Which dessert?\"", opened);
        Assert.Contains("\"turn\":2", opened);
        Assert.Contains("\"implementation_revision_id\":null", opened);   // nothing is claimed about implementation or verification
        Assert.Contains("\"verification_run_id\":null", opened);

        var decided = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 3");
        Assert.Contains("\"action\":\"answer\"", decided);
        Assert.Contains("\"answer\":\"Tiramisu.\"", decided);
        Assert.Contains("human_clarification", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 4"));
    }

    [Fact]
    public async Task An_open_question_replays_in_a_fresh_store_exactly_so_another_process_can_answer_it()
    {
        var original = Asking();
        await SeedAsync(original);

        var fresh = await _db.OpenStoreAsync();
        var replayed = (await LoadAndReplayAsync(fresh, TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(WorkflowState.AwaitingHuman, replayed.WorkflowState);
        Assert.Equal(original.State.PendingGate, replayed.PendingGate);
        Assert.Equal(GateType.Clarify, replayed.PendingGate!.Type);
    }

    [Fact]
    public async Task An_answered_and_a_cancelled_question_round_trip()
    {
        var answered = Asking();
        answered.AnswerClarification(answered.State.PendingGate!.Id, "Tiramisu.", At(2));
        await SeedAsync(answered);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;
        Assert.Equal(answered.State, replayed);
        Assert.Equal("Tiramisu.", replayed.DecisionFor(replayed.Gates[0].Id)!.Answer);
        Assert.Equal(SemanticChangeKind.HumanClarification, replayed.CurrentSemanticRevision.Kind);
    }

    [Fact]
    public async Task A_clarify_gate_row_with_an_implementation_binding_or_without_a_question_is_corruption()
    {
        var store = await SeedAsync(Asking());
        Damage("UPDATE task_events SET payload_json = json_remove(payload_json, '$.gate.clarification') WHERE sequence = 2");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_answer_smuggled_into_an_accept_decision_or_an_unknown_member_is_corruption()
    {
        var stream = Asking();
        stream.CancelGate(stream.State.PendingGate!.Id, At(2));
        var store = await SeedAsync(stream);
        Damage("UPDATE task_events SET payload_json = json_set(payload_json, '$.decision.answer', 'sneaky') WHERE sequence = 3");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_forged_answer_to_a_question_that_was_never_asked_fails_replay_and_never_becomes_state()
    {
        var stream = Asking();
        var store = await SeedAsync(stream);
        var forged = new GateDecided(new GateDecision(new GateId(Guid.NewGuid()), GateAction.Answer, null, At(2), "Forged."));
        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(2), forged)]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_forged_answer_without_its_semantic_revision_replays_but_a_second_answer_is_refused()
    {
        var stream = Asking();
        stream.AnswerClarification(stream.State.PendingGate!.Id, "Tiramisu.", At(2));
        var store = await SeedAsync(stream);
        var gate = stream.State.Gates[0];
        var again = new GateDecided(new GateDecision(gate.Id, GateAction.Answer, null, At(3), "Panna cotta."));
        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(3), again)]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }
}
