using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The ACCEPT gate's events are stored as versioned payloads, replay to exactly the state that produced them in a fresh store instance, and a damaged or
/// forged history fails loudly instead of replaying into an approval nobody gave.
/// </summary>
public sealed class GatePersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>1 TaskCreated, 2 ImplementationRevisionRecorded, 3 VerificationRecorded (passed), 4 GateOpened.</summary>
    private static TaskStream VerifiedWithOpenGate()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)],
            At(2));
        stream.OpenAcceptGate(At(3));
        return stream;
    }

    private async Task<SqliteEventStore> SeedAsync(TaskStream stream)
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private void Damage(string sql, params (string Name, object Value)[] parameters)
        => Assert.True(_db.Execute(sql, parameters) > 0, "The damaging statement must change something, or the test proves nothing.");

    [Fact]
    public async Task Gate_events_are_stored_with_stable_kinds_versions_and_names()
    {
        var stream = VerifiedWithOpenGate();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Please add a test.", At(4));
        await SeedAsync(stream);

        Assert.Equal("gate_opened", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 4"));
        Assert.Equal(1, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 4"));
        var opened = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 4");
        Assert.Contains("\"type\":\"accept\"", opened);
        Assert.Contains("\"verification_run_id\"", opened);

        Assert.Equal("gate_decided", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 5"));
        Assert.Equal(1, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 5"));
        var decided = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 5");
        Assert.Contains("\"action\":\"request_changes\"", decided);
        Assert.Contains("\"feedback\":\"Please add a test.\"", decided);
    }

    [Fact]
    public async Task A_pending_gate_replays_in_a_fresh_store_exactly()
    {
        var original = VerifiedWithOpenGate();
        await SeedAsync(original);

        var fresh = await _db.OpenStoreAsync();
        var replayed = (await LoadAndReplayAsync(fresh, TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(WorkflowState.AwaitingHuman, replayed.WorkflowState);
        Assert.Equal(original.State.PendingGate, replayed.PendingGate);
        Assert.True(replayed.IsCurrent(replayed.PendingGate!));
    }

    [Theory]
    [InlineData(GateAction.Accept)]
    [InlineData(GateAction.RequestChanges)]
    [InlineData(GateAction.Cancel)]
    public async Task Every_decision_round_trips_and_replays_to_the_same_state(GateAction action)
    {
        var original = VerifiedWithOpenGate();
        var gateId = original.State.PendingGate!.Id;

        switch (action)
        {
            case GateAction.Accept:
                original.AcceptGate(gateId, At(4));
                break;
            case GateAction.RequestChanges:
                original.RequestChanges(gateId, "Rename it \"properly\" — ünïcode & <angle>.", At(4));
                break;
            default:
                original.CancelGate(gateId, At(4));
                break;
        }

        await SeedAsync(original);
        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(action, replayed.DecisionFor(gateId)!.Action);
        Assert.Equal(original.State.DecisionFor(gateId)!.Feedback, replayed.DecisionFor(gateId)!.Feedback);
        Assert.Null(replayed.PendingGate);
    }

    // ---- damaged rows --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_gate_action_is_corruption()
    {
        var stream = VerifiedWithOpenGate();
        stream.AcceptGate(stream.State.PendingGate!.Id, At(4));
        var store = await SeedAsync(stream);
        Damage("UPDATE task_events SET payload_json = replace(payload_json, '\"accept\"', '\"ratify\"') WHERE sequence = 5");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_gate_type_is_corruption()
    {
        var store = await SeedAsync(VerifiedWithOpenGate());
        Damage("UPDATE task_events SET payload_json = replace(payload_json, '\"type\":\"accept\"', '\"type\":\"review\"') WHERE sequence = 4");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_gate_row_with_an_unsupported_payload_version_is_refused()
    {
        var store = await SeedAsync(VerifiedWithOpenGate());
        Damage("UPDATE task_events SET payload_schema_version = 2 WHERE sequence = 4");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
        Assert.Contains("version 2", error.Message);
    }

    [Fact]
    public async Task An_unexpected_member_in_a_gate_payload_is_corruption()
    {
        var store = await SeedAsync(VerifiedWithOpenGate());
        Damage("UPDATE task_events SET payload_json = json_set(payload_json, '$.approved_by', 'someone') WHERE sequence = 4");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    // ---- forged histories: well-formed rows that Core replay must refuse ---------------------------------------------------

    [Fact]
    public async Task A_decision_for_a_gate_that_was_never_opened_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        stream.AcceptGate(stream.State.PendingGate!.Id, At(4));
        var store = await SeedAsync(stream);
        Damage("UPDATE task_events SET payload_json = json_set(payload_json, '$.decision.gate_id', $id) WHERE sequence = 5", ("$id", Guid.NewGuid().ToString("D")));

        var history = await store.LoadAsync(TaskOne);

        Assert.Throws<InvalidGateException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public async Task An_accept_forged_over_a_gate_the_human_declined_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        stream.RequestChanges(stream.State.PendingGate!.Id, "Not yet.", At(4));
        var store = await SeedAsync(stream);

        // rewrite "request_changes" into "accept": the semantic revision the real decision created now follows an acceptance
        Damage("UPDATE task_events SET payload_json = json_remove(replace(payload_json, 'request_changes', 'accept'), '$.decision.feedback') WHERE sequence = 5");

        var history = await store.LoadAsync(TaskOne);

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public async Task An_accept_forged_over_a_gate_that_a_later_revision_made_stale_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(4));   // the gate no longer describes the run
        stream.CancelGate(stream.State.PendingGate!.Id, At(5));
        var store = await SeedAsync(stream);

        Damage("UPDATE task_events SET payload_json = replace(payload_json, '\"cancel\"', '\"accept\"') WHERE sequence = 6");

        var history = await store.LoadAsync(TaskOne);

        Assert.Throws<StaleGateException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public async Task A_gate_forged_over_a_verification_that_did_not_pass_fails_replay()
    {
        var stream = VerifiedWithOpenGate();
        var store = await SeedAsync(stream);

        // the stored gate claims the run was verified, but the verification row now says it failed (and its check agrees)
        Damage("UPDATE task_events SET payload_json = replace(replace(payload_json, '\"outcome\":\"passed\"', '\"outcome\":\"failed\"'), '\"exit_code\":0', '\"exit_code\":1') WHERE sequence = 3");

        var error = await Record.ExceptionAsync(async () => TaskStream.Replay(await store.LoadAsync(TaskOne), new GuidIdSource()));

        Assert.IsAssignableFrom<DomainException>(error);
    }

    [Fact]
    public async Task A_second_open_gate_forged_into_the_history_fails_replay()
    {
        var store = await SeedAsync(VerifiedWithOpenGate());
        var history = (await store.LoadAsync(TaskOne)).ToList();
        var opened = Assert.IsType<GateOpened>(history[3].Payload).Gate;

        // a second gate, opened while the first is still pending, as a forger would append it
        var second = new Gate(new GateId(Guid.NewGuid()), opened.TaskId, opened.Type, opened.SemanticRevisionId, opened.ImplementationRevisionId, opened.VerificationRunId, opened.OpenedAt);
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 5, history[3].Timestamp, new GateOpened(second)));

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }
}
