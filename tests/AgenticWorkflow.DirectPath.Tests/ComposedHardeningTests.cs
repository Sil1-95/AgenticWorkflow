using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Execution.Local;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// M2C offline proof of what the live tests prove with a real agent: the test-only disturbance hook is detected by the REAL
/// verifier over real Git and SQLite and repaired in the same session; and every kind of stop is classified distinctly.
/// No Herdr, no Claude, no model.
/// </summary>
public sealed class ComposedHardeningTests : IDisposable
{
    private readonly DirectFixtureRepo _repo = DirectFixtureRepo.Create(
        ("src/Meals/Menu.cs", "public string GetMeal() => \"pasta\";\n"),
        ("src/Meals/Waiter.cs", "public string Serve(Menu m) => m.GetMeal();\n"),
        ("tests/Meals.Tests/MenuTests.cs", "Assert.Equal(\"pasta\", new Menu().GetMeal());\n"),
        ("src/Meals/Meals.csproj", "<Project />\n"));

    private readonly string _database = Path.Combine(Path.GetTempPath(), "aw-hardening-" + Guid.NewGuid().ToString("N")[..8] + ".db");

    public void Dispose()
    {
        _repo.Dispose();
        LiveFixtureSupport.DeleteDatabase(_database);
    }

    private void RenameEverywhere()
    {
        foreach (var path in new[] { "src/Meals/Menu.cs", "src/Meals/Waiter.cs", "tests/Meals.Tests/MenuTests.cs" })
            _repo.Write(path, _repo.Read(path).Replace("GetMeal", "GetFood"));
    }

    private async Task<(DirectRunResult Result, WorkflowTaskService Service)> RunAsync(IAgentRuntime runtime, int maxTurns = 3)
    {
        var service = await DirectPathSetup.OpenServiceAsync(_database);
        await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood");

        var runner = new DirectPathRunner(service, runtime, new GitImplementationStateProvider(), new LocalVerificationRunner());
        var result = await runner.RunAsync(DirectPathSetup.TaskId, DirectPathSetup.Definition(_repo.Root, DirectPathSetup.SymbolChecks(), maxTurns));
        return (result, service);
    }

    // ---- the disturbance hook, with the real verifier ----------------------------------------------------------------------

    [Fact]
    public async Task A_disturbance_after_turn_one_is_failed_by_the_real_verifier_repaired_in_the_same_session_and_completed_on_the_repaired_revision()
    {
        var stub = new StubRuntime(RenameEverywhere, RenameEverywhere);
        var runtime = new ObservingAgentRuntime(stub);
        var disturbances = 0;

        runtime.AfterTurn = (_, turn, outcome) =>
        {
            if (turn.Value == 1 && outcome.Status == AgentTurnStatus.Done && disturbances++ == 0)
                _repo.Write("tests/Meals.Tests/MenuTests.cs", _repo.Read("tests/Meals.Tests/MenuTests.cs").Replace("GetFood", "GetMeal"));

            return Task.CompletedTask;
        };

        var (result, service) = await RunAsync(runtime);

        Assert.Equal(1, disturbances);
        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Single(runtime.Started);
        Assert.Equal(runtime.Sent[0].Session, runtime.Sent[1].Session);
        Assert.Equal([1, 2], runtime.Sent.Select(s => s.Turn.Value).ToArray());
        Assert.StartsWith("Controller verification failed", runtime.Sent[1].Prompt);
        Assert.Contains("check: old_symbol_absent", runtime.Sent[1].Prompt);
        Assert.DoesNotContain("check: new_symbol_present", runtime.Sent[1].Prompt);

        var task = (await service.LoadAsync(DirectPathSetup.TaskId)).Task;
        Assert.Equal(2, task.ImplementationRevisions.Length);
        Assert.Equal(VerificationOutcome.Failed, task.Verifications[0].Outcome);
        Assert.Equal(task.ImplementationRevisions[0].Id, task.Verifications[0].ImplementationRevisionId);
        Assert.Equal(VerificationOutcome.Passed, task.CurrentVerification!.Outcome);
        Assert.Equal(task.CurrentImplementationRevisionId, task.CurrentVerification.ImplementationRevisionId);
        Assert.NotEqual(task.ImplementationRevisions[0].Id, task.CurrentImplementationRevisionId);

        var onDisk = await new GitImplementationStateProvider().CaptureAsync(_repo.Root);
        Assert.Equal(onDisk.Fingerprint, task.CurrentImplementationRevision!.Fingerprint);
    }

    // ---- classification of every stop --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_runtime_that_never_becomes_ready_is_a_startup_block_not_a_runtime_error_and_records_only_the_block()
    {
        var stub = new StubRuntime { StartFailure = new AgentStartupBlockedException("the agent is blocked at startup (waiting: trust dialog); this runtime never answers dialogs") };

        var (result, service) = await RunAsync(stub);

        Assert.Equal(RunClassification.StartupBlock, RunClassifier.Classify(result));
        await AssertNothingRecordedButTheBlockAsync(service);
    }

