using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The lifecycle correction found by dogfood: a first agent attempt that ends on a runtime failure (a turn timeout, a dead agent process, a session that never
/// started) is recorded in the event stream as BLOCKED, never left RUNNING with nothing working on it. Model-free: the failure is scripted, the state is the real
/// durable layout, and every claim about the later state is read in a fresh process (<c>aw status</c>, <c>aw runs</c>).
/// </summary>
public sealed class RuntimeFailureCliTests
{
    private const string Cause = "the turn timed out after 00:30:00";

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static async Task<(CliHarness Harness, TaskId RunId)> FailedRunAsync(params string[] extraArgs)
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(new AgentTurnOutcome(AgentTurnStatus.Error, Cause));

        Assert.Equal(14, await harness.RunAsync([.. new[] { "run" }, .. extraArgs, "task"]));

        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        return (harness, runId);
    }

    [Fact]
    public async Task A_runtime_failure_in_the_first_attempt_leaves_the_run_BLOCKED_not_RUNNING()
    {
        var (harness, runId) = await FailedRunAsync();
        using var _ = harness;

        var task = await LoadAsync(harness, runId);

        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.StartsWith(RuntimeFailureBlock.BlockedReasonPrefix, task.StateReason);
        Assert.Contains(Cause, task.StateReason);
        Assert.Null(task.PendingGate);
        Assert.Empty(task.Verifications);
        Assert.False(RecoverableContinuation.Find(task) is not null, "a first attempt has no recorded human input to retry, so it is not M4D's recoverable block");
    }

    [Fact]
    public async Task The_end_of_run_report_says_BLOCKED_and_promises_no_recovery_it_does_not_have()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(new AgentTurnOutcome(AgentTurnStatus.Error, Cause));

        Assert.Equal(14, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();

        var output = harness.Out.ToString();
        Assert.Contains("ERROR - RUNTIME FAILURE", output);
        Assert.Contains("The workflow is BLOCKED, not RUNNING", output);
        Assert.Contains("No in-place recovery command is currently available", output);
        Assert.Contains($"aw abort {runId}", output);
        Assert.DoesNotContain("aw continue", output);
        Assert.DoesNotContain("Resume is not yet supported", output);
    }

    [Fact]
    public async Task A_fresh_process_status_shows_BLOCKED_with_the_cause_and_no_unsupported_action()
    {
        var (harness, runId) = await FailedRunAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: BLOCKED", status);
        Assert.DoesNotContain("State: RUNNING", status);
        Assert.Contains(RuntimeFailureBlock.BlockedReasonPrefix, status);
        Assert.Contains(Cause, status);
        Assert.Contains("No in-place recovery command is currently available", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.Contains("Last run result: RuntimeError (exit 14)", status);

        // nothing it advertises is a command that cannot work on this run
        Assert.DoesNotContain("aw continue", status);
        Assert.DoesNotContain("Resume is not yet supported", status);
        Assert.DoesNotContain("Pending work:", status);
    }

    [Fact]
    public async Task Continue_is_refused_for_it_because_it_retries_only_recorded_human_input()
    {
        var (harness, runId) = await FailedRunAsync();
        using var _ = harness;

        Assert.Equal(18, await harness.RunAsync("continue", runId.ToString()));

        Assert.Contains("not a recoverable continuation", harness.Error.ToString() + harness.Out);
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task Runs_simply_reflects_the_recorded_state()
    {
        var (harness, runId) = await FailedRunAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("runs"));

        var output = harness.Out.ToString();
        Assert.Contains($"{runId}  BLOCKED", output);
        Assert.DoesNotContain("RUNNING", output);
    }

    [Fact]
    public async Task Abort_cancels_the_blocked_run()
    {
        var (harness, runId) = await FailedRunAsync();
        using var _ = harness;

        Assert.Equal(16, await harness.RunAsync("abort", runId.ToString()));

        Assert.Equal(WorkflowState.Cancelled, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task A_session_that_cannot_start_is_blocked_too()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.StartFailure = new AgentRuntimeException("herdr is unreachable");

        Assert.Equal(14, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Contains("herdr is unreachable", task.StateReason);
    }

    [Fact]
    public async Task The_cause_is_bounded_and_carries_no_terminal_control_text()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var noisy = "boom \u001b[31mred\u001b[0m\n" + new string('x', 5000);
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(new AgentTurnOutcome(AgentTurnStatus.Error, noisy));

        Assert.Equal(14, await harness.RunAsync("run", "task"));

        var reason = (await LoadAsync(harness, harness.PrintedRunId())).StateReason;
        Assert.DoesNotContain('\u001b', reason);
        Assert.DoesNotContain('\n', reason);
        Assert.True(reason.Length < 700, $"reason is {reason.Length} chars");
    }

    // ---- a native interaction that was live is still AWAITING_HUMAN; only its loss is a block --------------------------------

    [Fact]
    public async Task A_live_native_wait_is_AWAITING_HUMAN_and_losing_it_before_anyone_answers_ends_BLOCKED()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        TaskId? runId = null;
        WorkflowState? whileWaiting = null;
        var step = 0;

        harness.Factory.Runtime.Turn = async (_, _, _) =>
        {
            if (step++ == 0)
                return ScriptedRuntime.Wait("approve Bash: dotnet --list-sdks");

            // the second wait on the same turn: the person has not answered, and the workflow says it is waiting for them
            runId = harness.PrintedRunId();
            whileWaiting = (await LoadAsync(harness, runId.Value)).WorkflowState;

            return new AgentTurnOutcome(AgentTurnStatus.Error, Cause);
        };

        Assert.Equal(14, await harness.RunAsync("run", "--interactive", "task"));

        Assert.Equal(WorkflowState.AwaitingHuman, whileWaiting);

        var task = await LoadAsync(harness, runId!.Value);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(RuntimeFailureBlock.IsBlockedBy(task));
    }

    // ---- what must not change --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_that_verifies_with_no_acceptance_still_completes_and_records_nothing_extra()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        Assert.Equal(0, await harness.RunAsync("run", "task"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.False(RuntimeFailureBlock.IsBlockedBy(task));
    }

    // ---- verification that keeps failing: a verdict, but the same truth about the state ----------------------------------------

    private static async Task<(CliHarness Harness, TaskId RunId)> UnresolvedRunAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig(maxTurns: 1));
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(12, await harness.RunAsync("run", "task"));

        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        return (harness, runId);
    }

    [Fact]
    public async Task Verification_that_keeps_failing_ends_BLOCKED_with_its_own_reason_recorded_once()
    {
        var (harness, runId) = await UnresolvedRunAsync();
        using var _ = harness;

        var task = await LoadAsync(harness, runId);

        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.StartsWith(UnresolvedVerificationBlock.BlockedReasonPrefix, task.StateReason);
        Assert.Contains("turn_limit_reached", task.StateReason);
        Assert.False(RuntimeFailureBlock.IsBlockedBy(task));
        Assert.Null(RecoverableContinuation.Find(task));
    }

    [Fact]
    public async Task A_fresh_process_status_and_runs_show_it_BLOCKED_with_the_reason_and_only_abort()
    {
        var (harness, runId) = await UnresolvedRunAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: BLOCKED", status);
        Assert.Contains("Reason: Verification unresolved", status);
        Assert.Contains("Verification: FAILED", status);
        Assert.Contains("No in-place recovery command is currently available", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("aw continue", status);
        Assert.DoesNotContain("Resume is not yet supported", status);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("runs"));
        Assert.Contains($"{runId}  BLOCKED", harness.Out.ToString());
    }

    [Fact]
    public async Task Continue_refuses_it_and_abort_cancels_it()
    {
        var (harness, runId) = await UnresolvedRunAsync();
        using var _ = harness;

        Assert.Equal(18, await harness.RunAsync("continue", runId.ToString()));
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);

        Assert.Equal(16, await harness.RunAsync("abort", runId.ToString()));
        Assert.Equal(WorkflowState.Cancelled, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task A_run_recorded_before_this_fix_still_replays_as_it_was_and_status_does_not_guess_a_state()
    {
        // The D4 shape: the process ended on a runtime failure before this fix existed, so the stream says RUNNING and nothing else. Nothing reinterprets it.
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var legacy = await LegacyRunningRunAsync(harness, new RunStore(harness.Home));

        Assert.Equal(0, await harness.RunAsync("status", legacy.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: RUNNING", status);
        Assert.Contains("no aw process currently holds this run", status);
        Assert.DoesNotContain("Resume is not yet supported", status);
        Assert.DoesNotContain($"aw continue {legacy}", status);
    }

    /// <summary>A durable run exactly as an older build left it after a runtime failure: created and bound, nothing else, process gone.</summary>
    private static async Task<TaskId> LegacyRunningRunAsync(CliHarness harness, RunStore store)
    {
        var runId = new TaskId(Guid.CreateVersion7());
        var project = ProjectOf(harness);

        store.Create(runId, new RunMetadata(RunMetadata.CurrentVersion, runId.ToString(), harness.Workspace, DateTimeOffset.UtcNow, project.ConfigPath, project.ConfigSha256));

        var tasks = await store.OpenNewAsync(runId, CancellationToken.None);
        var created = await tasks.CreateAsync(runId, "task", CompletionBoundary.WorkingTree, AcceptanceRequirement.HumanRequired, CancellationToken.None);
        await tasks.BindRoleAsync(created.Snapshot, DirectPathRoles.Root, project.ProfileDefinition, CancellationToken.None);

        return runId;
    }

    private static Cli.Configuration.ResolvedProject ProjectOf(CliHarness harness)
        => Cli.Configuration.ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);
}
