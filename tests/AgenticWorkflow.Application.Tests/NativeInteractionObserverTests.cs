using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The optional native-interaction observer only lets a person know. It is told after AWAITING_HUMAN is durable, only for an attended run, once per
/// interaction, and nothing it does (or throws) changes the workflow, the turn or the result.
/// </summary>
public sealed class NativeInteractionObserverTests
{
    private static readonly NativeAttachTarget Target = new("host-core", "terminal-7");

    private static AgentTurnOutcome Waiting(string what = "approve Bash: dotnet --list-sdks")
        => AgentTurnOutcome.AwaitingHuman(new AgentInteraction(AgentInteractionKind.Permission, what, Target));

    private static DirectRunOptions Attended => new() { WaitForHuman = true };

    /// <summary>Records every call, and the last recorded workflow state change at the moment of each.</summary>
    private sealed class RecordingObserver(DirectHarness harness) : INativeInteractionObserver
    {
        public List<string> Calls { get; } = [];

        public List<AgentInteraction> Opened { get; } = [];

        public WorkflowState? StateWhenOpened { get; private set; }

        public WorkflowState? StateWhenClosed { get; private set; }

        public void InteractionOpened(AgentInteraction interaction)
        {
            Opened.Add(interaction);
            Calls.Add("opened");
            StateWhenOpened = LastRecordedState();
        }

        public void InteractionClosed()
        {
            Calls.Add("closed");
            StateWhenClosed = LastRecordedState();
        }

        private WorkflowState? LastRecordedState()
            => harness.Store.History(Fixture.TaskOne).LastOrDefault(e => e.Kind == EventKinds.WorkflowStateChanged)?.Payload is WorkflowStateChanged changed ? changed.To : null;
    }

    private sealed class ThrowingObserver : INativeInteractionObserver
    {
        public int Calls { get; private set; }

        public void InteractionOpened(AgentInteraction interaction)
        {
            Calls++;
            throw new InvalidOperationException("opened failed");
        }

        public void InteractionClosed()
        {
            Calls++;
            throw new InvalidOperationException("closed failed");
        }
    }

    [Fact]
    public async Task The_observer_is_told_only_after_AWAITING_HUMAN_was_durably_recorded_and_told_again_when_the_workflow_is_running()
    {
        var h = await DirectHarness.CreateAsync();
        var recording = new RecordingObserver(h);
        h.Observer = recording;
        var seen = new List<string>();
        h.Runtime.OnWait = index =>
        {
            seen.Add($"wait {index}: {string.Join(',', recording.Calls)}");
            return Task.CompletedTask;
        };
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["opened", "closed"], recording.Calls);
        Assert.Equal(WorkflowState.AwaitingHuman, recording.StateWhenOpened);   // the wait was already recorded when anyone was told
        Assert.Equal(WorkflowState.Running, recording.StateWhenClosed);          // and RUNNING was already recorded when it ended

        // wait 0 before anything, wait 1 right after the first notification, wait 2 after the close
        Assert.Equal(["wait 0: ", "wait 1: opened", "wait 2: opened,closed"], seen);
        Assert.Equal(Waiting().Interaction, recording.Opened.Single());
    }

    [Fact]
    public async Task The_observer_is_never_told_when_nobody_is_attending()
    {
        var h = await DirectHarness.CreateAsync();
        var observer = new RecordingObserver(h);
        h.Observer = observer;
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting()]));

        var result = await h.RunAsync();   // WaitForHuman defaults to false

        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.Empty(observer.Calls);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task Each_interaction_is_opened_and_closed_once_even_across_several_waits_in_one_turn()
    {
        var h = await DirectHarness.CreateAsync();
        var observer = new RecordingObserver(h);
        h.Observer = observer;
        h.Runtime.Enqueue(new TurnScript(
            h.Workspace.RenameToGetFood,
            Observed: [Waiting("approve Bash: one"), AgentTurnOutcome.Resumed, Waiting("approve Bash: two"), AgentTurnOutcome.Resumed]));

        await h.RunAsync(options: Attended);

        Assert.Equal(["opened", "closed", "opened", "closed"], observer.Calls);
        Assert.Equal(["approve Bash: one", "approve Bash: two"], observer.Opened.Select(i => i.Description));
    }

    [Fact]
    public async Task A_turn_that_ends_during_the_wait_closes_it_when_the_workflow_returns_to_running()
    {
        var h = await DirectHarness.CreateAsync();
        var observer = new RecordingObserver(h);
        h.Observer = observer;
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting()]));

        await h.RunAsync(options: Attended);

        Assert.Equal(["opened", "closed"], observer.Calls);
        Assert.Equal(WorkflowState.Running, observer.StateWhenClosed);
    }

    [Fact]
    public async Task A_run_cancelled_during_the_wait_closes_the_observer_too()
    {
        var h = await DirectHarness.CreateAsync();
        var observer = new RecordingObserver(h);
        h.Observer = observer;
        using var cancel = new CancellationTokenSource();
        h.Runtime.OnWait = index =>
        {
            if (index == 1)
                cancel.Cancel();

            return Task.CompletedTask;
        };
        h.Runtime.Enqueue(new TurnScript(Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended, ct: cancel.Token);

        Assert.Equal(DirectRunStatus.Interrupted, result.Status);
        Assert.Equal(["opened", "closed"], observer.Calls);
    }

    [Fact]
    public async Task A_throwing_observer_changes_neither_the_workflow_nor_the_turn_nor_the_result()
    {
        var plain = await DirectHarness.CreateAsync();
        plain.Runtime.Enqueue(new TurnScript(plain.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));
        var expected = await plain.RunAsync(options: Attended);

        var h = await DirectHarness.CreateAsync();
        var throwing = new ThrowingObserver();
        h.Observer = throwing;
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, Observed: [Waiting(), AgentTurnOutcome.Resumed]));

        var result = await h.RunAsync(options: Attended);

        Assert.Equal(2, throwing.Calls);   // it was told, and its failures went nowhere
        Assert.Equal(expected.Status, result.Status);
        Assert.Equal(expected.Reason, result.Reason);
        Assert.Equal(expected.TurnsUsed, result.TurnsUsed);
        Assert.Equal(
            plain.Store.History(Fixture.TaskOne).Select(e => e.Kind),
            h.Store.History(Fixture.TaskOne).Select(e => e.Kind));
        Assert.Equal((await plain.ReloadAsync()).WorkflowState, (await h.ReloadAsync()).WorkflowState);
        Assert.Empty((await h.ReloadAsync()).PolicyDecisions);   // the controller decided nothing about the permission
        Assert.Single(h.Runtime.Sends);                          // and answered nothing
    }

    [Fact]
    public void The_observer_port_can_only_inform_it_has_no_way_to_answer()
    {
        var methods = typeof(INativeInteractionObserver).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["InteractionClosed", "InteractionOpened"], methods);
        Assert.All(typeof(INativeInteractionObserver).GetMethods(), m => Assert.Equal(typeof(void), m.ReturnType));
    }
}
