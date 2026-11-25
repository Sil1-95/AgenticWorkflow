using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// Damages a valid database with direct SQL (the schema's own constraints stay intact) and checks that loading
/// fails explicitly instead of producing something that merely looks valid.
/// </summary>
public sealed class CorruptionTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_unknown_event_kind_is_corruption()
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET event_kind = 'teleported' WHERE sequence = 2");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Contains("teleported", error.Message);
        Assert.Equal(2, error.Sequence);
        Assert.Equal(TaskOne, error.TaskId);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task An_unsupported_payload_schema_version_is_corruption(int version)
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET payload_schema_version = $v WHERE sequence = 2", ("$v", version));

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Contains($"version {version}", error.Message);
        Assert.Equal(2, error.Sequence);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{\"from\":\"running\",\"to\":\"blocked\",\"reason\":\"r\"} trailing")]
    public async Task A_malformed_payload_is_corruption(string payload)
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET payload_json = $p WHERE sequence = 2", ("$p", payload));

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Theory]
    [InlineData("{\"from\":\"running\",\"to\":\"blocked\"}")]
    [InlineData("{\"from\":\"running\",\"to\":null,\"reason\":\"r\"}")]
    [InlineData("{\"from\":\"running\",\"to\":\"blocked\",\"reason\":\"r\",\"surprise\":1}")]
    [InlineData("{\"from\":\"running\",\"to\":\"blocked\",\"reason\":\"r\",\"reason\":\"again\"}")]
    [InlineData("{\"from\":\"running\",\"to\":\"blocked\",\"reason\":7}")]
    public async Task A_missing_unknown_null_or_mistyped_field_is_corruption(string payload)
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET payload_json = $p WHERE sequence = 2", ("$p", payload));

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Theory]
    [InlineData("\"from\":\"running\"", "\"from\":\"flying\"")]
    [InlineData("\"to\":\"blocked\"", "\"to\":\"Blocked\"")]
    [InlineData("\"to\":\"blocked\"", "\"to\":\"1\"")]
    public async Task An_unknown_workflow_state_name_is_corruption(string find, string replace)
    {
        var store = await SeedAsync();
        DamageReplace(2, find, replace);

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Theory]
    [InlineData(1, "\"completion_boundary\":\"local_commit\"", "\"completion_boundary\":\"Not A Valid Id!\"")]
    [InlineData(1, "\"completion_boundary\":\"local_commit\"", "\"completion_boundary\":\"\"")]
    [InlineData(3, "\"effect\":\"require\"", "\"effect\":\"obliterate\"")]
    [InlineData(3, "\"rule\":\"deps.review\"", "\"rule\":\"UPPER\"")]
    public async Task An_invalid_typed_identifier_or_effect_is_corruption(long sequence, string find, string replace)
    {
        var store = await SeedAsync();
        DamageReplace(sequence, find, replace);

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_malformed_guid_or_the_empty_guid_is_corruption()
    {
        var store = await SeedAsync();

        // Event 4 is the implementation revision; break the semantic revision id it references.
        var semantic = ((ImplementationRevisionRecorded)(await store.LoadAsync(TaskOne))[3].Payload).Revision.SemanticRevisionId;
        DamageReplace(4, semantic.Value.ToString("D"), "not-a-guid");
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        DamageReplace(4, "not-a-guid", Guid.Empty.ToString("D"));
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-01-01T12:00:00Z")]
    [InlineData("2026-01-01T12:00:00.0000000+02:00")]
    [InlineData("")]
    public async Task A_non_canonical_stored_timestamp_is_corruption(string timestamp)
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET timestamp_utc = $t WHERE sequence = 2", ("$t", timestamp));

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Equal(2, error.Sequence);
    }

    [Fact]
    public async Task A_malformed_event_id_is_corruption()
    {
        var store = await SeedAsync();
        Damage("UPDATE task_events SET event_id = 'xyz' WHERE sequence = 2");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_sequence_gap_is_corruption()
    {
        var store = await SeedAsync();
        Damage("DELETE FROM task_events WHERE sequence = 3");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Contains("expected 3", error.Message);
    }

    [Fact]
    public async Task A_stream_that_does_not_start_at_sequence_one_is_corruption()
    {
        var store = await SeedAsync();
        Damage("DELETE FROM task_events WHERE sequence = 1");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Contains("expected 1", error.Message);
    }

    [Fact]
    public async Task Stream_metadata_that_disagrees_with_the_stored_events_is_corruption()
    {
        var store = await SeedAsync();
        Damage("UPDATE task_streams SET version = version + 1");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        Assert.Contains("version", error.Message);
    }

    [Fact]
    public async Task Truncated_history_below_the_recorded_version_is_corruption()
    {
        var store = await SeedAsync();
        Damage("DELETE FROM task_events WHERE sequence = 5");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_stream_with_metadata_but_no_events_is_corruption()
    {
        var store = await SeedAsync();
        Damage("DELETE FROM task_events");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_embedded_task_that_disagrees_with_the_stream_decodes_but_fails_core_replay()
    {
        var store = await SeedAsync();
        // Re-home the initial semantic revision inside event 1 to another task. Storage-wise this is a
        // perfectly readable payload; the domain kernel is what must reject it.
        DamageReplace(1, TaskOne.Value.ToString("D"), TaskTwo.Value.ToString("D"));

        var history = await store.LoadAsync(TaskOne);

        Assert.Equal(TaskTwo, ((TaskCreated)history[0].Payload).InitialSemanticRevision.TaskId);
        var error = Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
        Assert.IsType<InvalidEventHistoryException>(error);
    }

    [Fact]
    public async Task An_event_moved_into_another_stream_decodes_but_fails_core_replay()
    {
        var store = await SeedAsync();
        var other = NewTask(TaskTwo);
        other.ChangeWorkflowState(WorkflowState.Blocked, "x", At(1));
        await PersistAsync(store, other);

        // Move TaskOne's state-change event (sequence 2) onto the end of TaskTwo's stream.
        Damage("UPDATE task_events SET task_id = $b, sequence = 3 WHERE task_id = $a AND sequence = 2",
            ("$a", TaskOne.Value.ToString("D")), ("$b", TaskTwo.Value.ToString("D")));
        Damage("UPDATE task_streams SET version = 3 WHERE task_id = $b", ("$b", TaskTwo.Value.ToString("D")));

        var history = await store.LoadAsync(TaskTwo);

        Assert.Equal(3, history.Count);
        Assert.IsAssignableFrom<DomainException>(Record.Exception(() => TaskStream.Replay(history, new GuidIdSource())));

        // TaskOne now has a hole where that event used to be, which IS storage corruption.
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_core_domain_rule_violated_inside_a_readable_payload_is_not_reported_as_storage_corruption()
    {
        var store = await SeedAsync();
        var initial = ((TaskCreated)(await store.LoadAsync(TaskOne))[0].Payload).InitialSemanticRevision;

        // Event 5 is semantic revision ordinal 2. A null parent is well-formed JSON but not a legal revision.
        DamageReplace(5, $"\"parent_id\":\"{initial.Id}\"", "\"parent_id\":null");

        var error = await Record.ExceptionAsync(() => store.LoadAsync(TaskOne));

        Assert.IsType<InvalidRevisionLineageException>(error);
        Assert.IsNotType<EventStoreCorruptionException>(error);
    }

    [Fact]
    public async Task Corruption_in_one_stream_does_not_affect_loading_another()
    {
        var store = await SeedAsync();
        var other = NewTask(TaskTwo);
        await PersistAsync(store, other);
        Damage("UPDATE task_events SET event_kind = 'teleported' WHERE sequence = 2 AND task_id = $a", ("$a", TaskOne.Value.ToString("D")));

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
        Assert.Equal(other.Events, await store.LoadAsync(TaskTwo));
    }

    // ---- verification_recorded ----------------------------------------------------------------------------------

    /// <summary>Seed: 1 TaskCreated, 2 ImplementationRevisionRecorded, 3 VerificationRecorded (one passed check "tests").</summary>
    private async Task<SqliteEventStore> SeedVerificationAsync()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)],
            At(2));

        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    [Fact]
    public async Task A_verification_payload_with_an_unknown_outcome_name_is_corruption()
    {
        var store = await SeedVerificationAsync();
        DamageReplace(3, "\"outcome\":\"passed\"", "\"outcome\":\"mostly_passed\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_verification_payload_with_an_invalid_check_id_is_corruption()
    {
        var store = await SeedVerificationAsync();
        DamageReplace(3, "\"check_id\":\"tests\"", "\"check_id\":\"Not Valid\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_verification_payload_with_an_unknown_member_is_corruption()
    {
        var store = await SeedVerificationAsync();
        DamageReplace(3, "\"checks\":", "\"extra\":1,\"checks\":");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_verification_payload_missing_a_required_member_is_corruption()
    {
        var store = await SeedVerificationAsync();
        DamageReplace(3, "\"implementation_revision_id\"", "\"implementation_revision_idx\"");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_verification_row_with_an_unsupported_payload_version_is_refused()
    {
        var store = await SeedVerificationAsync();
        Damage("UPDATE task_events SET payload_schema_version = 4 WHERE sequence = 3");

        var error = await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
        Assert.Contains("version 4", error.Message);
    }

    [Fact]
    public async Task A_verification_whose_aggregate_contradicts_its_checks_decodes_as_a_domain_violation_not_corruption()
    {
        var store = await SeedVerificationAsync();

        // Well-formed JSON, but the run now claims "failed" while its only check passed.
        DamageReplace(3, "\"outcome\":\"passed\",\"checks\"", "\"outcome\":\"failed\",\"checks\"");

        var error = await Record.ExceptionAsync(() => store.LoadAsync(TaskOne));

        Assert.IsType<InvalidVerificationException>(error);
    }

    [Fact]
    public async Task A_verification_with_no_checks_is_a_domain_violation()
    {
        var store = await SeedVerificationAsync();
        Damage(
            "UPDATE task_events SET payload_json = json_set(payload_json, '$.run.checks', json('[]')) WHERE sequence = 3");

        var error = await Record.ExceptionAsync(() => store.LoadAsync(TaskOne));

        Assert.IsType<InvalidVerificationException>(error);
    }

    [Fact]
    public async Task A_verification_pointing_at_an_unknown_implementation_revision_decodes_but_fails_core_replay()
    {
        var store = await SeedVerificationAsync();
        Damage(
            "UPDATE task_events SET payload_json = json_set(payload_json, '$.run.implementation_revision_id', $id) WHERE sequence = 3",
            ("$id", Guid.NewGuid().ToString("D")));

        var history = await store.LoadAsync(TaskOne);

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    /// <summary>
    /// Seed: 1 TaskCreated, 2 WorkflowStateChanged (running -> blocked, "waiting"), 3 PolicyDecisionRecorded (require),
    /// 4 ImplementationRevisionRecorded, 5 SemanticRevisionCreated (ordinal 2).
    /// </summary>
    private async Task<SqliteEventStore> SeedAsync()
    {
        var stream = NewTask();
        stream.ChangeWorkflowState(WorkflowState.Blocked, "waiting", At(1));
        stream.RecordPolicyDecision(
            new PolicyRuleId("deps.review"), PolicyEffect.Require, new PolicyTrigger("manifest touched"), "Needs sign-off.", At(2));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(3));
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope grew", At(4));

        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private void Damage(string sql, params (string Name, object Value)[] parameters)
        => Assert.True(_db.Execute(sql, parameters) > 0, "The damaging statement must change something, or the test proves nothing.");

    private void DamageReplace(long sequence, string find, string replace)
        => Damage(
            "UPDATE task_events SET payload_json = REPLACE(payload_json, $find, $replace) WHERE sequence = $s AND instr(payload_json, $find) > 0",
            ("$find", find),
            ("$replace", replace),
            ("$s", sequence));
}
