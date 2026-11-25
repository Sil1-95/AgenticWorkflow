using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M5B through the real CLI composition: <c>aw run --require what</c> ends at a durable WHAT gate (exit 20) before anything is implemented; a later, fresh
/// <c>aw</c> process shows the proposal and <c>aw approve</c> / <c>aw request-changes</c> decide it once, resume the same native conversation and, only after
/// approval, let the work proceed. Only the agent runtime is scripted.
/// </summary>
public sealed class WhatCliTests
{
    private const string Objective = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt.";
    private const string Revised = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt only; touch nothing else.";

    private static AgentWhatSignal What(string objective = Objective)
        => new(objective, "src/Feature.txt contains the symbol.", "src/ only.", "No new files.", "No refactoring.", "The symbol is present under src/.", "The task names no file; two are plausible.");

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

    /// <summary>A forced run whose first turn proposed the WHAT without touching the workspace. Exit 20.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId, GateId WhatId)> ProposedAsync(string? acceptance = "none")
    {
        var harness = NewHarness(acceptance);
        harness.Factory.Signals!.WhatByTurn[1] = What();
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);

        Assert.Equal(20, await harness.RunAsync("run", "task", "--require", "what"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedWhatId());
        harness.ResetOutput();

        return result;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- the proposal ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forced_run_ends_at_the_WHAT_gate_with_the_sections_the_rationale_and_the_three_actions_and_no_attach_hint()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = What();

        var exit = await harness.RunAsync("run", "task", "--require", "what");

        var output = harness.Out.ToString();
        Assert.Equal(20, exit);
        Assert.Contains("Required: WHAT approval", output);
        Assert.Contains("WHAT PROPOSED - AWAITING YOUR APPROVAL", output);
        foreach (var section in new[] { "Objective", "Required behavior", "Scope", "Constraints", "Non-goals", "Acceptance conditions" })
            Assert.Contains(section, output);
        Assert.Contains(Objective, output);
        Assert.Contains("The task names no file; two are plausible.", output);
        Assert.Contains("aw approve", output);
        Assert.Contains("aw request-changes", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("aw answer", output);
        Assert.DoesNotContain("aw accept", output);
        Assert.DoesNotContain("COMPLETED", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.True(task.WhatRequired);
        Assert.Equal(GateType.What, task.PendingGate!.Type);
        Assert.Empty(task.ImplementationRevisions);
    }

    [Fact]
    public async Task The_requirement_can_only_be_a_known_stage_word_and_a_runtime_that_cannot_signal_is_refused_before_anything_starts()
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "review"));
        Assert.Equal(2, await harness.RunAsync("run", "task", "--require"));
        Assert.Empty(harness.Factory.Runtime.StartRequests);

        var plain = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        using var __ = plain;

        var exit = await plain.RunAsync("run", "task", "--require", "what");

