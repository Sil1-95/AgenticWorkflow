using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The real CLI composition (commands, project resolution, SQLite event stream, Git fingerprints, local verification, DirectPathRunner, output)
/// driven with a scripted agent runtime. No model and no Herdr; everything else is the product's code.
/// </summary>
public sealed class RunIntegrationTests
{
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> WritesSymbol(string symbol = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), $"{symbol}\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    [Fact]
    public async Task A_run_verifies_through_the_direct_path_and_leaves_a_durable_stream_awaiting_acceptance()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WritesSymbol();

        var exit = await harness.RunAsync("run", "Add the scripted symbol");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.Contains($"aw accept {harness.PrintedRunId()} --gate ", output);
        Assert.Contains("PASS symbol_present  ControllerExecuted", output);
        Assert.Contains("Turns: 1", output);
        Assert.Contains("src/Feature.txt", output);
        Assert.Contains($"Run: {harness.PrintedRunId()}", output);
        Assert.StartsWith("Run: ", output);
        Assert.Equal(string.Empty, harness.Error.ToString());

        // one real session with the configured profile, started once
        var request = Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Equal("claude-code-local", request.Profile.Value);
        Assert.Equal(1, harness.Factory.Created);
        Assert.Equal(1, harness.Factory.Disposed);
        Assert.Single(harness.Factory.Runtime.Stopped);

        // the first prompt carries the task and the controller framing, not the workflow specification
        var prompt = Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Contains("Add the scripted symbol", prompt);
        Assert.Contains("Do not commit, push", prompt);
        Assert.Contains("symbol_present", prompt);
        Assert.Contains("Tooling: you may only run commands that are pre-approved", prompt);

        // the authoritative stream says the run awaits the human at a current gate; metadata and outcome only describe
        var store = new RunStore(harness.Home);
        var runId = harness.PrintedRunId();
        var task = (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(harness.PrintedGateId(), task.PendingGate!.Id);
        Assert.True(task.IsCurrent(task.PendingGate));
        Assert.Equal(VerificationOutcome.Passed, task.CurrentVerification!.Outcome);
        Assert.Equal("claude-code-local", task.GetBinding(new("root")).Profile.Id.Value);
        Assert.Equal(CompletionBoundary.WorkingTree, task.CompletionBoundary);
        Assert.Equal(harness.Workspace, store.ReadMetadata(runId).Metadata!.Workspace);
        Assert.Equal("AwaitingAcceptance", store.ReadOutcome(runId).Outcome!.Outcome);
    }

    [Fact]
    public async Task Run_state_lives_under_the_state_root_and_never_in_the_repository()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WritesSymbol();

        await harness.RunAsync("run", "task");

