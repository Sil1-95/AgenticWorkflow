using System.Diagnostics;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// A real controller host (real transport, real dispatcher, real supervisor, reconciler and workflow) over the CLI harness's real state, serving in this process. Only the agent
/// runtime is scripted. <see cref="Crash"/> makes the controller disappear the way a killed process does (its run locks are released and nothing else is cleaned up), so
/// a second controller on the same state root can be started to recover what the first left behind.
/// </summary>
internal sealed class SupervisedController : IAsyncDisposable
{
    private readonly CliHarness _harness;
    private readonly string _root;
    private readonly Func<string, string?> _environment;

    private SupervisedController(CliHarness harness, string root, Func<string, string?> environment, RunSupervisor supervisor, ControllerHost host, Task<ControllerExit> exit, CancellationTokenSource cancel)
    {
        Cancel = cancel;
        _harness = harness;
        _root = root;
        _environment = environment;
        Supervisor = supervisor;
        Host = host;
        Exit = exit;
    }

    public RunSupervisor Supervisor { get; }

    public ControllerHost Host { get; }

    public Task<ControllerExit> Exit { get; }

    public static async Task<SupervisedController> StartAsync(CliHarness harness, TimeSpan? idle = null, bool durable = true)
    {
        var services = harness.Services();
        var root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        var store = new RunStore(root);
        var paths = new ControllerPaths(root);
        var log = new ControllerLog(paths.Log);
        var instanceId = Guid.NewGuid();
        var supervisor = new RunSupervisor(services, store, instanceId, log.Write);
        var reconciler = new RunReconciler(services, store, supervisor, log.Write);
        supervisor.Stranded = id => reconciler.ReconcileAsync(id, CancellationToken.None);

        var host = new ControllerHost(
            ControllerEndpoints.For(root, services.GetEnvironmentVariable),
            new RunTaskCatalog(services, store),
            new ControllerHostOptions { InstanceLockPath = paths.InstanceLock, Version = "test", InstanceId = instanceId, IdleTimeout = idle ?? TimeSpan.Zero, WorkShutdownGrace = TimeSpan.FromSeconds(3) },
            log,
            new ControllerWorkflow(services, store, supervisor, reconciler, log.Write),
            durable);

        var ready = new TaskCompletionSource<ControllerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new CancellationTokenSource();
        var exit = Task.Run(() => host.RunAsync(info => ready.TrySetResult(info), cancel.Token));
        var controller = new SupervisedController(harness, root, services.GetEnvironmentVariable, supervisor, host, exit, cancel);

        await Task.WhenAny(ready.Task, exit).WaitAsync(ControllerTestSupport.Patience);

        if (!ready.Task.IsCompleted)
            await exit;

        return controller;
    }

    public CancellationTokenSource Cancel { get; }

    /// <summary>Stops the controller as a kill would: locks released, nothing cleaned up, no agent host stopped.</summary>
    public async Task CrashAsync()
    {
        Supervisor.Abandon();
        await Cancel.CancelAsync();
        await Exit.WaitAsync(ControllerTestSupport.Patience);
    }

    public async Task<ControllerReply> SendAsync(ControllerRequest request)
    {
        await using var session = await ControllerTestSupport.ConnectAsync(ControllerEndpoints.For(_root, _environment));

        return await session.SendAsync(request, CancellationToken.None);
    }

    public async Task<TaskReadModel> ReadAsync(TaskId id)
    {
        var reply = await SendAsync(new ReadTaskRequest { TaskId = id.ToString() });

        return Assert.IsType<TaskResult>(reply.Result).Task;
    }

    public async Task<TaskReadModel> WaitForAsync(TaskId id, Func<TaskReadModel, bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var task = await ReadAsync(id);

            if (condition(task))
                return task;

            if (clock.Elapsed > ControllerTestSupport.Patience)
                throw new TimeoutException($"Timed out waiting for: {what} (state {task.State}, stage {task.Stage}).");

            await Task.Delay(25);
        }
    }

    public async Task<WorkflowAcceptedResult> StartTaskAsync(string objective = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt.", bool requireWhat = false, TaskPresence? presence = null)
    {
        var reply = await SendAsync(new StartTaskRequest { Objective = objective, WorkingDirectory = _harness.Workspace, RequireWhat = requireWhat, Presence = presence });

        return Assert.IsType<WorkflowAcceptedResult>(reply.Result);
    }

    public async ValueTask DisposeAsync()
    {
        await Cancel.CancelAsync();

        try
        {
            await Exit.WaitAsync(ControllerTestSupport.Patience);
        }
        catch (Exception e) when (e is ControllerEndpointException or OperationCanceledException or TimeoutException)
        {
            // nothing more to stop
        }

        await Supervisor.DisposeAsync();
        Cancel.Dispose();
    }
}

public sealed class ControllerSupervisionTests
{
    private const string Objective = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt.";

    private static AgentWhatSignal What(string objective = Objective)
        => new(objective, "src/Feature.txt contains the symbol.", "src/ only.", "No new files.", "No refactoring.", "The symbol is present under src/.", "The task names no file.");

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes()
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");

            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private const string PiProfiles = """
        "profiles": { "pi-qwen": { "harness": "pi", "model": "llama/Qwen3.8-27B", "reasoning": "low" } },
        """;

    private static CliHarness NewHarness(string? acceptance = "none", string? profile = null)
    {
        var harness = new CliHarness(profile is null ? CliHarness.ValidConfig(acceptance: acceptance) : CliHarness.ValidConfig(profile: profile, acceptance: acceptance, extra: PiProfiles));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();

        return harness;
    }

    /// <summary>A turn that does not end until <paramref name="release"/> is set; <paramref name="started"/> is set when it begins.</summary>
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Gated(TaskCompletionSource started, TaskCompletionSource release, bool thenWrite = true)
        => async (_, workspace, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);

