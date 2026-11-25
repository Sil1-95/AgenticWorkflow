using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Tests;

public sealed class StatusAndPackagingTests
{
    /// <summary>A run that passed verification and now awaits the human's ACCEPT (exit 17); nothing is decided yet.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId)> AwaitingAcceptanceRunAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        return (harness, harness.PrintedRunId());
    }

    /// <summary>A run that was verified and then explicitly accepted with <c>aw accept</c>.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId)> CompletedRunAsync()
    {
        var (harness, runId) = await AwaitingAcceptanceRunAsync();

        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString()));

        return (harness, runId);
    }

    private static async Task<(CliHarness Harness, TaskId RunId)> BlockedRunAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait("approve Bash: dotnet --list-sdks"));

        Assert.Equal(15, await harness.RunAsync("run", "task"));

        return (harness, harness.PrintedRunId());
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- aw status ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_replays_a_completed_run_in_a_fresh_app_instance()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        harness.Out.GetStringBuilder().Clear();

        var exit = await harness.RunAsync("status", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains($"Run: {runId}", output);
        Assert.Contains("State: COMPLETED", output);
        Assert.Contains("Verification: PASSED", output);
        Assert.Contains("PASS symbol_present", output);
        Assert.Contains($"Workspace: {harness.Workspace}", output);
        Assert.DoesNotContain("Resume is not yet supported", output);
        Assert.Equal(1, harness.Factory.Created); // status started no runtime
    }

    [Fact]
    public async Task Status_replays_a_run_that_stopped_at_a_native_interaction_as_blocked_and_does_not_invite_an_answer()
    {
        var (harness, runId) = await BlockedRunAsync();
        using var _ = harness;
        harness.Out.GetStringBuilder().Clear();

        var exit = await harness.RunAsync("status", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.DoesNotContain("COMPLETED", output.Replace("Completion: not reached", string.Empty));
        Assert.Contains("State: BLOCKED", output);
        Assert.DoesNotContain("AWAITING_HUMAN", output);
        Assert.Contains("Native agent interaction was required (permission: approve Bash: dotnet --list-sdks)", output);
        Assert.Contains("native session has ended", output);
        Assert.Contains("Verification: none recorded", output);
        Assert.Contains("Completion: not reached", output);
        Assert.Contains("Last run result: NativeInteractionUnavailable (exit 15)", output);
        Assert.Contains("Agent session: ended", output);
        Assert.Contains("can no longer be answered", output);
        Assert.Contains("aw abort", output);

        // it must not send the person to a session that no longer exists
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("respond in", output);
    }

    [Fact]
    public async Task Status_of_a_run_left_awaiting_human_by_a_dead_process_says_the_answer_is_no_longer_possible()
    {
        var (harness, runId) = await BlockedRunAsync();
        using var _ = harness;
        await ReopenAsAwaitingHumanAsync(harness, runId);   // what a process killed mid-wait leaves behind
        harness.Out.GetStringBuilder().Clear();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var output = harness.Out.ToString();
        Assert.Contains("State: AWAITING_HUMAN", output);
        Assert.Contains("Agent session: not live", output);
        Assert.Contains("can no longer be answered", output);
        Assert.DoesNotContain("hc attach", output);
    }

    private static async Task ReopenAsAwaitingHumanAsync(CliHarness harness, TaskId runId)
    {
        var tasks = await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None);
        var snapshot = (await tasks.FindAsync(runId, CancellationToken.None))!;
        await tasks.ChangeWorkflowStateAsync(snapshot, WorkflowState.AwaitingHuman, "The agent is waiting for a person in its own interface.", CancellationToken.None);
    }

    [Fact]
    public async Task Status_never_modifies_the_run_files()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        var store = new RunStore(harness.Home);
        var files = new[] { store.DatabasePath(runId), Path.Combine(store.RunDirectory(runId), "metadata.json"), Path.Combine(store.RunDirectory(runId), "outcome.json") };
        var before = files.Select(Hash).ToArray();

        await harness.RunAsync("status", runId.ToString());
        await harness.RunAsync("status", runId.ToString());

        Assert.Equal(before, files.Select(Hash).ToArray());
    }

    [Fact]
    public async Task The_event_stream_wins_over_metadata_and_over_a_contradicting_recorded_outcome()
    {
        var (harness, runId) = await BlockedRunAsync();
        using var _ = harness;
        var store = new RunStore(harness.Home);
        store.WriteOutcome(runId, new RunOutcomeRecord(RunOutcomeRecord.CurrentVersion, "Completed", 0, "forged", DateTimeOffset.UtcNow, 0));
        harness.Out.GetStringBuilder().Clear();

        await harness.RunAsync("status", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Contains("State: BLOCKED", output);
        Assert.DoesNotContain("State: COMPLETED", output);
        Assert.Contains("Warning: the recorded run result disagrees with the event stream; the event stream is authoritative.", output);
    }

    [Fact]
    public async Task A_run_with_no_recorded_outcome_still_reports_its_stream_state_and_says_so()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        File.Delete(Path.Combine(new RunStore(harness.Home).RunDirectory(runId), "outcome.json"));
        harness.Out.GetStringBuilder().Clear();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("State: COMPLETED", harness.Out.ToString());
        Assert.Contains("Last run result: not recorded", harness.Out.ToString());
    }

    [Fact]
    public async Task Missing_metadata_does_not_hide_the_stream_state()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        File.Delete(Path.Combine(new RunStore(harness.Home).RunDirectory(runId), "metadata.json"));
        harness.Out.GetStringBuilder().Clear();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("State: COMPLETED", harness.Out.ToString());
        Assert.Contains("Workspace: unknown (", harness.Out.ToString());
    }

    [Fact]
    public async Task A_missing_run_is_reported_as_not_found_and_nothing_is_created()
    {
        using var harness = new CliHarness();
        var id = Guid.CreateVersion7();

        var exit = await harness.RunAsync("status", id.ToString());

        Assert.Equal(3, exit);
        Assert.Contains("RUN NOT FOUND", harness.Error.ToString());
        Assert.False(Directory.Exists(Path.Combine(harness.Home, "runs")));
    }

    [Fact]
    public async Task A_corrupt_event_stream_is_unreadable_and_is_left_exactly_as_it_was()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        var database = new RunStore(harness.Home).DatabasePath(runId);
        File.WriteAllText(database, "this is not a sqlite database at all, just text that is long enough to look like a file");
        var before = Hash(database);

        var exit = await harness.RunAsync("status", runId.ToString());

        Assert.Equal(4, exit);
        Assert.Contains("RUN STATE UNREADABLE", harness.Error.ToString());
        Assert.Equal(before, Hash(database));
    }

    [Fact]
    public async Task An_empty_or_missing_database_is_unreadable_not_silently_initialised()
    {
        using var harness = new CliHarness();
        var store = new RunStore(harness.Home);
        var id = new TaskId(Guid.CreateVersion7());
        Directory.CreateDirectory(store.RunDirectory(id));
        File.WriteAllBytes(store.DatabasePath(id), []);

        Assert.Equal(4, await harness.RunAsync("status", id.ToString()));
        Assert.Equal(0, new FileInfo(store.DatabasePath(id)).Length);

        var other = new TaskId(Guid.CreateVersion7());
        Directory.CreateDirectory(store.RunDirectory(other));

        Assert.Equal(4, await harness.RunAsync("status", other.ToString()));
        Assert.False(File.Exists(store.DatabasePath(other)));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0197d2e15c3a7b8e9f102a3b4c5d6e7f")]
    public async Task A_malformed_run_id_is_a_usage_error(string id)
    {
        using var harness = new CliHarness();

        Assert.Equal(2, await harness.RunAsync("status", id));
        Assert.Contains("is not a run id", harness.Error.ToString());
    }

    // ---- command line, help and exit-code stability ---------------------------------------------------------------------------

    [Fact]
    public void Exit_codes_are_a_stable_documented_contract()
    {
        Assert.Equal(0, ExitCodes.Success);
        Assert.Equal(1, ExitCodes.InternalError);
        Assert.Equal(2, ExitCodes.UsageOrConfiguration);
        Assert.Equal(3, ExitCodes.RunNotFound);
        Assert.Equal(4, ExitCodes.RunStateUnreadable);
        Assert.Equal(11, ExitCodes.StartupBlock);
        Assert.Equal(12, ExitCodes.VerificationUnresolved);
        Assert.Equal(13, ExitCodes.StaleWorkflowState);
        Assert.Equal(14, ExitCodes.RuntimeError);
        Assert.Equal(15, ExitCodes.NativeInteractionUnavailable);
        Assert.Equal(16, ExitCodes.Aborted);
        Assert.Equal(130, ExitCodes.Cancelled);

        Assert.Equal(17, ExitCodes.AwaitingAcceptance);
        Assert.Equal(18, ExitCodes.GateDecisionRefused);
        Assert.Equal(
            [0, 11, 12, 13, 14, 15, 16, 17, 130],
            new[] { RunOutcome.Completed, RunOutcome.StartupBlock, RunOutcome.VerificationUnresolved, RunOutcome.StaleWorkflowState, RunOutcome.RuntimeError, RunOutcome.NativeInteractionUnavailable, RunOutcome.Aborted, RunOutcome.AwaitingAcceptance, RunOutcome.Cancelled }
                .Select(ExitCodes.For));

        var all = typeof(ExitCodes).GetFields().Select(f => (int)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public async Task Help_and_version_need_no_project_and_help_documents_every_exit_code()
    {
        using var harness = new CliHarness();

        Assert.Equal(0, await harness.RunAsync("--help"));
        var help = harness.Out.ToString();

        foreach (var code in new[] { "11", "12", "13", "14", "15", "16", "17", "18", "130" })
            Assert.Contains($" {code} ", help);

        Assert.DoesNotContain(" 10 ", help); // 10 was "agent blocked"; a blocked agent is a native human interaction now

        Assert.Contains("run", help);
        Assert.Contains("status", help);
        Assert.Contains("show", help);
        Assert.Contains("abort", help);
        Assert.Contains("accept", help);
        Assert.Contains("request-changes", help);

        harness.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await harness.RunAsync("--version"));
        Assert.Matches(@"^aw \d+\.\d+\.\d+\s*$", harness.Out.ToString());
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("run")]
    [InlineData("run", "a", "b")]
    [InlineData("run", "--task-file", "x.md", "also text")]
    [InlineData("run", "--unknown", "x", "task")]
    [InlineData("status")]
    [InlineData("status", "a", "b")]
    [InlineData("show", "extra")]
    public async Task Bad_command_lines_are_usage_errors_that_launch_nothing(params string[] args)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, await harness.RunAsync(args));
        Assert.Equal(0, harness.Factory.Created);
        Assert.NotEqual(string.Empty, harness.Error.ToString());
    }

    // ---- output provenance and the run report ---------------------------------------------------------------------------------

    [Fact]
    public async Task Run_output_separates_controller_evidence_from_agent_claims()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(new AgentTurnOutcome(AgentTurnStatus.Done, "I ran all the tests and they pass, trust me."));
        };

        await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.DoesNotContain("trust me", output);
        Assert.Matches(@"PASS symbol_present +ControllerExecuted", output);
    }

    [Fact]
    public async Task Run_report_and_status_say_why_each_check_was_executed_by_the_controller()
    {
        var (harness, runId) = await AwaitingAcceptanceRunAsync();
        using var _ = harness;

        // the CLI harness has no evidence source, so the reason is the stable "nothing to reuse" one, in the run report and in a later aw status
        Assert.Matches(@"PASS symbol_present +ControllerExecuted +\(no_evidence_available\)", harness.Out.ToString());

        harness.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        Assert.Matches(@"PASS symbol_present +ControllerExecuted +\(no_evidence_available\)", harness.Out.ToString());
    }

    [Fact]
    public void A_reused_check_shows_what_was_observed_and_why_and_never_any_output()
    {
        var state = new ImplementationFingerprint("git-worktree-sha256:abcdef0123456789");
        var run = new VerificationRun(
            new VerificationRunId(Guid.NewGuid()),
            new TaskId(Guid.NewGuid()),
            new ImplementationRevisionId(Guid.NewGuid()),
            VerificationOutcome.Failed,
            [
                new VerificationCheckResult(new VerificationCheckId("build"), VerificationOutcome.Passed, "agent-observed", 0, new AgentObservation("claude-code", "toolu_01", "dotnet build", 1800, state), "agent_observed_current_state"),
                new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Failed, "exit 1 (expected 0)", 1, evidenceReason: "state_mismatch"),
                new VerificationCheckResult(new VerificationCheckId("workspace_stable"), VerificationOutcome.Passed, "stable", 0),
                new VerificationCheckResult(new VerificationCheckId("legacy"), VerificationOutcome.Passed, "exit 0", 0),
            ],
            DateTimeOffset.UnixEpoch);
        var text = new System.Text.StringBuilder();

        RunReportRenderer.AppendChecks(text, run);

        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(@"PASS build +AgentObserved +\(agent_observed_current_state\)$", lines[0]);
        Assert.Contains("observed: claude-code ran `dotnet build` (tool use toolu_01, 1.8s); not re-executed", lines[1]);
        Assert.Matches(@"FAIL tests +ControllerExecuted +\(state_mismatch\) +exit 1 \(expected 0\)$", lines[2]);
        Assert.Matches(@"PASS workspace_stable +ControllerExecuted$", lines[3]);
        Assert.Matches(@"PASS legacy +ControllerExecuted$", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Run_progress_names_each_phase_on_the_output_in_order()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        var order = new[] { "Run: ", "Workspace: ", "Profile: ", "State: ", "turn 1: working", "verifying symbol_present", "VERIFIED - AWAITING ACCEPT" };
        var position = -1;

        foreach (var marker in order)
        {
            var next = output.IndexOf(marker, position + 1, StringComparison.OrdinalIgnoreCase);
            Assert.True(next > position, $"'{marker}' should appear after the previous marker in:\n{output}");
            position = next;
        }
    }

    [Fact]
    public async Task Each_outcome_class_has_its_own_heading_and_only_an_accepted_run_is_called_completed()
    {
        var headings = Enum.GetValues<RunOutcome>().Select(RunReportRenderer.Heading).ToArray();

        Assert.Equal(headings.Length, headings.Distinct().Count());
        Assert.Single(headings, h => h.Contains("COMPLETED"));
        await Task.CompletedTask;
    }

    // ---- hook packaging -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_hook_is_resolved_beside_the_executable_never_from_a_source_tree()
    {
        var install = Path.Combine(Path.GetTempPath(), "aw-install-" + Guid.NewGuid().ToString("N")[..8]);
        var factory = new HerdrAgentRuntimeFactory(install, new ExecutableLocator(_ => null));

        Assert.Equal(Path.Combine(install, "AgenticWorkflow.ClaudeEvidenceHook.exe"), factory.HookPath);
        Assert.DoesNotContain("bin", factory.HookPath.Split(Path.DirectorySeparatorChar).Skip(Path.GetTempPath().Split(Path.DirectorySeparatorChar).Length));
        Assert.DoesNotContain("src", factory.HookPath.Split(Path.DirectorySeparatorChar).Skip(Path.GetTempPath().Split(Path.DirectorySeparatorChar).Length));
    }

    [Fact]
    public void A_missing_hook_or_program_is_reported_as_an_unmet_prerequisite()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var project = Configuration.ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);
        var install = Path.Combine(harness.Home, "empty-install");
        Directory.CreateDirectory(install);

        var prerequisites = new HerdrAgentRuntimeFactory(install, new ExecutableLocator(_ => string.Empty)).CheckPrerequisites(project);

        Assert.All(prerequisites, p => Assert.False(p.IsMet));
        Assert.Contains(prerequisites, p => p.Problem!.Contains("'hc' was not found"));
        Assert.Contains(prerequisites, p => p.Problem!.Contains("'claude' was not found"));
        Assert.Contains(prerequisites, p => p.Problem!.Contains("must be installed next to aw"));
    }

    [Fact]
    public void The_built_cli_ships_the_hook_in_its_own_output_directory()
    {
        var directory = AppContext.BaseDirectory;

        foreach (var file in new[] { "aw.exe", "aw.dll", "AgenticWorkflow.ClaudeEvidenceHook.exe", "AgenticWorkflow.ClaudeEvidenceHook.dll" })
            Assert.True(File.Exists(Path.Combine(directory, file)), $"{file} should sit beside the CLI in {directory}");
    }

    [Fact]
    public void The_production_composition_points_at_the_install_directory()
    {
        var hook = new HerdrAgentRuntimeFactory(AppContext.BaseDirectory, new ExecutableLocator(_ => null)).HookPath;

        Assert.True(File.Exists(hook));
        Assert.DoesNotContain(Path.Combine("AgenticWorkflow.ClaudeEvidenceHook", "bin"), hook);
    }

    [Fact]
    public async Task No_source_file_of_the_cli_hardcodes_a_build_tree_path()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "AgenticWorkflow.Cli")))
            root = Path.GetDirectoryName(root);

        Assert.NotNull(root);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "AgenticWorkflow.Cli"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("bin\\Debug", text);
            Assert.DoesNotContain("bin/Debug", text);
            Assert.DoesNotContain("AgenticWorkflow.ClaudeEvidenceHook\\bin", text);
        }
    }

    // ---- through the real executable ------------------------------------------------------------------------------------------

    private sealed record ProcessResult(int ExitCode, string Out, string Error);

    private static ProcessResult RunAw(string home, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "aw.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.Environment["AGENTICWORKFLOW_HOME"] = home;

        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "aw.exe did not exit");

        return new ProcessResult(process.ExitCode, output, error.Result);
    }

    [Fact]
    public async Task A_new_aw_process_reports_the_completed_run_a_previous_composition_recorded()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;

        var status = RunAw(harness.Home, "status", runId.ToString());

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("State: COMPLETED", status.Out);
        Assert.Contains("Verification: PASSED", status.Out);
    }

    [Fact]
    public async Task A_new_aw_process_reports_a_run_that_stopped_at_a_native_interaction_as_blocked()
    {
        var (harness, runId) = await BlockedRunAsync();
        using var _ = harness;

        var status = RunAw(harness.Home, "status", runId.ToString());

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("State: BLOCKED", status.Out);
        Assert.Contains("NativeInteractionUnavailable", status.Out);
    }

    [Fact]
    public void The_real_executable_reports_exit_codes_for_version_unknown_runs_and_bad_usage()
    {
        var home = Path.Combine(Path.GetTempPath(), "aw-exe-home-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Assert.Equal(0, RunAw(home, "--version").ExitCode);
            Assert.Equal(2, RunAw(home).ExitCode);
            Assert.Equal(2, RunAw(home, "nonsense").ExitCode);
            Assert.Equal(3, RunAw(home, "status", Guid.CreateVersion7().ToString()).ExitCode);
            Assert.False(Directory.Exists(home));
        }
        finally
        {
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void The_real_executable_refuses_a_run_without_configuration_before_launching_anything()
    {
        var empty = Path.Combine(Path.GetTempPath(), "aw-exe-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);

        try
        {
            var result = RunAw(Path.Combine(empty, "home"), "run", "--workspace", empty, "task");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("No project configuration", result.Error);
            Assert.False(Directory.Exists(Path.Combine(empty, "home")));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Recorded_outcome_files_are_versioned_json()
    {
        var (harness, runId) = await CompletedRunAsync();
        using var _ = harness;
        var directory = new RunStore(harness.Home).RunDirectory(runId);

        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "metadata.json")));
        using var outcome = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "outcome.json")));

        Assert.Equal(1, metadata.RootElement.GetProperty("metadataVersion").GetInt32());
        Assert.Equal(runId.ToString(), metadata.RootElement.GetProperty("runId").GetString());
        Assert.Equal(1, outcome.RootElement.GetProperty("outcomeVersion").GetInt32());
        Assert.Equal("Completed", outcome.RootElement.GetProperty("outcome").GetString());
    }
}
