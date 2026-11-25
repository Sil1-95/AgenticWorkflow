using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// M8 through the real CLI composition: <c>aw run --require prove</c> needs an explicit prover profile (refused before anything exists otherwise), the implementer writes a
/// Proof Contract, a fresh prover session judges the verified work, the run completes only over a current proof PASS, and <c>aw status</c> and the run report show the
/// contract, the rounds in order and the prover's cost without a transcript. Only the agent runtime is scripted.
/// </summary>
public sealed class ProofCliTests
{
    private const string WithProver = "\"proverProfile\": \"claude-code-local\",";
    private const string WithProverAndReviewer = "\"proverProfile\": \"claude-code-local\", \"reviewerProfile\": \"claude-code-local\",";

    private static AgentProveRequestSignal Contract(string rationale = "The symbol must exist and nothing else may change.")
        => new(
            rationale,
            [
                new AgentProofClaim(
                    "c1",
                    "The symbol is exposed in src/Feature.txt.",
                    [new AgentProofEvidenceRequirement("verification", "symbol_present")],
                    "The file contains the symbol on its own line."),
            ]);

    private static AgentProofSubmission Pass(string summary = "The symbol is exposed and nothing else changed.")
        => new(summary, [new AgentProofClaimResult("c1", "pass", ["verification:symbol_present"], "Established by the controller's check.")]);

    private static AgentProofSubmission Fail()
        => new("One claim does not hold.", [new AgentProofClaimResult("c1", "fail", ["verification:symbol_present"], "Feature.txt has no trailing marker.")]);