        Assert.NotEqual(0, exit);
        Assert.Empty(plain.Factory.Runtime.StartRequests);
        Assert.Contains("what_unavailable", plain.Out + plain.Error.ToString());
    }

    [Fact]
    public async Task Work_done_before_a_proposal_blocks_the_run_with_exit_21_and_is_never_verified_or_completed()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("run", "task", "--require", "what");

        var output = harness.Out.ToString();
        Assert.Equal(21, exit);
        Assert.Contains("BLOCKED - WHAT STAGE VIOLATED", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("aw continue", output);   // continue is not a generic retry

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.Null(task.CurrentVerification);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("WHAT stage", harness.Out.ToString());
        Assert.NotEqual(0, await harness.RunAsync("continue", task.Id.ToString()));
    }

    // ---- status and runs, from a fresh process --------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_shows_the_pending_WHAT_its_sections_why_and_exactly_the_three_actions()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("AWAITING_HUMAN", status);
        Assert.Contains("WHAT", status);
        Assert.Contains(Objective, status);
        Assert.Contains("The task names no file; two are plausible.", status);
        Assert.Contains($"aw approve {runId} --gate {whatId}", status);
        Assert.Contains($"aw request-changes {runId} --gate {whatId}", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("aw answer", status);
        Assert.DoesNotContain("aw accept", status);
    }

    [Fact]
    public async Task Runs_annotates_a_pending_WHAT_from_durable_state_alone()
    {
        var (harness, runId, _) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("runs"));

        var line = harness.Out.ToString().Split('\n').First(l => l.Contains(runId.ToString(), StringComparison.Ordinal));
        Assert.Contains("AWAITING_HUMAN (WHAT)", line);
    }

    // ---- aw approve ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_resumes_the_same_conversation_with_the_approved_WHAT_and_then_the_work_completes()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString());

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("approved", output);
        Assert.Contains("Nothing is implemented, verified or complete yet", output);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Agent continuation: resumed native conversation", output);

        var resume = Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Equal(ScriptedConversations.FirstReference, resume.Conversation.Reference);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.StartsWith("The WHAT proposal was approved.", prompt, StringComparison.Ordinal);
        Assert.Contains(Objective, prompt);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(GateStatus.Approved, task.StatusOf(task.Gates[0]));
        Assert.Single(task.GateDecisions);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatApproval],
            task.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(task.CurrentSemanticRevisionId, task.CurrentImplementationRevision!.SemanticRevisionId);
    }

    [Fact]
    public async Task With_human_acceptance_approval_leads_to_the_ordinary_ACCEPT_gate_never_to_acceptance()
    {
        var (harness, runId, whatId) = await ProposedAsync(acceptance: "human");
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString());

        Assert.Equal(17, exit);
        var task = await LoadAsync(harness, runId);
        Assert.Equal(GateType.Accept, task.PendingGate!.Type);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
    }

    [Fact]
    public async Task Approve_needs_the_gate_and_a_second_a_wrong_or_an_unknown_approval_is_refused_and_records_nothing()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        Assert.Equal(2, await harness.RunAsync("approve", runId.ToString()));
        Assert.Equal(2, await harness.RunAsync("approve", runId.ToString(), "--gate", "not-a-guid"));
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", Guid.NewGuid().ToString()));
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
        Assert.Empty(harness.Factory.Conversations!.Resumes);

        Assert.Equal(0, await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString()));
        harness.ResetOutput();

        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString()));
        Assert.Contains("already decided", harness.Out + harness.Error.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Single(task.GateDecisions);
        Assert.Equal(2, task.SemanticRevisions.Length);
    }

    [Fact]
    public async Task Approve_refuses_a_question_an_acceptance_gate_and_a_run_with_nothing_pending()
    {
        var asked = NewHarness();
        using var _ = asked;
        asked.Factory.Signals!.ByTurn[1] = new AgentClarificationSignal("Which symbol?", "Not named.");
        Assert.Equal(19, await asked.RunAsync("run", "task"));
        var runId = asked.PrintedRunId();
        var questionId = asked.PrintedQuestionId();

        Assert.Equal(18, await asked.RunAsync("approve", runId.ToString(), "--gate", questionId.ToString()));
        Assert.Empty((await LoadAsync(asked, runId)).GateDecisions);

        var accepting = new CliHarness(CliHarness.ValidConfig(acceptance: "human"));
        using var __ = accepting;
        accepting.Factory.Runtime.Turn = Writes();
        Assert.Equal(17, await accepting.RunAsync("run", "task"));
        Assert.Equal(18, await accepting.RunAsync("approve", accepting.PrintedRunId().ToString(), "--gate", accepting.PrintedGateId().ToString()));
        Assert.Empty((await LoadAsync(accepting, accepting.PrintedRunId())).GateDecisions);
    }

    [Fact]
    public async Task Answer_and_accept_do_not_decide_a_WHAT_gate()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;

        Assert.Equal(18, await harness.RunAsync("answer", runId.ToString(), "--question", whatId.ToString(), "--text", "yes"));
        Assert.Equal(18, await harness.RunAsync("accept", runId.ToString(), "--gate", whatId.ToString()));
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
    }

    // ---- aw request-changes ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_round_of_changes_keeps_v1_historical_v2_current_and_implements_only_after_the_second_approval()
    {
        var (harness, runId, v1) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[2] = What(Revised);
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);   // revising changes nothing

        var changes = await harness.RunAsync("request-changes", runId.ToString(), "--gate", v1.ToString(), "--feedback", "Only src/Feature.txt.");

        var output = harness.Out.ToString();
        Assert.Equal(20, changes);   // the revision is a new proposal, not an implementation
        Assert.Contains(Revised, output);
        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.StartsWith("The human requested changes to the WHAT proposal:", prompt, StringComparison.Ordinal);
        Assert.Contains("Only src/Feature.txt.", prompt);
        Assert.Contains("Do not implement yet.", prompt);

        var v2 = harness.PrintedWhatId();
        Assert.NotEqual(v1, v2);
        var mid = await LoadAsync(harness, runId);
        Assert.Equal(GateStatus.ChangesRequested, mid.StatusOf(mid.Gates[0]));
        Assert.Equal(Objective, mid.Gates[0].What!.Objective);   // v1 is preserved
        Assert.Equal(Revised, mid.PendingGate!.What!.Objective);   // v2 is current
        Assert.Empty(mid.ImplementationRevisions);

        harness.ResetOutput();
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", v1.ToString()));   // the superseded v1 cannot be approved

        harness.Factory.Runtime.Turn = Writes();
        Assert.Equal(0, await harness.RunAsync("approve", runId.ToString(), "--gate", v2.ToString()));

        var done = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, done.WorkflowState);
        Assert.Equal(
            [SemanticChangeKind.Initial, SemanticChangeKind.HumanWhatChanges, SemanticChangeKind.HumanWhatApproval],
            done.SemanticRevisions.Select(r => r.Kind).ToArray());
        Assert.Equal(done.CurrentSemanticRevisionId, done.CurrentImplementationRevision!.SemanticRevisionId);
        Assert.Equal(2, harness.Factory.Conversations!.Resumes.Count);
        Assert.All(harness.Factory.Conversations.Resumes, r => Assert.Equal(ScriptedConversations.FirstReference, r.Conversation.Reference));

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("Only src/Feature.txt.", status);   // the feedback history is shown, in order
        Assert.True(status.IndexOf(Objective, StringComparison.Ordinal) < status.IndexOf(Revised, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_changes_needs_feedback_and_work_is_still_refused_while_the_WHAT_is_unapproved()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        Assert.Equal(2, await harness.RunAsync("request-changes", runId.ToString(), "--gate", whatId.ToString()));
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);

        // the agent ignores the instruction and implements instead of revising: the stage is violated, nothing is accepted as work
        harness.Factory.Signals!.WhatByTurn[2] = What(Revised);
        Assert.Equal(21, await harness.RunAsync("request-changes", runId.ToString(), "--gate", whatId.ToString(), "--feedback", "Narrower."));

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ImplementationRevisions);
        Assert.False(task.MayImplement);
    }

    // ---- abort and recovery ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Aborting_cancels_the_pending_WHAT_and_it_can_no_longer_be_approved()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(16, exit);
        Assert.Contains("WHAT", harness.Out.ToString());
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Cancelled, task.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, task.StatusOf(task.Gates[0]));
        Assert.Equal(18, await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString()));
    }

    [Fact]
    public async Task A_resume_failure_after_the_approval_is_BLOCKED_and_continue_finishes_it_without_a_second_decision_or_revision()
    {
        var (harness, runId, whatId) = await ProposedAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");

        Assert.Equal(14, await harness.RunAsync("approve", runId.ToString(), "--gate", whatId.ToString()));

        var blocked = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(GateAction.Approve, blocked.DecisionFor(whatId)!.Action);
        Assert.Empty(blocked.ImplementationRevisions);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains($"aw continue {runId}", harness.Out.ToString());

        harness.Factory.Conversations.ResumeFailure = null;
        harness.Factory.Runtime.Turn = Writes();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(task.GateDecisions);
        Assert.Equal(2, task.SemanticRevisions.Length);   // the initial one and exactly one approval
        Assert.Single(task.Gates);
        Assert.Contains("The WHAT proposal was approved.", harness.Factory.Runtime.Prompts[^1]);
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
        Assert.DoesNotContain("Required: WHAT", output);
        Assert.Contains("COMPLETED", output);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.WhatRequired);
        Assert.Empty(task.Gates);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task The_agent_may_choose_the_stage_on_its_own_and_the_run_then_waits_for_the_human_without_being_forced()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = What();

        Assert.Equal(20, await harness.RunAsync("run", "task"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.WhatRequired);
        Assert.Equal(GateType.What, task.PendingGate!.Type);
        Assert.Empty(task.ImplementationRevisions);
    }

    [Fact]
    public async Task Help_documents_approve_the_requirement_and_the_new_exit_codes()
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("help"));

        var help = harness.Out.ToString();
        Assert.Contains("aw approve", help);
        Assert.Contains("--require what", help);
        Assert.Contains("20", help);
        Assert.Contains("21", help);
    }
}
