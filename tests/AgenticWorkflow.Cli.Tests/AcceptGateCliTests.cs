using System.Security.Cryptography;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The M4A final ACCEPT gate through the real CLI composition (commands, SQLite event stream, Git fingerprints, local verification, runner) with only the
/// agent runtime scripted. Every decision here is a fresh <c>CliApp</c> over the durable state, as a later aw process would be.
/// </summary>
public sealed class AcceptGateCliTests
{
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes(string content = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), content + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static async Task<(CliHarness Harness, TaskId RunId, GateId GateId)> AwaitingAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Writes();

        Assert.Equal(17, await harness.RunAsync("run", "task"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedGateId());
        harness.ResetOutput();

        return result;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- run: verified work awaits the human, it is not complete ----------------------------------------------------------

    [Fact]
    public async Task A_verified_run_ends_awaiting_acceptance_with_a_distinct_exit_code_and_the_actions_that_decide_it()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.NotEqual(15, exit);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.DoesNotContain("COMPLETED", output);
        Assert.Contains("Verification: PASSED", output);
        Assert.Contains("aw accept", output);
        Assert.Contains("aw request-changes", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("Resume is not yet supported", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.False(task.IsTerminal);
        Assert.Equal(harness.PrintedGateId(), task.PendingGate!.Id);
        Assert.Equal("AwaitingAcceptance", new RunStore(harness.Home).ReadOutcome(harness.PrintedRunId()).Outcome!.Outcome);

        // the agent session ended before the human was asked: nothing is kept alive for the gate
        Assert.Single(harness.Factory.Runtime.Stopped);
        Assert.Equal(1, harness.Factory.Disposed);
    }

    [Fact]
    public async Task A_fresh_process_status_shows_the_pending_gate_its_binding_and_its_actions_and_is_not_a_native_wait()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("status", runId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("State: AWAITING_HUMAN", output);
        Assert.Contains("Pending gate: ACCEPT (owned by AgenticWorkflow", output);
        Assert.Contains($"gate: {gateId}", output);
        Assert.Contains("binding: current", output);
        Assert.Contains("working tree: unchanged since verification", output);
        Assert.Contains("Verification: PASSED", output);
        Assert.Contains($"aw accept {runId} --gate {gateId}", output);
        Assert.Contains("aw request-changes", output);
        Assert.Contains("awaiting the human's ACCEPT", output);

        // the gate belongs to the workflow, so none of the native-interaction wording applies
        Assert.DoesNotContain("Owner: the agent's native interface", output);
        Assert.DoesNotContain("Agent session: ended", output);
        Assert.DoesNotContain("Resume is not yet supported", output);
        Assert.Equal(1, harness.Factory.Created);   // status started no runtime
    }

    // ---- accept -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Accept_from_a_fresh_process_completes_the_run_without_any_agent_or_model()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        var startedBefore = harness.Factory.Runtime.StartRequests.Count;
        var promptsBefore = harness.Factory.Runtime.Prompts.Count;

        var exit = await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.StartsWith("COMPLETED", output);
        Assert.Contains($"Accepted gate: {gateId}", output);
        Assert.Equal(string.Empty, harness.Error.ToString());

        // no runtime, no session, no prompt
        Assert.Equal(1, harness.Factory.Created);
        Assert.Equal(startedBefore, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(promptsBefore, harness.Factory.Runtime.Prompts.Count);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(GateAction.Accept, task.DecisionFor(gateId)!.Action);
        Assert.Null(task.PendingGate);
        Assert.Equal("Completed", new RunStore(harness.Home).ReadOutcome(runId).Outcome!.Outcome);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("State: COMPLETED", harness.Out.ToString());
        Assert.Contains($"ACCEPT {gateId}: ACCEPTED", harness.Out.ToString());
    }

    [Fact]
    public async Task Accept_without_naming_the_gate_decides_the_open_one()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString()));
        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task A_second_accept_is_refused_and_records_nothing()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString()));
        var versionBefore = (await LoadAsync(harness, runId)).LastSequence;
        harness.ResetOutput();

        var exit = await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString());

        Assert.Equal(18, exit);
        Assert.Contains("ACCEPT REFUSED", harness.Error.ToString());
        Assert.Equal(string.Empty, harness.Out.ToString());
        Assert.Equal(versionBefore, (await LoadAsync(harness, runId)).LastSequence);
        Assert.Single((await LoadAsync(harness, runId)).GateDecisions);
    }

    [Fact]
    public async Task Accept_refuses_a_gate_the_run_does_not_have()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("accept", runId.ToString(), "--gate", Guid.NewGuid().ToString());

        Assert.Equal(18, exit);
        Assert.Contains("unknown gate", harness.Error.ToString());
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task Accept_refuses_a_gate_that_belongs_to_another_run()
    {
        var (first, firstRun, firstGate) = await AwaitingAsync();
        using var _a = first;
        using var second = new CliHarness(CliHarness.ValidConfig());
        second.Factory.Runtime.Turn = Writes();
        Assert.Equal(17, await second.RunAsync("run", "other task"));
        var secondRun = second.PrintedRunId();

        // the first run's gate id, offered to the second run
        var exit = await second.RunAsync("accept", secondRun.ToString(), "--gate", firstGate.ToString());

        Assert.Equal(18, exit);
        Assert.Contains("unknown gate", second.Error.ToString());
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(second, secondRun)).WorkflowState);
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(first, firstRun)).WorkflowState);
    }

    [Fact]
    public async Task Accept_before_verification_is_refused_because_no_gate_exists()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        var exit = await harness.RunAsync("accept", runId.ToString());

        Assert.Equal(18, exit);
        Assert.Contains("no gate is waiting", harness.Error.ToString());
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
    }

    [Fact]
    public async Task Accept_after_the_working_tree_changed_is_refused_because_the_gate_describes_what_was_verified()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        File.AppendAllText(Path.Combine(harness.Workspace, "src", "Feature.txt"), "// edited after verification\n");
        var sequenceBefore = (await LoadAsync(harness, runId)).LastSequence;

        var exit = await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString());

        Assert.Equal(18, exit);
        Assert.Contains("working tree changed", harness.Error.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Equal(sequenceBefore, task.LastSequence);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Empty(task.GateDecisions);

        // and status says so before the person tries
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("working tree: CHANGED since verification", harness.Out.ToString());
    }

    [Fact]
    public async Task Accept_of_a_run_that_does_not_exist_is_not_found_and_creates_nothing()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var id = Guid.CreateVersion7();

        var exit = await harness.RunAsync("accept", id.ToString());

        Assert.Equal(3, exit);
        Assert.Contains("RUN NOT FOUND", harness.Error.ToString());
        Assert.False(Directory.Exists(new RunStore(harness.Home).RunDirectory(new TaskId(id))));
    }

    [Fact]
    public async Task Accept_of_a_corrupt_run_is_unreadable_and_leaves_it_exactly_as_it_was()
    {
        var (harness, runId, _) = await AwaitingAsync();
        var database = new RunStore(harness.Home).DatabasePath(runId);
        using var _ = harness;
        File.WriteAllText(database, "this is not a sqlite database at all, just text that is long enough to look like a file");
        var before = HashOf(database);

        var exit = await harness.RunAsync("accept", runId.ToString());

        Assert.Equal(4, exit);
        Assert.Contains("RUN STATE UNREADABLE", harness.Error.ToString());
        Assert.Equal(before, HashOf(database));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("looks good, ship it")]
    public async Task Words_beside_the_run_id_are_a_usage_error_never_an_approval(string words)
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("accept", runId.ToString(), words));

        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
    }

    [Fact]
    public async Task Accept_needs_a_run_id()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, await harness.RunAsync("accept"));
    }

    [Fact]
    public async Task Accept_takes_a_real_run_id_not_a_description_of_one()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, await harness.RunAsync("accept", "the-last-run"));
    }

    // ---- abort at the gate ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Abort_at_the_gate_closes_it_without_approval_and_cancels_the_run()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(16, exit);
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Cancelled, task.WorkflowState);
        Assert.Equal(GateAction.Cancel, task.DecisionFor(gateId)!.Action);

        harness.ResetOutput();
        Assert.Equal(18, await harness.RunAsync("accept", runId.ToString(), "--gate", gateId.ToString()));
    }

    // ---- request changes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Request_changes_closes_the_gate_runs_a_new_session_with_the_feedback_and_opens_a_new_gate()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes(CliHarness.Symbol + " // now with tests");

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", firstGate.ToString(), "--feedback", "Please also add a test for the empty case.");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Contains("changes requested (not approved", output);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.DoesNotContain("COMPLETED", output);

        // this scripted runtime cannot resume, so: an explicit fresh session, sent the original task and the feedback and no transcript of the first
        Assert.Equal(2, harness.Factory.Runtime.StartRequests.Count);
        Assert.Equal(2, harness.Factory.Created);
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains("task", prompt);
        Assert.Contains("Please also add a test for the empty case.", prompt);
        Assert.Contains("symbol_present", prompt);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(2, task.Gates.Length);
        Assert.Equal(GateAction.RequestChanges, task.DecisionFor(firstGate)!.Action);
        Assert.Equal("Please also add a test for the empty case.", task.DecisionFor(firstGate)!.Feedback);
        Assert.NotEqual(firstGate, task.PendingGate!.Id);
        Assert.True(task.IsCurrent(task.PendingGate));
        Assert.Equal(SemanticChangeKind.HumanRequestChanges, task.CurrentSemanticRevision.Kind);

        // the old gate can never be accepted; the new one can, from a fresh process
        harness.ResetOutput();
        Assert.Equal(18, await harness.RunAsync("accept", runId.ToString(), "--gate", firstGate.ToString()));
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("accept", runId.ToString(), "--gate", task.PendingGate.Id.ToString()));
        Assert.Equal(WorkflowState.Completed, (await LoadAsync(harness, runId)).WorkflowState);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("CHANGES REQUESTED", harness.Out.ToString());
        Assert.Contains("ACCEPTED", harness.Out.ToString());
        Assert.Contains("feedback: Please also add a test for the empty case.", harness.Out.ToString());
    }

    [Fact]
    public async Task Request_changes_whose_new_work_fails_verification_does_not_open_a_gate()
    {
        var (harness, runId, firstGate) = await AwaitingAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes("WRONG");   // removes the symbol the check needs, on every turn

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--feedback", "Rewrite it.");

        Assert.Equal(12, exit);
        var task = await LoadAsync(harness, runId);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Null(task.PendingGate);
        Assert.Single(task.Gates);
        Assert.Equal(GateStatus.ChangesRequested, task.StatusOf(task.Gates[0]));
        Assert.Equal(firstGate, task.Gates[0].Id);
    }

    [Fact]
    public async Task Request_changes_needs_feedback_and_records_nothing_without_it()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;
        var sequenceBefore = (await LoadAsync(harness, runId)).LastSequence;

        Assert.Equal(2, await harness.RunAsync("request-changes", runId.ToString()));
        Assert.Equal(2, await harness.RunAsync("request-changes", runId.ToString(), "--feedback", "   "));
        Assert.Equal(2, await harness.RunAsync("request-changes", runId.ToString(), "--feedback", new string('x', 2001)));

        Assert.Equal(sequenceBefore, (await LoadAsync(harness, runId)).LastSequence);
        Assert.Single(harness.Factory.Runtime.StartRequests);   // no second session was started
    }

    [Fact]
    public async Task Request_changes_refuses_before_recording_anything_when_the_configuration_changed()
    {
        var (harness, runId, gateId) = await AwaitingAsync();
        using var _ = harness;
        harness.Write("agenticworkflow.json", CliHarness.ValidConfig(maxTurns: 9));
        var sequenceBefore = (await LoadAsync(harness, runId)).LastSequence;

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", gateId.ToString(), "--feedback", "More.");

        Assert.Equal(2, exit);
        Assert.Contains("changed since this run started", harness.Error.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Equal(sequenceBefore, task.LastSequence);
        Assert.Equal(gateId, task.PendingGate!.Id);
        Assert.Equal(1, harness.Factory.Created);
    }

    [Fact]
    public async Task Request_changes_of_a_stale_or_unknown_gate_is_refused()
    {
        var (harness, runId, _) = await AwaitingAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("request-changes", runId.ToString(), "--gate", Guid.NewGuid().ToString(), "--feedback", "More.");

        Assert.Equal(18, exit);
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Equal(1, harness.Factory.Created);
    }
}
