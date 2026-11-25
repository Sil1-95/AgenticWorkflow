using System.Diagnostics;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The explicit-binding contract over the controller's typed operations: <c>start_task.bindings</c> and <c>bind_role</c> resolve and validate before anything is recorded or scheduled,
/// the reply and the read model show the resolved bindings (with their configuration), a role a stage needs and lacks is reported as structured data and never chosen, and the controller and
/// the command line record exactly the same thing for the same request. A real controller over the CLI harness's state; only the agent runtime is scripted.
/// </summary>
public sealed class ExecutionBindingControllerTests
{
    private const string Profiles = """
        "profiles": {
          "claude-main": { "harness": "claude-code", "model": "opus", "reasoning": "high" },
          "claude-review": { "harness": "claude-code", "model": "sonnet", "reasoning": "low" },
          "pi-main": { "harness": "pi" }
        },
        """;

    private static CliHarness NewHarness(string profile = "claude-main")
    {
        var harness = new CliHarness(CliHarness.ValidConfig(profile: profile, acceptance: "none", extra: Profiles));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        return harness;
    }

    private static StartTaskRequest Start(CliHarness harness, Dictionary<string, string>? bindings = null, bool review = false, bool prove = false)
        => new() { Objective = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt.", WorkingDirectory = harness.Workspace, RequireReview = review, RequireProve = prove, Bindings = bindings };

    private static TaskId IdOf(ControllerReply reply) => new(Assert.IsType<WorkflowAcceptedResult>(reply.Result).Task.TaskId);

    private static RoleBindingView Binding(TaskReadModel task, string role) => task.Bindings.Single(b => b.Role == role);

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- start_task with explicit bindings -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_start_with_explicit_bindings_records_and_shows_each_role_with_its_resolved_configuration()
    {
        using var harness = NewHarness();
        harness.Factory.Signals!.ReviewByCall[1] = new AgentReviewSubmission("PASS", "Nothing blocks.", [], null);
        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(Start(harness, new() { ["root"] = "claude-review", ["reviewer"] = "claude-main" }, review: true));

        Assert.True(reply.Ok, reply.Error?.Message);
        var accepted = Assert.IsType<WorkflowAcceptedResult>(reply.Result);
        var immediate = accepted.Task;
        Assert.Equal(("claude-review", "sonnet", "low"), (Binding(immediate, "root").Profile.Id, Binding(immediate, "root").Configuration!.Model, Binding(immediate, "root").Configuration!.Reasoning));
        Assert.Equal(("claude-main", "opus", "high"), (Binding(immediate, "reviewer").Profile.Id, Binding(immediate, "reviewer").Configuration!.Model, Binding(immediate, "reviewer").Configuration!.Reasoning));
        Assert.Equal("claude-code", Binding(immediate, "root").Configuration!.Harness);
        Assert.False(string.IsNullOrEmpty(Binding(immediate, "root").Configuration!.Fingerprint));
        Assert.Empty(immediate.MissingBindings);

        var runId = IdOf(reply);
        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Equal("sonnet", Binding(done, "root").Configuration!.Model);

        // the agents that were launched were launched with exactly those values
        var launched = Assert.Single(harness.Factory.Launches);
        Assert.Equal("sonnet", launched.Find(new RoleId("root"))!.Execution.Model);
        Assert.Equal("opus", launched.Find(new RoleId("reviewer"))!.Execution.Model);
    }

    [Fact]
    public async Task A_start_with_no_bindings_uses_the_projects_own_and_shows_the_resolved_configuration_without_reading_the_project()
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(Start(harness));

        var task = Assert.IsType<WorkflowAcceptedResult>(reply.Result).Task;
        var root = Binding(task, "root");
        Assert.Equal("claude-main", root.Profile.Id);
        Assert.Equal(("claude-code", "opus", "high"), (root.Configuration!.Harness, root.Configuration.Model, root.Configuration.Reasoning));
        Assert.Single(task.Bindings);                   // nothing else was bound on anyone's behalf
    }

