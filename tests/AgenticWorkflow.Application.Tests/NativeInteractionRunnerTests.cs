using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The runner OBSERVES a native human interaction (the agent waiting for a person in its own interface). It records AWAITING_HUMAN when the runtime
/// reports the wait and RUNNING when it reports the agent resumed; it never allows, denies or answers, never learns what the person chose, and
/// never counts the wait as a turn of its own.
/// </summary>
public sealed class NativeInteractionRunnerTests
{
    private static AgentTurnOutcome Waiting(string what = "approve Bash: dotnet --list-sdks", AgentInteractionKind kind = AgentInteractionKind.Permission)
        => AgentTurnOutcome.AwaitingHuman(new AgentInteraction(kind, what, new NativeAttachTarget("host-session", "terminal-1")));

    private static DirectRunOptions Attended => new() { WaitForHuman = true };

    private static IReadOnlyList<EventEnvelope> StateChanges(DirectHarness h)
        => h.Store.History(Fixture.TaskOne).Where(e => e.Kind == EventKinds.WorkflowStateChanged).ToList();

    [Fact]
    public async Task An_attended_wait_is_recorded_then_cleared_when_the_agent_resumes_and_the_same_turn_completes()
    {
        var h = await DirectHarness.CreateAsync();
        var seen = new List<WorkflowState>();
        h.Runtime.OnWait = async index => seen.Add((await h.ReloadAsync()).WorkflowState);
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);

