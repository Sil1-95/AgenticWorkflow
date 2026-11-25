using System.Reflection;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

public sealed class SerializationTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void The_rich_stream_exercises_every_core_event_type()
    {
        var kindsInCore = typeof(EventKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var concreteEventTypes = typeof(DomainEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(DomainEvent)))
            .ToList();

        // If Core gains an event type, this fails until the codec and this fixture cover it.
        Assert.Equal(concreteEventTypes.Count, kindsInCore.Count);
        Assert.Equal(kindsInCore, RichCompletedTask().Events.Select(e => e.Kind).ToHashSet());
        Assert.Equal(
            concreteEventTypes.Select(t => t.Name).Order(),
            RichCompletedTask().Events.Select(e => e.Payload.GetType().Name).Distinct().Order());
    }

    [Fact]
    public async Task Every_event_round_trips_exactly()
    {
        var stream = RichCompletedTask();
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        var loaded = await store.LoadAsync(TaskOne);

        Assert.Equal(stream.Events.Count, loaded.Count);
        for (var i = 0; i < loaded.Count; i++)
            Assert.Equal(stream.Events[i], loaded[i]);
    }

    [Fact]
    public async Task Typed_ids_boundary_roles_authority_and_optional_references_survive()
    {
        var original = RichCompletedTask();
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, original);

        var state = (await LoadAndReplayAsync(store, TaskOne)).State;

        Assert.Equal(original.State.Id, state.Id);
        Assert.Equal(new CompletionBoundary("staging_deploy"), state.CompletionBoundary);
        Assert.Equal(original.State.Intent, state.Intent);

        var implementer = state.GetBinding(new RoleId("implementer"));
        Assert.Equal(
            ["code_edit", "repository_tools"],
            implementer.Role.RequiredCapabilities.Select(c => c.Value));
        Assert.Equal(
            ["code_edit", "fresh_context", "repository_tools"],
            implementer.Profile.Capabilities.Select(c => c.Value));
        Assert.Equal("Primary implementer", implementer.Profile.Description);
        Assert.Null(state.GetBinding(new RoleId("observer")).Profile.Description);
        Assert.Empty(state.GetBinding(new RoleId("observer")).Role.RequiredCapabilities);

        Assert.Equal(
            ["dependency_change", "public_api_change"],
            state.Authority.AllowedWithoutHuman.Select(a => a.Value));
        Assert.Equal(["deployment"], state.Authority.ReservedForHuman.Select(a => a.Value));

        Assert.Equal(new SemanticChangeKind("compliance_ruling"), state.SemanticRevisions[1].Kind);
        Assert.Equal(state.SemanticRevisions[0].Id, state.SemanticRevisions[1].ParentId);
        Assert.Equal("  tree:ABC::123  ", state.ImplementationRevisions[0].Fingerprint.Value);

        var fullDecision = state.PolicyDecisions[0];
        Assert.Equal(new ActionId("dependency_change"), fullDecision.Trigger.Action);
        Assert.Equal("evt-123", fullDecision.Trigger.Reference);
        Assert.Equal(state.SemanticRevisions[1].Id, fullDecision.SemanticRevisionId);
        Assert.Equal(state.ImplementationRevisions[1].Id, fullDecision.ImplementationRevisionId);

        var bareDecision = state.PolicyDecisions[1];
        Assert.Null(bareDecision.Trigger.Action);
        Assert.Null(bareDecision.Trigger.Reference);
        Assert.Null(bareDecision.SemanticRevisionId);
        Assert.Null(bareDecision.ImplementationRevisionId);
    }

    [Fact]
    public async Task Verification_runs_round_trip_with_check_results_and_history()
    {
        var original = RichCompletedTask();
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, original);

        var state = (await LoadAndReplayAsync(store, TaskOne)).State;

        Assert.Equal(5, state.Verifications.Length);
        Assert.True(original.State.Verifications.SequenceEqual(state.Verifications));

        var failed = state.Verifications[0];
        Assert.Equal(VerificationOutcome.Failed, failed.Outcome);
        Assert.Equal(2, failed.Checks.Length);
        Assert.Equal(new VerificationCheckId("tests"), failed.Checks[0].CheckId);
        Assert.Equal(1, failed.Checks[0].ExitCode);
        Assert.Null(failed.Checks[1].ExitCode);
        Assert.Equal("no matches — \"ok\"", failed.Checks[1].Summary);

        Assert.Equal(state.ImplementationRevisions[1].Id, failed.ImplementationRevisionId);
        Assert.Equal(VerificationOutcome.Passed, state.CurrentVerification!.Outcome);
        Assert.Equal(state.CurrentImplementationRevisionId, state.CurrentVerification.ImplementationRevisionId);
    }

    [Fact]
    public async Task Verification_events_are_stored_as_kind_verification_recorded_with_payload_version_three()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0)],
            At(2));
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        Assert.Equal("verification_recorded", _db.Scalar<string>("SELECT event_kind FROM task_events WHERE sequence = 3"));
        Assert.Equal(3, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 3"));
        var json = _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 3");
        Assert.Contains("\"outcome\":\"passed\"", json);
        Assert.Contains("\"check_id\":\"tests\"", json);
        Assert.Contains("\"implementation_revision_id\"", json);
    }

    [Fact]
    public async Task Implementation_revisions_keep_their_semantic_revision_binding()
    {
        var original = RichCompletedTask();
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, original);

        var state = (await LoadAndReplayAsync(store, TaskOne)).State;

        Assert.Equal(5, state.ImplementationRevisions.Length);
        Assert.Equal(state.SemanticRevisions[0].Id, state.ImplementationRevisions[0].SemanticRevisionId);
        Assert.Equal(state.SemanticRevisions[1].Id, state.ImplementationRevisions[1].SemanticRevisionId);
        Assert.Equal(
            original.State.ImplementationRevisions.Select(r => r.SemanticRevisionId),
            state.ImplementationRevisions.Select(r => r.SemanticRevisionId));
    }

    [Theory]
    [InlineData(PolicyEffect.Prevent, "prevent")]
    [InlineData(PolicyEffect.Require, "require")]
    [InlineData(PolicyEffect.Detect, "detect")]
    [InlineData(PolicyEffect.Notify, "notify")]
    public async Task Every_policy_effect_round_trips_under_its_stable_name(PolicyEffect effect, string stored)
    {
        var stream = NewTask();
        stream.RecordPolicyDecision(new PolicyRuleId("r"), effect, new PolicyTrigger("t"), "why", At(1));
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        var loaded = await store.LoadAsync(TaskOne);

        Assert.Equal(effect, ((PolicyDecisionRecorded)loaded[1].Payload).Decision.Effect);
        Assert.Contains($"\"effect\":\"{stored}\"", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 2"));
    }

    [Theory]
    [InlineData(WorkflowState.Completed)]
    [InlineData(WorkflowState.Cancelled)]
    [InlineData(WorkflowState.Failed)]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.AwaitingHuman)]
    public async Task Every_workflow_state_round_trips(WorkflowState target)
    {
        var stream = AsLegacy(NewTask());   // structure test: only a legacy history may complete by a plain state change
        stream.ChangeWorkflowState(target, "because", At(1));
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        var state = (await LoadAndReplayAsync(store, TaskOne)).State;

        Assert.Equal(target, state.WorkflowState);
        Assert.Equal(stream.State, state);
    }

    [Fact]
    public async Task Timestamps_are_stored_as_canonical_utc_and_load_as_the_same_instant()
    {
        var instant = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.5)).AddTicks(1234567);
        var stream = TaskStream.Create(TaskOne, "t", CompletionBoundary.WorkingTree, instant, new SequentialIdSource());
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        var loaded = (await store.LoadAsync(TaskOne))[0];

        Assert.Equal("2026-03-03T23:36:07.1234567Z", _db.Scalar<string>("SELECT timestamp_utc FROM task_events"));
        Assert.Contains("\"created_at\":\"2026-03-03T23:36:07.1234567Z\"", _db.Scalar<string>("SELECT payload_json FROM task_events"));
        Assert.Equal(instant, loaded.Timestamp);
        Assert.Equal(TimeSpan.Zero, loaded.Timestamp.Offset);
        Assert.Equal(instant.UtcTicks, loaded.Timestamp.UtcTicks);
    }

    [Fact]
    public async Task Rows_carry_the_kind_and_payload_schema_version()
    {
        var store = await _db.OpenStoreAsync();
        var stream = RichCompletedTask();
        await PersistAsync(store, stream);

        Assert.Equal(
            stream.Events.Select(e => e.Kind),
            Column<string>("event_kind"));

        // verification_recorded is at version 3 (provenance M2D, reuse reason M2F) and task_created at version 2 (acceptance requirement); every other kind is still version 1
        Assert.Equal(
            stream.Events.Select(e => e.Kind switch { "verification_recorded" => 3, "task_created" => 2, _ => 1 }).Select(v => (long)v),
            Column<long>("payload_schema_version"));
    }

    [Fact]
    public async Task Persisted_payload_is_independent_of_core_type_names()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, NewTask());

        var json = _db.Scalar<string>("SELECT payload_json FROM task_events");

        Assert.DoesNotContain("AgenticWorkflow", json);
        Assert.DoesNotContain("$type", json);
        Assert.Contains("\"initial_semantic_revision\"", json);
    }

    private List<T> Column<T>(string column)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM task_events ORDER BY sequence";

        var values = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            values.Add((T)Convert.ChangeType(reader.GetValue(0), typeof(T)));

        return values;
    }
}