    private static CliHarness NewHarness(string extra = WithProver, string? acceptance = "none", int maxTurns = 3)
    {
        var harness = new CliHarness(CliHarness.ValidConfig(acceptance: acceptance, extra: extra, maxTurns: maxTurns));
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
    public async Task require_prove_is_persisted_with_the_explicit_prover_binding_and_implies_nothing_else()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        Assert.Equal(0, exit);
        var output = harness.Out.ToString();
        Assert.Contains("Required: formal PROVE", output);
        Assert.DoesNotContain("Required: WHAT", output);
        Assert.DoesNotContain("Required: HOW", output);
        Assert.DoesNotContain("Required: independent REVIEW", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.True(task.ProveRequired);
        Assert.True(task.IsProveRequired);
        Assert.False(task.ReviewRequired);
        Assert.False(task.WhatRequired);
        Assert.False(task.HowRequired);
        Assert.Empty(task.Reviews);
        Assert.Equal(new ProfileId("claude-code-local"), task.RoleBindings[WellKnownRoles.Prover].Profile.Id);
        Assert.False(task.RoleBindings.ContainsKey(WellKnownRoles.Reviewer));
    }

    [Fact]
    public async Task Without_a_proverProfile_a_forced_proof_is_refused_before_any_state_or_session_exists()
    {
        var harness = NewHarness(extra: string.Empty);
        using var _ = harness;

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        Assert.Equal(2, exit);
        Assert.Contains("missing_binding", harness.Error.ToString());
        Assert.Contains("prover", harness.Error.ToString());
        Assert.Contains("never", harness.Error.ToString());
        Assert.Empty(harness.Factory.Runtime.StartRequests);
        Assert.Equal(0, harness.Factory.Created);
        Assert.DoesNotContain(Directory.GetFiles(harness.Home, "*", SearchOption.AllDirectories), f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_reviewerProfile_alone_never_stands_in_for_the_prover()
    {
        var harness = NewHarness(extra: "\"reviewerProfile\": \"claude-code-local\",");
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", "prove"));
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    [Theory]
    [InlineData("prove", true, false, false, false)]
    [InlineData("prove,review", true, false, false, true)]
    [InlineData("review,prove", true, false, false, true)]
    [InlineData("what,prove", true, true, false, false)]
    [InlineData("how,review,prove", true, false, true, true)]
    [InlineData("prove,how,what", true, true, true, false)]
    public async Task The_requirement_composes_in_any_order_and_each_word_stands_for_itself(string value, bool prove, bool what, bool how, bool review)
    {
        var harness = NewHarness(WithProverAndReviewer);
        using var _ = harness;
        harness.Factory.Signals!.WhatByTurn[1] = new AgentWhatSignal("Expose the symbol.", "The symbol is present.", "src/ only.", null, null, "It is present.", "No file is named.");
        harness.Factory.Signals.HowByTurn[1] = new AgentHowSignal("One write.", "One owner.", null, null, "Smallest.", null, "Two shapes are plausible.");

        await harness.RunAsync("run", "task", "--require", value);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(prove, task.ProveRequired);
        Assert.Equal(what, task.WhatRequired);
        Assert.Equal(how, task.HowRequired);
        Assert.Equal(review, task.ReviewRequired);
    }

    [Theory]
    [InlineData("prove,prove")]
    [InlineData("prove,ship")]
    [InlineData("prove,")]
    public async Task A_repeated_or_unknown_word_in_the_requirement_is_a_usage_error_and_nothing_starts(string value)
    {
        var harness = NewHarness();
        using var _ = harness;

        Assert.Equal(2, await harness.RunAsync("run", "task", "--require", value));
        Assert.Empty(harness.Factory.Runtime.StartRequests);
    }

    // ---- a proof run -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_passing_proof_completes_the_run_and_the_report_and_status_show_the_contract_and_the_proof_without_any_transcript()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", output);
        Assert.Contains("Proof: PASS (current)", output);
        Assert.Contains("Prover: profile 'claude-code-local' (explicitly bound", output);
        Assert.Contains("Proof Contract: 1 claim(s)", output);
        Assert.Contains("requires: verification:symbol_present", output);
        Assert.Contains("Proof round 1: PASS (current)", output);
        Assert.Contains("independent of the implementer's", output);
        Assert.Contains("The symbol is exposed and nothing else changed.", output);

        var runId = harness.PrintedRunId();
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        var status = harness.Out.ToString();
        Assert.Contains("PROVE: REQUIRED", status);
        Assert.Contains("Proof: PASS (current)", status);
        Assert.DoesNotContain("STALE", status);
        Assert.DoesNotContain("transcript", status, StringComparison.OrdinalIgnoreCase);

        // one implementer session and one prover session, never the same
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Single(harness.Factory.Runtime.ProverPrompts);
        Assert.Equal(AgentSessionPurpose.Prove, harness.Factory.Runtime.StartRequests[1].Purpose);
        Assert.DoesNotContain(harness.Factory.Runtime.Prompts[0], harness.Factory.Runtime.ProverPrompts[0]);
        Assert.Contains("Proof Contract", harness.Factory.Runtime.Prompts[0]);   // the implementer is told how to write one
    }

    [Fact]
    public async Task The_run_report_keeps_the_proof_rounds_in_order_when_the_first_proof_failed()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Fail();
        harness.Factory.Signals.ProofByCall[2] = Pass("The marker is present now.");
        harness.Factory.Runtime.Turn = (turn, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), turn == 1 ? CliHarness.Symbol + "\n" : CliHarness.Symbol + "\n-- end --\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.True(output.IndexOf("Proof round 1: FAIL", StringComparison.Ordinal) < output.IndexOf("Proof round 2: PASS", StringComparison.Ordinal));
        Assert.Contains("Feature.txt has no trailing marker", output);
        Assert.Contains("Proof: PASS (current)", output);

        // the SAME implementer session heard the findings; the second prover is a different, fresh session
        Assert.Equal(2, harness.Factory.Runtime.Prompts.Count);
        Assert.StartsWith("Formal PROVE did not establish every claim", harness.Factory.Runtime.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(2, harness.Factory.Runtime.ProverPrompts.Count);
        Assert.Contains("The previous proof (round 1, FAIL)", harness.Factory.Runtime.ProverPrompts[1]);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(2, task.Proofs.Length);
        Assert.NotEqual(task.Proofs[0].ProverSession, task.Proofs[1].ProverSession);
        Assert.Equal(task.Proofs[0].ImplementerSession, task.Proofs[1].ImplementerSession);
        Assert.Single(task.ProofContracts);
        Assert.Equal(ProofStatus.PassedCurrent, task.ProofStatus);
    }

    [Fact]
    public async Task With_REVIEW_and_PROVE_both_required_the_review_passes_first_and_the_report_shows_both()
    {
        var harness = NewHarness(WithProverAndReviewer);
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ReviewByCall[1] = new AgentReviewSubmission("PASS", "Reviewed and fine.", [], null);
        harness.Factory.Signals.ProofByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task", "--require", "review,prove");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("Review: PASS (current)", output);
        Assert.Contains("Proof: PASS (current)", output);
        Assert.Equal([AgentSessionPurpose.Implementation, AgentSessionPurpose.Review, AgentSessionPurpose.Prove], harness.Factory.Runtime.StartRequests.Select(r => r.Purpose).ToArray());

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(task.Reviews[0].Id, task.Proofs[0].ReviewId);
        Assert.Equal(new ProfileId("claude-code-local"), task.RoleBindings[WellKnownRoles.Prover].Profile.Id);
        Assert.Equal(new ProfileId("claude-code-local"), task.RoleBindings[WellKnownRoles.Reviewer].Profile.Id);
    }

    // ---- the blocks and their exit codes --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prover_that_changes_the_workspace_exits_28_blocked_with_the_workspace_left_exactly_as_it_was()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Pass();
        harness.Factory.Runtime.ProverTurn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Prover.txt"), "the prover was here\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        var output = harness.Out.ToString();
        Assert.Equal(28, exit);
        Assert.Contains("BLOCKED - PROVER MODIFIED THE WORKSPACE", output);
        Assert.Contains("nothing was absorbed as an implementation and nothing was reverted", output);
        Assert.True(File.Exists(Path.Combine(harness.Workspace, "src", "Prover.txt")));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.Proofs);
        Assert.Single(task.ImplementationRevisions);

        // 'aw continue' is not a generic retry: this block is not a recoverable continuation
        harness.ResetOutput();
        Assert.NotEqual(0, await harness.RunAsync("continue", task.Id.ToString()));
    }

    [Fact]
    public async Task A_missing_Proof_Contract_exits_30_blocked_after_one_reminder_with_the_work_preserved()
    {
        var harness = NewHarness();
        using var _ = harness;

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        var output = harness.Out.ToString();
        Assert.Equal(30, exit);
        Assert.Contains("BLOCKED - PROOF CONTRACT MISSING", output);
        Assert.Equal(2, harness.Factory.Runtime.Prompts.Count);   // the task and exactly one reminder
        Assert.Single(harness.Factory.Runtime.StartRequests);     // no prover was started
        Assert.Contains(CliHarness.Symbol, File.ReadAllText(Path.Combine(harness.Workspace, "src", "Feature.txt")));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ProofContracts);
        Assert.True(task.IsProofContractMissing);
    }