    [Theory]
    [InlineData("claude-ghost", ControllerErrorCodes.UnknownProfile)]
    [InlineData("pi-main", ControllerErrorCodes.UnsupportedConfiguration)]
    public async Task A_start_naming_a_profile_that_is_unknown_or_cannot_be_run_is_refused_before_anything_exists(string profile, string code)
    {
        using var harness = NewHarness();

        if (profile == "pi-main")
            harness.Factory.ExecutionProblems.Add("the installed Pi could not be found or asked what it supports");

        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(Start(harness, new() { ["root"] = profile }));

        Assert.False(reply.Ok);
        Assert.Equal(code, reply.Error!.Code);
        Assert.Contains(profile, reply.Error.Message);
        Assert.Empty(new RunStore(harness.Home).ListRunIds());
        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
    }

    [Fact]
    public async Task A_start_the_harness_cannot_honour_is_refused_naming_the_problem_and_schedules_nothing()
    {
        using var harness = NewHarness();
        harness.Factory.ExecutionProblems.Add("reasoning level 'high' is not offered");
        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(Start(harness));

        Assert.False(reply.Ok);
        Assert.Equal(ControllerErrorCodes.UnsupportedConfiguration, reply.Error!.Code);
        Assert.Contains("claude-main", reply.Error.Message);
        Assert.Contains("'high'", reply.Error.Message);
        Assert.Empty(new RunStore(harness.Home).ListRunIds());
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
    }

    [Theory]
    [InlineData(true, false, "reviewer")]
    [InlineData(false, true, "prover")]
    public async Task A_start_requiring_a_stage_whose_role_has_no_profile_anywhere_is_refused_as_a_missing_binding_and_no_other_profile_is_used(bool review, bool prove, string role)
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(Start(harness, review: review, prove: prove));

