using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// A harness that needs a person while it is still starting (its first-run "trust this folder"). Only when a person is attending is the runtime allowed to keep such a
/// session and report the wait; the workflow records it like any native interaction, and nothing here ever answers it.
/// </summary>
public sealed class StartupInteractionCliTests
{
    private static readonly NativeAttachTarget Target = new("scripted-core", "scripted-1");

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> WritesTheFeature()
        => (_, workspace, _) =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), $"{CliHarness.Symbol}\n");

            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static async Task<WorkflowState> StateOfTheRunAsync(CliHarness harness)
    {
        var store = new RunStore(harness.Home);
        var runId = Assert.Single(store.ListRunIds());

        return (await (await store.OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task.WorkflowState;
    }

    [Fact]
    public async Task An_attended_run_records_the_startup_wait_as_awaiting_human_and_returns_to_running_when_the_agent_is_ready()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var states = new List<WorkflowState>();
        harness.Factory.Runtime.Turn = WritesTheFeature();
        harness.Factory.Runtime.OnStart = async (request, token) =>
        {
            var interactions = Assert.IsAssignableFrom<IAgentStartupInteractions>(request.StartupInteractions);

            await interactions.OpenedAsync(new AgentInteraction(AgentInteractionKind.Permission, "trust this folder", Target), token);
            states.Add(await StateOfTheRunAsync(harness));

            await interactions.ClosedAsync(token);
            states.Add(await StateOfTheRunAsync(harness));
        };

        var exit = await harness.RunAsync("run", "--interactive", "task");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Equal([WorkflowState.AwaitingHuman, WorkflowState.Running], states);
        Assert.Contains("NATIVE INTERACTION REQUIRED", output);
        Assert.Contains("permission: trust this folder", output);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);

        // aw never answered it: one session, one prompt.
        Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Single(harness.Factory.Runtime.Prompts);
    }

    [Fact]
    public async Task A_run_nobody_is_attending_is_never_offered_the_chance_to_wait_for_a_person_at_startup()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = WritesTheFeature();

        var exit = await harness.RunAsync("run", "--non-interactive", "task");

        Assert.Equal(17, exit);
        Assert.All(harness.Factory.Runtime.StartRequests, request => Assert.Null(request.StartupInteractions));
    }

    [Fact]
    public async Task An_attended_run_whose_agent_never_becomes_ready_is_blocked_like_any_startup_failure()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.StartFailure = new AgentStartupBlockedException("the agent was still waiting for a person after 15 minutes at startup; it was not answered");

        var exit = await harness.RunAsync("run", "--interactive", "task");

        Assert.NotEqual(0, exit);
        Assert.Contains("agent_startup_blocked", harness.Out.ToString() + harness.Error.ToString());
        Assert.Equal(WorkflowState.Blocked, await StateOfTheRunAsync(harness));
    }
}