    [Fact]
    public async Task A_proof_that_still_fails_with_no_turn_left_exits_29_incomplete()
    {
        var harness = NewHarness(maxTurns: 1);
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Fail();

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        var output = harness.Out.ToString();
        Assert.Equal(29, exit);
        Assert.Contains("INCOMPLETE - PROOF STILL FAILING", output);
        Assert.Contains("Proof round 1: FAIL", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Equal(ProofStatus.Failed, task.ProofStatus);
    }

    // ---- an agent-requested proof ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_contract_with_no_proverProfile_exits_27_and_states_the_late_binding_limitation()
    {
        var harness = NewHarness(extra: string.Empty);
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(27, exit);
        Assert.Contains("BLOCKED - PROVER NOT BOUND", output);
        Assert.Contains("late", output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Factory.Runtime.StartRequests);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("PROVE: REQUIRED (requested by the agent", harness.Out.ToString());
        Assert.Contains("Prover: NOT BOUND", harness.Out.ToString());   // no prover was started, least of all with the implementer's profile
    }

    [Fact]
    public async Task An_agent_contract_with_a_proverProfile_is_additive_and_proved_before_completion()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Signals.ProofByCall[1] = Pass();

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(0, exit);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.False(task.ProveRequired);
        Assert.True(task.ProveEscalated);
        Assert.Single(task.Proofs);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", task.Id.ToString()));
        Assert.Contains("requested by the agent", harness.Out.ToString());
    }

    // ---- aw show ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Show_prints_the_explicit_prover_binding_and_says_when_there_is_none()
    {
        var bound = NewHarness();
        using var _ = bound;
        var none = NewHarness(extra: string.Empty);
        using var __ = none;

        Assert.Equal(0, await bound.RunAsync("show"));
        Assert.Equal(0, await none.RunAsync("show"));

        Assert.Contains("Prover profile: claude-code-local", bound.Out.ToString());
        Assert.Contains("bound to the prover role when a run starts", bound.Out.ToString());
        Assert.Contains("Prover profile: none", none.Out.ToString());
        Assert.Contains("--require prove needs --prover-profile", none.Out.ToString());
    }

    // ---- no PROVE: nothing changes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_without_the_requirement_is_unchanged_one_session_no_prover_no_proof_section()
    {
        var harness = NewHarness();   // a proverProfile exists, but nothing required a proof
        using var _ = harness;

        var exit = await harness.RunAsync("run", "task");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("COMPLETED", output);
        Assert.DoesNotContain("Proof:", output);
        Assert.DoesNotContain("Prover:", output);
        Assert.DoesNotContain("PROVE", output);
        Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Empty(harness.Factory.Runtime.ProverPrompts);
        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Empty(task.Proofs);
        Assert.False(task.IsProveRequired);
    }

    // ---- recovery -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prover_that_failed_before_submitting_is_recovered_by_continue_with_a_fresh_prover_and_without_repeating_the_work()
    {
        var harness = NewHarness();
        using var _ = harness;
        harness.Factory.Signals!.ProveRequestByTurn[1] = Contract();
        harness.Factory.Runtime.ProverTurn = (_, _, _) => throw new InvalidOperationException("the prover pane crashed");

        var exit = await harness.RunAsync("run", "task", "--require", "prove");

        Assert.NotEqual(0, exit);
        var runId = harness.PrintedRunId();
        var stranded = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Blocked, stranded.WorkflowState);
        Assert.Empty(stranded.Proofs);
        Assert.Single(stranded.ProofContracts);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.Contains("aw continue", harness.Out.ToString());

        harness.Factory.Runtime.ProverTurn = (_, _, _) => Task.FromResult(AgentTurnOutcome.Done);
        harness.Factory.Signals.ProofByCall[1] = Pass();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var recovered = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, recovered.WorkflowState);
        Assert.Single(recovered.Proofs);
        Assert.Single(recovered.ProofContracts);                                                           // the contract is not asked for again
        Assert.Equal(stranded.ImplementationRevisions.Length, recovered.ImplementationRevisions.Length);   // nothing re-implemented or duplicated
        Assert.Equal(stranded.Verifications.Length, recovered.Verifications.Length);                       // and nothing re-verified
        Assert.Single(harness.Factory.Runtime.Prompts);                                                    // the implementer was never involved
    }
}
