using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M6 through the real CLI composition: an approved HOW, new evidence the agent reports, and a later, fresh <c>aw</c> process that shows exactly what was invalidated,
/// what is stale and what is provisional, then decides the revised HOW. Only the agent runtime is scripted.
/// </summary>
public sealed class ReclassificationCliTests
{
    private const string Evidence = "The checker rejects any write that is not routed through the audit log.";
    private const string Reason = "The approved strategy writes the file directly, which bypasses the audit log.";
    private const string RevisedStrategy = "Write the symbol through the audit log helper; no direct write.";

    private static AgentHowSignal How(string strategy = "Write the symbol into src/Feature.txt directly.")
        => new(strategy, "One helper owns the write.", null, null, "A direct write is the smallest change.", null, "A direct write and a helper are both plausible.");

    private static AgentReclassificationSignal Report()
        => new(Evidence, ReclassificationStage.How, Reason);

    private static CliHarness NewHarness(string? acceptance = "none")
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();

        return harness;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    /// <summary>
    /// A forced run: HOW v1 proposed (turn 1) and approved; the resumed turn 2 leaves provisional edits and reports the evidence; turn 3 proposes HOW v2. Ends at exit 22.
    /// </summary>
    private static async Task<(CliHarness Harness, TaskId RunId, GateId V1, GateId V2)> ReclassifiedAsync(Func<int, string, CancellationToken, Task<AgentTurnOutcome>>? turn3 = null)
    {
        var harness = NewHarness();
        harness.Factory.Signals!.HowByTurn[1] = How();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(22, await harness.RunAsync("run", "task", "--require", "how"));
        var runId = harness.PrintedRunId();
        var v1 = harness.PrintedWhatId();
        harness.ResetOutput();

        harness.Factory.Signals.ReclassByTurn[2] = Report();
        harness.Factory.Signals.HowByTurn[3] = How(RevisedStrategy);
        harness.Factory.Runtime.Turn = turn3 ?? ((n, workspace, _) =>
        {
            if (n == 2)
                File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), "provisional\n");

            return Task.FromResult(AgentTurnOutcome.Done);
        });

        Assert.Equal(22, await harness.RunAsync("approve", runId.ToString(), "--gate", v1.ToString()));
        var v2 = harness.PrintedWhatId();

        return (harness, runId, v1, v2);
    }

    [Fact]
    public async Task The_report_after_a_reclassification_shows_the_evidence_the_invalidated_HOW_stale_state_the_provisional_workspace_and_the_new_HOW()
    {
        var (harness, runId, v1, v2) = await ReclassifiedAsync();
        using var _ = harness;

        var output = harness.Out.ToString();
        Assert.NotEqual(v1, v2);
        Assert.Contains("HOW PROPOSED - AWAITING YOUR APPROVAL", output);
        Assert.Contains("Reclassification (semantic revision 3, agent_reclassification, active", output);
        Assert.Contains(Evidence, output);
        Assert.Contains(Reason, output);
        Assert.Contains("rerouted to: HOW (the WHAT remains valid)", output);
        Assert.Contains($"invalidated: HOW {v1}", output);
        Assert.Contains("implementation: none recorded (the workspace holds provisional edits that are not an implementation)", output);
        Assert.Contains("provisional work preserved (not reverted, not absorbed as an implementation)", output);
        Assert.Contains(RevisedStrategy, output);
        Assert.Contains($"aw approve {runId} --gate {v2}", output);
        Assert.Contains($"aw request-changes {runId} --gate {v2}", output);
        Assert.DoesNotContain("COMPLETED", output);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(GateStatus.Invalidated, task.StatusOf(task.Gates.First(g => g.Id == v1)));
        Assert.StartsWith("provisional", File.ReadAllText(Path.Combine(harness.Workspace, "src", "Feature.txt")), StringComparison.Ordinal);   // not reverted
    }

    [Fact]
    public async Task Status_from_a_fresh_process_shows_the_semantic_revision_the_invalidated_approval_in_the_history_and_the_next_action()
    {
        var (harness, runId, v1, v2) = await ReclassifiedAsync();
        using var _ = harness;
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("Semantic revision: 3 (agent_reclassification)", status);   // 1 initial, 2 HOW approval, 3 the reclassification; HOW v2 is proposed under it
        Assert.Contains("HOW: not approved yet", status);
        Assert.Contains("Reclassification (", status);
        Assert.Contains($"invalidated: HOW {v1}", status);
        Assert.Contains($"HOW {v1}: APPROVED -> INVALIDATED", status);
        Assert.Contains("Pending gate: HOW", status);
        Assert.Contains(RevisedStrategy, status);
        Assert.Contains($"aw approve {runId} --gate {v2}", status);
        Assert.DoesNotContain("aw continue", status);
    }

    [Fact]
    public async Task Runs_annotates_a_reclassified_pending_HOW_from_durable_state_alone()
    {
        var (harness, runId, _, _) = await ReclassifiedAsync();
        using var _ = harness;
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var line = harness.Out.ToString().Split('\n').First(l => l.Contains(runId.ToString(), StringComparison.Ordinal));
        Assert.Contains("AWAITING_HUMAN (HOW, reclassified)", line);
    }

    [Fact]
    public async Task Approving_the_revised_HOW_resumes_the_same_conversation_adapts_the_provisional_work_and_completes_with_fresh_evidence()
    {
        var (harness, runId, v1, v2) = await ReclassifiedAsync();
        using var _ = harness;
        harness.ResetOutput();
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("approve", runId.ToString(), "--gate", v2.ToString());

        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", harness.Out.ToString());
        Assert.Single(harness.Factory.Runtime.StartRequests);   // one native conversation throughout
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.StartsWith("The HOW proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains(RevisedStrategy, prompt);
        Assert.Contains("Adapt it", prompt);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(GateStatus.Invalidated, task.StatusOf(task.Gates.First(g => g.Id == v1)));
        Assert.Equal(GateStatus.Approved, task.StatusOf(task.Gates.First(g => g.Id == v2)));
        Assert.True(task.HasCurrentPassingVerification);
        Assert.Equal(task.CurrentSemanticRevisionId, task.CurrentImplementationRevision!.SemanticRevisionId);
        Assert.Null(task.ActiveReclassification);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("resolved", status);
        Assert.Contains("HOW: approved (current)", status);
        Assert.Contains($"HOW {v1}: APPROVED -> INVALIDATED", status);
        Assert.Contains("Implementation: current", status);
        Assert.Contains("Verification: PASSED", status);
    }

    [Fact]
    public async Task A_runtime_failure_after_the_report_is_BLOCKED_and_continue_retries_only_that_without_a_second_report_or_revision()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.HowByTurn[1] = How();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        Assert.Equal(22, await harness.RunAsync("run", "task", "--require", "how"));
        var runId = harness.PrintedRunId();
        var v1 = harness.PrintedWhatId();
        harness.ResetOutput();

        harness.Factory.Signals.ReclassByTurn[2] = Report();
        harness.Factory.Signals.HowByTurn[3] = How(RevisedStrategy);
        harness.Factory.Runtime.Turn = (n, _, _) => n == 3
            ? Task.FromException<AgentTurnOutcome>(new AgentRuntimeException("the connection was lost"))
            : Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(14, await harness.RunAsync("approve", runId.ToString(), "--gate", v1.ToString()));

        var blocked = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(HumanInputKind.Reclassification, RecoverableContinuation.Find(blocked)!.Kind);
        var reclassificationId = Assert.Single(blocked.Reclassifications).Id;

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains($"aw continue {runId}", harness.Out.ToString());
        Assert.Contains("The agent's reclassification of the HOW", harness.Out.ToString());
        Assert.Contains("scope baseline recorded", harness.Out.ToString());

        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        harness.ResetOutput();

        Assert.Equal(22, await harness.RunAsync("continue", runId.ToString()));

        var recovered = await LoadAsync(harness, runId);
        Assert.Equal(GateType.How, recovered.PendingGate!.Type);
        Assert.Equal(reclassificationId, Assert.Single(recovered.Reclassifications).Id);   // recorded once
        Assert.Equal(3, recovered.SemanticRevisions.Length);
        Assert.Contains("Evidence:", harness.Out.ToString());
        Assert.Contains(RevisedStrategy, harness.Out.ToString());

        harness.ResetOutput();
        Assert.Equal(18, await harness.RunAsync("continue", runId.ToString()));   // nothing left to retry
    }
}