            if (thenWrite)
                File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");

            return AgentTurnOutcome.Done;
        };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static TaskId IdOf(WorkflowAcceptedResult accepted) => new(accepted.Task.TaskId);

    private static GateView PendingGate(TaskReadModel task) => task.Gates.Single(g => g.Id == task.PendingRequest!.GateId);

    private static ApproveProposalRequest Approval(TaskReadModel task, ProposalKind kind = ProposalKind.What)
    {
        var gate = PendingGate(task);

        return new ApproveProposalRequest
        {
            Proposal = kind,
            TaskId = task.TaskId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(),
            ContentHash = (kind == ProposalKind.What ? gate.What?.ContentHash : gate.How?.ContentHash) ?? "none",
        };
    }

    /// <summary>A controller and a task whose WHAT was proposed and now awaits the person.</summary>
    private static async Task<(CliHarness Harness, SupervisedController Controller, TaskReadModel Task)> ProposedAsync()
    {
        var harness = NewHarness();
        harness.Factory.Signals!.WhatByTurn[1] = What();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        var controller = await SupervisedController.StartAsync(harness);
        var accepted = await controller.StartTaskAsync(requireWhat: true);
        var task = await controller.WaitForAsync(IdOf(accepted), t => t.State == WorkflowState.AwaitingHuman && t.PendingRequest is not null, "the WHAT proposal");
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);

        return (harness, controller, task);
    }

    // ---- non-blocking start and ownership ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_task_returns_before_the_agent_has_finished_and_the_controller_owns_the_run_until_it_ends()
    {
        using var harness = NewHarness();
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release);
        await using var controller = await SupervisedController.StartAsync(harness);

        var clock = Stopwatch.StartNew();
        var accepted = await controller.StartTaskAsync();
        var returnedAfter = clock.Elapsed;
        var runId = IdOf(accepted);

        Assert.True(accepted.WorkScheduled);
        Assert.Equal(ControllerOperations.StartTask, accepted.Operation);
        Assert.Equal(WorkflowState.Running, accepted.Task.State);
        Assert.Equal(Objective, accepted.Task.Intent);
        Assert.Equal(1, controller.Supervisor.ActiveRuns);
        Assert.True(controller.Supervisor.Owns(runId));
        Assert.True(returnedAfter < TimeSpan.FromSeconds(10), $"start_task waited for the agent ({returnedAfter}).");

        await started.Task.WaitAsync(ControllerTestSupport.Patience);
        Assert.False((await controller.ReadAsync(runId)).State == WorkflowState.Completed);

        release.SetResult();
        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");

        Assert.Equal(WorkflowState.Completed, done.State);
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);
        Assert.False(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), RunStore.SupervisionFileName)));
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task While_the_controller_works_on_a_run_no_other_process_and_no_second_request_can_drive_it()
    {
        using var harness = NewHarness();
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release);
        await using var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.StartTaskAsync());
        await started.Task.WaitAsync(ControllerTestSupport.Patience);

        var viaController = await controller.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() });
        Assert.Equal(ControllerErrorCodes.RunOwned, viaController.Error!.Code);

        var abort = await controller.SendAsync(new AbortTaskRequest { TaskId = runId.ToString() });
        Assert.Equal(ControllerErrorCodes.RunOwned, abort.Error!.Code);

        var direct = await harness.RunSeparatelyAsync("continue", runId.ToString(), "--direct");
        Assert.NotEqual(0, direct.Exit);

        var directAbort = await harness.RunSeparatelyAsync("abort", runId.ToString(), "--direct");
        Assert.NotEqual(0, directAbort.Exit);

        Assert.Equal(WorkflowState.Running, (await controller.ReadAsync(runId)).State);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        release.SetResult();
        await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    // ---- WHAT ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_WHAT_approval_with_a_binding_that_is_not_the_one_shown_is_refused_and_records_nothing()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        await using var __ = controller;
        var version = (await controller.ReadAsync(new TaskId(task.TaskId))).StreamVersion;
        var good = Approval(task);

        var cases = new (ControllerRequest Request, string Code)[]
        {
            (good with { ContentHash = "0000" }, ControllerErrorCodes.StaleBinding),
            (good with { SemanticRevisionId = Guid.NewGuid().ToString() }, ControllerErrorCodes.StaleBinding),
            (good with { Proposal = ProposalKind.How }, ControllerErrorCodes.WrongGateKind),
            (good with { GateId = Guid.NewGuid().ToString() }, ControllerErrorCodes.NoSuchGate),
            (good with { GateId = "not-a-guid" }, ControllerErrorCodes.InvalidGateId),
            (good with { TaskId = Guid.NewGuid().ToString() }, ControllerErrorCodes.NotFound),
        };

        foreach (var (request, code) in cases)
        {
            var reply = await controller.SendAsync(request);

            Assert.False(reply.Ok);
            Assert.Equal(code, reply.Error!.Code);
        }

        var after = await controller.ReadAsync(new TaskId(task.TaskId));
        Assert.Equal(version, after.StreamVersion);
        Assert.Equal(WorkflowState.AwaitingHuman, after.State);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_WHAT_approval_continues_the_same_conversation_in_the_background_and_a_repeat_or_late_decision_is_refused()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        await using var __ = controller;
        var runId = new TaskId(task.TaskId);
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release);

        var reply = await controller.SendAsync(Approval(task));

        var accepted = Assert.IsType<WorkflowAcceptedResult>(reply.Result);
        Assert.Equal(ControllerOperations.ApproveWhat, accepted.Operation);
        Assert.True(accepted.WorkScheduled);
        Assert.Equal(WorkflowState.Running, accepted.Task.State);

        await started.Task.WaitAsync(ControllerTestSupport.Patience);
        var duplicate = await controller.SendAsync(Approval(task));
        Assert.False(duplicate.Ok);
        Assert.Contains(duplicate.Error!.Code, new[] { ControllerErrorCodes.AlreadyDecided, ControllerErrorCodes.StaleGate });

        release.SetResult();
        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Single(done.Gates);

        var late = await controller.SendAsync(Approval(task));
        Assert.Equal(ControllerErrorCodes.AlreadyDecided, late.Error!.Code);

        Assert.Single(harness.Factory.Runtime.StartRequests);
        var resume = Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Equal(ScriptedConversations.FirstReference, resume.Conversation.Reference);
        Assert.Equal(GateStatus.Approved, (await LoadAsync(harness, runId)).StatusOf((await LoadAsync(harness, runId)).Gates[0]));
    }

    [Fact]
    public async Task Requesting_changes_to_the_WHAT_supersedes_it_and_the_old_gate_can_no_longer_be_approved()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        await using var __ = controller;
        var runId = new TaskId(task.TaskId);
        harness.Factory.Signals!.WhatByTurn[2] = What("Expose AW_SCRIPTED_SYMBOL from src/Feature.txt only.");
        var old = PendingGate(task);

        var stale = await controller.SendAsync(new RequestTaskChangesRequest
        {
            Gate = DecisionGateKind.What,
            TaskId = runId.ToString(),
            GateId = old.Id.ToString(),
            SemanticRevisionId = Guid.NewGuid().ToString(),
            Feedback = "Only that file.",
        });
        Assert.Equal(ControllerErrorCodes.StaleBinding, stale.Error!.Code);

        var wrongKind = await controller.SendAsync(new RequestTaskChangesRequest
        {
            Gate = DecisionGateKind.How,
            TaskId = runId.ToString(),
            GateId = old.Id.ToString(),
            SemanticRevisionId = old.Binding.SemanticRevisionId.ToString(),
            Feedback = "Only that file.",
        });
        Assert.Equal(ControllerErrorCodes.WrongGateKind, wrongKind.Error!.Code);

        var reply = await controller.SendAsync(new RequestTaskChangesRequest
        {
            Gate = DecisionGateKind.What,
            TaskId = runId.ToString(),
            GateId = old.Id.ToString(),
            SemanticRevisionId = old.Binding.SemanticRevisionId.ToString(),
            Feedback = "Only that file.",
        });

        Assert.True(Assert.IsType<WorkflowAcceptedResult>(reply.Result).WorkScheduled);
        var revised = await controller.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.Gates.Count == 2 && t.PendingRequest?.GateId != old.Id, "the revised WHAT");
        Assert.NotEqual(old.Id, PendingGate(revised).Id);

        var superseded = await controller.SendAsync(Approval(task));
        Assert.False(superseded.Ok);
        Assert.Contains(superseded.Error!.Code, new[] { ControllerErrorCodes.AlreadyDecided, ControllerErrorCodes.StaleGate });

        harness.Factory.Runtime.Turn = Writes();
        Assert.True((await controller.SendAsync(Approval(revised))).Ok);
        await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
    }

    // ---- concurrency ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Simultaneous_identical_decisions_record_exactly_one_and_start_exactly_one_stretch()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        await using var __ = controller;
        var runId = new TaskId(task.TaskId);
        harness.Factory.Runtime.Turn = Writes();

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => controller.SendAsync(Approval(task))));

        var accepted = replies.Where(r => r.Ok).ToList();
        Assert.Single(accepted);
        Assert.All(replies.Where(r => !r.Ok), r => Assert.Contains(r.Error!.Code, new[] { ControllerErrorCodes.AlreadyDecided, ControllerErrorCodes.StaleGate, ControllerErrorCodes.RunOwned, ControllerErrorCodes.StateMoved }));

        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Single(done.Gates);
        Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task Simultaneous_starts_create_independent_runs_each_with_one_owner()
    {
        // Five runs share one workspace here, so what each run concludes about the files is not the point (that is the workspace's own, long-standing caveat); each run having
        // exactly one owner, its own history and no effect on the others' ownership is.
        using var harness = NewHarness();
        var release = Signal();
        var running = 0;
        harness.Factory.Runtime.Turn = async (_, _, token) =>
        {
            Interlocked.Increment(ref running);
            await release.Task.WaitAsync(token);

            return AgentTurnOutcome.Done;
        };
        await using var controller = await SupervisedController.StartAsync(harness);

        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => controller.StartTaskAsync($"Task number {i}")));

        var ids = replies.Select(IdOf).ToList();
        Assert.Equal(5, ids.Distinct().Count());
        Assert.All(replies, r => Assert.True(r.WorkScheduled));
        await UntilAsync(() => Volatile.Read(ref running) == 5);
        Assert.Equal(5, controller.Supervisor.ActiveRuns);
        Assert.All(ids, id => Assert.True(controller.Supervisor.Owns(id)));

        release.SetResult();
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);

        foreach (var id in ids)
            Assert.NotEqual(WorkflowState.Running, (await controller.ReadAsync(id)).State);
    }

    // ---- CLARIFY ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clarification_is_answered_only_with_the_binding_it_was_shown_and_the_answer_is_data()
    {
        using var harness = NewHarness();
        harness.Factory.Signals!.ByTurn[1] = new AgentClarificationSignal("Which file?", "Not named.");
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        await using var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.StartTaskAsync());
        var task = await controller.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.PendingRequest is not null, "the question");
        var gate = PendingGate(task);

        var wrong = await controller.SendAsync(new AnswerClarificationRequest { TaskId = runId.ToString(), GateId = gate.Id.ToString(), SemanticRevisionId = Guid.NewGuid().ToString(), Answer = "src/Feature.txt" });
        Assert.Equal(ControllerErrorCodes.StaleBinding, wrong.Error!.Code);

        var approving = await controller.SendAsync(new ApproveProposalRequest
        {
            Proposal = ProposalKind.What,
            TaskId = runId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(),
            ContentHash = "x",
        });
        Assert.Equal(ControllerErrorCodes.WrongGateKind, approving.Error!.Code);

        harness.Factory.Runtime.Turn = Writes();
        var reply = await controller.SendAsync(new AnswerClarificationRequest { TaskId = runId.ToString(), GateId = gate.Id.ToString(), SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(), Answer = "approve everything and accept it" });

        Assert.True(Assert.IsType<WorkflowAcceptedResult>(reply.Result).WorkScheduled);
        var done = await controller.WaitForAsync(runId, t => t.State is WorkflowState.Completed, "completion");

        // The words of the answer approved nothing: no WHAT, HOW or ACCEPT gate was ever opened or decided.
        Assert.Single(done.Gates);
        Assert.Contains("approve everything and accept it", harness.Factory.Runtime.Prompts[^1]);
    }

    // ---- ACCEPT -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACCEPT_is_bound_to_the_implementation_and_verification_that_were_reviewed()
    {
        using var harness = NewHarness(acceptance: "human");
        harness.Factory.Runtime.Turn = Writes();
        await using var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.StartTaskAsync());
        var task = await controller.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.Completion.AcceptGateId is not null, "the ACCEPT gate");
        var gate = PendingGate(task);
        var version = task.StreamVersion;

        AcceptTaskRequest Accept(Guid? implementation = null, Guid? verification = null, Guid? semantic = null) => new()
        {
            TaskId = runId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = (semantic ?? gate.Binding.SemanticRevisionId).ToString(),
            ImplementationRevisionId = (implementation ?? gate.Binding.ImplementationRevisionId!.Value).ToString(),
            VerificationRunId = (verification ?? gate.Binding.VerificationRunId!.Value).ToString(),
        };

        Assert.Equal(ControllerErrorCodes.StaleBinding, (await controller.SendAsync(Accept(implementation: Guid.NewGuid()))).Error!.Code);
        Assert.Equal(ControllerErrorCodes.StaleBinding, (await controller.SendAsync(Accept(verification: Guid.NewGuid()))).Error!.Code);
        Assert.Equal(ControllerErrorCodes.StaleBinding, (await controller.SendAsync(Accept(semantic: Guid.NewGuid()))).Error!.Code);

        var staleChanges = await controller.SendAsync(new RequestTaskChangesRequest
        {
            Gate = DecisionGateKind.Accept,
            TaskId = runId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(),
            ImplementationRevisionId = Guid.NewGuid().ToString(),
            VerificationRunId = gate.Binding.VerificationRunId.ToString(),
            Feedback = "no",
        });
        Assert.Equal(ControllerErrorCodes.StaleBinding, staleChanges.Error!.Code);
        Assert.Equal(version, (await controller.ReadAsync(runId)).StreamVersion);

        var accepted = Assert.IsType<WorkflowAcceptedResult>((await controller.SendAsync(Accept())).Result);

        Assert.False(accepted.WorkScheduled);
        Assert.Equal(WorkflowState.Completed, accepted.Task.State);
        Assert.Equal(ControllerErrorCodes.AlreadyDecided, (await controller.SendAsync(Accept())).Error?.Code);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
    }

    [Fact]
    public async Task Requesting_changes_to_an_ACCEPT_gate_sends_the_same_work_back_to_the_agent_in_the_background()
    {
        using var harness = NewHarness(acceptance: "human");
        harness.Factory.Runtime.Turn = Writes();
        await using var controller = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await controller.StartTaskAsync());
        var task = await controller.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.Completion.AcceptGateId is not null, "the ACCEPT gate");
        var gate = PendingGate(task);
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release, thenWrite: false);

        var reply = await controller.SendAsync(new RequestTaskChangesRequest
        {
            Gate = DecisionGateKind.Accept,
            TaskId = runId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(),
            ImplementationRevisionId = gate.Binding.ImplementationRevisionId.ToString(),
            VerificationRunId = gate.Binding.VerificationRunId.ToString(),
            Feedback = "Also add a comment.",
        });

        Assert.True(Assert.IsType<WorkflowAcceptedResult>(reply.Result).WorkScheduled);
        await started.Task.WaitAsync(ControllerTestSupport.Patience);
        Assert.Contains("Also add a comment.", harness.Factory.Runtime.Prompts[^1]);
        release.SetResult();
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);
    }

    // ---- abort -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_waiting_task_is_aborted_once_and_nothing_follows()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        await using var __ = controller;
        var runId = new TaskId(task.TaskId);

        var reply = await controller.SendAsync(new AbortTaskRequest { TaskId = runId.ToString(), GateId = PendingGate(task).Id.ToString() });

        var aborted = Assert.IsType<WorkflowAcceptedResult>(reply.Result);
        Assert.False(aborted.WorkScheduled);
        Assert.Equal(WorkflowState.Cancelled, aborted.Task.State);

        Assert.False((await controller.SendAsync(new AbortTaskRequest { TaskId = runId.ToString() })).Ok);
        Assert.False((await controller.SendAsync(Approval(task))).Ok);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
    }

    // ---- idle ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_controller_that_supervises_work_never_idles_out_and_one_with_nothing_to_do_does()
    {
        using var harness = NewHarness();
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release);
        await using var controller = await SupervisedController.StartAsync(harness, idle: TimeSpan.FromMilliseconds(400));
        var runId = IdOf(await controller.StartTaskAsync());
        await started.Task.WaitAsync(ControllerTestSupport.Patience);

        // Several idle periods pass with no client connected and the agent still working.
        await Task.Delay(TimeSpan.FromMilliseconds(1800));
        Assert.False(controller.Exit.IsCompleted);
        Assert.Equal(1, controller.Supervisor.ActiveRuns);

        release.SetResult();
        await UntilAsync(() => controller.Supervisor.ActiveRuns == 0);

        // With nothing supervised and nobody connected, the idle clock runs again from the end of the work.
        Assert.Equal(ControllerExit.Idle, await controller.Exit.WaitAsync(ControllerTestSupport.Patience));
        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task A_shutdown_request_does_not_stop_a_controller_that_is_supervising_work_unless_it_is_forced()
    {
        using var harness = NewHarness();
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = Gated(started, release);
        await using var controller = await SupervisedController.StartAsync(harness);
        await controller.StartTaskAsync();
        await started.Task.WaitAsync(ControllerTestSupport.Patience);

        var refused = Assert.IsType<ShutdownResult>((await controller.SendAsync(new ShutdownRequest())).Result);
        Assert.False(refused.Accepted);
        Assert.Equal(1, refused.ActiveRuns);
        Assert.False(controller.Exit.IsCompleted);

        var health = Assert.IsType<HealthResult>((await controller.SendAsync(new HealthRequest())).Result);
        Assert.Equal(1, health.Supervision.ActiveRuns);
        Assert.True(health.Supervision.Durable);

        var forced = Assert.IsType<ShutdownResult>((await controller.SendAsync(new ShutdownRequest { Force = true })).Result);
        Assert.True(forced.Accepted);
        Assert.Equal(ControllerExit.Stopped, await controller.Exit.WaitAsync(ControllerTestSupport.Patience));
        release.TrySetResult();
    }

    [Fact]
    public async Task A_controller_that_cannot_outlive_its_parent_refuses_to_own_work_and_says_so()
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness, durable: false);

        var health = Assert.IsType<HealthResult>((await controller.SendAsync(new HealthRequest())).Result);
        Assert.False(health.Supervision.Durable);

        var refused = await controller.SendAsync(new StartTaskRequest { Objective = Objective, WorkingDirectory = harness.Workspace });

        Assert.Equal(ControllerErrorCodes.ControllerNotDurable, refused.Error!.Code);
        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.Empty(new RunStore(harness.Home).ListRunIds());

        // A routed command finds the controller unfit and runs directly, as it always did.
        harness.IsTerminal = false;
        harness.Factory.Runtime.Turn = Writes();
        Assert.Equal(0, await harness.RunAsync("run", Objective.Replace(' ', '_')));
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    // ---- malformed requests -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_workflow_requests_are_refused_and_start_nothing()
    {
        using var harness = NewHarness();
        await using var controller = await SupervisedController.StartAsync(harness);

        var requests = new ControllerRequest[]
        {
            new StartTaskRequest { Objective = "  ", WorkingDirectory = harness.Workspace },
            new StartTaskRequest { Objective = new string('x', 16_001), WorkingDirectory = harness.Workspace },
            new StartTaskRequest { Objective = Objective, WorkingDirectory = "relative/dir" },
            new StartTaskRequest { Objective = Objective, WorkingDirectory = harness.Workspace, Workspace = new string('p', 2000) },
            new ContinueTaskRequest { TaskId = "nope" },
            new AbortTaskRequest { TaskId = Guid.NewGuid().ToString() },
            new AnswerClarificationRequest { TaskId = Guid.NewGuid().ToString(), GateId = Guid.NewGuid().ToString(), SemanticRevisionId = Guid.NewGuid().ToString(), Answer = "" },
        };

        foreach (var request in requests)
            Assert.False((await controller.SendAsync(request)).Ok);

        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.Empty(new RunStore(harness.Home).ListRunIds());
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
        Assert.True((await controller.SendAsync(new HealthRequest())).Ok);
    }

    // ---- crash and restart --------------------------------------------------------------------------------------------------------------

    /// <summary>A WHAT-approved task whose controller died while the agent was working on it, and the harness for the replacement.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId, long VersionBefore)> InterruptedAsync(string? ownedSession = null, string? profile = null)
    {
        var harness = NewHarness(profile: profile);
        harness.Factory.OwnedSession = ownedSession;
        harness.Factory.Signals!.WhatByTurn[1] = What();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        var first = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await first.StartTaskAsync(requireWhat: true));
        var task = await first.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.PendingRequest is not null, "the WHAT proposal");

        var started = Signal();
        harness.Factory.Runtime.Turn = Gated(started, new TaskCompletionSource(), thenWrite: false);
        Assert.True((await first.SendAsync(Approval(task))).Ok);
        await started.Task.WaitAsync(ControllerTestSupport.Patience);
        Assert.Equal(WorkflowState.Running, (await first.ReadAsync(runId)).State);

        await first.CrashAsync();

        return (harness, runId, await DurableVersionAsync(harness, runId));
    }

    [Fact]
    public async Task A_replacement_controller_resumes_an_interrupted_stretch_in_the_same_conversation_without_a_second_agent_or_duplicate_events()
    {
        var (harness, runId, versionBefore) = await InterruptedAsync();
        using var _ = harness;

        // The dead controller left the run durably RUNNING with nobody holding it.
        Assert.Equal(WorkflowState.Running, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.False(new RunStore(harness.Home).IsLive(runId));
        Assert.True(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), RunStore.SupervisionFileName)));
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;
        var resumesBefore = harness.Factory.Conversations!.Resumes.Count;

        harness.Factory.Runtime.Turn = Writes();
        await using var second = await SupervisedController.StartAsync(harness);
        var done = await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "the resumed work to complete");

        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);   // never a fresh conversation
        Assert.Equal(resumesBefore + 1, harness.Factory.Conversations.Resumes.Count);
        Assert.All(harness.Factory.Conversations.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));
        Assert.Single(done.Gates);
        Assert.Single(done.Gates[0].Decision is null ? [] : new[] { done.Gates[0].Decision });
        Assert.True(done.StreamVersion > versionBefore);
        Assert.Single(done.SemanticRevisions, r => r.Kind == "human_what_approval");
        await UntilAsync(() => second.Supervisor.ActiveRuns == 0);
        Assert.False(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), RunStore.SupervisionFileName)));
    }

    [Fact]
    public async Task A_pi_bound_run_is_supervised_and_recovered_by_the_same_rules_and_resumes_its_recorded_pi_binding_once()
    {
        var (harness, runId, versionBefore) = await InterruptedAsync(profile: "pi-qwen");
        using var _ = harness;

        Assert.Equal(WorkflowState.Running, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.False(new RunStore(harness.Home).IsLive(runId));
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;
        var launchesBefore = harness.Factory.Launches.Count;

        harness.Factory.Runtime.Turn = Writes();
        await using var second = await SupervisedController.StartAsync(harness);
        var done = await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "the resumed Pi work to complete");

        var resumed = harness.Factory.Launches.Skip(launchesBefore).Single().Find(DirectPathRoles.RootRoleId)!;
        Assert.Equal(("pi", "llama/Qwen3.8-27B", "low"), (resumed.Execution.Harness, resumed.Execution.Model, resumed.Execution.Reasoning));
        Assert.All(harness.Factory.Launches, launch => Assert.Equal("pi", launch.Find(DirectPathRoles.RootRoleId)!.Execution.Harness));   // never run as Claude
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.All(harness.Factory.Conversations!.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));
        Assert.Single(done.Gates);
        Assert.Single(done.SemanticRevisions, r => r.Kind == "human_what_approval");
        Assert.True(done.StreamVersion > versionBefore);
        await UntilAsync(() => second.Supervisor.ActiveRuns == 0);
    }

    [Fact]
    public async Task A_decision_made_before_the_crash_cannot_be_made_again_after_the_restart()
    {
        var (harness, runId, _) = await InterruptedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();
        await using var second = await SupervisedController.StartAsync(harness);
        var done = await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;
        var resumesBefore = harness.Factory.Conversations!.Resumes.Count;

        var gate = done.Gates[0];
        var again = await second.SendAsync(new ApproveProposalRequest
        {
            Proposal = ProposalKind.What,
            TaskId = runId.ToString(),
            GateId = gate.Id.ToString(),
            SemanticRevisionId = gate.Binding.SemanticRevisionId.ToString(),
            ContentHash = gate.What!.ContentHash,
        });

        Assert.Equal(ControllerErrorCodes.AlreadyDecided, again.Error!.Code);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(resumesBefore, harness.Factory.Conversations.Resumes.Count);
    }

    [Fact]
    public async Task The_supervision_record_names_the_controller_that_health_reports()
    {
        using var harness = NewHarness();
        var started = Signal();
        var release = Signal();
        harness.Factory.Runtime.Turn = async (_, _, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);

            return AgentTurnOutcome.Done;
        };
        await using var controller = await SupervisedController.StartAsync(harness);

        var accepted = await controller.StartTaskAsync("Say who owns this run");
        await started.Task.WaitAsync(ControllerTestSupport.Patience);

        var info = Assert.IsType<HealthResult>((await controller.SendAsync(new HealthRequest())).Result).Controller;
        var record = new RunStore(harness.Home).ReadSupervision(IdOf(accepted)).Record;

        Assert.NotNull(record);
        Assert.Equal(info.InstanceId.ToString(), record.ControllerInstance);
        Assert.Equal(info.ProcessId, record.ControllerProcess);
        release.SetResult();
    }

    [Fact]
    public async Task The_agent_host_a_dead_controller_left_behind_is_ended_before_its_stretch_is_resumed()
    {
        var (harness, runId, _) = await InterruptedAsync(ownedSession: "agenticworkflow-aw-deadbeef");

        var record = new RunStore(harness.Home).ReadSupervision(runId).Record;
        Assert.Equal(["agenticworkflow-aw-deadbeef"], record!.Sessions);

        harness.Factory.Runtime.Turn = Writes();
        await using var second = await SupervisedController.StartAsync(harness);
        using var _ = harness;
        await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");

        Assert.Equal(["agenticworkflow-aw-deadbeef"], harness.Factory.OrphanStops);
    }

    [Fact]
    public async Task When_the_old_agent_host_cannot_be_confirmed_stopped_the_run_is_blocked_recoverably_and_no_second_agent_is_started()
    {
        var (harness, runId, _) = await InterruptedAsync(ownedSession: "agenticworkflow-aw-deadbeef");
        using var _ = harness;
        harness.Factory.OrphanStopSucceeds = false;
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;
        var resumesBefore = harness.Factory.Conversations!.Resumes.Count;

        await using var second = await SupervisedController.StartAsync(harness);
        var blocked = await second.ReadAsync(runId);

        Assert.Equal(WorkflowState.Blocked, blocked.State);
        Assert.NotNull(blocked.Recovery?.PendingWork);
        Assert.Equal(0, second.Supervisor.ActiveRuns);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(resumesBefore, harness.Factory.Conversations.Resumes.Count);

        // Still not confirmed: continue is refused, and nothing starts.
        var refused = await second.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() });
        Assert.Equal(ControllerErrorCodes.PreconditionFailed, refused.Error!.Code);
        Assert.Equal(resumesBefore, harness.Factory.Conversations.Resumes.Count);

        // Once it is confirmed gone, the person's continue resumes the same conversation.
        harness.Factory.OrphanStopSucceeds = true;
        harness.Factory.Runtime.Turn = Writes();
        var reply = await second.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() });

        Assert.True(Assert.IsType<WorkflowAcceptedResult>(reply.Result).WorkScheduled);
        await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
    }

    [Fact]
    public async Task A_stretch_that_keeps_killing_its_controller_is_not_resumed_forever()
    {
        var (harness, runId, _) = await InterruptedAsync();
        using var _ = harness;
        var store = new RunStore(harness.Home);
        var record = store.ReadSupervision(runId).Record!;
        store.WriteSupervision(runId, record with { Attempts = SupervisionRecord.MaxAutomaticAttempts });
        var resumesBefore = harness.Factory.Conversations!.Resumes.Count;

        await using var second = await SupervisedController.StartAsync(harness);
        var blocked = await second.ReadAsync(runId);

        Assert.Equal(WorkflowState.Blocked, blocked.State);
        Assert.NotNull(blocked.Recovery?.PendingWork);
        Assert.Equal(resumesBefore, harness.Factory.Conversations.Resumes.Count);
    }

    [Fact]
    public async Task A_first_stretch_that_was_interrupted_is_blocked_for_a_person_and_never_restarted_from_a_guess()
    {
        using var harness = NewHarness();
        var started = Signal();
        harness.Factory.Runtime.Turn = Gated(started, new TaskCompletionSource(), thenWrite: false);
        var first = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await first.StartTaskAsync());
        await started.Task.WaitAsync(ControllerTestSupport.Patience);
        await first.CrashAsync();
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;

        harness.Factory.Runtime.Turn = Writes();
        await using var second = await SupervisedController.StartAsync(harness);
        var blocked = await second.ReadAsync(runId);

        Assert.Equal(WorkflowState.Blocked, blocked.State);
        Assert.Contains("not resumed", blocked.StateReason);
        Assert.Null(blocked.Recovery?.PendingWork);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(0, second.Supervisor.ActiveRuns);

        // Nothing offers a restart from a guess; abort is the way out.
        Assert.False((await second.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() })).Ok);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);

        var aborted = Assert.IsType<WorkflowAcceptedResult>((await second.SendAsync(new AbortTaskRequest { TaskId = runId.ToString() })).Result);
        Assert.Equal(WorkflowState.Cancelled, aborted.Task.State);
    }

    [Fact]
    public async Task A_run_waiting_for_a_person_is_left_alone_by_a_replacement_controller()
    {
        var (harness, controller, task) = await ProposedAsync();
        using var _ = harness;
        var runId = new TaskId(task.TaskId);
        await controller.CrashAsync();
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;

        await using var second = await SupervisedController.StartAsync(harness);
        var after = await second.ReadAsync(runId);

        Assert.Equal(WorkflowState.AwaitingHuman, after.State);
        Assert.Equal(task.StreamVersion, after.StreamVersion);
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.True((await second.SendAsync(Approval(after))).Ok);
    }

    /// <summary>An attended task whose agent is waiting for a person in its own session when its controller dies; returns the harness and the run.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId)> WaitingInTheAgentsSessionAsync()
    {
        var harness = NewHarness();
        harness.Factory.OwnedSession = "agenticworkflow-aw-deadbeef";
        var step = 0;
        var never = new TaskCompletionSource<AgentTurnOutcome>();
        harness.Factory.Runtime.Turn = (_, _, token) => step++ == 0 ? Task.FromResult(ScriptedRuntime.Wait("approve Bash: dotnet --list-sdks")) : never.Task.WaitAsync(token);

        var first = await SupervisedController.StartAsync(harness);
        var runId = IdOf(await first.StartTaskAsync(presence: TaskPresence.Attended));
        await first.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.PendingRequest?.Kind == HumanRequestKind.NativeAgentInteraction, "the native wait");
        var store = new RunStore(harness.Home);
        await UntilAsync(() => store.ReadNativeInteraction(runId).Record is not null);

        await first.CrashAsync();

        return (harness, runId);
    }

    [Fact]
    public async Task A_native_wait_whose_controller_died_is_recorded_BLOCKED_and_offers_abort_as_the_only_way_out()
    {
        var (harness, runId) = await WaitingInTheAgentsSessionAsync();
        using var _ = harness;
        var store = new RunStore(harness.Home);

        // The dead controller left the run asking for a person in a session that is gone with it.
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        var startsBefore = harness.Factory.Runtime.StartRequests.Count;

        await using var second = await SupervisedController.StartAsync(harness);
        var blocked = await second.WaitForAsync(runId, t => t.State == WorkflowState.Blocked, "the stale native wait to be recorded as BLOCKED");

        Assert.Equal(BlockKind.NativeInteractionUnavailable, blocked.Block!.Kind);
        Assert.Contains("controller that held the native session ended", blocked.Block.Reason);
        Assert.Contains("approve Bash: dotnet --list-sdks", blocked.Block.Reason);
        Assert.Equal([RecoveryAction.Abort], blocked.Recovery!.Actions);
        Assert.Null(blocked.PendingRequest);

        // The agent host the dead controller recorded was ended, the stale annotation is gone, and no second agent was started.
        Assert.Equal(["agenticworkflow-aw-deadbeef"], harness.Factory.OrphanStops);
        Assert.Null(store.ReadNativeInteraction(runId).Record);
        Assert.False(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), RunStore.SupervisionFileName)));
        Assert.Equal(startsBefore, harness.Factory.Runtime.StartRequests.Count);

        // Nothing offers to continue a session that cannot be answered; abort is the way out.
        Assert.False((await second.SendAsync(new ContinueTaskRequest { TaskId = runId.ToString() })).Ok);
        var aborted = Assert.IsType<WorkflowAcceptedResult>((await second.SendAsync(new AbortTaskRequest { TaskId = runId.ToString() })).Result);
        Assert.Equal(WorkflowState.Cancelled, aborted.Task.State);
    }

    [Fact]
    public async Task A_native_wait_is_left_as_it_is_when_its_agent_host_cannot_be_confirmed_stopped()
    {
        var (harness, runId) = await WaitingInTheAgentsSessionAsync();
        using var _ = harness;
        harness.Factory.OrphanStopSucceeds = false;

        await using var second = await SupervisedController.StartAsync(harness);
        var after = await second.ReadAsync(runId);

        // Nothing is recorded over a session that may still be alive; the next controller tries again.
        Assert.Equal(WorkflowState.AwaitingHuman, after.State);
        Assert.True(File.Exists(Path.Combine(harness.Home, "runs", runId.ToString(), RunStore.SupervisionFileName)));
    }

    [Fact]
    public async Task Two_replacement_controllers_never_both_resume_the_same_run()
    {
        var (harness, runId, _) = await InterruptedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();
        var resumesBefore = harness.Factory.Conversations!.Resumes.Count;

        await using var second = await SupervisedController.StartAsync(harness);

        // The instance lock admits one controller per state root; a second host leaves without touching anything.
        var services = harness.Services();
        var root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        var paths = new ControllerPaths(root);
        var duplicate = new ControllerHost(
            ControllerEndpoints.For(root, services.GetEnvironmentVariable),
            new RunTaskCatalog(services, new RunStore(root)),
            new ControllerHostOptions { InstanceLockPath = paths.InstanceLock, Version = "test" },
            new ControllerLog(paths.Log));

        Assert.Equal(ControllerExit.AlreadyRunning, await duplicate.RunAsync(null, CancellationToken.None));

        await second.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Equal(resumesBefore + 1, harness.Factory.Conversations.Resumes.Count);
    }

    // ---- the command line and the controller ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_a_controller_running_the_mutating_commands_are_routed_through_its_typed_operations()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = What();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        await using var controller = await SupervisedController.StartAsync(harness);

        var run = await harness.RunSeparatelyAsync("run", "Expose", "--require", "what");
        Assert.Equal(0, run.Exit);
        Assert.Contains("Accepted", run.Out);

        var listing = new RunStore(harness.Home).ListRunIds();
        var runId = Assert.Single(listing);
        var task = await controller.WaitForAsync(runId, t => t.State == WorkflowState.AwaitingHuman && t.PendingRequest is not null, "the WHAT proposal");
        var gate = PendingGate(task);

        var stale = await harness.RunSeparatelyAsync("approve", runId.ToString(), "--gate", Guid.NewGuid().ToString());
        Assert.Equal(18, stale.Exit);
        Assert.Contains("Nothing was recorded", stale.Error);

        harness.Factory.Runtime.Turn = Writes();
        var approve = await harness.RunSeparatelyAsync("approve", runId.ToString(), "--gate", gate.Id.ToString());
        Assert.Equal(0, approve.Exit);
        Assert.Contains("Accepted", approve.Out);

        await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Single(harness.Factory.Runtime.StartRequests);

        var again = await harness.RunSeparatelyAsync("approve", runId.ToString(), "--gate", gate.Id.ToString());
        Assert.Equal(18, again.Exit);
    }

    [Fact]
    public async Task The_direct_flag_runs_a_command_in_its_own_process_and_the_controller_is_not_involved()
    {
        using var harness = NewHarness();
        harness.Factory.Runtime.Turn = Writes();
        await using var controller = await SupervisedController.StartAsync(harness);

        var run = await harness.RunSeparatelyAsync("run", "Expose", "--direct");

        Assert.Equal(0, run.Exit);
        Assert.Contains("COMPLETED", run.Out);
        Assert.Equal(0, controller.Supervisor.ActiveRuns);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task Without_a_controller_a_command_runs_directly_and_never_starts_one()
    {
        using var harness = NewHarness();
        harness.Factory.Runtime.Turn = Writes();
        var launcher = new InProcessControllerLauncher(harness);
        harness.ControllerLauncher = launcher;
        await using var _ = launcher;

        var run = await harness.RunSeparatelyAsync("run", "Expose");

        Assert.Equal(0, run.Exit);
        Assert.Contains("COMPLETED", run.Out);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_typed_request_can_be_sent_from_standard_input_and_is_decoded_as_strictly_as_the_controller_does()
    {
        using var harness = NewHarness();
        harness.Factory.Runtime.Turn = Writes();
        await using var controller = await SupervisedController.StartAsync(harness);

        harness.Input = new StringReader($$"""{"operation":"start_task","objective":"Expose","workingDirectory":{{System.Text.Json.JsonSerializer.Serialize(harness.Workspace)}}}""");
        var started = await harness.RunSeparatelyAsync("ctl", "send");

        Assert.Equal(0, started.Exit);
        Assert.Contains("\"kind\": \"accepted\"", started.Out);

        harness.Input = new StringReader("""{"operation":"start_task","objective":"Expose","workingDirectory":"/x","run":"rm -rf /"}""");
        var unknownMember = await harness.RunSeparatelyAsync("ctl", "send");
        Assert.Equal(2, unknownMember.Exit);

        harness.Input = new StringReader("""{"operation":"exec","command":"rm -rf /"}""");
        var unsupported = await harness.RunSeparatelyAsync("ctl", "send");
        Assert.Equal(2, unsupported.Exit);
        Assert.Contains("unsupported_operation", unsupported.Out);

        var run = Assert.Single(new RunStore(harness.Home).ListRunIds());
        await controller.WaitForAsync(run, t => t.State == WorkflowState.Completed, "completion");
    }

    /// <summary>The run's stream version as the durable store has it, with no controller needed to serve it.</summary>
    private static async Task<long> DurableVersionAsync(CliHarness harness, TaskId runId)
    {
        var services = harness.Services();
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));
        var outcome = await new RunTaskCatalog(services, store).ReadAsync(runId, CancellationToken.None);

        return outcome.Model!.StreamVersion;
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
