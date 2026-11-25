using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M8 at the controller: once the work verified (and, when required, was reviewed and passed), a fresh read-only prover conversation judges the implementer's Proof Contract
/// claim by claim and submits one structured proof. A current PASS lets the task complete (or open ACCEPT); FAIL or INCONCLUSIVE resume the ORIGINAL implementer with the
/// delta only, then verification, REVIEW first when required, and a NEW fresh prover. The prover is read-only, never the implementer or the reviewer, never receives a
/// transcript, and a stale proof never completes anything.
/// </summary>
public sealed class ProofRunnerTests
{
    private const string ProverProfileName = "claude-prover";
    private const string ReviewerProfileName = "claude-reviewer";

    private static AgentProveRequestSignal Contract(string rationale = "The rename must keep every caller compiling.", params AgentProofClaim[] claims)
        => new(rationale, claims.Length == 0 ? [Claim()] : claims);

    private static AgentProofClaim Claim(string id = "c1", string requires = "tests")
        => new(id, "No caller of GetMeal() is left behind.", [new AgentProofEvidenceRequirement("verification", requires)], "Every reference compiles and the suite passes.");

    private static AgentProofClaimResult Result(string id = "c1", string verdict = "pass", string summary = "Established.", params string[] evidence)
        => new(id, verdict, evidence.Length == 0 ? ["verification:tests"] : evidence, summary);

    private static AgentProofSubmission Pass(string summary = "Every claim holds.", params AgentProofClaimResult[] results)
        => new(summary, results.Length == 0 ? [Result()] : results);

    private static AgentProofSubmission Fail(string summary = "A caller still uses the old name.")
        => new(summary, [Result("c1", "fail", "src/Other.cs still calls GetMeal().", "verification:tests")]);

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        public FakeSignals Signals { get; } = new();

        public FakeBaselineSink Baselines { get; } = new();

