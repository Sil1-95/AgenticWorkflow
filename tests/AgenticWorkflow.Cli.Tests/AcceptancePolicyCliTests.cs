using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// whether a final human ACCEPT is required is an explicit, persisted per-run choice (project default <c>"acceptance"</c>, per-run <c>--acceptance</c>),
/// never inferred. Through the real CLI composition with only the agent runtime scripted.
/// </summary>
public sealed class AcceptancePolicyCliTests
{
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes()
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static CliHarness NewHarness(string? acceptance)
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance));
        harness.Factory.Runtime.Turn = Writes();
        return harness;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- configuration ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_product_default_is_no_acceptance_and_both_words_parse()
    {
        Assert.Equal(AcceptanceRequirement.NotRequired, ProjectConfigParser.Parse(CliHarness.ValidConfig(acceptance: null)).Acceptance);
        Assert.Equal(AcceptanceRequirement.NotRequired, ProjectConfigParser.Parse(CliHarness.ValidConfig(acceptance: "none")).Acceptance);
        Assert.Equal(AcceptanceRequirement.HumanRequired, ProjectConfigParser.Parse(CliHarness.ValidConfig(acceptance: "human")).Acceptance);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("NONE")]
    [InlineData("")]
    public void An_unknown_acceptance_word_is_a_configuration_error(string word)
    {
        var e = Assert.Throws<ConfigurationException>(() => ProjectConfigParser.Parse(CliHarness.ValidConfig(acceptance: word)));

        Assert.Contains(e.Problems, p => p.Contains("acceptance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_acceptance_option_is_a_usage_error_and_starts_nothing()
    {
        using var harness = NewHarness("none");

        Assert.Equal(2, await harness.RunAsync("run", "task", "--acceptance", "maybe"));
        Assert.Equal(2, await harness.RunAsync("run", "task", "--acceptance"));
        Assert.Empty(harness.Factory.Runtime.Stopped);
    }

    // ---- no acceptance required ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_acceptance_a_verified_run_completes_with_exit_zero_and_no_gate()
    {
        using var harness = NewHarness("none");

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Verification: PASSED", output);
        Assert.Contains("Acceptance: not required", output);
        Assert.DoesNotContain("AWAITING ACCEPT", output);
        Assert.DoesNotContain("aw accept", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(AcceptanceRequirement.NotRequired, task.Acceptance);
        Assert.Empty(task.Gates);
        Assert.Empty(task.GateDecisions);
        Assert.Single(harness.Factory.Runtime.Stopped);
    }

    [Fact]
    public async Task The_product_default_when_the_config_is_silent_is_no_acceptance()
    {
        using var harness = NewHarness(null);

        Assert.Equal(0, await harness.RunAsync("run", "task"));

        Assert.Equal(AcceptanceRequirement.NotRequired, (await LoadAsync(harness, harness.PrintedRunId())).Acceptance);
    }

    [Fact]
    public async Task Failed_verification_never_completes_a_run_that_requires_no_acceptance()
    {
        using var harness = NewHarness("none");
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        var exit = await harness.RunAsync("run", "task");

        Assert.NotEqual(0, exit);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.IsTerminal && task.WorkflowState == WorkflowState.Completed);
        Assert.Empty(task.Gates);
    }

    [Fact]
    public async Task Accept_on_a_run_that_requires_no_acceptance_is_refused_and_changes_nothing()
    {
        using var harness = NewHarness("none");
        Assert.Equal(0, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        var accept = await harness.RunAsync("accept", runId.ToString());
        var changes = await harness.RunAsync("request-changes", runId.ToString(), "--feedback", "please redo it");
        var abort = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(18, accept);
        Assert.Equal(18, changes);
        Assert.Equal(2, abort); // a completed run has nothing to abort
        Assert.Contains("does not require acceptance", harness.Error.ToString() + harness.Out.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Empty(task.Gates);
    }

    [Fact]
    public async Task A_fresh_process_reports_the_completed_run_and_its_policy()
    {
        using var harness = NewHarness("none");
        Assert.Equal(0, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var output = harness.Out.ToString();
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Acceptance: not required", output);
    }

    // ---- acceptance required ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_acceptance_required_the_run_still_opens_the_gate_and_exits_17()
    {
        using var harness = NewHarness("human");

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        var output = harness.Out.ToString();
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.Contains("Acceptance: required (human ACCEPT)", output);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(AcceptanceRequirement.HumanRequired, task.Acceptance);
        Assert.NotNull(task.PendingGate);
    }

    [Fact]
    public async Task The_human_gate_then_completes_in_a_fresh_process_with_no_new_model_session()
    {
        using var harness = NewHarness("human");
        Assert.Equal(17, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        var starts = harness.Factory.Runtime.Stopped.Count;
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString()));

        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Equal(starts, harness.Factory.Runtime.Stopped.Count);
    }

    // ---- the per-run choice -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("none", "human", AcceptanceRequirement.HumanRequired, "--acceptance")]
    [InlineData("human", "none", AcceptanceRequirement.NotRequired, "--acceptance")]
    public async Task The_run_option_overrides_the_project_default_and_the_effective_policy_is_printed(string configured, string option, AcceptanceRequirement expected, string source)
    {
        using var harness = NewHarness(configured);

        var exit = await harness.RunAsync("run", "task", "--acceptance", option);

        Assert.Equal(expected == AcceptanceRequirement.NotRequired ? 0 : 17, exit);
        Assert.Equal(expected, (await LoadAsync(harness, harness.PrintedRunId())).Acceptance);
        Assert.Contains($"({source})", harness.Out.ToString());
    }

    [Fact]
    public async Task The_policy_comes_from_the_event_stream_not_the_config_after_creation()
    {
        using var harness = NewHarness("human");
        Assert.Equal(17, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        // the project default flips to none after the run exists; the run keeps what it was created with
        harness.Write("agenticworkflow.json", CliHarness.ValidConfig(acceptance: "none"));

        Assert.Equal(AcceptanceRequirement.HumanRequired, (await LoadAsync(harness, runId)).Acceptance);
        var task = await LoadAsync(harness, runId);
        Assert.NotNull(task.PendingGate);

        await harness.RunAsync("status", runId.ToString());
        Assert.Contains("required (human ACCEPT)", harness.Out.ToString());
    }

    [Fact]
    public async Task Show_displays_the_configured_default_for_new_runs()
    {
        using var none = NewHarness("none");
        using var human = NewHarness("human");

        Assert.Equal(0, await none.RunAsync("show"));
        Assert.Equal(0, await human.RunAsync("show"));

        Assert.Contains("not required", none.Out.ToString());
        Assert.Contains("required (human ACCEPT)", human.Out.ToString());
    }
}
