using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>Per-check verification provenance: round trip, legacy version-1 rows, and loud failure on anything unknown or inconsistent.</summary>
public sealed class ProvenancePersistenceTests : IDisposable
{
    private const string Fingerprint = "git-worktree-sha256:abc";

    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static AgentObservation Observation(long? duration = 4200)
        => new("claude-code", "toolu_01ABC", "dotnet test Fixture.sln", duration, new ImplementationFingerprint(Fingerprint));

    private async Task<SqliteEventStore> SeedMixedAsync()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint(Fingerprint), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [
                new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "agent-observed", 0, Observation(), "agent_observed_current_state"),
                new VerificationCheckResult(new VerificationCheckId("smoke"), VerificationOutcome.Passed, "exit 0 (expected 0)", 0, evidenceReason: "command_mismatch"),
                new VerificationCheckResult(new VerificationCheckId("workspace_stable"), VerificationOutcome.Passed, "stable", 0),
            ],
            At(2));

        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    [Fact]
    public async Task Agent_observed_and_controller_executed_checks_round_trip_with_their_provenance()
    {
        var store = await SeedMixedAsync();

        var run = (await new Application.WorkflowTaskService(store, new SequentialIdSource(), new FixedClock()).LoadAsync(TaskOne)).Task.CurrentVerification!;

        var tests = run.Checks.Single(c => c.CheckId.Value == "tests");
        Assert.Equal(VerificationEvidenceSource.AgentObserved, tests.Source);
        Assert.Equal(Observation(), tests.Observation);

        var smoke = run.Checks.Single(c => c.CheckId.Value == "smoke");
        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, smoke.Source);
        Assert.Null(smoke.Observation);
    }

    [Fact]
    public async Task The_reason_for_reusing_or_executing_each_check_round_trips_and_is_absent_where_none_was_recorded()
    {
        var store = await SeedMixedAsync();

        var run = (await new Application.WorkflowTaskService(store, new SequentialIdSource(), new FixedClock()).LoadAsync(TaskOne)).Task.CurrentVerification!;

        Assert.Equal("agent_observed_current_state", run.Checks.Single(c => c.CheckId.Value == "tests").EvidenceReason);
        Assert.Equal("command_mismatch", run.Checks.Single(c => c.CheckId.Value == "smoke").EvidenceReason);
        Assert.Null(run.Checks.Single(c => c.CheckId.Value == "workspace_stable").EvidenceReason);
    }

    [Fact]
    public async Task A_replay_through_a_fresh_connection_preserves_provenance_exactly()
    {
        await SeedMixedAsync();

        var reopened = await _db.OpenStoreAsync();
        var events = await reopened.LoadAsync(TaskOne);
        var replayed = TaskStream.Replay(events, new SequentialIdSource()).State;

        var original = Assert.Single(replayed.Verifications);
        Assert.Equal(Observation(), original.Checks[0].Observation);
        Assert.Null(original.Checks[1].Observation);
        Assert.Equal("agent_observed_current_state", original.Checks[0].EvidenceReason);
        Assert.Equal("command_mismatch", original.Checks[1].EvidenceReason);
    }

    [Fact]
    public async Task The_stored_row_is_version_three_and_names_its_source_observation_and_reason_explicitly()
    {
        await SeedMixedAsync();

        Assert.Equal(3, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE event_kind = 'verification_recorded'"));

        var json = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'verification_recorded'");
        Assert.Contains("\"evidence_reason\":\"agent_observed_current_state\"", json);
        Assert.Contains("\"evidence_reason\":\"command_mismatch\"", json);
        Assert.Contains("\"source\":\"agent_observed\"", json);
        Assert.Contains("\"source\":\"controller_executed\"", json);
        Assert.Contains("\"tool_use_id\":\"toolu_01ABC\"", json);
        Assert.Contains("\"command_identity\":\"dotnet test Fixture.sln\"", json);
        Assert.Contains("\"duration_ms\":4200", json);
        Assert.Contains($"\"state_fingerprint\":\"{Fingerprint}\"", json);
        Assert.DoesNotContain("stdout", json);
    }

    [Fact]
    public async Task A_legacy_version_one_row_still_decodes_and_every_check_is_controller_executed()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)],
            At(2));
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        // Rewrite the row exactly as the previous build wrote it: version 1, no provenance members.
        Assert.Equal(
            1,
            _db.Execute(
                "UPDATE task_events SET payload_schema_version = 1, payload_json = REPLACE(REPLACE(REPLACE(payload_json, ',\"source\":\"controller_executed\"', ''), ',\"observation\":null', ''), ',\"evidence_reason\":null', '') WHERE event_kind = 'verification_recorded'"));
        Assert.DoesNotContain("source", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'verification_recorded'"));

        var loaded = (await store.LoadAsync(TaskOne)).Select(e => e.Payload).OfType<VerificationRecorded>().Single().Run;

        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, loaded.Checks.Single().Source);
        Assert.Null(loaded.Checks.Single().EvidenceReason);
    }

    [Fact]
    public async Task A_legacy_version_two_row_still_decodes_with_its_provenance_and_no_reason()
    {
        await SeedMixedAsync();

        // Rewrite the row exactly as the previous build wrote it: version 2, provenance but no reason member.
        Assert.Equal(
            1,
            _db.Execute(
                "UPDATE task_events SET payload_schema_version = 2, payload_json = REPLACE(REPLACE(REPLACE(payload_json, ',\"evidence_reason\":\"agent_observed_current_state\"', ''), ',\"evidence_reason\":\"command_mismatch\"', ''), ',\"evidence_reason\":null', '') WHERE event_kind = 'verification_recorded'"));
        Assert.DoesNotContain("evidence_reason", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'verification_recorded'"));

        var store = await _db.OpenStoreAsync();
        var loaded = (await store.LoadAsync(TaskOne)).Select(e => e.Payload).OfType<VerificationRecorded>().Single().Run;

        Assert.Equal(VerificationEvidenceSource.AgentObserved, loaded.Checks[0].Source);
        Assert.Equal(Observation(), loaded.Checks[0].Observation);
        Assert.All(loaded.Checks, c => Assert.Null(c.EvidenceReason));
    }

    [Fact]
    public async Task A_version_two_row_that_carries_a_reason_is_corruption()
    {
        var store = await SeedMixedAsync();
        Damage("UPDATE task_events SET payload_schema_version = 2 WHERE event_kind = 'verification_recorded'");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_overlong_reason_is_corruption_on_load()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"evidence_reason\":\"command_mismatch\"", $"\"evidence_reason\":\"{new string('x', VerificationCheckResult.MaxEvidenceReasonLength + 1)}\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_version_one_row_that_claims_provenance_is_corruption()
    {
        var store = await SeedMixedAsync();
        Damage("UPDATE task_events SET payload_schema_version = 1 WHERE event_kind = 'verification_recorded'");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_version_two_row_without_a_source_is_corruption()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"source\":\"controller_executed\",", string.Empty);

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_source_name_is_corruption()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"source\":\"agent_observed\"", "\"source\":\"agent_vibes\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_agent_observed_check_without_its_observation_is_corruption()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"source\":\"controller_executed\"", "\"source\":\"agent_observed\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_controller_executed_check_that_carries_an_observation_is_corruption()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"source\":\"agent_observed\"", "\"source\":\"controller_executed\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_member_inside_an_observation_is_corruption()
    {
        var store = await SeedMixedAsync();
        DamageReplace("\"tool_use_id\":\"toolu_01ABC\"", "\"tool_use_id\":\"toolu_01ABC\",\"stdout\":\"all green\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_observation_whose_state_is_not_the_verified_revisions_is_a_domain_violation_on_load()
    {
        var store = await SeedMixedAsync();
        DamageReplace($"\"state_fingerprint\":\"{Fingerprint}\"", "\"state_fingerprint\":\"git-worktree-sha256:some-other-state\"");

        var error = await Record.ExceptionAsync(() => store.LoadAsync(TaskOne));

        // The row decodes; Core refuses the history when it is replayed.
        error ??= await Record.ExceptionAsync(async () => TaskStream.Replay(await store.LoadAsync(TaskOne), new SequentialIdSource()));
        Assert.IsType<InvalidVerificationException>(error);
    }

    private void Damage(string sql)
        => Assert.True(_db.Execute(sql) > 0, "The damaging statement must change something, or the test proves nothing.");

    private void DamageReplace(string find, string replace)
        => Assert.True(
            _db.Execute(
                "UPDATE task_events SET payload_json = REPLACE(payload_json, $find, $replace) WHERE event_kind = 'verification_recorded' AND instr(payload_json, $find) > 0",
                ("$find", find),
                ("$replace", replace)) > 0,
            "The damaging statement must change something, or the test proves nothing.");

    private sealed class FixedClock : Application.IClock
    {
        public DateTimeOffset UtcNow => T0.AddHours(1);
    }
}
