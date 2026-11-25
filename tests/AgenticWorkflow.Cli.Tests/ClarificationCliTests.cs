using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M5A through the real CLI composition: the agent's question ends the run as AWAITING_HUMAN (exit 19) with a durable CLARIFY gate; a later, fresh <c>aw</c>
/// process shows it and <c>aw answer</c> records the answer once, resumes the same native conversation and finishes the work. Only the agent runtime is scripted.
/// </summary>
public sealed class ClarificationCliTests
{
    private const string Question = "Which symbol should the feature expose?";
    private const string Answer = "Use AW_SCRIPTED_SYMBOL.";

    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Writes(string content = CliHarness.Symbol)
        => (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), content + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

    /// <summary>A run whose first turn asked the question (it changed nothing before asking). Exit 19.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId, GateId QuestionId)> AskedAsync(string? acceptance = "none")
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Signals.ByTurn[1] = new AgentClarificationSignal(Question, "The symbol is not named in the repository.");

        Assert.Equal(19, await harness.RunAsync("run", "task"));

        var result = (harness, harness.PrintedRunId(), harness.PrintedQuestionId());
        harness.ResetOutput();

        return result;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- the question ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_question_ends_the_run_awaiting_the_human_with_the_actions_and_no_attach_hint()
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        using var _ = harness;
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Signals.ByTurn[1] = new AgentClarificationSignal(Question, "Not named anywhere.");

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(19, exit);
        Assert.Contains("QUESTION - AWAITING YOUR ANSWER", output);
        Assert.Contains(Question, output);
        Assert.Contains("Not named anywhere.", output);
        Assert.Contains("aw answer", output);
        Assert.Contains("aw abort", output);
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("COMPLETED", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Clarify, task.PendingGate!.Type);
        Assert.Empty(task.ImplementationRevisions);
    }

    [Fact]
    public async Task Status_in_a_fresh_process_shows_the_question_why_the_revision_and_the_actions()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("AWAITING_HUMAN", status);
        Assert.Contains("CLARIFY", status);
        Assert.Contains(Question, status);
        Assert.Contains("The symbol is not named in the repository.", status);
        Assert.Contains("asked under semantic revision: 1", status);
        Assert.Contains($"aw answer {runId} --question {questionId}", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("aw accept", status);
    }

    // ---- aw answer ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Answering_resumes_the_same_conversation_with_only_the_question_and_answer_and_completes_the_run()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", Answer);

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("answered (not an approval", output);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Agent continuation: resumed native conversation", output);

        var resume = Assert.Single(harness.Factory.Conversations!.Resumes);
        Assert.Equal(ScriptedConversations.FirstReference, resume.Conversation.Reference);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        var prompt = harness.Factory.Runtime.Prompts[^1];
        Assert.Contains(Question, prompt);
        Assert.Contains(Answer, prompt);

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(SemanticChangeKind.HumanClarification, task.CurrentSemanticRevision.Kind);
        Assert.Equal(GateStatus.Answered, task.StatusOf(task.Gates[0]));
        Assert.Equal(Answer, task.DecisionFor(questionId)!.Answer);
        Assert.Single(task.Gates);   // acceptance none: no ACCEPT gate
    }

    [Fact]
    public async Task With_human_acceptance_the_answer_leads_to_the_ordinary_accept_gate_and_never_to_acceptance()
    {
        var (harness, runId, questionId) = await AskedAsync(acceptance: "human");
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", Answer);

        Assert.Equal(17, exit);
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Accept, task.PendingGate!.Type);
        Assert.Equal(task.CurrentSemanticRevisionId, task.PendingGate.SemanticRevisionId);
    }

    [Fact]
    public async Task A_second_answer_a_wrong_question_and_missing_arguments_are_refused_and_record_nothing()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        Assert.Equal(18, await harness.RunAsync("answer", runId.ToString(), "--question", Guid.NewGuid().ToString(), "--text", Answer));
        Assert.Equal(2, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString()));
        Assert.Equal(2, await harness.RunAsync("answer", runId.ToString(), "--text", Answer));
        Assert.Equal(2, await harness.RunAsync("answer", runId.ToString(), "--question", "not-a-guid", "--text", Answer));
        Assert.Equal(2, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", new string('a', 2001)));
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Empty(harness.Factory.Conversations!.Resumes);

        Assert.Equal(0, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", Answer));
        harness.ResetOutput();

        Assert.Equal(18, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", "Another."));
        Assert.Contains("already decided", harness.Out + harness.Error.ToString());
        Assert.Single((await LoadAsync(harness, runId)).GateDecisions);
    }

    [Fact]
    public async Task An_accept_gate_is_not_answered_and_a_question_is_not_accepted()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;

        Assert.Equal(18, await harness.RunAsync("accept", runId.ToString(), "--gate", questionId.ToString()));
        Assert.Equal(18, await harness.RunAsync("request-changes", runId.ToString(), "--gate", questionId.ToString(), "--feedback", "no"));
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.Empty((await LoadAsync(harness, runId)).GateDecisions);
    }

    [Fact]
    public async Task Aborting_cancels_a_question_even_for_a_run_that_never_required_acceptance()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;

        var exit = await harness.RunAsync("abort", runId.ToString());

        Assert.Equal(16, exit);
        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Cancelled, task.WorkflowState);
        Assert.Equal(GateStatus.Cancelled, task.StatusOf(task.Gates[0]));
        Assert.Equal(18, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", Answer));
    }

    // ---- recovery -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resume_failure_after_the_answer_is_BLOCKED_and_continue_finishes_it_without_a_second_answer_or_revision()
    {
        var (harness, runId, questionId) = await AskedAsync();
        using var _ = harness;
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");

        Assert.Equal(14, await harness.RunAsync("answer", runId.ToString(), "--question", questionId.ToString(), "--text", Answer));

        var blocked = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, blocked.WorkflowState);
        Assert.Equal(GateAction.Answer, blocked.DecisionFor(questionId)!.Action);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("BLOCKED", status);
        Assert.Contains(Answer, status);
        Assert.Contains($"aw continue {runId}", status);

        harness.Factory.Conversations.ResumeFailure = null;
        harness.Factory.Runtime.Turn = Writes();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var task = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(task.GateDecisions);
        Assert.Equal(2, task.SemanticRevisions.Length);   // the initial one and exactly one human_clarification
        Assert.Contains(Answer, harness.Factory.Runtime.Prompts[^1]);
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public async Task A_run_without_the_signal_capability_is_unchanged_and_never_told_it_may_ask()
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        using var _ = harness;
        harness.Factory.Runtime.Turn = Writes();

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(AgentClarificationSignal.ToolName, harness.Factory.Runtime.Prompts[0]);
        Assert.Empty((await LoadAsync(harness, harness.PrintedRunId())).Gates);
    }
}
