using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M7 through the real CLI composition: <c>aw run --require review</c> needs an explicit reviewer profile (refused before anything exists otherwise), the verified work is
/// judged by a fresh reviewer session, the run completes only over a current PASS, and <c>aw status</c> and the run report show the rounds in order without a transcript.
/// Only the agent runtime is scripted.
/// </summary>
public sealed class ReviewCliTests
{
    private const string WithReviewer = "\"reviewerProfile\": \"claude-code-local\",";

    private static AgentReviewSubmission Pass(string summary = "The symbol is exposed and nothing else changed.")
        => new("PASS", summary, [], null);

    private static AgentReviewSubmission Changes()
        => new("CHANGES_REQUIRED", "One real defect.", [new AgentReviewFinding("Feature.txt has no trailing marker", "src/Feature.txt ends without the agreed marker.", "Consumers cannot find the end.", "Append the marker line.", "src/Feature.txt:1")], null);

    private static CliHarness NewHarness(string extra = WithReviewer, string? acceptance = "none")
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance, extra: extra));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        return harness;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    // ---- the requirement and its binding --------------------------------------------------------------------------------------------

    [Fact]
    public async Task require_review_is_persisted_with_the_explicit_reviewer_binding_and_does_not_imply_WHAT_or_HOW()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ReviewByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        Assert.Equal(0, exit);
        var output = harness.Out.ToString();
        Assert.Contains("Required: independent REVIEW", output);
        Assert.DoesNotContain("Required: WHAT", output);
        Assert.DoesNotContain("Required: HOW", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.True(task.ReviewRequired);
        Assert.True(task.IsReviewRequired);
        Assert.False(task.WhatRequired);
        Assert.False(task.HowRequired);
        Assert.Equal(new ProfileId("claude-code-local"), task.RoleBindings[WellKnownRoles.Reviewer].Profile.Id);
    }

    [Fact]
    public async Task Without_a_reviewerProfile_a_forced_review_is_refused_before_any_state_or_session_exists()
    {
        var harness = NewHarness(extra: string.Empty);
        using var _ = harness;

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        Assert.Equal(2, exit);
        Assert.Contains("missing_binding", harness.Error.ToString());
        Assert.Contains("reviewer", harness.Error.ToString());
        Assert.Contains("never", harness.Error.ToString());
        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.Equal(0, harness.Factory.Created);
        Assert.DoesNotContain(Directory.GetFiles(harness.Home, "*", SearchOption.AllDirectories), f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("review", true, false, false)]
    [InlineData("what,review", true, true, false)]
    [InlineData("review,what", true, true, false)]
    [InlineData("how,review", true, false, true)]
    [InlineData("review,how", true, false, true)]
    [InlineData("what,how,review", true, true, true)]
    [InlineData("review,how,what", true, true, true)]
    public async Task The_requirement_composes_in_any_order_and_each_word_stands_for_itself(string value, bool review, bool what, bool how)
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = new AgentWhatSignal("Expose the symbol.", "The symbol is present.", "src/ only.", null, null, "It is present.", "No file is named.");
        harness.Factory.Signals.HowByTurn[1] = new AgentHowSignal("One write.", "One owner.", null, null, "Smallest.", null, "Two shapes are plausible.");

        await harness.RunAsync("run", "task", "--require", value);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(review, task.ReviewRequired);
        Assert.Equal(what, task.WhatRequired);
        Assert.Equal(how, task.HowRequired);
    }

    [Theory]
    [InlineData("review,review")]
    [InlineData("review,ship")]
    [InlineData("review,")]
    public async Task A_repeated_or_unknown_word_in_the_requirement_is_a_usage_error_and_nothing_starts(string value)
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", value));
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    // ---- a review run ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_passing_review_completes_the_run_and_the_report_and_status_show_it_as_current_without_any_transcript()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ReviewByCall[1] = Pass("The symbol is exposed and nothing else changed.");

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Review: PASS (current)", output);
        Assert.Contains("Reviewer: profile 'claude-code-local' (explicitly bound", output);
        Assert.Contains("Review round 1: PASS (current)", output);
        Assert.Contains("independent of the implementer's", output);
        Assert.Contains("no configured check re-run", output);
        Assert.Contains("The symbol is exposed and nothing else changed.", output);

        var runId = harness.PrintedRunId();
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("Required: independent REVIEW", status);
        Assert.Contains("Review: PASS (current)", status);
        Assert.DoesNotContain("STALE", status);
        Assert.DoesNotContain("transcript", status, StringComparison.OrdinalIgnoreCase);

        // one implementer session and one reviewer session, never the same
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Single(harness.Factory.Runtime.ReviewerPrompts);
        Assert.Equal(AgentSessionPurpose.Review, harness.Factory.Runtime.StartRequests[1].Purpose);
        Assert.DoesNotContain(harness.Factory.Runtime.Prompts[0], harness.Factory.Runtime.ReviewerPrompts[0]);
    }

    [Fact]
    public async Task The_run_report_keeps_the_review_rounds_in_order_when_the_first_review_required_changes()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ReviewByCall[1] = Changes();
        harness.Factory.Signals.ReviewByCall[2] = Pass("The marker is present now.");
        harness.Factory.Runtime.Turn = (turn, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), turn == 1 ? CliHarness.Symbol + "\n" : CliHarness.Symbol + "\n-- end --\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.True(output.IndexOf("Review round 1: CHANGES_REQUIRED", StringComparison.Ordinal) < output.IndexOf("Review round 2: PASS", StringComparison.Ordinal));
        Assert.Contains("Feature.txt has no trailing marker", output);
        Assert.Contains("Review: PASS (current)", output);

        // the SAME implementer session heard the findings; the second reviewer is a different, fresh session
        Assert.Equal(2, harness.Factory.Runtime.Prompts.Count);
        Assert.StartsWith("Independent REVIEW found:", harness.Factory.Runtime.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(2, harness.Factory.Runtime.ReviewerPrompts.Count);
        Assert.Contains("The previous review (round 1, CHANGES_REQUIRED)", harness.Factory.Runtime.ReviewerPrompts[1]);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(2, task.Reviews.Length);
        Assert.NotEqual(task.Reviews[0].ReviewerSession, task.Reviews[1].ReviewerSession);
        Assert.Equal(task.Reviews[0].ImplementerSession, task.Reviews[1].ImplementerSession);
    }

    [Fact]
    public async Task A_reviewer_that_changes_the_workspace_exits_25_blocked_with_the_workspace_left_exactly_as_it_was()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ReviewByCall[1] = Pass();
        harness.Factory.Runtime.ReviewerTurn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Reviewer.txt"), "the reviewer was here\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        var output = harness.Out.ToString();
        Assert.Equal(25, exit);
        Assert.Contains("BLOCKED - REVIEWER MODIFIED THE WORKSPACE", output);
        Assert.Contains("nothing was absorbed as an implementation and nothing was reverted", output);
        Assert.True(File.Exists(Path.Combine(harness.Workspace, "src", "Reviewer.txt")));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.Reviews);
        Assert.Single(task.ImplementationRevisions);

        // 'aw continue' is not a generic retry: this block is not a recoverable continuation
        harness.ResetOutput();
        Assert.NotEqual(0, await harness.RunAsync("continue", task.Id.ToString()));
    }

    // ---- an agent-requested review -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_requested_review_with_no_reviewerProfile_exits_24_and_points_at_the_explicit_bind()
    {
        var harness = NewHarness(extra: string.Empty);
        using var _ = harness;
        harness.Factory.Signals!.ReviewRequestByTurn[1] = new AgentReviewRequestSignal("This changes a shared file format.");

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(24, exit);
        Assert.Contains("BLOCKED - REVIEWER NOT BOUND", output);
        Assert.Contains("aw bind", output);
        Assert.Contains("NOT BOUND", output);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("Required: independent REVIEW (requested by the agent", harness.Out.ToString());
        Assert.Contains("Reviewer: NOT BOUND", harness.Out.ToString());   // no reviewer was started, least of all with the implementer's profile
    }

    [Fact]
    public async Task An_agent_requested_review_with_a_reviewerProfile_is_additive_and_runs_before_completion()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ReviewRequestByTurn[1] = new AgentReviewRequestSignal("This changes a shared file format.");
        harness.Factory.Signals.ReviewByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(0, exit);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.ReviewRequired);
        Assert.True(task.ReviewEscalated);
        Assert.Single(task.Reviews);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("requested by the agent", harness.Out.ToString());
    }

    // ---- no review: nothing changes ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_without_the_requirement_is_unchanged_one_session_no_reviewer_no_review_section()
    {
        var harness = NewHarness();   // a reviewerProfile exists, but nothing required a review
        using var _ = harness;

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", output);
        Assert.DoesNotContain("Review:", output);
        Assert.DoesNotContain("Reviewer:", output);
        Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Empty(harness.Factory.Runtime.ReviewerPrompts);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Empty(task.Reviews);
        Assert.False(task.IsReviewRequired);
    }

    // ---- recovery -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_that_failed_before_reporting_is_recovered_by_continue_with_a_fresh_reviewer_and_without_repeating_the_work()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Runtime.ReviewerTurn = (_, _, _) => throw new InvalidOperationException("the reviewer pane crashed");

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        Assert.NotEqual(0, exit);
        var runId = harness.PrintedRunId();
        var stranded = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, stranded.WorkflowState);
        Assert.Empty(stranded.Reviews);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("independent review", harness.Out.ToString());
        Assert.Contains("aw continue", harness.Out.ToString());

        harness.Factory.Runtime.ReviewerTurn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        harness.Factory.Signals!.ReviewByCall[1] = Pass();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var recovered = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, recovered.WorkflowState);
        Assert.Single(recovered.Reviews);
        Assert.Equal(stranded.ImplementationRevisions.Length, recovered.ImplementationRevisions.Length);   // nothing re-implemented or duplicated
        Assert.Equal(stranded.Verifications.Length, recovered.Verifications.Length);                       // and nothing re-verified
        Assert.Single(harness.Factory.Runtime.Prompts);                                                    // the implementer was never involved
        Assert.Contains("independent review", harness.Out.ToString());
    }
}