        Assert.False(reply.Ok);
        Assert.Equal(ControllerErrorCodes.MissingBinding, reply.Error!.Code);
        Assert.Contains(role, reply.Error.Message);
        Assert.Empty(new RunStore(harness.Home).ListRunIds());
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_start_naming_a_role_that_cannot_be_bound_or_a_malformed_profile_is_an_invalid_request_and_starts_nothing()
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness);

        foreach (var bindings in new[] { new Dictionary<string, string> { ["implementer"] = "claude-main" }, new() { ["root"] = "Claude Main" }, new() { ["root"] = "a;b" } })
        {
            var reply = await controller.SendAsync(Start(harness, bindings));

            Assert.False(reply.Ok);
            Assert.Equal(ControllerErrorCodes.InvalidArgument, reply.Error!.Code);
        }

        Assert.Empty(new RunStore(harness.Home).ListRunIds());
    }

    // ---- a role a stage needs mid-run, bound explicitly -----------------------------------------------------------------------------

    private static async Task<(CliHarness Harness, SupervisedController Controller, TaskId RunId, TaskReadModel Task)> WaitingForReviewerAsync()
    {
        var harness = NewHarness();
        harness.Factory.Signals!.ReviewRequestByTurn[1] = new AgentReviewRequestSignal("This changes a shared file format.");
        harness.Factory.Signals.ReviewByCall[1] = new AgentReviewSubmission("PASS", "Nothing blocks.", [], null);
        var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.SendAsync(Start(harness)));
        var task = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Blocked && t.MissingBindings.Count > 0, "the missing reviewer binding");
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);

        return (harness, controller, runId, task);
    }

    [Fact]
    public async Task A_review_the_agent_asked_for_with_no_reviewer_waits_and_the_read_model_names_the_missing_binding_without_choosing_one()
    {
        var (harness, controller, _, task) = await WaitingForReviewerAsync();
        using var _ = harness;
        await using var __ = controller;

        var missing = Assert.Single(task.MissingBindings);
        Assert.Equal(("reviewer", "review_required"), (missing.Role, missing.Reason));
        Assert.Equal(["repository_tools"], missing.RequiredCapabilities);
        Assert.DoesNotContain(task.Bindings, b => b.Role == "reviewer");
        Assert.Single(harness.Factory.Runtime.StartRequests);        // no reviewer was launched, with the implementer's profile or any other
    }

    [Fact]
    public async Task Bind_role_records_the_resolved_configuration_starts_nothing_and_continue_then_launches_the_reviewer_as_bound()
    {
        var (harness, controller, runId, _) = await WaitingForReviewerAsync();
        using var _ = harness;
        await using var __ = controller;

        var bound = await controller.SendAsync(new BindRoleRequest { TaskId = runId.ToString(), Role = "reviewer", Profile = "claude-review" });

        Assert.True(bound.Ok, bound.Error?.Message);
        var result = Assert.IsType<WorkflowAcceptedResult>(bound.Result);
        Assert.Equal(ControllerOperations.BindRole, result.Operation);
        Assert.False(result.WorkScheduled);
        Assert.Equal(("sonnet", "low"), (Binding(result.Task, "reviewer").Configuration!.Model, Binding(result.Task, "reviewer").Configuration!.Reasoning));
        Assert.Empty(result.Task.MissingBindings);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        Assert.True((await controller.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() })).Ok);
        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion after the review");

        Assert.Equal("sonnet", Binding(done, "reviewer").Configuration!.Model);
        Assert.Equal("sonnet", harness.Factory.Launches.Last().Find(new RoleId("reviewer"))!.Execution.Model);
        Assert.Equal("opus", harness.Factory.Launches.Last().Find(new RoleId("root"))!.Execution.Model);
    }

    [Fact]
    public async Task Binding_again_is_idempotent_for_the_same_profile_and_refused_as_already_bound_for_another()
    {
        var (harness, controller, runId, _) = await WaitingForReviewerAsync();
        using var _ = harness;
        await using var __ = controller;
        var request = new BindRoleRequest { TaskId = runId.ToString(), Role = "reviewer", Profile = "claude-review" };
        Assert.True((await controller.SendAsync(request)).Ok);
        var version = (await controller.ReadAsync(runId)).StreamVersion;

        var again = await controller.SendAsync(request);
        var other = await controller.SendAsync(request with { Profile = "claude-main" });
        var root = await controller.SendAsync(request with { Role = "root", Profile = "claude-review" });

        Assert.True(again.Ok);
        Assert.Equal(version, (await controller.ReadAsync(runId)).StreamVersion);
        Assert.Equal(ControllerErrorCodes.AlreadyBound, other.Error!.Code);
        Assert.Equal(ControllerErrorCodes.AlreadyBound, root.Error!.Code);
        Assert.Equal("claude-review", Binding(await controller.ReadAsync(runId), "reviewer").Profile.Id);
        Assert.Equal("claude-main", Binding(await controller.ReadAsync(runId), "root").Profile.Id);
    }

    [Theory]
    [InlineData("claude-ghost", ControllerErrorCodes.UnknownProfile)]
    [InlineData("pi-main", ControllerErrorCodes.UnsupportedConfiguration)]
    public async Task Bind_role_to_an_unknown_or_unrunnable_profile_is_refused_and_records_nothing(string profile, string code)
    {
        var (harness, controller, runId, before) = await WaitingForReviewerAsync();
        using var _ = harness;
        await using var __ = controller;

        if (profile == "pi-main")
            harness.Factory.ExecutionProblems.Add("the installed Pi could not be found or asked what it supports");

        var reply = await controller.SendAsync(new BindRoleRequest { TaskId = runId.ToString(), Role = "reviewer", Profile = profile });

        Assert.Equal(code, reply.Error!.Code);
        var after = await controller.ReadAsync(runId);
        Assert.Equal(before.StreamVersion, after.StreamVersion);
        Assert.Single(after.MissingBindings);
    }

    [Fact]
    public async Task Bind_role_is_refused_while_the_controller_is_working_on_the_run()
    {
        using var harness = NewHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.Runtime.Turn = async (_, workspace, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");

            return AgentTurnOutcome.Done;
        };
        await using var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.SendAsync(Start(harness)));
        await started.Task.WaitAsync(ControllerTestSupport.Patience);

        var reply = await controller.SendAsync(new BindRoleRequest { TaskId = runId.ToString(), Role = "reviewer", Profile = "claude-review" });

        Assert.Equal(ControllerErrorCodes.RunOwned, reply.Error!.Code);
        Assert.DoesNotContain(new RoleId("reviewer"), (await LoadAsync(harness, runId)).RoleBindings.Keys);
        release.SetResult();
        await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
    }

    [Fact]
    public async Task Bind_role_for_a_task_that_does_not_exist_is_a_structured_refusal()
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness);

        var reply = await controller.SendAsync(new BindRoleRequest { TaskId = Guid.NewGuid().ToString(), Role = "reviewer", Profile = "claude-review" });

        Assert.False(reply.Ok);
        Assert.NotNull(reply.Error);
    }

    // ---- the controller and the command line agree ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_controller_and_the_command_line_record_the_same_execution_configuration_for_the_same_request()
    {
        using var viaController = NewHarness();
        await using var controller = await SupervisedController.StartAsync(viaController);
        var controllerRun = IdOf(await controller.SendAsync(Start(viaController, new() { ["root"] = "claude-review" })));
        await controller.WaitForAsync(controllerRun, t => t.State == WorkflowState.Completed, "completion");

        using var viaCli = NewHarness();
        Assert.Equal(0, await viaCli.RunAsync("run", "task", "--profile", "claude-review"));

        var fromController = (await LoadAsync(viaController, controllerRun)).RoleBindings[new RoleId("root")];
        var fromCli = (await LoadAsync(viaCli, viaCli.PrintedRunId())).RoleBindings[new RoleId("root")];
        Assert.Equal(fromCli.Profile.Id, fromController.Profile.Id);
        Assert.Equal(fromCli.Execution, fromController.Execution);
    }

    [Fact]
    public async Task With_a_controller_running_run_with_profile_flags_and_bind_are_routed_through_its_typed_operations_and_agree_with_the_direct_path()
    {
        using var harness = NewHarness();
        harness.Factory.Signals!.ReviewRequestByTurn[1] = new AgentReviewRequestSignal("Worth a second look.");
        harness.Factory.Signals.ReviewByCall[1] = new AgentReviewSubmission("PASS", "Nothing blocks.", [], null);
        await using var controller = await SupervisedController.StartAsync(harness);

        var run = await harness.RunSeparatelyAsync("run", "Expose", "--profile", "claude-review");
        Assert.Equal(0, run.Exit);
        var runId = Assert.Single(new RunStore(harness.Home).ListRunIds());
        var waiting = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Blocked && t.MissingBindings.Count > 0, "the missing reviewer binding");
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);
        Assert.Equal("sonnet", Binding(waiting, "root").Configuration!.Model);          // the flag reached the controller as a typed binding

        var bind = await harness.RunSeparatelyAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-main");
        Assert.Equal(0, bind.Exit);
        Assert.Equal("opus", Binding(await controller.ReadAsync(runId), "reviewer").Configuration!.Model);

        var conflicting = await harness.RunSeparatelyAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review");
        Assert.Equal(18, conflicting.Exit);                                              // the same exit code the direct path uses
        Assert.Contains("already_bound", conflicting.Error + conflicting.Out);

        var unknown = await harness.RunSeparatelyAsync("bind", runId.ToString(), "--role", "prover", "--profile", "claude-ghost");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("unknown_profile", unknown.Error + unknown.Out);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();

        while (!condition())
        {
            if (clock.Elapsed > ControllerTestSupport.Patience)
                throw new TimeoutException("The condition was not reached.");

            await Task.Delay(25);
        }
    }
}
