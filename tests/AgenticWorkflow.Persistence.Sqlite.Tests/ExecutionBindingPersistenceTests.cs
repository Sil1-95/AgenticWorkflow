using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The execution configuration a role binding records (harness, model, reasoning, fingerprint): stored with the binding event, replayed exactly, and absent (null) for a binding recorded
/// before it existed, whose stored payload is byte-for-byte what it always was.
/// </summary>
public sealed class ExecutionBindingPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static string BindingPayload(TempDatabase db, long sequence)
        => db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'role_bound' AND sequence = $s", ("$s", sequence));

    [Fact]
    public async Task A_recorded_execution_configuration_is_stored_with_the_binding_and_replays_exactly()
    {
        var stream = NewTask();
        var execution = new ExecutionConfiguration("claude-code", "opus", "high", "abc123");
        stream.BindRole(Role("root", "code_edit"), Profile("claude-main", null, "code_edit"), At(1), execution);
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);

        var state = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        var binding = state.RoleBindings[new RoleId("root")];
        Assert.Equal(execution, binding.Execution);
        Assert.Equal(new ProfileId("claude-main"), binding.Profile.Id);
        var json = BindingPayload(_db, 2);
        Assert.Contains("\"harness\":\"claude-code\"", json);
        Assert.Contains("\"model\":\"opus\"", json);
        Assert.Contains("\"reasoning\":\"high\"", json);
        Assert.Contains("\"fingerprint\":\"abc123\"", json);
    }

    [Fact]
    public async Task A_profile_that_chooses_no_model_or_reasoning_records_them_as_absent_not_as_a_default()
    {
        var stream = NewTask();
        stream.BindRole(Role("root", "code_edit"), Profile("claude-code-local", null, "code_edit"), At(1), new ExecutionConfiguration("claude-code", null, null, "fp"));
        await PersistAsync(await _db.OpenStoreAsync(), stream);

        var execution = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State.RoleBindings[new RoleId("root")].Execution!;

        Assert.Null(execution.Model);
        Assert.Null(execution.Reasoning);
        var json = BindingPayload(_db, 2);
        Assert.DoesNotContain("\"model\"", json);
        Assert.DoesNotContain("\"reasoning\"", json);
    }

    [Fact]
    public async Task A_binding_recorded_before_execution_configuration_existed_has_no_execution_member_and_replays_with_null()
    {
        var stream = NewTask();
        stream.BindRole(Role("root", "code_edit"), Profile("claude-code-local", null, "code_edit"), At(1));
        await PersistAsync(await _db.OpenStoreAsync(), stream);

        var json = BindingPayload(_db, 2);
        var state = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.DoesNotContain("execution", json);
        Assert.Null(state.RoleBindings[new RoleId("root")].Execution);
    }

    [Fact]
    public async Task Two_roles_bound_to_different_configurations_each_keep_their_own()
    {
        var stream = NewTask();
        stream.BindRole(Role("root", "code_edit"), Profile("claude-main", null, "code_edit"), At(1), new ExecutionConfiguration("claude-code", "opus", "high", "a"));
        stream.BindRole(Role("reviewer", "repository_tools"), Profile("claude-review", null, "repository_tools"), At(2), new ExecutionConfiguration("claude-code", "sonnet", "low", "b"));
        await PersistAsync(await _db.OpenStoreAsync(), stream);

        var bindings = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State.RoleBindings;

        Assert.Equal("opus", bindings[new RoleId("root")].Execution!.Model);
        Assert.Equal("sonnet", bindings[new RoleId("reviewer")].Execution!.Model);
    }
}