        public DirectPathRunner Runner()
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options: null, evidence: null, new AgentConversationSupport(Conversations, Store), new AgentClarificationSupport(Signals, Baselines));

        public Task<DirectRunResult> RunAsync(int maxTurns = 5, Profile? prover = null)
            => Runner().RunAsync(Fixture.TaskOne, Definition(maxTurns, prover));

        public DirectTaskDefinition Definition(int maxTurns = 5, Profile? prover = null)
            => H.Definition(maxTurns) with { ProverProfile = prover };

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e.Payload is T);

        public IEnumerable<AgentSessionRequest> ProverStarts => H.Runtime.Starts.Where(s => s.Purpose == AgentSessionPurpose.Prove);

        public IEnumerable<AgentSessionRequest> ReviewerStarts => H.Runtime.Starts.Where(s => s.Purpose == AgentSessionPurpose.Review);

        public IEnumerable<AgentSessionRequest> ImplementerStarts => H.Runtime.Starts.Where(s => s.Purpose is not (AgentSessionPurpose.Review or AgentSessionPurpose.Prove));

        public IEnumerable<string> PromptsOf(AgentSessionId session) => H.Runtime.Sends.Where(s => s.Session == session).Select(s => s.Prompt);

        public string ProverPrompt(int round) => H.Runtime.Sends.Where(s => s.Prompt.StartsWith("You are the independent PROVER", StringComparison.Ordinal)).ElementAt(round - 1).Prompt;
    }

    private static async Task<Rig> NewRigAsync(
        bool prove = true,
        string? proverProfile = ProverProfileName,
        AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired,
        bool review = false,
        string? reviewerProfile = null,
        bool howRequired = false)
    {
        var h = await DirectHarness.CreateAsync(
            acceptance: acceptance, howRequired: howRequired, reviewRequired: review, reviewerProfile: reviewerProfile, proveRequired: prove, proverProfile: proverProfile);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    // ---- sequencing and completion -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_passing_proof_of_the_verified_implementation_completes_the_task_without_rerunning_the_checks()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        var proof = Assert.Single(task.Proofs);
        Assert.Equal(ProofVerdict.Pass, proof.Verdict);
        Assert.Equal(ProofStatus.PassedCurrent, task.ProofStatus);
        Assert.Equal(task.CurrentVerification!.Id, proof.VerificationRunId);
        Assert.Equal(task.CurrentImplementationRevisionId, proof.ImplementationRevisionId);
        Assert.Equal(task.CurrentProofContract!.ContentHash, proof.ContractHash);
        Assert.Equal(1, rig.Events<ProofContractRecorded>());
        Assert.Equal(1, rig.Events<ProofRecorded>());
        Assert.Single(rig.H.Verifier.Ran);          // the controller verified once; the prover cost no second full run
        Assert.False(proof.ConfiguredCheckRerun);
    }

    [Fact]
    public async Task With_human_acceptance_a_passing_proof_opens_the_ACCEPT_gate_and_is_not_the_acceptance()
    {
        var rig = await NewRigAsync(acceptance: AcceptanceRequirement.HumanRequired);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Accept, task.PendingGate!.Type);
        Assert.True(task.HasCurrentProofPass);
    }

    [Fact]
    public async Task A_task_that_does_not_require_PROVE_runs_exactly_as_before_with_one_session_and_no_prover()
    {
        var rig = await NewRigAsync(prove: false, proverProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Empty(rig.ProverStarts);
        var task = await rig.LoadAsync();
        Assert.Empty(task.Proofs);
        Assert.Empty(task.ProofContracts);
        Assert.Equal(0, rig.Events<ProofRecorded>());
        Assert.Empty(rig.Signals.ProofCalls);
    }

    [Fact]
    public async Task Required_PROVE_implies_neither_review_nor_a_human_gate_and_runs_after_verification()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing(), rig.H.Renames(), DirectHarness.DoesNothing());   // verification fails once, is repaired, then proved
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.False(task.IsReviewRequired);
        Assert.Empty(task.Reviews);
        Assert.Empty(rig.ReviewerStarts);
        Assert.Equal(2, rig.H.Verifier.Ran.Count);
        Assert.Single(rig.ProverStarts);
        var order = rig.H.Runtime.Sends.Select(s => s.Session).ToList();
        Assert.Equal(new AgentSessionId("session-2"), order[^1]);   // the prover is last: it only ever saw the passing verification
    }

    // ---- prover binding ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_prover_binding_the_run_stops_unproved_and_neither_the_implementer_nor_the_reviewer_is_ever_used()
    {
        var rig = await NewRigAsync(proverProfile: null, review: true, reviewerProfile: ReviewerProfileName);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseReview(1, new AgentReviewSubmission("PASS", "Fine.", [], null));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProverUnbound, result.Status);
        Assert.Contains("prover_unbound", result.Reason);
        Assert.Contains("late", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.ProverStarts);                          // the reviewer's profile was not borrowed
        var task = await rig.LoadAsync();
        Assert.False(task.RoleBindings.ContainsKey(WellKnownRoles.Prover));
        Assert.Empty(task.Proofs);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.True(task.IsProveRequired);
    }

    [Fact]
    public async Task The_projects_configured_prover_profile_is_bound_when_the_proof_is_first_needed()
    {
        var rig = await NewRigAsync(proverProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync(prover: Fixture.Profile(ProverProfileName, "repository_tools"));

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var binding = (await rig.LoadAsync()).RoleBindings[WellKnownRoles.Prover];
        Assert.Equal(new ProfileId(ProverProfileName), binding.Profile.Id);
        Assert.Equal(new ProfileId(ProverProfileName), Assert.Single(rig.ProverStarts).Profile);
    }

    [Fact]
    public async Task The_prover_is_a_new_conversation_started_for_PROVE_and_the_implementers_is_never_resumed_for_it()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        await rig.RunAsync();

        Assert.Equal(2, rig.H.Runtime.Starts.Count);
        Assert.Equal(AgentSessionPurpose.Prove, rig.H.Runtime.Starts[1].Purpose);
        Assert.Empty(rig.Conversations.Resumes);
        var proof = Assert.Single((await rig.LoadAsync()).Proofs);
        Assert.NotEqual(proof.ImplementerSession, proof.ProverSession);
        Assert.Contains(new AgentSessionId("session-2"), rig.H.Runtime.Stops);
    }

    // ---- the contract --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_contract_gets_exactly_one_reminder_in_the_implementers_session_and_then_blocks_with_the_work_preserved()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProofContractMissing, result.Status);
        Assert.Contains("proof_contract_missing", result.Reason);
        var task = await rig.LoadAsync();
        Assert.True(ProofUnresolvedBlock.IsBlockedBy(task));
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.Empty(task.ProofContracts);
        Assert.Empty(rig.ProverStarts);
        var prompts = rig.PromptsOf(new AgentSessionId("session-1")).ToList();
        Assert.Equal(2, prompts.Count);                                              // the task, and one reminder
        Assert.Contains("Proof Contract", prompts[1]);
        Assert.Contains("def", rig.H.Workspace.Files["src/Greeting.cs"] + "def");    // the work is still in the workspace (nothing was reverted)
        Assert.Contains("GetFood", rig.H.Workspace.Files["src/Greeting.cs"]);
    }

    [Fact]
    public async Task A_contract_written_in_answer_to_the_reminder_is_recorded_and_the_task_goes_on_to_be_proved()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(2, Contract());     // turn 1 wrote none; the reminder turn writes it
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.Single(task.ProofContracts);
        Assert.Equal(2, task.CurrentProofContract!.Turn);   // written in the reminder turn
        Assert.Single(task.Proofs);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_malformed_contract_is_recorded_as_nothing_and_its_problem_is_what_the_reminder_reports()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(claims: new AgentProofClaim("c1", "A claim.", [new AgentProofEvidenceRequirement("transcript", "everything")], "It holds.")));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProofContractMissing, result.Status);
        Assert.Empty((await rig.LoadAsync()).ProofContracts);
        Assert.Contains(result.Notes, n => n.Contains("malformed Proof Contract"));
        Assert.Equal(0, rig.Events<ProofContractRecorded>());
    }

    [Fact]
    public async Task Two_contracts_in_one_turn_are_ambiguous_and_neither_is_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(), Contract("A different rationale."));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProofContractMissing, result.Status);
        Assert.Empty((await rig.LoadAsync()).ProofContracts);
        Assert.Contains(result.Notes, n => n.Contains("2 Proof Contracts in one turn"));
    }

    [Fact]
    public async Task A_second_contract_for_the_same_semantic_revision_is_never_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProveRequest(2, Contract("A rewritten contract."));
        rig.Signals.RaiseProof(1, Fail());
        rig.Signals.RaiseProof(2, Pass());

        await rig.RunAsync();

        var task = await rig.LoadAsync();
        Assert.Single(task.ProofContracts);
        Assert.Equal("The rename must keep every caller compiling.", task.CurrentProofContract!.Rationale);
        Assert.Equal(1, rig.Events<ProofContractRecorded>());
    }

    [Fact]
    public async Task A_contract_that_requires_a_review_when_none_is_required_is_refused_and_the_reminder_says_why()
    {
        var rig = await NewRigAsync();   // PROVE required, REVIEW not
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(claims: new AgentProofClaim("c1", "A claim.", [new AgentProofEvidenceRequirement("review", "current-pass")], "It holds.")));
        rig.Signals.RaiseProveRequest(2, Contract());   // the reminder turn writes a satisfiable one
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var reminder = rig.PromptsOf(new AgentSessionId("session-1")).ElementAt(1);
        Assert.Contains("no independent review is required", reminder);
        Assert.Equal(1, rig.Events<ProofContractRecorded>());
    }

    [Fact]
    public async Task A_contract_that_requires_a_verification_the_project_did_not_configure_is_refused_naming_the_configured_checks()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(claims: new AgentProofClaim("c1", "A claim.", [new AgentProofEvidenceRequirement("verification", "lint")], "It holds.")));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProofContractMissing, result.Status);
        Assert.Empty((await rig.LoadAsync()).ProofContracts);
        Assert.Contains("verification:lint", rig.PromptsOf(new AgentSessionId("session-1")).ElementAt(1));
        Assert.Contains("configured: tests", rig.PromptsOf(new AgentSessionId("session-1")).ElementAt(1));
    }

    [Fact]
    public async Task A_contract_may_require_the_review_when_a_review_is_required()
    {
        var rig = await NewRigAsync(review: true, reviewerProfile: ReviewerProfileName);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(claims: new AgentProofClaim("c1", "A claim.", [new AgentProofEvidenceRequirement("review", "current-pass")], "It holds.")));
        rig.Signals.RaiseReview(1, new AgentReviewSubmission("PASS", "Reviewed.", [], null));
        rig.Signals.RaiseProof(1, Pass(results: Result("c1", "pass", "Established by the review.", "review:current-pass")));

        var result = await rig.RunAsync();

        Assert.Single((await rig.LoadAsync()).ProofContracts);   // recorded: the review is required, so the requirement is satisfiable
        Assert.NotEqual(DirectRunStatus.ProofContractMissing, result.Status);
    }

    [Fact]
    public async Task The_implementer_is_told_which_checks_exist_and_whether_a_review_can_be_cited()
    {
        var plain = await NewRigAsync();
        plain.H.Runtime.Enqueue(plain.H.Renames());
        plain.Signals.RaiseProveRequest(1, Contract());
        plain.Signals.RaiseProof(1, Pass());
        await plain.RunAsync();

        var withReview = await NewRigAsync(review: true, reviewerProfile: ReviewerProfileName);
        withReview.H.Runtime.Enqueue(withReview.H.Renames());
        withReview.Signals.RaiseProveRequest(1, Contract());
        await withReview.RunAsync();

        var first = plain.PromptsOf(new AgentSessionId("session-1")).First();
        Assert.Contains("the configured checks are `tests`", first);
        Assert.Contains("Do NOT use `review:current-pass`", first);

        var reviewed = withReview.PromptsOf(new AgentSessionId("session-1")).First();
        Assert.Contains("the configured checks are `tests`", reviewed);
        Assert.DoesNotContain("Do NOT use `review:current-pass`", reviewed);
        Assert.Contains("this task requires an independent review", reviewed);
    }

    // ---- dynamic request -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_contract_on_a_task_that_did_not_require_PROVE_adds_the_requirement_and_the_proof_runs()
    {
        var rig = await NewRigAsync(prove: false);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.True(task.ProveEscalated);
        Assert.False(task.ProveRequired);
        Assert.True(task.IsProveRequired);
        Assert.Single(task.Proofs);
        Assert.Single(task.SemanticRevisions);   // additive: no semantic revision
    }

    [Fact]
    public async Task A_dynamic_contract_with_no_prover_binding_stops_safely_and_preserves_the_work()
    {
        var rig = await NewRigAsync(prove: false, proverProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames());
        rig.Signals.RaiseProveRequest(1, Contract());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProverUnbound, result.Status);
        var task = await rig.LoadAsync();
        Assert.True(task.IsProveRequired);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Contains("GetFood", rig.H.Workspace.Files["src/Greeting.cs"]);
        Assert.Contains("new run", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- what the prover is given --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_prover_context_is_the_deterministic_packet_and_never_the_implementers_transcript()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        await rig.RunAsync();

        var packet = rig.ProverPrompt(1);
        var implementerPrompt = rig.PromptsOf(new AgentSessionId("session-1")).First();

        Assert.Contains("Rename GetMeal() to GetFood()", packet);
        Assert.Contains("Semantic revision 1", packet);
        Assert.Contains("Claim c1:", packet);
        Assert.Contains("requires: verification:tests", packet);
        Assert.Contains("Implementation revision:", packet);
        Assert.Contains("tests:", packet);                                  // the verification the controller already holds
        Assert.Contains("Do NOT run the configured checks again", packet);
        Assert.Contains("src/Greeting.cs", packet);                         // the files changed
        Assert.Contains(AgentProofSubmission.ToolName, packet);
        Assert.Contains("read-only", packet);
        Assert.DoesNotContain("Rename the public method", packet);          // the implementer's own working prompt
        Assert.DoesNotContain(implementerPrompt, packet);
        Assert.DoesNotContain("Done.", packet);
    }

    // ---- proofs --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_structured_FAIL_is_recorded_with_its_claims_provenance_and_a_derived_verdict()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Fail());
        rig.Signals.RaiseProof(2, Pass());

        await rig.RunAsync();

        var first = (await rig.LoadAsync()).Proofs[0];
        Assert.Equal(ProofVerdict.Fail, first.Verdict);                      // derived from the claim verdicts, never submitted
        var claim = Assert.Single(first.Report.Claims);
        Assert.Equal(ProofVerdict.Fail, claim.Verdict);
        Assert.Equal("src/Other.cs still calls GetMeal().", claim.Summary);
        Assert.Equal(new ProfileId(ProverProfileName), first.ProverProfile);
        Assert.Equal(1, first.Round);
        Assert.Equal(first.Report.ContentHash, first.ReportHash);
    }

    [Theory]
    [InlineData("maybe", "verification:tests")]        // unknown verdict
    [InlineData("pass", "command:zero")]               // a malformed evidence reference
    [InlineData("pass", "file:/etc/passwd")]           // outside the closed vocabulary
    public async Task A_malformed_proof_is_rejected_and_nothing_is_recorded(string verdict, string evidence)
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, new AgentProofSubmission("Judged.", [new AgentProofClaimResult("c1", verdict, [evidence], "x")]));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("proof_report_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Proofs);
        Assert.Equal(0, rig.Events<ProofRecorded>());
    }

    [Fact]
    public async Task A_proof_that_does_not_judge_every_claim_or_judges_an_unknown_one_is_refused_after_one_correction()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract(claims: [Claim("c1"), Claim("c2")]));
        rig.Signals.RaiseProof(1, Pass(results: Result("c1")));            // c2 missing
        rig.Signals.RaiseProof(2, Pass(results: [Result("c1"), Result("c9")]));   // an unknown claim

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("proof_report_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Proofs);
        Assert.Equal(2, rig.PromptsOf(new AgentSessionId("session-2")).Count());   // the packet and one correction, nothing else
    }

    [Fact]
    public async Task A_PASS_that_cites_no_evidence_for_a_required_verification_is_refused()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        var uncited = new AgentProofSubmission("Judged.", [new AgentProofClaimResult("c1", "pass", [], "Looks right.")]);
        rig.Signals.RaiseProof(1, uncited);
        rig.Signals.RaiseProof(2, uncited);

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.Empty((await rig.LoadAsync()).Proofs);
        Assert.NotEqual(WorkflowState.Completed, (await rig.LoadAsync()).WorkflowState);
    }

    [Fact]
    public async Task A_refused_proof_corrected_in_the_same_prover_session_is_recorded_exactly_once()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, new AgentProofSubmission("Judged.", [new AgentProofClaimResult("c1", "pass", [], "Looks right.")]));   // refused: cites nothing
        rig.Signals.RaiseProof(2, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var proof = Assert.Single((await rig.LoadAsync()).Proofs);
        Assert.Equal(ProofVerdict.Pass, proof.Verdict);
        Assert.Equal(1, rig.Events<ProofRecorded>());
        Assert.Single(rig.ProverStarts);
        Assert.Equal(2, rig.PromptsOf(new AgentSessionId("session-2")).Count());   // the packet and the one correction, in the same conversation
    }

    [Fact]
    public async Task A_refused_proof_followed_by_no_correction_is_reported_as_a_refused_proof_not_as_no_proof()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, new AgentProofSubmission("Judged.", [new AgentProofClaimResult("c1", "pass", [], "Looks right.")]));   // refused; the correction turn submits nothing

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("proof_correction_not_submitted", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("without submitting a proof", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Proofs);
        Assert.Equal(0, rig.Events<ProofRecorded>());
        Assert.Equal(2, rig.PromptsOf(new AgentSessionId("session-2")).Count());   // bounded: one correction, never a third turn
    }

    [Fact]
    public async Task Two_proofs_in_one_turn_are_a_conflict_and_neither_is_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass(), Fail());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("proof_conflict", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Proofs);
    }

    [Fact]
    public async Task A_prover_that_never_submits_gets_one_reminder_in_its_own_session_and_then_fails_with_nothing_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("proof_not_submitted", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, rig.PromptsOf(new AgentSessionId("session-2")).Count());
        Assert.Empty((await rig.LoadAsync()).Proofs);
    }

    // ---- read-only -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prover_that_changes_the_workspace_blocks_the_run_and_nothing_is_absorbed_recorded_or_reverted()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the prover fixed it"));
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ProverModifiedWorkspace, result.Status);
        Assert.StartsWith("prover_modified_workspace", result.Reason, StringComparison.Ordinal);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(ProverWorkspaceBlock.IsBlockedBy(task));
        Assert.Empty(task.Proofs);
        Assert.Single(task.ImplementationRevisions);                                 // the prover's change is not an implementation revision
        Assert.Contains("the prover fixed it", rig.H.Workspace.Files["src/Greeting.cs"]);
        Assert.Null(RecoverableContinuation.Find(task));                             // 'aw continue' is not a generic retry
    }

    // ---- the repair loop -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("fail")]
    [InlineData("inconclusive")]
    public async Task A_proof_that_did_not_pass_resumes_the_original_implementer_with_only_the_delta_then_a_NEW_fresh_prover_judges_the_repair(string verdict)
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),                                                                                            // implementer turn 1
            DirectHarness.DoesNothing(),                                                                                // prover 1
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the last caller is migrated"),       // implementer repairs
            DirectHarness.DoesNothing());                                                                               // prover 2
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, new AgentProofSubmission("Not established.", [Result("c1", verdict, "A caller was not checked.", "verification:tests")]));
        rig.Signals.RaiseProof(2, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();

        var implementer = new AgentSessionId("session-1");
        var prompts = rig.PromptsOf(implementer).ToList();
        Assert.Equal(2, prompts.Count);
        Assert.StartsWith("Formal PROVE did not establish every claim", prompts[1], StringComparison.Ordinal);
        Assert.Contains("A caller was not checked.", prompts[1]);
        Assert.DoesNotContain("Rename the public method", prompts[1]);   // only the delta: the task is not restated

        Assert.Equal(2, rig.ProverStarts.Count());
        Assert.Single(rig.ImplementerStarts);
        Assert.Equal(2, task.Proofs.Length);
        Assert.NotEqual(task.Proofs[0].ProverSession, task.Proofs[1].ProverSession);
        Assert.Equal(task.Proofs[0].ImplementerSession, task.Proofs[1].ImplementerSession);   // the original implementer throughout
        Assert.Equal(2, task.Proofs[1].Round);
        Assert.Single(task.ProofContracts);                                                   // the contract is immutable across implementation repairs
        Assert.Equal(task.Proofs[0].ContractHash, task.Proofs[1].ContractHash);
        Assert.Equal(ProofStatus.PassedCurrent, task.ProofStatus);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_repair_that_changes_files_is_verified_again_before_the_next_proof_and_the_first_proof_becomes_history()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the last caller is migrated"),
            DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Fail());
        rig.Signals.RaiseProof(2, Pass());

        await rig.RunAsync();

        var task = await rig.LoadAsync();
        Assert.Equal(2, task.ImplementationRevisions.Length);
        Assert.Equal(2, rig.H.Verifier.Ran.Count);
        Assert.NotEqual(task.Proofs[0].ImplementationRevisionId, task.Proofs[1].ImplementationRevisionId);
        Assert.NotEqual(task.Proofs[0].VerificationRunId, task.Proofs[1].VerificationRunId);
        Assert.False(task.IsProofCurrent(task.Proofs[0]));
        Assert.True(task.IsProofCurrent(task.Proofs[1]));
    }

    [Fact]
    public async Task The_second_prover_receives_the_previous_per_claim_results_but_never_a_transcript()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the last caller is migrated"),
            DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Fail());
        rig.Signals.RaiseProof(2, Pass());

        await rig.RunAsync();

        var first = rig.ProverPrompt(1);
        var second = rig.ProverPrompt(2);

        Assert.Contains("proof round 2", second);
        Assert.Contains("The previous proof (round 1, FAIL)", second);
        Assert.Contains("src/Other.cs still calls GetMeal().", second);
        Assert.DoesNotContain("Formal PROVE did not establish", second);   // the implementer's repair prompt
        Assert.DoesNotContain("The previous proof", first);
        Assert.NotEqual(rig.H.Runtime.Sends.First(s => s.Prompt == first).Session, rig.H.Runtime.Sends.First(s => s.Prompt == second).Session);
        Assert.Equal(1, rig.H.Runtime.Sends.Count(s => s.Prompt == first));
    }

    [Fact]
    public async Task A_proof_that_still_fails_when_the_implementer_has_no_turn_left_is_unresolved_and_not_complete()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Fail());

        var result = await rig.RunAsync(maxTurns: 1);

        Assert.Equal(DirectRunStatus.ProofUnresolved, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(ProofStatus.Failed, task.ProofStatus);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.True(ProofUnresolvedBlock.IsBlockedBy(task));
    }

    // ---- review + prove ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_both_required_the_order_is_verification_then_a_current_review_pass_then_the_proof_then_completion()
    {
        var rig = await NewRigAsync(review: true, reviewerProfile: ReviewerProfileName);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseReview(1, new AgentReviewSubmission("PASS", "Reviewed.", [], null));
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        var review = Assert.Single(task.Reviews);
        var proof = Assert.Single(task.Proofs);
        Assert.Equal(review.Id, proof.ReviewId);                                    // the proof is bound to the review it followed
        Assert.Equal(proof.VerificationRunId, review.VerificationRunId);
        var starts = rig.H.Runtime.Starts.Select(s => s.Purpose).ToList();
        Assert.Equal([AgentSessionPurpose.Implementation, AgentSessionPurpose.Review, AgentSessionPurpose.Prove], starts);
        Assert.Contains("Reviewed.", rig.ProverPrompt(1));                           // the prover is given the review's structured result
        Assert.Contains($"review:{review.Id}", rig.ProverPrompt(1));
    }

    [Fact]
    public async Task A_review_that_requires_changes_is_repaired_and_re_reviewed_before_any_prover_starts()
    {
        var rig = await NewRigAsync(review: true, reviewerProfile: ReviewerProfileName);
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"),
            DirectHarness.DoesNothing(),
            DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseReview(1, new AgentReviewSubmission("CHANGES_REQUIRED", "One defect.", [new AgentReviewFinding("Alias missing", "Callers break.", "Builds fail.", "Keep a forwarder.", null)], null));
        rig.Signals.RaiseReview(2, new AgentReviewSubmission("PASS", "Now fine.", [], null));
        rig.Signals.RaiseProof(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var purposes = rig.H.Runtime.Starts.Select(s => s.Purpose).ToList();
        Assert.Equal([AgentSessionPurpose.Implementation, AgentSessionPurpose.Review, AgentSessionPurpose.Review, AgentSessionPurpose.Prove], purposes);
        var task = await rig.LoadAsync();
        Assert.Equal(2, task.Reviews.Length);
        Assert.Single(task.Proofs);
        Assert.Equal(task.Reviews[1].Id, task.Proofs[0].ReviewId);
    }

    [Fact]
    public async Task A_proof_repair_then_runs_review_again_before_the_new_prover()
    {
        var rig = await NewRigAsync(review: true, reviewerProfile: ReviewerProfileName);
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),                                                                              // reviewer 1
            DirectHarness.DoesNothing(),                                                                              // prover 1
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the last caller is migrated"),     // repair
            DirectHarness.DoesNothing(),                                                                              // reviewer 2
            DirectHarness.DoesNothing());                                                                             // prover 2
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseReview(1, new AgentReviewSubmission("PASS", "Reviewed.", [], null));
        rig.Signals.RaiseReview(2, new AgentReviewSubmission("PASS", "Reviewed the repair.", [], null));
        rig.Signals.RaiseProof(1, Fail());
        rig.Signals.RaiseProof(2, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var purposes = rig.H.Runtime.Starts.Select(s => s.Purpose).ToList();
        Assert.Equal(
            [AgentSessionPurpose.Implementation, AgentSessionPurpose.Review, AgentSessionPurpose.Prove, AgentSessionPurpose.Review, AgentSessionPurpose.Prove],
            purposes);
        var task = await rig.LoadAsync();
        Assert.Equal(task.Reviews[1].Id, task.Proofs[1].ReviewId);
        Assert.False(task.IsProofCurrent(task.Proofs[0]));
    }

    // ---- recovery ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_prover_that_died_before_a_durable_proof_is_retried_with_a_fresh_prover_and_nothing_is_duplicated()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        var failures = 0;
        rig.H.Runtime.OnWait = _ =>
        {
            if (rig.H.Runtime.Starts.Count == 2 && failures++ == 0)
                throw new InvalidOperationException("the prover pane crashed");

            return Task.CompletedTask;
        };

        var first = await rig.RunAsync();

        Assert.NotEqual(DirectRunStatus.Completed, first.Status);
        var stranded = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, stranded.WorkflowState);
        Assert.Empty(stranded.Proofs);
        var pending = RecoverableContinuation.Find(stranded);
        Assert.NotNull(pending);
        Assert.Equal(HumanInputKind.ProvePending, pending!.Kind);
        var implementationRevisions = stranded.ImplementationRevisions.Length;
        var verifications = stranded.Verifications.Length;
        var contracts = stranded.ProofContracts.Length;

        rig.Signals.RaiseProof(1, Pass());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        await new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne);
        var current = await rig.H.Workspace.CaptureAsync(@"C:\fixture");
        var recovered = await rig.Runner().RecoverHumanInputAsync(Fixture.TaskOne, rig.Definition(), current);

        Assert.Equal(DirectRunStatus.Completed, recovered.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(implementationRevisions, task.ImplementationRevisions.Length);   // not duplicated
        Assert.Equal(verifications, task.Verifications.Length);                       // not re-run
        Assert.Equal(contracts, task.ProofContracts.Length);                          // the contract is not asked for again
        Assert.Single(task.Proofs);
        Assert.Equal(1, rig.Events<ProofRecorded>());
        Assert.Equal(2, rig.ProverStarts.Count());                                    // a fresh prover over the same packet
        Assert.Single(rig.ImplementerStarts);
    }

    [Fact]
    public async Task A_failed_repair_after_the_proof_was_recorded_retries_the_continuation_in_the_original_conversation_and_not_the_proof()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(1, Contract());
        rig.Signals.RaiseProof(1, Fail());
        var waits = 0;
        rig.H.Runtime.OnWait = _ =>
        {
            // implementer turn 1 and prover 1 pass; the implementer's repair turn (the third wait) fails
            if (++waits == 3)
                throw new InvalidOperationException("the implementer pane crashed");

            return Task.CompletedTask;
        };

        var first = await rig.RunAsync();

        Assert.NotEqual(DirectRunStatus.Completed, first.Status);
        var stranded = await rig.LoadAsync();
        var pending = RecoverableContinuation.Find(stranded);
        Assert.NotNull(pending);
        Assert.Equal(HumanInputKind.ProofFindings, pending!.Kind);
        Assert.Equal(1, rig.Events<ProofRecorded>());

        rig.H.Runtime.OnWait = null;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the last caller is migrated"), DirectHarness.DoesNothing());
        rig.Signals.RaiseProof(2, Pass());
        await new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne);
        var baseline = rig.Baselines.Saved.Last(s => s.Gate == pending.BaselineKey).Baseline;
        var recovered = await rig.Runner().RecoverHumanInputAsync(Fixture.TaskOne, rig.Definition(), baseline);

        Assert.Equal(DirectRunStatus.Completed, recovered.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(2, task.Proofs.Length);                                  // the first proof was kept once; the second is the new prover's
        Assert.Single(rig.Conversations.Resumes);                             // the ORIGINAL implementer conversation was resumed
        Assert.Single(rig.ImplementerStarts);
        var resumed = rig.H.Runtime.Sends.Select(s => s.Prompt).Where(p => p.Contains("Formal PROVE did not establish")).ToList();
        Assert.Equal(2, resumed.Count);                                       // once before the failure, once as the recovered delta
        Assert.Contains("interrupted by a runtime failure", resumed[1]);
    }

    // ---- M6 interaction ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reclassification_stales_the_contract_and_the_proof_keeps_PROVE_required_and_a_new_contract_is_needed()
    {
        var rig = await NewRigAsync(howRequired: true);

        rig.Signals.RaiseHow(1, new AgentHowSignal("Rename in place.", "Greeting owns it.", "Callers use GetFood().", null, "Simplest.", "Existing tests.", "A forwarding alias is also plausible."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var proposed = await rig.RunAsync();
        Assert.Equal(DirectRunStatus.AwaitingHow, proposed.Status);
        await new HowService(rig.H.Service).ApproveAsync(Fixture.TaskOne, proposed.Task!.Task.PendingGate!.Id);

        // implemented and verified under HOW v1; the contract is written; the proof FAILS; the repair reports the approved HOW does not hold
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseProveRequest(2, Contract());   // call 1 was the HOW proposal turn
        rig.Signals.RaiseProof(1, Fail("The approved strategy cannot keep old callers working."));
        rig.Signals.RaiseReclassification(3, new AgentReclassificationSignal("Old callers need a forwarding alias; renaming in place breaks them.", ReclassificationStage.How, "The approved strategy has no alias."));
        rig.Signals.RaiseHow(4, new AgentHowSignal("Rename and keep a forwarding alias.", "Greeting owns it.", "GetMeal() forwards to GetFood().", null, "Old callers keep working.", "Existing tests plus one for the alias.", "The proof showed the trade-off changed."));
        var baseline = rig.Baselines.Saved.Last().Baseline;

        var rerouted = await rig.Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, rig.Definition(), baseline);

        Assert.Equal(DirectRunStatus.AwaitingHow, rerouted.Status);
        var task = await rig.LoadAsync();
        var proof = Assert.Single(task.Proofs);
        Assert.False(task.IsProofCurrent(proof));                  // the proof describes semantics that are gone
        Assert.Null(task.CurrentProofContract);                    // and so does the contract: a new one is needed
        Assert.Single(task.ProofContracts);                        // history is kept
        Assert.True(task.IsProveRequired);                         // PROVE stays required
        Assert.False(task.IsProveSatisfied);
        Assert.True(task.IsProofContractMissing);
        Assert.Equal(ProofStatus.NotPassedStale, task.ProofStatus);
        Assert.Equal(1, rig.Events<ProofRecorded>());              // nothing was re-recorded or duplicated
    }
}
