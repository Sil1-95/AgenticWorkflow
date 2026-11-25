using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

public sealed class TaskLifecycleTests
{
    [Fact]
    public void New_task_is_running_with_initial_semantic_revision_and_no_implementation_revision()
    {
        var task = NewTask().State;

        Assert.Equal(TaskOne, task.Id);
        Assert.Equal("Add retry to the importer", task.Intent);
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Equal(CompletionBoundary.LocalCommit, task.CompletionBoundary);
        Assert.Equal(T0, task.CreatedAt);
        Assert.Single(task.SemanticRevisions);
        Assert.Equal(task.SemanticRevisions[0].Id, task.CurrentSemanticRevisionId);
        Assert.Null(task.CurrentImplementationRevisionId);
        Assert.Empty(task.RoleBindings);
        Assert.Equal(AuthorityEnvelope.Empty, task.Authority);
        Assert.Equal(1, task.LastSequence);
    }

    [Fact]
    public void Task_creation_requires_a_non_blank_intent()
        => Assert.Throws<ArgumentException>(
            () => TaskStream.Create(TaskOne, "  ", CompletionBoundary.WorkingTree, T0, new SequentialIdSource()));

    [Theory]
    [InlineData(WorkflowState.AwaitingHuman)]
    [InlineData(WorkflowState.Blocked)]
    [InlineData(WorkflowState.Completed)]
    [InlineData(WorkflowState.Cancelled)]
    [InlineData(WorkflowState.Failed)]
    public void Running_may_move_to_every_other_state(WorkflowState target)
    {
        var stream = NewLegacyTask();   // structure test: only a legacy history may complete by a plain state change

        stream.ChangeWorkflowState(target, "because", At(1));

        Assert.Equal(target, stream.State.WorkflowState);
    }

    [Fact]
    public void Non_terminal_states_can_move_between_each_other_and_back()
    {
        var stream = NewTask();

        stream.ChangeWorkflowState(WorkflowState.AwaitingHuman, "need authority", At(1));
        stream.ChangeWorkflowState(WorkflowState.Blocked, "CI is down", At(2));
        stream.ChangeWorkflowState(WorkflowState.Running, "CI is back", At(3));

        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
        Assert.Equal("CI is back", stream.State.StateReason);
    }

    [Theory]
    [InlineData(WorkflowState.Completed)]
    [InlineData(WorkflowState.Cancelled)]
    [InlineData(WorkflowState.Failed)]
    public void Terminal_states_are_final_for_every_further_transition(WorkflowState terminal)
    {
        var stream = NewLegacyTask();
        stream.ChangeWorkflowState(terminal, "done", At(1));
        var eventCount = stream.Events.Count;

        foreach (var target in Enum.GetValues<WorkflowState>())
        {
            var ex = Assert.Throws<TaskTerminatedException>(() => stream.ChangeWorkflowState(target, "again", At(2)));
            Assert.Equal(terminal, ex.State);
        }

        Assert.True(stream.State.IsTerminal);
        Assert.Equal(terminal, stream.State.WorkflowState);
        Assert.Equal(eventCount, stream.Events.Count);
    }

    [Fact]
    public void A_terminal_task_accepts_no_other_event_either()
    {
        var stream = NewTask();
        stream.ChangeWorkflowState(WorkflowState.Cancelled, "user cancelled", At(1));

        Assert.Throws<TaskTerminatedException>(
            () => stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "late change", At(2)));
        Assert.Throws<TaskTerminatedException>(
            () => stream.RecordImplementationRevision(new ImplementationFingerprint("x"), At(2)));
        Assert.Throws<TaskTerminatedException>(
            () => stream.BindRole(Role("r"), Profile("p"), At(2)));
        Assert.Throws<TaskTerminatedException>(
            () => stream.ChangeAuthority(AuthorityEnvelope.Empty, "late", At(2)));
        Assert.Throws<TaskTerminatedException>(
            () => stream.RecordPolicyDecision(new PolicyRuleId("r"), PolicyEffect.Notify, Trigger(), "late", At(2)));
    }

    [Fact]
    public void Transition_records_from_to_and_reason_in_the_event()
    {
        var stream = NewTask();

        stream.ChangeWorkflowState(WorkflowState.Blocked, "waiting for the vendor API key", At(1));

        var change = Assert.IsType<WorkflowStateChanged>(stream.Events[^1].Payload);
        Assert.Equal(WorkflowState.Running, change.From);
        Assert.Equal(WorkflowState.Blocked, change.To);
        Assert.Equal("waiting for the vendor API key", change.Reason);
        Assert.Equal("waiting for the vendor API key", stream.State.StateReason);
        Assert.Equal(EventKinds.WorkflowStateChanged, stream.Events[^1].Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Every_transition_requires_a_reason(string reason)
    {
        var stream = NewTask();

        Assert.Throws<InvalidWorkflowTransitionException>(
            () => stream.ChangeWorkflowState(WorkflowState.Blocked, reason, At(1)));

        Assert.Equal(WorkflowState.Running, stream.State.WorkflowState);
        Assert.Single(stream.Events);
    }

    [Fact]
    public void Transition_to_the_current_state_is_rejected()
        => Assert.Throws<InvalidWorkflowTransitionException>(
            () => NewTask().ChangeWorkflowState(WorkflowState.Running, "still running", At(1)));

    [Fact]
    public void Unknown_state_value_is_rejected()
        => Assert.Throws<InvalidWorkflowTransitionException>(
            () => NewTask().ChangeWorkflowState((WorkflowState)99, "bogus", At(1)));

    [Fact]
    public void Completion_boundary_is_independent_of_workflow_state_and_extensible()
    {
        var custom = new CompletionBoundary("staging_deploy");
        var stream = AsLegacy(TaskStream.Create(TaskOne, "Ship it", custom, T0, new SequentialIdSource(), AcceptanceRequirement.HumanRequired));

        stream.ChangeWorkflowState(WorkflowState.Completed, "done", At(1));

        Assert.Equal(custom, stream.State.CompletionBoundary);
        Assert.NotEqual(CompletionBoundary.Deployment, stream.State.CompletionBoundary);
        Assert.Equal(
            ["working_tree", "local_commit", "integrated_branch", "remote_branch", "pull_request", "release", "deployment"],
            new[]
            {
                CompletionBoundary.WorkingTree, CompletionBoundary.LocalCommit, CompletionBoundary.IntegratedBranch,
                CompletionBoundary.RemoteBranch, CompletionBoundary.PullRequest, CompletionBoundary.Release,
                CompletionBoundary.Deployment,
            }.Select(b => b.Value));
    }

    [Fact]
    public void Failed_command_leaves_history_and_state_untouched()
    {
        var stream = NewTask();
        var before = stream.State;

        Assert.ThrowsAny<Exception>(() => stream.ChangeWorkflowState(WorkflowState.Running, "no-op", At(1)));

        Assert.Same(before, stream.State);
        Assert.Single(stream.Events);
    }
}