        // wait 0 (before anything), wait 1 (after AWAITING_HUMAN was recorded), wait 2 (after RUNNING was recorded), nothing else
        Assert.Equal([WorkflowState.Running, WorkflowState.AwaitingHuman, WorkflowState.Running], seen);
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);

        // the wait is not a turn: one prompt was sent, one turn counted, one session used
        Assert.Single(h.Runtime.Sends);
        Assert.Equal(1, result.TurnsUsed);
        Assert.Single(h.Runtime.Starts);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task The_recorded_reason_names_what_the_agent_waits_for_and_nothing_else_is_decided()
    {
        var h = await DirectHarness.CreateAsync();
        string? reasonWhileWaiting = null;
        h.Runtime.OnWait = async index =>
        {
            if (index == 1)
                reasonWhileWaiting = (await h.ReloadAsync()).StateReason;
        };
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Contains("permission: approve Bash: dotnet --list-sdks", reasonWhileWaiting);

        var task = await h.ReloadAsync();
        Assert.Empty(task.PolicyDecisions);   // the controller decided nothing about the permission
        Assert.Single(h.Runtime.Sends);       // and sent nothing to answer it
    }

    [Fact]
    public async Task Nobody_attending_records_the_wait_stops_the_session_and_then_blocks_the_workflow_without_answering_it()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting()]));

        var result = await h.RunAsync();   // WaitForHuman defaults to false

        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.StartsWith("native_interaction_unavailable", result.Reason);
        Assert.Contains("approve Bash: dotnet --list-sdks", result.Reason);

        // AWAITING_HUMAN was recorded while the interaction was live, then BLOCKED once the session was gone; nothing else was recorded
        var changes = StateChanges(h);
        Assert.Equal(2, changes.Count);
        Assert.Equal(WorkflowState.AwaitingHuman, ((WorkflowStateChanged)changes[0].Payload).To);
        Assert.Equal(WorkflowState.Blocked, ((WorkflowStateChanged)changes[1].Payload).To);

        // a fresh replay ends BLOCKED, with a reason that does not promise anyone can still answer
        var task = (await h.Service.LoadAsync(Fixture.TaskOne)).Task;
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.False(task.IsTerminal);
        Assert.Contains("Native agent interaction was required (permission: approve Bash: dotnet --list-sdks)", task.StateReason);
        Assert.Contains("no interactive supervision surface was available", task.StateReason);
        Assert.Contains("native session has ended", task.StateReason);
        Assert.Equal(WorkflowState.Blocked, result.Task!.Task.WorkflowState);

        // no revision, verification, policy decision or human decision was fabricated; one prompt, one stop
        Assert.Empty(task.ImplementationRevisions);
        Assert.Empty(task.Verifications);
        Assert.Empty(task.PolicyDecisions);
        Assert.Empty(h.Verifier.Ran);
        Assert.Single(h.Runtime.Sends);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task The_workflow_is_blocked_only_after_the_session_was_stopped()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting()]));
        var stateAtStop = new List<WorkflowState>();
        h.Runtime.OnStop = async () => stateAtStop.Add((await h.ReloadAsync()).WorkflowState);

        await h.RunAsync();

        // while the session was still being stopped the interaction was still the recorded state; only afterwards does it become BLOCKED
        Assert.Equal([WorkflowState.AwaitingHuman], stateAtStop);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task A_session_that_is_kept_alive_leaves_the_wait_answerable_as_awaiting_human()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting()]));

        var result = await h.RunAsync(options: new DirectRunOptions { StopSessionWhenDone = false });

        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);
        Assert.Empty(h.Runtime.Stops);
    }

    [Fact]
    public async Task A_session_that_could_not_be_stopped_is_not_claimed_gone_and_stays_awaiting_human()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting()]));
        h.Runtime.FailStop = new InvalidOperationException("stop failed");

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);
        Assert.Contains(result.Notes, n => n.Contains("stopping session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attended_run_never_passes_through_BLOCKED()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        await h.RunAsync(options: Attended);

        var targets = StateChanges(h).Select(e => ((WorkflowStateChanged)e.Payload).To).ToArray();
        Assert.Equal([WorkflowState.AwaitingHuman, WorkflowState.Running], targets);   // the ACCEPT gate is not a generic state change
    }

    [Fact]
    public async Task Several_waits_in_one_turn_are_each_observed_and_still_one_turn()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(
            h.Workspace.RenameToGetFood,
            Observed: [Waiting("approve Bash: one"), AgentTurnOutcome.Resumed, Waiting("approve Bash: two"), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(1, result.TurnsUsed);
        Assert.Equal(4, StateChanges(h).Count);   // AWAITING_HUMAN, RUNNING, AWAITING_HUMAN, RUNNING; the ACCEPT gate then opens by its own event
    }

    [Fact]
    public async Task A_turn_that_ends_while_a_wait_is_recorded_returns_the_workflow_to_running_first()
    {
        var h = await DirectHarness.CreateAsync();
        // the agent finishes right after the wait was reported, with no separate "resumed" observation
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting()]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task An_error_after_a_wait_does_not_leave_the_workflow_claiming_the_wait()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Outcome: new AgentTurnOutcome(AgentTurnStatus.Error, "the agent exited"), Observed: [Waiting()]));

        var result = await h.RunAsync(options: Attended);

        // the wait is released (RUNNING) and then, since nothing works on the run any more, the failure is recorded: BLOCKED, never AWAITING_HUMAN or RUNNING
        Assert.Equal(DirectRunStatus.AgentError, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task Cancelling_while_waiting_stops_the_session_and_does_not_leave_AWAITING_HUMAN()
    {
        var h = await DirectHarness.CreateAsync();
        using var cancel = new CancellationTokenSource();
        h.Runtime.OnWait = index =>
        {
            if (index == 1)
                cancel.Cancel();   // the person pressed Ctrl+C while the agent waited

            return Task.CompletedTask;
        };
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended, ct: cancel.Token);

        Assert.Equal(DirectRunStatus.Interrupted, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task The_interaction_text_is_bounded_single_line_display_text()
    {
        var h = await DirectHarness.CreateAsync();
        var noisy = "approve Bash: echo \u001b[31mred\u001b[0m\n" + new string('x', 5000);
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting(noisy)]));

        var result = await h.RunAsync();

        var reason = (await h.ReloadAsync()).StateReason;
        Assert.DoesNotContain('\u001b', reason);
        Assert.DoesNotContain('\n', reason);
        Assert.True(reason.Length < 600, $"reason is {reason.Length} chars");
        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task A_wait_reported_without_an_interaction_is_a_runtime_failure_that_blocks()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Observed: [new AgentTurnOutcome(AgentTurnStatus.AwaitingHuman)]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public void The_runtime_port_has_no_way_to_answer_an_interaction()
    {
        // the whole surface a runtime exposes to the controller: starting, sending a turn, waiting for it and stopping
        var methods = typeof(IAgentRuntime).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(["SendTurnAsync", "StartSessionAsync", "StopSessionAsync", "WaitForTurnAsync"], methods);
        Assert.DoesNotContain(
            typeof(AgentSessionRequest).GetProperties().Select(p => p.Name),
            name => name.Contains("Supervis", StringComparison.Ordinal) || name.Contains("Human", StringComparison.Ordinal));
    }
}