    [Fact]
    public async Task No_structured_ready_status_is_also_a_startup_block()
    {
        var stub = new StubRuntime { StartFailure = new AgentStartupBlockedException("no structured ready status within 00:02:00; a dialog may be waiting") };

        var (result, _) = await RunAsync(stub);

        Assert.Equal(RunClassification.StartupBlock, RunClassifier.Classify(result));
    }

    [Fact]
    public async Task A_native_permission_wait_with_nobody_attending_ends_blocked_and_nothing_is_answered()
    {
        var interaction = new AgentInteraction(AgentInteractionKind.Permission, "approve Bash: dotnet --list-sdks");
        var stub = new StubRuntime { Outcome = AgentTurnOutcome.AwaitingHuman(interaction) };

        var (result, service) = await RunAsync(stub);

        Assert.Equal(RunClassification.NativeInteractionUnavailable, RunClassifier.Classify(result));
        Assert.Equal(DirectRunStatus.NativeInteractionUnavailable, result.Status);
        Assert.Single(stub.Prompts);       // nothing was sent to answer it
        Assert.Equal(1, stub.Stops);       // only the runner's own session is stopped

        // the workflow says only what was observed; no revision, verification or decision was invented
        var task = (await service.LoadAsync(DirectPathSetup.TaskId)).Task;
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Contains("approve Bash: dotnet --list-sdks", task.StateReason);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Empty(task.Verifications);
        Assert.Empty(task.PolicyDecisions);
    }

    [Fact]
    public async Task A_verification_that_keeps_failing_is_a_verification_failure_not_a_runtime_error()
    {
        var (result, service) = await RunAsync(new StubRuntime(() => { }, () => { }), maxTurns: 2);

        Assert.Equal(RunClassification.VerificationFailure, RunClassifier.Classify(result));
        Assert.NotEqual(WorkflowState.Completed, (await service.LoadAsync(DirectPathSetup.TaskId)).Task.WorkflowState);
    }

    [Fact]
    public async Task A_process_or_runtime_error_is_a_runtime_error()
    {
        var (errored, _) = await RunAsync(new StubRuntime { Outcome = new AgentTurnOutcome(AgentTurnStatus.Error, "the agent exited") });
        Assert.Equal(RunClassification.RuntimeError, RunClassifier.Classify(errored));

        File.Delete(_database);
        var (failed, _) = await RunAsync(new StubRuntime { WaitFailure = new AgentRuntimeException("the connection to the core was lost") });
        Assert.Equal(RunClassification.RuntimeError, RunClassifier.Classify(failed));
    }

    [Fact]
    public void A_stale_workflow_is_its_own_classification()
    {
        var stale = new DirectRunResult { Status = DirectRunStatus.StaleWorkflowState, Reason = "stale_workflow_state: the task moved" };
        Assert.Equal(RunClassification.StaleWorkflowState, RunClassifier.Classify(stale));
    }

    [Theory]
    [InlineData(DirectRunStatus.AwaitingAcceptance, RunClassification.AwaitingAcceptance)]
    [InlineData(DirectRunStatus.Rejected, RunClassification.Other)]
    [InlineData(DirectRunStatus.Interrupted, RunClassification.Other)]
    [InlineData(DirectRunStatus.Indeterminate, RunClassification.Other)]
    [InlineData(DirectRunStatus.AgentCancelled, RunClassification.RuntimeError)]
    public void Remaining_statuses_have_one_stable_classification(DirectRunStatus status, RunClassification expected)
        => Assert.Equal(expected, RunClassifier.Classify(new DirectRunResult { Status = status, Reason = "x" }));

    /// <summary>A run that never became ready claims no implementation, verification or policy outcome; the only thing recorded is that nothing can progress (BLOCKED).</summary>
    private static async Task AssertNothingRecordedButTheBlockAsync(WorkflowTaskService service)
    {
        var task = (await service.LoadAsync(DirectPathSetup.TaskId)).Task;

        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(RuntimeFailureBlock.IsBlockedBy(task));
        Assert.Empty(task.ImplementationRevisions);
        Assert.Empty(task.Verifications);
        Assert.Empty(task.PolicyDecisions);
    }

    /// <summary>A stub agent: each turn runs one edit action; configurable outcome and failures.</summary>
    private sealed class StubRuntime(params Action[] turns) : IAgentRuntime
    {
        private int _turn;

        public List<string> Prompts { get; } = [];

        public int Stops { get; private set; }

        public AgentTurnOutcome Outcome { get; init; } = AgentTurnOutcome.Done;

        public Exception? StartFailure { get; init; }

        public Exception? WaitFailure { get; init; }

        public Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
            => StartFailure is null ? Task.FromResult(new AgentSessionId("stub")) : Task.FromException<AgentSessionId>(StartFailure);

        public Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new AgentTurnNumber(++_turn));
        }

        public Task<AgentTurnOutcome> WaitForTurnAsync(AgentSessionId session, AgentTurnNumber turn, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (WaitFailure is not null)
                return Task.FromException<AgentTurnOutcome>(WaitFailure);

            if (turn.Value <= turns.Length)
                turns[turn.Value - 1]();

            return Task.FromResult(Outcome);
        }

        public Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.CompletedTask;
        }
    }
}
