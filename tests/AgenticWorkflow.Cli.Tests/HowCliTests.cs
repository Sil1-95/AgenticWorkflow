using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M5C through the real CLI composition: <c>aw run --require how</c> ends at a durable HOW gate (exit 22) before anything is implemented; a later, fresh
/// <c>aw</c> process shows the proposal and <c>aw approve</c> / <c>aw request-changes</c> decide it once, resume the same native conversation and, only after
/// approval, let the work proceed. With both stages required the WHAT comes first. Only the agent runtime is scripted.
/// </summary>
public sealed class HowCliTests
{
    private const string Strategy = "Write the symbol into src/Feature.txt through a single helper and keep the file format unchanged.";
    private const string Revised = "Write the symbol into src/Feature.txt directly; no helper.";

    private static AgentHowSignal How(string strategy = Strategy)
        => new(strategy, "One helper owns the write.", "The caller invokes the helper once.", "The file format does not change.", "A helper is one more indirection for a one-line write.", "The symbol is present afterwards.", "A helper and an inline write are both plausible.");

    private static AgentWhatSignal What()
        => new("Expose AW_SCRIPTED_SYMBOL from src/Feature.txt.", "The symbol is present.", "src/ only.", null, null, "The symbol is present under src/.", "The task names no file.");

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes(string content = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), content + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    private static CliHarness NewHarness(string? acceptance = "none")
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();