        var runId = harness.PrintedRunId();
        Assert.True(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), "workflow.db")));
        Assert.True(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), "metadata.json")));
        Assert.DoesNotContain(".db", harness.Git("status", "--short", "--untracked-files=all"));
        Assert.DoesNotContain("workflow.db", harness.Git("ls-files", "--others", "--exclude-standard"));
    }

    [Fact]
    public async Task A_failing_check_sends_a_repair_turn_to_the_same_session_and_then_awaits_acceptance()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (turn, workspace, ct) => WritesSymbol(turn == 1 ? "WRONG" : CliHarness.Symbol)(turn, workspace, ct);

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(17, exit);
        Assert.Equal(2, harness.Factory.Runtime.Prompts.Count);
        Assert.Contains("Controller verification failed", harness.Factory.Runtime.Prompts[1]);
        Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Contains("Turns: 2", harness.Out.ToString());
        Assert.Contains("repair after failed verification", harness.Out.ToString());
    }

    [Fact]
    public async Task Verification_that_never_passes_is_incomplete_distinct_and_names_the_failing_check()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(maxTurns: 2));
        harness.Factory.Runtime.Turn = WritesSymbol("WRONG");

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(12, exit);
        Assert.Contains("INCOMPLETE - VERIFICATION STILL FAILING", output);
        Assert.Contains("FAIL symbol_present", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.DoesNotContain("Resume is not yet supported", output);
        Assert.Contains("The workflow is BLOCKED, not RUNNING: verification is still failing", output);
        Assert.Contains("No in-place recovery command is currently available", output);
        Assert.DoesNotContain("aw continue", output);
    }

    [Fact]
    public async Task An_agent_waiting_for_a_person_with_nobody_attending_stops_truthfully_without_answering_anything()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait("approve Bash: rm -rf build"));

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(15, exit);
        Assert.Contains("AWAITING HUMAN", output);
        Assert.Contains("approve Bash: rm -rf build", output);
        Assert.Contains("BLOCKED - NATIVE INTERACTION UNAVAILABLE", output);
        Assert.Contains("can no longer be answered", output);
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("NATIVE INTERACTION REQUIRED", output);
        Assert.Contains("aw did not allow, deny or answer anything", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.DoesNotContain("Decision [d/a/q]", output);

        // the prompt was never answered: one turn was sent, and the only session call after it is the stop
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Single(harness.Factory.Runtime.Stopped);

        var store = new RunStore(harness.Home);
        var runId = harness.PrintedRunId();
        var task = (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.Verifications);
        Assert.Empty(task.PolicyDecisions);
        Assert.Equal("NativeInteractionUnavailable", store.ReadOutcome(runId).Outcome!.Outcome);
        Assert.Equal(1, harness.Factory.Disposed);
    }

    [Fact]
    public async Task A_startup_block_is_a_different_outcome_from_an_agent_block_and_says_what_to_do()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.StartFailure = new AgentStartupBlockedException("the agent is blocked at startup (permission: trust this folder?); this runtime never answers dialogs");

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(11, exit);
        Assert.Contains("BLOCKED - AGENT STARTUP", output);
        Assert.DoesNotContain("AGENT PERMISSION", output);
        Assert.Contains("not trusted", output);
        Assert.Contains("aw never accepts trust", output);
        Assert.Empty(harness.Factory.Runtime.Prompts);
        Assert.Equal(1, harness.Factory.Disposed);
    }

    [Fact]
    public async Task Another_runtime_failure_is_a_runtime_error()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(new AgentTurnOutcome(AgentTurnStatus.Error, "the agent process ended"));

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(14, exit);
        Assert.Contains("ERROR - RUNTIME FAILURE", harness.Out.ToString());
    }

    [Fact]
    public async Task Cancellation_stops_the_owned_session_releases_the_runtime_and_records_a_non_success_result()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        using var cancel = new CancellationTokenSource();

        harness.Factory.Runtime.Turn = async (_, _, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return AgentTurnOutcome.Done;
        };

        var exit = await harness.RunAsync(cancel.Token, "run", "task");

        Assert.Equal(130, exit);
        Assert.Contains("CANCELLED", harness.Out.ToString());
        Assert.Single(harness.Factory.Runtime.Stopped);
        Assert.Equal(1, harness.Factory.Disposed);

        var store = new RunStore(harness.Home);
        var runId = harness.PrintedRunId();
        Assert.Equal("Cancelled", store.ReadOutcome(runId).Outcome!.Outcome);

        var task = (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_task_can_come_from_a_file()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WritesSymbol();
        harness.Write("task.md", "  Implement it\nwith care.\n");

        var exit = await harness.RunAsync("run", "--task-file", "task.md");

        Assert.Equal(17, exit);
        Assert.Contains("Implement it\nwith care.", harness.Factory.Runtime.Prompts[0].Replace("\r\n", "\n"));
    }

    // ---- refusals happen before anything is created or launched ---------------------------------------------------------------

    [Fact]
    public async Task Missing_configuration_fails_before_any_run_or_agent_exists()
    {
        using var harness = new CliHarness();

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("No project configuration", harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
        Assert.False(Directory.Exists(Path.Combine(harness.Home, "runs")));
        Assert.False(File.Exists(Path.Combine(harness.Workspace, "agenticworkflow.json")));
    }

    [Fact]
    public async Task An_unavailable_profile_fails_before_launch_and_nothing_else_is_tried()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(profile: "codex-local"));

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("no fallback", harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_missing_prerequisite_fails_before_launch()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Unmet.Add(new("herdr (hc)", null, "'hc' was not found on PATH"));

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("'hc' was not found on PATH", harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
    }

    [Fact]
    public async Task State_inside_the_repository_is_refused()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Environment[CliEnvironment.HomeVariable] = Path.Combine(harness.Workspace, ".aw-state");

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("inside the workspace", harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
        Assert.False(Directory.Exists(Path.Combine(harness.Workspace, ".aw-state")));
    }

    [Fact]
    public async Task A_workspace_that_is_not_a_git_root_is_refused_before_a_run_is_created()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var plain = Path.Combine(harness.Home, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "agenticworkflow.json"), CliHarness.ValidConfig());

        var exit = await harness.RunAsync("run", "--workspace", plain, "task");

        Assert.Equal(2, exit);
        Assert.Contains("workspace cannot be used", harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
        Assert.False(Directory.Exists(Path.Combine(harness.Home, "runs")));
    }

    [Fact]
    public async Task The_run_never_touches_target_git_history_index_or_stash()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WritesSymbol();
        var head = harness.Git("rev-parse", "HEAD");
        var branches = harness.Git("branch", "-a");

        await harness.RunAsync("run", "task");

        Assert.Equal(head, harness.Git("rev-parse", "HEAD"));
        Assert.Equal(branches, harness.Git("branch", "-a"));
        Assert.Equal(string.Empty, harness.Git("diff", "--cached", "--name-only"));
        Assert.Equal(string.Empty, harness.Git("stash", "list"));
    }

    [Fact]
    public async Task Scope_comes_from_the_configuration_and_a_protected_change_fails_verification()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(maxTurns: 1));
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "locked.txt"), $"{CliHarness.Symbol}\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(12, exit);
        Assert.Contains("FAIL workspace_scope", harness.Out.ToString());
    }

    // ---- show ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Show_prints_the_resolved_project_and_launches_nothing()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: "flutter"));

        var exit = await harness.RunAsync("show");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains($"Workspace: {harness.Workspace}", output);
        Assert.Contains("Role: root", output);
        Assert.Contains("Agent profile: claude-code-local", output);
        Assert.Contains("Toolchain: flutter", output);
        Assert.Contains("Max turns: 3", output);
        Assert.Contains("Completion boundary:", output);
        Assert.Contains("symbol_present: git grep", output);
        Assert.Contains("  src/", output);
        Assert.Contains("  src/locked.txt", output);
        Assert.Contains("Bash(flutter analyze:*)", output);
        Assert.Contains($"State storage: {harness.Home}", output);
        Assert.Contains("Prerequisites:", output);

        Assert.Equal(0, harness.Factory.Created);
        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.False(Directory.Exists(Path.Combine(harness.Home, "runs")));
    }

    [Fact]
    public async Task Show_reports_an_invalid_configuration_and_a_missing_prerequisite_without_launching_anything()
    {
        using var invalid = new CliHarness(CliHarness.ValidConfig(toolchain: "npm"));
        Assert.Equal(2, await invalid.RunAsync("show"));
        Assert.Contains("'npm' is not supported", invalid.Error.ToString());

        using var unmet = new CliHarness(CliHarness.ValidConfig());
        unmet.Factory.Unmet.Add(new("evidence hook", null, "missing"));
        Assert.Equal(2, await unmet.RunAsync("show"));
        Assert.Contains("evidence hook: MISSING", unmet.Out.ToString());
        Assert.Equal(0, unmet.Factory.Created);
    }

    // ---- state root -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_state_root_defaults_to_local_app_data_and_an_absolute_override_wins()
    {
        Assert.Equal(@"C:\Users\x\AppData\Local\AgenticWorkflow", RunStore.ResolveRoot(_ => null, @"C:\Users\x\AppData\Local"));
        Assert.Equal(@"D:\state", RunStore.ResolveRoot(name => name == CliEnvironment.HomeVariable ? @"D:\state\" : null, @"C:\Users\x\AppData\Local"));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgenticWorkflow"),
            RunStore.ResolveRoot(_ => null, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }

    [Theory]
    [InlineData("relative\\dir")]
    [InlineData(".")]
    public void A_relative_state_override_is_refused(string value)
        => Assert.Throws<Cli.Configuration.ConfigurationException>(() => RunStore.ResolveRoot(name => name == CliEnvironment.HomeVariable ? value : null, "C:\\x"));

    [Fact]
    public void Inside_detection_is_path_aware_not_prefix_text()
    {
        Assert.True(RunStore.IsInside(@"C:\repo\state", @"C:\repo"));
        Assert.True(RunStore.IsInside(@"C:\repo", @"C:\repo"));
        Assert.False(RunStore.IsInside(@"C:\repo-state", @"C:\repo"));
    }
}
