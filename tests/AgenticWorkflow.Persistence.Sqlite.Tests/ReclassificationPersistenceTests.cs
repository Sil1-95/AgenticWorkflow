using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The reclassification event is stored as one versioned payload with stable names, replays in a fresh store instance to exactly the live state
/// (invalidations, escalation, provisional work, stale implementation and verification all derive from it), and a damaged or forged record fails loudly.
/// </summary>
public sealed class ReclassificationPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static HowProposal How(string strategy = "Introduce a retry policy object.")
        => new(strategy, "RetryPolicy decides.", null, null, "One more class.", null, "Two designs are plausible.", 1);

    /// <summary>HOW approved, implemented, verified, then reclassified to the HOW by new evidence.</summary>
    private static TaskStream Reclassified(bool provisional = true)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry", CompletionBoundary.LocalCommit, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, false, true);
        var how = stream.OpenHowGate(new GateId(Guid.NewGuid()), How(), At(1));
        stream.ApproveHow(how.Id, At(2));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(4));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(5));
        stream.Reclassify(new ReclassificationId(Guid.NewGuid()), ReclassificationStage.How, "The budget is 10ms; a policy object cannot meet it.", "The approved strategy cannot meet the hidden budget.", 3, "session-1", new ImplementationFingerprint("tree:edited"), provisional, At(7));
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
    public async Task The_event_is_stored_with_a_stable_kind_version_and_names()
    {
        var stream = Reclassified();
        await SeedAsync(stream);
        var sequence = stream.Events.Count;

        Assert.Equal("reclassification_recorded", _db.Scalar<string>($"SELECT event_kind FROM task_events WHERE sequence = {sequence}"));
        var json = _db.Scalar<string>($"SELECT payload_json FROM task_events WHERE sequence = {sequence}");
        Assert.Contains("\"stage\":\"how\"", json);
        Assert.Contains("\"provisional_work\":true", json);
        Assert.Contains("agent_reclassification", json);
        Assert.Contains("\"invalidated_gates\":[\"", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reclassified_history_replays_in_a_fresh_store_to_exactly_the_live_state(bool provisional)
    {
        var original = Reclassified(provisional);
        await SeedAsync(original);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(provisional, replayed.HasProvisionalWork);
        Assert.False(replayed.MayImplement);
        Assert.True(replayed.IsVerificationStale);
        Assert.Equal(GateStatus.Invalidated, replayed.StatusOf(replayed.Gates[0]));
        Assert.Equal("session-1", replayed.Reclassifications.Single().Session);
    }

    [Fact]
    public async Task The_history_continues_after_a_restart_with_the_new_approval_and_a_fresh_implementation()
    {
        await SeedAsync(Reclassified());
        var store = await _db.OpenStoreAsync();
        var resumed = await LoadAndReplayAsync(store, TaskOne);
        var before = resumed.Events.Count;

        var how = resumed.OpenHowGate(new GateId(Guid.NewGuid()), How("Inline loop."), At(9));
        resumed.ApproveHow(how.Id, At(10));
        resumed.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(12));
        resumed.RecordVerification(resumed.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(13));
        await store.AppendAsync(TaskOne, before, resumed.Events.Skip(before).ToArray());

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(resumed.State, replayed);
        Assert.True(replayed.HasCurrentPassingVerification);
        Assert.Null(replayed.ActiveReclassification);
    }

    [Fact]
    public async Task A_reclassification_row_missing_a_member_or_with_an_unknown_stage_is_corruption()
    {
        var stream = Reclassified();
        var store = await SeedAsync(stream);
        var sequence = stream.Events.Count;

        Damage($"UPDATE task_events SET payload_json = json_remove(payload_json, '$.reclassification.evidence') WHERE sequence = {sequence}");
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_stage_name_is_corruption()
    {
        var stream = Reclassified();
        var store = await SeedAsync(stream);
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.reclassification.stage', 'review') WHERE sequence = {stream.Events.Count}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task Evidence_beyond_the_bound_is_corruption()
    {
        var stream = Reclassified();
        var store = await SeedAsync(stream);
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.reclassification.evidence', '{new string('x', Reclassification.MaxEvidenceLength + 1)}') WHERE sequence = {stream.Events.Count}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_forged_stored_reclassification_that_invalidates_nothing_it_should_fails_replay()
    {
        var stream = Reclassified();
        var store = await SeedAsync(stream);
        // the invalidated gate list is emptied while the HOW stays approved in the history: replay must refuse the edit
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.reclassification.invalidated_gates', json('[]')) WHERE sequence = {stream.Events.Count}");

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_duplicated_reclassification_event_fails_replay()
    {
        var stream = Reclassified();
        var store = await SeedAsync(stream);
        var duplicate = (ReclassificationRecorded)stream.Events[^1].Payload;

        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(8), duplicate)]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }
}