        return harness;
    }

    /// <summary>A forced run whose first turn proposed the HOW without touching the workspace. Exit 22.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId, GateId HowId)> ProposedAsync(string? acceptance = "none")
    {
        var harness = NewHarness(acceptance);
        harness.Factory.Signals!.HowByTurn[1] = How();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(22, await harness.RunAsync("run", "task", "--require", "how"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedWhatId());
        harness.ResetOutput();

        return result;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- the proposal ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forced_run_ends_at_the_HOW_gate_with_the_sections_the_rationale_the_binding_and_the_three_actions_and_no_attach_hint()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.HowByTurn[1] = How();

        var exit = await harness.RunAsync("run", "task", "--require", "how");

        var output = harness.Out.ToString();
        Assert.Equal(22, exit);
        Assert.Contains("Required: HOW approval", output);
        Assert.DoesNotContain("Required: WHAT", output);
        Assert.Contains("HOW PROPOSED - AWAITING YOUR APPROVAL", output);
        foreach (var section in new[] { "Strategy", "Components and responsibilities", "Data and control flow", "Compatibility and migration", "Risks and tradeoffs", "Verification approach" })
            Assert.Contains(section, output);
        Assert.Contains(Strategy, output);
        Assert.Contains("A helper and an inline write are both plausible.", output);
        Assert.Contains("semantic revision 1", output);
        Assert.Contains("aw approve", output);
        Assert.Contains("aw request-changes", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("aw answer", output);
        Assert.DoesNotContain("aw accept", output);
        Assert.DoesNotContain("COMPLETED", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.True(task.HowRequired);
        Assert.False(task.WhatRequired);
        Assert.Equal(GateType.How, task.PendingGate!.Type);
        Assert.Empty(task.ImplementationRevisions);
    }

    [Fact]
    public async Task The_requirement_accepts_what_how_or_both_and_nothing_else()
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "how,ship"));
        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "how,how"));
        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "what,"));
        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "all"));
        Assert.Empty(harness.Factory.Runtime.StartRequests);

        harness.Factory.Signals!.WhatByTurn[1] = What();
        Assert.Equal(20, await harness.RunAsync("run", "task", "--require", "how,what"));   // order is irrelevant; the WHAT still comes first
        var both = await LoadAsync(harness, harness.PrintedRunId());
        Assert.True(both.WhatRequired);
        Assert.True(both.HowRequired);
    }

    [Fact]
    public async Task A_runtime_that_cannot_signal_is_refused_before_anything_starts()
    {
        var plain = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        using var _ = plain;

        var exit = await plain.RunAsync("run", "task", "--require", "how");

        Assert.NotEqual(0, exit);
        Assert.Empty(plain.Factory.Runtime.StartRequests);
        Assert.Contains("how_unavailable", plain.Out + plain.Error.ToString());
    }

    [Fact]
    public async Task Work_done_before_a_proposal_blocks_the_run_with_exit_23_and_is_never_verified_or_completed()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("run", "task", "--require", "how");

        var output = harness.Out.ToString();
        Assert.Equal(23, exit);
        Assert.Contains("BLOCKED - HOW STAGE VIOLATED", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("aw continue", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("HOW stage", harness.Out.ToString());
        Assert.NotEqual(0, await harness.RunAsync("continue", task.Id.ToString()));
    }

    // ---- status and runs, from a fresh process --------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_shows_the_pending_HOW_its_sections_why_the_binding_fingerprint_and_exactly_the_three_actions()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("AWAITING_HUMAN", status);
        Assert.Contains("Required: HOW approval", status);
        Assert.Contains("Pending gate: HOW", status);
        Assert.Contains(Strategy, status);
        Assert.Contains("A helper and an inline write are both plausible.", status);
        Assert.Contains("binding: current", status);
        Assert.Contains("content ", status);
        Assert.Contains($"aw approve {runId} --gate {howId}", status);
        Assert.Contains($"aw request-changes {runId} --gate {howId}", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("aw answer", status);
        Assert.DoesNotContain("aw accept", status);
    }

    [Fact]
    public async Task Runs_annotates_a_pending_HOW_from_durable_state_alone()
    {
        var (harness, runId, _) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("runs"));

        var line = harness.Out.ToString().Split('\n').First(l => l.Contains(runId.ToString(), StringComparison.Ordinal));
        Assert.Contains("AWAITING_HUMAN (HOW)", line);
    }

    // ---- aw approve ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_resumes_the_same_conversation_with_the_approved_HOW_and_then_the_work_completes()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("HOW proposal", output);
        Assert.Contains("approved", output);
        Assert.Contains("Nothing is implemented, verified or complete yet", output);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Agent continuation: resumed native conversation", output);

        var resume = Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Equal(ScriptedConversations.FirstReference, resume.Conversation.Reference);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.StartsWith("The HOW proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains(Strategy, prompt);
        Assert.EndsWith("Continue from the current workspace using this implementation strategy.", prompt.TrimEnd(), StringComparison.Ordinal);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(task.GateDecisions);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanHowApproval],
            task.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(task.CurrentSemanticRevisionId, task.CurrentImplementationRevision!.SemanticRevisionId);
    }

    [Fact]
    public async Task With_human_acceptance_approval_leads_to_the_ordinary_ACCEPT_gate_never_to_acceptance()
    {
        var (harness, runId, howId) = await ProposedAsync(acceptance: "human");
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString());

        Assert.Equal(17, exit);
        var task = await LoadAsync(harness, runId);
        Assert.Equal(GateType.Accept, task.PendingGate!.Type);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
    }

    [Fact]
    public async Task A_second_a_wrong_or_an_unknown_approval_is_refused_and_records_nothing()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        Assert.Equal(2, await harness.RunAsync("approve", runId.ToString()));
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", Guid.NewGuid().ToString()));
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
        Assert.Empty(harness.Factory.Conversations!.Resumes);

        Assert.Equal(0, await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString()));
        harness.ResetOutput();

        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString()));
        Assert.Contains("already decided", harness.Out + harness.Error.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Single(task.GateDecisions);
        Assert.Equal(2, task.SemanticRevisions.Length);
    }

    [Fact]
    public async Task Answer_and_accept_do_not_decide_a_HOW_gate()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(18, await harness.RunAsync("answer", runId.ToString(), "--question", howId.ToString(), "--text", "yes"));
        Assert.Equal(18, await harness.RunAsync("accept", runId.ToString(), "--gate", howId.ToString()));
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
    }

    // ---- aw request-changes ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_round_of_changes_keeps_v1_historical_v2_current_and_implements_only_after_the_second_approval()
    {
        var (harness, runId, v1) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Signals!.HowByTurn[2] = How(Revised);
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);   // revising changes nothing

        var changes = await harness.RunAsync("request-changes", runId.ToString(), "--gate", v1.ToString(), "--feedback", "No helper; write it inline.");

        var output = harness.Out.ToString();
        Assert.Equal(22, changes);   // the revision is a new proposal, not an implementation
        Assert.Contains(Revised, output);
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.StartsWith("The human requested changes to the HOW proposal:", prompt, StringComparison.Ordinal);
        Assert.Contains("No helper; write it inline.", prompt);
        Assert.Contains("Do not implement yet.", prompt);

        var v2 = harness.PrintedWhatId();
        Assert.NotEqual(v1, v2);
        var mid = await LoadAsync(harness, runId);
        Assert.Equal(GateStatus.ChangesRequested, mid.StatusOf(mid.Gates[0]));
        Assert.Equal(Strategy, mid.Gates[0].How!.Strategy);
        Assert.Equal(Revised, mid.PendingGate!.How!.Strategy);
        Assert.Empty(mid.ImplementationRevisions);

        harness.ResetOutput();
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", v1.ToString()));

        harness.Factory.Runtime.Turn = Writes();
        Assert.Equal(0, await harness.RunAsync("approve", runId.ToString(), "--gate", v2.ToString()));

        var done = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, done.WorkflowState);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanHowChanges, SemanticChangeKind.HumanHowApproval],
            done.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(2, harness.Factory.Conversations!.Resumes.Count);
        Assert.All(harness.Factory.Conversations.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("No helper; write it inline.", status);   // the feedback history is shown, in order
        Assert.True(status.IndexOf(Strategy, StringComparison.Ordinal) < status.IndexOf(Revised, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_that_implements_instead_of_revising_violates_the_HOW_stage()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();
        harness.Factory.Signals!.HowByTurn[2] = How(Revised);

        Assert.Equal(23, await harness.RunAsync("request-changes", runId.ToString(), "--gate", howId.ToString(), "--feedback", "Narrower."));

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.False(task.MayImplement);
    }

    // ---- abort and recovery ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Aborting_cancels_the_pending_HOW_and_it_can_no_longer_be_approved()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(16, exit);
        Assert.Contains("HOW", harness.Out.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Cancelled, task.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, task.StatusOf(task.Gates[0]));
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString()));
    }

    [Fact]
    public async Task A_resume_failure_after_the_approval_is_BLOCKED_and_continue_finishes_it_without_a_second_decision_or_revision()
    {
        var (harness, runId, howId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");

        Assert.Equal(14, await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString()));

        var blocked = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(GateAction.Approve, blocked.DecisionFor(howId)!.Action);
        Assert.Empty(blocked.ImplementationRevisions);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains($"aw continue {runId}", status);
        Assert.Contains("HOW", status);

        harness.Factory.Conversations.ResumeFailure = null;
        harness.Factory.Runtime.Turn = Writes();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(task.GateDecisions);
        Assert.Equal(2, task.SemanticRevisions.Length);
        Assert.Single(task.Gates);
        Assert.Contains("The HOW proposal was approved.", harness.Factory.Runtime.Prompts[^1]);
    }

    // ---- WHAT then HOW ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requiring_both_runs_the_WHAT_first_then_the_HOW_and_implements_only_after_both_are_approved()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = What();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(20, await harness.RunAsync("run", "task", "--require", "what,how"));
        var runId = harness.PrintedRunId();
        var whatId = harness.PrintedWhatId();
        Assert.Contains("Required: WHAT approval", harness.Out.ToString());
        Assert.Contains("Required: HOW approval", harness.Out.ToString());
        harness.ResetOutput();

        // approving the WHAT does not permit implementation: the agent is told the HOW is still required and proposes it
        harness.Factory.Signals.HowByTurn[2] = How();
        Assert.Equal(22, await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString()));
        var afterWhat = await LoadAsync(harness, runId);
        Assert.True(afterWhat.WhatApproved);
        Assert.False(afterWhat.MayImplement);
        Assert.Equal(GateType.How, afterWhat.PendingGate!.Type);
        Assert.Contains("The WHAT is approved. HOW approval is still required.", harness.Factory.Runtime.Prompts[^1]);
        Assert.Empty(afterWhat.ImplementationRevisions);
        var howId = harness.PrintedWhatId();
        harness.ResetOutput();

        harness.Factory.Runtime.Turn = Writes();
        Assert.Equal(0, await harness.RunAsync("approve", runId.ToString(), "--gate", howId.ToString()));

        var done = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, done.WorkflowState);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatApproval, SemanticChangeKind.HumanHowApproval],
            done.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal([GateType.What, GateType.How], done.Gates.Select(g => g.Type).ToArray());
        Assert.StartsWith("The HOW proposal was approved.", harness.Factory.Runtime.Prompts[^1], StringComparison.Ordinal);
        Assert.All(harness.Factory.Conversations!.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));
    }

    // ---- everything else is unchanged ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_requirement_a_task_the_agent_does_not_propose_for_runs_directly_and_is_never_asked_for_a_stage()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.DoesNotContain("Required: HOW", output);
        Assert.Contains("COMPLETED", output);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.HowRequired);
        Assert.Empty(task.Gates);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task The_agent_may_choose_the_stage_on_its_own_and_the_run_then_waits_for_the_human_without_being_forced()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.HowByTurn[1] = How();

        Assert.Equal(22, await harness.RunAsync("run", "task"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.HowRequired);
        Assert.Equal(GateType.How, task.PendingGate!.Type);
        Assert.Empty(task.ImplementationRevisions);
    }

    [Fact]
    public async Task Help_documents_the_HOW_requirement_and_its_exit_codes()
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("help"));

        var help = harness.Out.ToString();
        Assert.Contains("--require what|how|review|prove|what,how,review,prove", help);
        Assert.Contains("22 the agent proposed the HOW", help);
        Assert.Contains("23 the HOW stage was violated", help);
        Assert.Contains("docs/concepts.md", help);
    }
}
