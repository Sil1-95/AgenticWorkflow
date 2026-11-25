using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The CLI never owns an agent's ordinary tool permission. When the agent waits for a person in its own interface, <c>aw</c> says so and where to
/// answer, keeps waiting (attended) or stops truthfully (nobody attending), and has no prompt, no keys and no decision of its own.
/// </summary>
public sealed class NativeInteractionCliTests
{
    /// <summary>The agent waits, the person acts natively (invisible to aw), the agent resumes and finishes the same turn.</summary>
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> WaitsThenFinishes(List<string> calls)
    {
        var step = 0;

        return (_, workspace, _) =>
        {
            calls.Add($"wait {step}");

            switch (step++)
            {
                case 0:
                    return Task.FromResult(ScriptedRuntime.Wait("approve Bash: dotnet --list-sdks"));
                case 1:
                    return Task.FromResult(AgentTurnOutcome.Resumed);
                default:
                    Directory.CreateDirectory(Path.Combine(workspace, "src"));
                    File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), $"{CliHarness.Symbol}\n");
                    return Task.FromResult(AgentTurnOutcome.Done);
            }
        };
    }

    [Theory]
    [InlineData("--interactive", false)]
    [InlineData("", true)]   // attached to a terminal: a person is assumed
    public async Task An_attended_run_reports_the_native_wait_keeps_waiting_and_then_awaits_acceptance_in_the_same_turn(string flag, bool terminal)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.IsTerminal = terminal;
        var calls = new List<string>();
        harness.Factory.Runtime.Turn = WaitsThenFinishes(calls);

        var args = flag.Length == 0 ? new[] { "run", "task" } : ["run", flag, "task"];
        var exit = await harness.RunAsync(args);

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("NATIVE INTERACTION REQUIRED", output);
        Assert.Contains("permission: approve Bash: dotnet --list-sdks", output);
        Assert.Contains("hc attach scripted-1", output);
        Assert.Contains("agent resumed after the native interaction", output);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.Contains("Turns: 1", output);

        // aw has no prompt of its own and asked for no input
        Assert.DoesNotContain("Decision [d/a/q]", output);
        Assert.Equal(string.Empty, harness.Error.ToString());

        // one prompt, never an answer; the wait and resume were both observed on the same turn
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Equal(["wait 0", "wait 1", "wait 2"], calls);

        var store = new RunStore(harness.Home);
        var runId = harness.PrintedRunId();
        var task = (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.NotNull(task.PendingGate);
        Assert.Empty(task.PolicyDecisions);
    }

    [Fact]
    public async Task Non_interactive_overrides_a_terminal_and_stops_at_the_wait_with_exit_15()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.IsTerminal = true;
        harness.Factory.Runtime.Turn = WaitsThenFinishes([]);

        var exit = await harness.RunAsync("run", "--non-interactive", "task");

        var output = harness.Out.ToString();
        Assert.Equal(15, exit);
        Assert.Contains("nobody is attending this run, so it will stop", output);
        Assert.Contains("BLOCKED - NATIVE INTERACTION UNAVAILABLE", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Single(harness.Factory.Runtime.Stopped);
    }

    [Fact]
    public async Task Without_a_terminal_and_without_a_flag_nobody_is_assumed_to_attend()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WaitsThenFinishes([]);

        Assert.Equal(15, await harness.RunAsync("run", "task"));
    }

    [Fact]
    public async Task Interactive_and_non_interactive_together_is_a_usage_error()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, await harness.RunAsync("run", "--interactive", "--non-interactive", "task"));
        Assert.Equal(0, harness.Factory.Created);
    }

    [Fact]
    public async Task Terminal_control_characters_in_the_agents_own_text_are_never_echoed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait("approve Bash: echo \u001b[2Jgone"));

        await harness.RunAsync("run", "--non-interactive", "task");

        Assert.DoesNotContain('\u001b', harness.Out.ToString());
    }

    // ---- aw abort -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Abort_cancels_a_blocked_run_and_decides_nothing_for_the_agent()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        Assert.Equal(WorkflowState.Blocked, (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task.WorkflowState);
        harness.Out.GetStringBuilder().Clear();

        var exit = await harness.RunAsync("abort", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(16, exit);
        Assert.Contains("CANCELLED", output);
        Assert.Contains("Nothing the agent was waiting for was allowed or denied", output);

        var store = new RunStore(harness.Home);
        var task = (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;
        Assert.Equal(WorkflowState.Cancelled, task.WorkflowState);
        Assert.Empty(task.PolicyDecisions);
        Assert.Equal("Aborted", store.ReadOutcome(runId).Outcome!.Outcome);

        harness.Out.GetStringBuilder().Clear();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("State: CANCELLED", harness.Out.ToString());
    }

    [Fact]
    public async Task Abort_refuses_a_run_that_is_already_completed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WaitsThenFinishes([]);
        harness.IsTerminal = true;
        Assert.Equal(17, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString()));
        harness.ResetOutput();

        var exit = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(2, exit);
        Assert.Contains("not BLOCKED or AWAITING_HUMAN", harness.Error.ToString());

        var store = new RunStore(harness.Home);
        Assert.Equal(WorkflowState.Completed, (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task.WorkflowState);
    }

    [Fact]
    public async Task Abort_also_cancels_a_run_left_awaiting_human_by_a_process_that_died()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        var store = new RunStore(harness.Home);
        var tasks = await store.OpenExistingAsync(runId, CancellationToken.None);
        await tasks.ChangeWorkflowStateAsync((await tasks.FindAsync(runId, CancellationToken.None))!, WorkflowState.AwaitingHuman, "The agent is waiting for a person in its own interface.", CancellationToken.None);

        Assert.Equal(16, await harness.RunAsync("abort", runId.ToString()));
        Assert.Equal(WorkflowState.Cancelled, (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task.WorkflowState);
    }

    [Fact]
    public async Task Abort_refuses_a_run_whose_session_is_still_live_in_another_process()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();

        using (new RunStore(harness.Home).AcquireLiveLock(runId))
        {
            harness.Error.GetStringBuilder().Clear();

            Assert.Equal(2, await harness.RunAsync("abort", runId.ToString()));
            Assert.Contains("still live", harness.Error.ToString());
        }

        var store = new RunStore(harness.Home);
        Assert.Equal(WorkflowState.Blocked, (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task.WorkflowState);
    }
}
