using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Tests;

/// <summary><c>aw status &lt;run&gt; --json</c>: the structured read model of a run on stdout, and nothing else.</summary>
public sealed class StatusJsonTests
{
    private static async Task<(CliHarness Harness, TaskId RunId, GateId GateId)> AwaitingAcceptanceAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedGateId());
        harness.ResetOutput();

        return result;
    }

    private static JsonElement Parse(string output) => JsonDocument.Parse(output).RootElement;

    [Fact]
    public async Task A_run_awaiting_acceptance_is_one_json_document_with_its_gate_and_operational_facts()
    {
        var (harness, runId, gateId) = await AwaitingAcceptanceAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("status", runId.ToString(), "--json");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, harness.Error.ToString());
        Assert.EndsWith("}\n", harness.Out.ToString());

        var root = Parse(harness.Out.ToString());   // the whole output is the document: nothing decorates it
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(runId.Value.ToString("D"), root.GetProperty("taskId").GetString());
        Assert.Equal("awaiting_human", root.GetProperty("state").GetString());
        Assert.Equal("accept", root.GetProperty("stage").GetString());
        Assert.Equal("accept_decision", root.GetProperty("pendingRequest").GetProperty("kind").GetString());
        Assert.Equal(gateId.Value.ToString("D"), root.GetProperty("pendingRequest").GetProperty("gateId").GetString());
        Assert.Equal(["accept", "request_changes", "abort"], root.GetProperty("pendingRequest").GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("passed", root.GetProperty("verification").GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("completion").GetProperty("isEligible").GetBoolean());

        var gate = root.GetProperty("gates")[0];
        Assert.True(gate.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(0, gate.GetProperty("staleReasons").GetArrayLength());

        var operational = root.GetProperty("operational");
        Assert.Equal(harness.Workspace, operational.GetProperty("project").GetProperty("workspace").GetString());
        Assert.Equal("not_live", operational.GetProperty("liveness").GetString());
        Assert.Equal("unchanged", operational.GetProperty("workingTree").GetString());
        Assert.Equal("AwaitingAcceptance", operational.GetProperty("lastProcess").GetProperty("outcome").GetString());
        Assert.True(operational.GetProperty("lastProcess").GetProperty("agreesWithWorkflow").GetBoolean());
        Assert.Equal(1, harness.Factory.Created);   // status started no runtime
    }

    [Fact]
    public async Task The_json_and_the_text_report_describe_the_same_run()
    {
        var (harness, runId, gateId) = await AwaitingAcceptanceAsync();
        using var _ = harness;

        await harness.RunAsync("status", runId.ToString());
        var text = harness.Out.ToString();
        harness.ResetOutput();
        await harness.RunAsync("status", runId.ToString(), "--json");
        var json = Parse(harness.Out.ToString());

        Assert.Contains("State: AWAITING_HUMAN", text);
        Assert.Contains("Verification: PASSED", text);
        Assert.Contains($"aw accept {runId} --gate {gateId}", text);
        Assert.Contains("working tree: unchanged since verification", text);
        Assert.Equal("awaiting_human", json.GetProperty("state").GetString());
        Assert.Equal("passed", json.GetProperty("verification").GetProperty("outcome").GetString());
        Assert.Equal(gateId.Value.ToString("D"), json.GetProperty("completion").GetProperty("acceptGateId").GetString());
        Assert.Equal("unchanged", json.GetProperty("operational").GetProperty("workingTree").GetString());
    }

    [Fact]
    public async Task A_changed_working_tree_is_reported_against_the_pending_accept()
    {
        var (harness, runId, _) = await AwaitingAcceptanceAsync();
        using var _ = harness;
        File.AppendAllText(Path.Combine(harness.Workspace, "src", "Feature.txt"), "tampered\n");

        await harness.RunAsync("status", runId.ToString(), "--json");

        Assert.Equal("changed", Parse(harness.Out.ToString()).GetProperty("operational").GetProperty("workingTree").GetString());
    }

    [Fact]
    public async Task A_completed_run_is_terminal_with_nothing_pending()
    {
        var (harness, runId, _) = await AwaitingAcceptanceAsync();
        using var _ = harness;
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString()));
        harness.ResetOutput();

        var exit = await harness.RunAsync("status", runId.ToString(), "--json");

        var root = Parse(harness.Out.ToString());
        Assert.Equal(0, exit);
        Assert.Equal("completed", root.GetProperty("state").GetString());
        Assert.Equal("none", root.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pendingRequest").ValueKind);
        Assert.Equal("accepted", root.GetProperty("completion").GetProperty("accept").GetString());
        Assert.Equal("Completed", root.GetProperty("operational").GetProperty("lastProcess").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_run_that_stopped_at_a_native_interaction_is_blocked_with_its_kind_and_recovery()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait("approve Bash: dotnet --list-sdks"));
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        var exit = await harness.RunAsync("status", runId.ToString(), "--json");

        var root = Parse(harness.Out.ToString());
        Assert.Equal(0, exit);
        Assert.Equal("blocked", root.GetProperty("state").GetString());
        Assert.Equal("native_interaction_unavailable", root.GetProperty("block").GetProperty("kind").GetString());
        Assert.Equal(["abort"], root.GetProperty("recovery").GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("NativeInteractionUnavailable", root.GetProperty("operational").GetProperty("lastProcess").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_json_is_the_same_text_every_time()
    {
        var (harness, runId, _) = await AwaitingAcceptanceAsync();
        using var _ = harness;

        await harness.RunAsync("status", runId.ToString(), "--json");
        var first = harness.Out.ToString();
        harness.ResetOutput();
        await harness.RunAsync("status", runId.ToString(), "--json");

        Assert.Equal(first, harness.Out.ToString());
    }

    // ---- failures ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_run_is_a_structured_not_found_with_exit_3_and_creates_nothing()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var id = Guid.CreateVersion7();

        var exit = await harness.RunAsync("status", id.ToString(), "--json");

        var root = Parse(harness.Out.ToString());
        Assert.Equal(3, exit);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("not_found", root.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("error").GetProperty("message").GetString()));
        Assert.False(root.TryGetProperty("state", out _));
        Assert.Equal(string.Empty, harness.Error.ToString());
        Assert.False(Directory.Exists(new RunStore(harness.Home).RunDirectory(new TaskId(id))));
    }

    [Fact]
    public async Task A_corrupt_run_is_a_structured_state_unreadable_with_exit_4_and_is_left_untouched()
    {
        var (harness, runId, _) = await AwaitingAcceptanceAsync();
        using var _ = harness;
        var database = new RunStore(harness.Home).DatabasePath(runId);
        File.WriteAllText(database, "this is not a sqlite database at all, just text that is long enough to look like a file");
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)));

        var exit = await harness.RunAsync("status", runId.ToString(), "--json");

        Assert.Equal(4, exit);
        Assert.Equal("state_unreadable", Parse(harness.Out.ToString()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(string.Empty, harness.Error.ToString());
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))));
    }

    [Fact]
    public async Task A_value_that_is_not_a_run_id_is_a_structured_invalid_task_id_with_exit_2()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        var exit = await harness.RunAsync("status", "not-a-run", "--json");

        Assert.Equal(2, exit);
        Assert.Equal("invalid_task_id", Parse(harness.Out.ToString()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(string.Empty, harness.Error.ToString());
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--json", "--json")]
    [InlineData("--format=json", "x")]
    [InlineData("--json=true", "x")]
    public async Task A_malformed_command_line_is_an_ordinary_usage_error_on_stderr(params string[] args)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        var exit = await harness.RunAsync(["status", .. args]);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, harness.Out.ToString());
        Assert.NotEqual(string.Empty, harness.Error.ToString());
    }

    // ---- the text form is unchanged ---------------------------------------------------------------------------------

    [Fact]
    public async Task Without_json_the_text_report_is_unchanged_and_help_documents_the_flag()
    {
        var (harness, runId, _) = await AwaitingAcceptanceAsync();
        using var _ = harness;

        await harness.RunAsync("status", runId.ToString());
        var text = harness.Out.ToString();
        harness.ResetOutput();
        await harness.RunAsync("status", "--help");

        Assert.StartsWith("Run: ", text);
        Assert.DoesNotContain("schemaVersion", text);
        Assert.Contains("aw status <run-id> [--json]", harness.Out.ToString());
    }
}
