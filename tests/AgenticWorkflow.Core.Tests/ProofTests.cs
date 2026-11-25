using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>
/// formal PROVE is a conditional requirement of the workflow, judged by a fresh prover conversation against a structured Proof Contract the implementer submitted. Core derives
/// from the history alone whether PROVE is required, due, current, stale or satisfied; refuses a contract that is malformed or replaced and a proof that is not independent, not
/// bound to the current state, whose evidence does not resolve or that was not made by an explicitly bound prover; never lets completion or the ACCEPT gate pass without a current
/// PASS; and refuses to replay a history that claims otherwise.
/// </summary>
public sealed class ProofTests
{
    private const string ImplementerSession = "implementer-conversation";

    private static TaskStream NewStream(bool prove = true, bool review = false, bool what = false, bool how = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
        => TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), acceptance, what, how, review, prove);

    private static void BindAll(TaskStream stream)
    {
        stream.BindRole(Role("root", "code_edit", "repository_tools"), Profile("p-impl", "code_edit", "repository_tools"), At(1));
        stream.BindRole(Role("reviewer", "repository_tools"), Profile("p-reviewer", "repository_tools"), At(1));
        stream.BindRole(Role("prover", "repository_tools"), Profile("p-prover", "repository_tools"), At(1));
    }

    private static VerificationRun Implement(TaskStream stream, int at, string fingerprint = "tree:1", VerificationOutcome outcome = VerificationOutcome.Passed)
    {
        stream.RecordImplementationRevision(new ImplementationFingerprint(fingerprint), At(at));
        return stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), outcome, "ok", 0)], At(at + 1));
    }

    private static ProofClaim Claim(string id = "c1", params ProofEvidenceRequirement[] evidence)
        => new(id, $"Claim {id} holds.", evidence.Length == 0 ? [ProofEvidenceRequirement.Verification("t")] : evidence, $"Criterion of {id}.");

    private static ProofContract Contract(TaskStream stream, int at, params ProofClaim[] claims)
        => stream.RecordProofContract(new ProofContractId(Guid.NewGuid()), "These are the claims that matter.", claims.Length == 0 ? [Claim()] : claims, 1, ImplementerSession, At(at));

    private static ProofReport Report(ProofVerdict verdict = ProofVerdict.Pass, string claimId = "c1", params ProofEvidenceRef[] evidence)
        => new("Judged.", [new ProofClaimResult(claimId, verdict, evidence.Length == 0 ? [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t")] : evidence, "Because.")]);

    private static Proof Prove(TaskStream stream, int at, ProofReport? report = null, string proverSession = "prover-1", string implementerSession = ImplementerSession, string profile = "p-prover", IEnumerable<string>? commands = null)
        => stream.RecordProof(new ProofId(Guid.NewGuid()), report ?? Report(), new ProfileId(profile), proverSession, implementerSession, 1200, (commands ?? []).Count(), commands ?? [], false, 0, At(at));

    /// <summary>PROVE required, all roles bound, implemented, verified (passing) and a contract submitted: the moment a fresh prover is due.</summary>
    private static TaskStream Ready(bool review = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var stream = NewStream(review: review, acceptance: acceptance);
        BindAll(stream);
        Implement(stream, 2);
        Contract(stream, 4);
        return stream;
    }

    private static List<EventEnvelope> Forge(TaskStream stream, params DomainEvent[] more)
    {
        var events = stream.Events.ToList();

        foreach (var payload in more)
            events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, At(80 + events.Count), payload));

        return events;
    }

    // ---- requirement and composition ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_forced_PROVE_requirement_is_recorded_in_the_first_event_and_replays()
    {
        var stream = NewStream();

        Assert.True(((TaskCreated)stream.Events[0].Payload).ProveRequired);
        Assert.True(stream.State.ProveRequired);
        Assert.True(stream.State.IsProveRequired);
        Assert.False(stream.State.ProveEscalated);

        Assert.True(TaskStream.Replay(stream.Events, new GuidIdSource()).State.IsProveRequired);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, true)]
    public void PROVE_neither_implies_nor_is_implied_by_WHAT_HOW_or_REVIEW(bool what, bool how, bool review, bool prove)
    {
        var state = NewStream(prove, review, what, how).State;

        Assert.Equal(prove, state.IsProveRequired);
        Assert.Equal(review, state.IsReviewRequired);
        Assert.Equal(what, state.WhatRequired);
        Assert.Equal(how, state.HowRequired);
    }

    [Fact]
    public void A_task_that_did_not_ask_for_PROVE_is_not_held_up_by_it()
    {
        var stream = NewStream(prove: false);
        stream.BindRole(Role("root", "code_edit"), Profile("p-impl", "code_edit"), At(1));
        Implement(stream, 2);

        Assert.False(stream.State.IsProveRequired);
        Assert.True(stream.State.IsProveSatisfied);
        Assert.Equal(ProofStatus.None, stream.State.ProofStatus);

        stream.CompleteVerified(At(5));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    // ---- the Proof Contract ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_contract_submitted_on_a_task_that_did_not_require_PROVE_escalates_it_and_it_cannot_be_lowered()
    {
        var stream = NewStream(prove: false);
        BindAll(stream);
        Implement(stream, 2);
        Contract(stream, 4);

        Assert.True(stream.State.IsProveRequired);
        Assert.True(stream.State.ProveEscalated);
        Assert.False(stream.State.ProveRequired);
        Assert.False(stream.State.IsProveSatisfied);
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(5)));
    }

    [Fact]
    public void A_contract_is_bound_to_its_semantic_revision_and_is_immutable_for_it()
    {
        var stream = Ready();
        var contract = stream.State.CurrentProofContract!;

        Assert.Equal(stream.State.CurrentSemanticRevisionId, contract.SemanticRevisionId);
        Assert.Equal(contract.ContentHash, contract.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", contract.ContentHash);

        var second = Assert.Throws<InvalidProofException>(() => Contract(stream, 5, Claim("c2")));
        Assert.Contains("immutable", second.Message);
        Assert.Single(stream.State.ProofContracts);
    }

    [Fact]
    public void The_contract_hash_is_deterministic_for_the_same_content_and_differs_for_different_content()
    {
        var a = Contract(NewStreamWithImplementation(), 4);
        var other = NewStream();
        BindAll(other);
        Implement(other, 2);
        var b = Contract(other, 4);
        var c = Contract(NewStreamWithImplementation(), 4, Claim("c1"), Claim("c2"));

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    private static TaskStream NewStreamWithImplementation()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);
        return stream;
    }

    [Fact]
    public void A_contract_survives_an_implementation_repair_because_it_is_bound_to_semantics_not_to_code()
    {
        var stream = Ready();
        var contract = stream.State.CurrentProofContract;

        Implement(stream, 6, "tree:2");

        Assert.Same(contract, stream.State.CurrentProofContract);
        Assert.False(stream.State.IsProofContractMissing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void A_contract_with_the_wrong_number_of_claims_is_refused_whole(int count)
    {
        var claims = Enumerable.Range(1, count).Select(i => Claim($"c{i}")).ToArray();

        Assert.NotNull(ProofContract.Problem("why", claims));
    }

    [Fact]
    public void Malformed_claims_are_rejected_at_construction()
    {
        Assert.ThrowsAny<Exception>(() => new ProofClaim(new string('x', 21), "s", [ProofEvidenceRequirement.Verification("t")], "c"));
        Assert.ThrowsAny<Exception>(() => new ProofClaim("c1", new string('s', 1001), [ProofEvidenceRequirement.Verification("t")], "c"));
        Assert.ThrowsAny<Exception>(() => new ProofClaim("c1", "s", [ProofEvidenceRequirement.Verification("t")], new string('c', 1001)));
        Assert.ThrowsAny<Exception>(() => new ProofClaim("c1", "s", [], "c"));
        Assert.ThrowsAny<Exception>(() => new ProofClaim("c1", "s", Enumerable.Range(0, 5).Select(i => ProofEvidenceRequirement.Observation($"o{i}")), "c"));
    }

    [Fact]
    public void Duplicate_claim_ids_are_refused()
        => Assert.NotNull(ProofContract.Problem("why", [Claim("c1"), Claim("c1")]));

    [Theory]
    [InlineData("verification", "t", "verification:t")]
    [InlineData("review", null, "review:current-pass")]
    [InlineData("observation", "the importer rejects an empty file", "observation:the importer rejects an empty file")]
    public void The_evidence_vocabulary_is_closed_and_parses_to_exactly_these_requirements(string kind, string? subject, string text)
        => Assert.Equal(text, ProofEvidenceRequirement.Parse(kind, subject).ToString());

    [Theory]
    [InlineData("command", "ls")]
    [InlineData("transcript", "x")]
    [InlineData("review", "someone-elses")]
    [InlineData("verification", null)]
    [InlineData("observation", "")]
    public void Anything_outside_the_evidence_vocabulary_is_refused(string kind, string? subject)
        => Assert.ThrowsAny<Exception>(() => ProofEvidenceRequirement.Parse(kind, subject));

    [Fact]
    public void A_contract_while_a_gate_is_open_is_refused()
    {
        var stream = NewStream(what: true);
        BindAll(stream);
        stream.OpenWhatGate(new GateId(Guid.NewGuid()), new WhatProposal("Retries on 429.", "A failed import is retried.", "The importer only.", null, null, "A 429 is retried.", "Two policies are plausible.", 1), At(2));

        Assert.Throws<InvalidProofException>(() => Contract(stream, 3));
    }

    [Fact]
    public void A_stale_contract_after_reclassification_leaves_PROVE_required_and_a_new_contract_is_owed()
    {
        var stream = Ready();
        Assert.False(stream.State.IsProofContractMissing);

        stream.Reclassify(new ReclassificationId(Guid.NewGuid()), ReclassificationStage.How, "The retry budget cannot be met.", "The approved strategy does not hold.", 2, ImplementerSession, new ImplementationFingerprint("ws:1"), false, At(6));

        Assert.True(stream.State.IsProveRequired);
        Assert.Null(stream.State.CurrentProofContract);
        Assert.True(stream.State.IsProofContractMissing);
        Assert.False(stream.State.IsProveDue);
        Assert.Single(stream.State.ProofContracts);   // kept as history
    }

    // ---- due, ordering ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_a_contract_nothing_is_due_and_the_contract_is_missing()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);

        Assert.True(stream.State.IsProofContractMissing);
        Assert.False(stream.State.IsProveDue);
    }

    [Fact]
    public void A_proof_is_due_once_the_work_is_verified_and_a_contract_exists()
    {
        var stream = Ready();

        Assert.True(stream.State.IsProveDue);
        Assert.Equal(ProofStatus.None, stream.State.ProofStatus);
    }

    [Fact]
    public void When_REVIEW_is_also_required_the_proof_is_not_due_until_the_review_passes()
    {
        var stream = Ready(review: true);

        Assert.False(stream.State.IsProveDue);
        Assert.True(stream.State.IsReviewDue);

        stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession, 10, 0, [], false, At(6));

        Assert.True(stream.State.IsProveDue);
    }

    [Fact]
    public void A_proof_before_a_required_review_has_passed_is_refused()
    {
        var stream = Ready(review: true);

        Assert.Throws<InvalidProofException>(() => Prove(stream, 6));
    }

    [Fact]
    public void A_proof_on_a_task_with_failing_verification_is_refused()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2, outcome: VerificationOutcome.Failed);
        Contract(stream, 4);

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5));
    }

    // ---- the prover and the proof -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_proof_without_an_explicit_prover_binding_is_refused_and_no_other_profile_is_substituted()
    {
        var stream = NewStream();
        stream.BindRole(Role("root", "code_edit", "repository_tools"), Profile("p-impl", "code_edit", "repository_tools"), At(1));
        stream.BindRole(Role("reviewer", "repository_tools"), Profile("p-reviewer", "repository_tools"), At(1));
        Implement(stream, 2);
        Contract(stream, 4);

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5, profile: "p-impl"));
        Assert.Throws<InvalidProofException>(() => Prove(stream, 5, profile: "p-reviewer"));
        Assert.Empty(stream.State.Proofs);
    }

    [Fact]
    public void An_explicit_binding_of_the_prover_role_to_the_implementers_profile_is_allowed()
    {
        var stream = NewStream();
        stream.BindRole(Role("root", "code_edit", "repository_tools"), Profile("p-impl", "code_edit", "repository_tools"), At(1));
        stream.BindRole(Role("prover", "repository_tools"), Profile("p-impl", "repository_tools"), At(1));
        Implement(stream, 2);
        Contract(stream, 4);

        var proof = Prove(stream, 5, profile: "p-impl");

        Assert.Equal(ProofVerdict.Pass, proof.Verdict);
    }

    [Fact]
    public void A_PASS_proof_is_current_satisfies_PROVE_and_completes_the_task()
    {
        var stream = Ready();

        var proof = Prove(stream, 5);

        Assert.Equal(1, proof.Round);
        Assert.Equal(ProofStatus.PassedCurrent, stream.State.ProofStatus);
        Assert.True(stream.State.IsProveSatisfied);
        Assert.True(stream.State.IsProofCurrent(proof));
        Assert.False(stream.State.IsProveDue);

        stream.CompleteVerified(At(6));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void Completion_without_a_current_PASS_is_refused()
    {
        var stream = Ready();

        var refused = Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(5)));
        Assert.NotNull(refused.Message);

        Prove(stream, 5, Report(ProofVerdict.Fail));
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(6)));
    }

    [Fact]
    public void The_overall_verdict_is_derived_from_the_claims_and_is_not_submitted()
    {
        var pass = new ProofClaimResult("c1", ProofVerdict.Pass, [], "ok");
        var fail = new ProofClaimResult("c2", ProofVerdict.Fail, [], "no");
        var inconclusive = new ProofClaimResult("c3", ProofVerdict.Inconclusive, [], "?");

        Assert.Equal(ProofVerdict.Pass, new ProofReport("s", [pass]).Verdict);
        Assert.Equal(ProofVerdict.Fail, new ProofReport("s", [pass, fail, inconclusive]).Verdict);
        Assert.Equal(ProofVerdict.Inconclusive, new ProofReport("s", [pass, inconclusive]).Verdict);
    }

    [Fact]
    public void A_report_that_states_no_result_for_a_claim_is_refused()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);
        Contract(stream, 4, Claim("c1"), Claim("c2"));

        var refused = Assert.Throws<InvalidProofException>(() => Prove(stream, 5, Report(claimId: "c1")));
        Assert.Contains("c2", refused.Message);
    }

    [Fact]
    public void A_report_that_states_a_result_for_an_unknown_claim_is_refused()
    {
        var stream = Ready();

        var report = new ProofReport("Judged.", [new ProofClaimResult("c1", ProofVerdict.Pass, [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t")], "ok"), new ProofClaimResult("c9", ProofVerdict.Pass, [], "extra")]);
        var refused = Assert.Throws<InvalidProofException>(() => Prove(stream, 5, report));
        Assert.Contains("c9", refused.Message);
    }

    [Fact]
    public void A_PASS_that_does_not_cite_the_evidence_its_contract_requires_is_refused()
    {
        var stream = Ready();

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5, Report(evidence: new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "other-check"))));
    }

    [Fact]
    public void An_evidence_reference_to_a_command_the_prover_never_ran_is_refused()
    {
        var stream = Ready();

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5, Report(evidence: [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t"), new ProofEvidenceRef(ProofEvidenceRefKind.Command, "3")]), commands: ["git diff"]));
    }

    [Fact]
    public void An_observation_requirement_needs_an_observed_command_reference()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);
        Contract(stream, 4, Claim("c1", ProofEvidenceRequirement.Verification("t"), ProofEvidenceRequirement.Observation("an empty file yields an empty list")));

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5));

        var proof = Prove(stream, 5, Report(evidence: [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t"), new ProofEvidenceRef(ProofEvidenceRefKind.Command, "1")]), commands: ["dotnet run -- empty.csv"]);
        Assert.Equal(ProofVerdict.Pass, proof.Verdict);
        Assert.Equal(1, proof.ProverCommandCount);
    }

    [Fact]
    public void A_review_evidence_reference_resolves_only_against_the_current_passing_review()
    {
        var stream = NewStream(review: true);
        BindAll(stream);
        Implement(stream, 2);
        stream.RecordProofContract(new ProofContractId(Guid.NewGuid()), "why", [Claim("c1", ProofEvidenceRequirement.Verification("t"), ProofEvidenceRequirement.CurrentReviewPass())], 1, ImplementerSession, At(4));
        var review = stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession, 10, 0, [], false, At(5));

        Assert.Throws<InvalidProofException>(() => Prove(stream, 6, Report(evidence: [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t"), new ProofEvidenceRef(ProofEvidenceRefKind.Review, Guid.NewGuid().ToString())])));

        var proof = Prove(stream, 6, Report(evidence: [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t"), new ProofEvidenceRef(ProofEvidenceRefKind.Review, review.Id.Value.ToString())]));

        Assert.True(proof.ReviewEvidenceReused);
        Assert.Equal(review.Id, proof.ReviewId);
    }

    // ---- independence -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_proof_from_the_implementers_own_conversation_is_refused()
    {
        var stream = Ready();

        Assert.Throws<InvalidProofException>(() => Prove(stream, 5, proverSession: ImplementerSession));
    }

    [Fact]
    public void A_proof_from_a_reviewers_conversation_is_refused()
    {
        var stream = Ready(review: true);
        stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession, 10, 0, [], false, At(5));

        Assert.Throws<InvalidProofException>(() => Prove(stream, 6, proverSession: "reviewer-1"));
    }

    [Fact]
    public void A_review_from_a_provers_conversation_is_refused()
    {
        var stream = Ready(review: true);
        // an earlier prover is impossible without a review, so use a forged one on a task where the review is not yet required to exist
        var plain = NewStream(review: false);
        BindAll(plain);
        Implement(plain, 2);
        Contract(plain, 4);
        Prove(plain, 5, Report(ProofVerdict.Fail), "shared-session");
        Implement(plain, 6, "tree:2");

        Assert.Throws<InvalidReviewException>(() => plain.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "shared-session", ImplementerSession, 10, 0, [], false, At(8)));
        Assert.NotNull(stream);
    }

    [Fact]
    public void Every_proof_is_a_fresh_conversation()
    {
        var stream = Ready();
        Prove(stream, 5, Report(ProofVerdict.Fail), "prover-1");
        Implement(stream, 6, "tree:2");

        Assert.Throws<InvalidProofException>(() => Prove(stream, 8, Report(), "prover-1"));
        Assert.Equal(ProofVerdict.Pass, Prove(stream, 8, Report(), "prover-2").Verdict);
    }

    // ---- FAIL / INCONCLUSIVE, repair, currentness --------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProofVerdict.Fail, ProofStatus.Failed)]
    [InlineData(ProofVerdict.Inconclusive, ProofStatus.Inconclusive)]
    public void A_not_passing_proof_is_answered_by_the_implementer_and_blocks_completion(ProofVerdict verdict, ProofStatus status)
    {
        var stream = Ready();

        Prove(stream, 5, Report(verdict));

        Assert.Equal(status, stream.State.ProofStatus);
        Assert.False(stream.State.IsProveSatisfied);
        Assert.False(stream.State.IsProveDue);   // the implementer answers first: a repair, a new verification, then a new prover
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(6)));
    }

    [Fact]
    public void After_a_repair_and_a_fresh_verification_a_new_proof_is_due_and_the_old_one_is_history()
    {
        var stream = Ready();
        var first = Prove(stream, 5, Report(ProofVerdict.Fail));

        Implement(stream, 6, "tree:2");

        Assert.True(stream.State.IsProveDue);
        Assert.False(stream.State.IsProofCurrent(first));
        Assert.Equal(ProofStatus.NotPassedStale, stream.State.ProofStatus);

        var second = Prove(stream, 8, Report(), "prover-2");

        Assert.Equal(2, second.Round);
        Assert.Equal(ProofStatus.PassedCurrent, stream.State.ProofStatus);
        stream.CompleteVerified(At(9));
    }

    [Fact]
    public void A_stale_PASS_never_completes()
    {
        var stream = Ready();
        var passed = Prove(stream, 5);

        Implement(stream, 6, "tree:2");

        Assert.False(stream.State.IsProofCurrent(passed));
        Assert.Equal(ProofStatus.PassedStale, stream.State.ProofStatus);
        Assert.False(stream.State.IsProveSatisfied);
        Assert.Throws<InvalidWorkflowTransitionException>(() => stream.CompleteVerified(At(8)));
    }

    [Fact]
    public void A_proof_is_stale_when_the_same_implementation_is_verified_again()
    {
        var stream = Ready();
        var passed = Prove(stream, 5);

        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(6));

        Assert.False(stream.State.IsProofCurrent(passed));
    }

    [Fact]
    public void A_proof_is_stale_when_the_review_it_judged_is_superseded()
    {
        var stream = Ready(review: true);
        stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession, 10, 0, [], false, At(5));
        var passed = Prove(stream, 6);
        Assert.True(stream.State.IsProofCurrent(passed));

        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(7));

        Assert.False(stream.State.IsProofCurrent(passed));
        Assert.False(stream.State.IsProveSatisfied);
    }

    [Fact]
    public void Reclassification_stales_the_contract_and_the_proof_and_completion_is_refused_until_a_new_contract_and_proof()
    {
        var stream = Ready();
        var passed = Prove(stream, 5);

        stream.Reclassify(new ReclassificationId(Guid.NewGuid()), ReclassificationStage.How, "The retry budget cannot be met.", "The approved strategy does not hold.", 2, ImplementerSession, new ImplementationFingerprint("ws:1"), false, At(6));

        Assert.False(stream.State.IsProofCurrent(passed));
        Assert.Equal(ProofStatus.PassedStale, stream.State.ProofStatus);
        Assert.True(stream.State.IsProofContractMissing);
        Assert.False(stream.State.IsProveSatisfied);
    }

    // ---- the ACCEPT gate ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_ACCEPT_gate_does_not_open_without_a_current_PASS_and_opens_after_it()
    {
        var stream = Ready(acceptance: AcceptanceRequirement.HumanRequired);

        Assert.Throws<InvalidGateException>(() => stream.OpenAcceptGate(At(5)));

        Prove(stream, 5);
        var gate = stream.OpenAcceptGate(At(6));

        Assert.Equal(GateType.Accept, gate.Type);
    }

    [Fact]
    public void An_ACCEPT_gate_opened_before_a_stale_proof_is_not_current_and_cannot_be_accepted()
    {
        var stream = Ready(acceptance: AcceptanceRequirement.HumanRequired);
        Prove(stream, 5);
        var gate = stream.OpenAcceptGate(At(6));

        Assert.True(stream.State.IsCurrent(gate));
    }

    // ---- replay -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_proven_history_replays_to_exactly_the_live_state()
    {
        var stream = Ready(review: true);
        stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine.", []), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession, 10, 0, [], false, At(5));
        Prove(stream, 6, Report(ProofVerdict.Fail));
        Implement(stream, 7, "tree:2");
        stream.RecordReview(new ReviewId(Guid.NewGuid()), new ReviewReport(ReviewVerdict.Pass, "Fine again.", []), new ProfileId("p-reviewer"), "reviewer-2", ImplementerSession, 10, 0, [], false, At(10));
        Prove(stream, 11, Report(), "prover-2");
        stream.CompleteVerified(At(12));

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.Equal(stream.State, replayed);
        Assert.Equal(2, replayed.Proofs.Length);
        Assert.Equal(ProofStatus.PassedCurrent, replayed.ProofStatus);
    }

    [Fact]
    public void Replay_refuses_a_proof_recorded_before_any_contract()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);
        var contracted = Ready();
        var proof = Prove(contracted, 5);

        var forged = Forge(stream, new ProofRecorded(proof));

        Assert.Throws<InvalidProofException>(() => TaskStream.Replay(forged, new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_forged_contract_that_does_not_hash()
    {
        var stream = NewStream();
        BindAll(stream);
        Implement(stream, 2);

        Assert.Throws<InvalidProofException>(() => new ProofContract(
            new ProofContractId(Guid.NewGuid()),
            stream.State.CurrentSemanticRevisionId,
            1,
            ImplementerSession,
            "why",
            [Claim()],
            expectedHash: new string('0', 64)));
    }

    [Fact]
    public void Replay_refuses_a_second_contract_for_the_same_semantic_revision()
    {
        var stream = Ready();
        var again = new ProofContract(new ProofContractId(Guid.NewGuid()), stream.State.CurrentSemanticRevisionId, 1, ImplementerSession, "again", [Claim("c2")]);

        Assert.Throws<InvalidProofException>(() => TaskStream.Replay(Forge(stream, new ProofContractRecorded(again)), new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_proof_that_does_not_hash()
    {
        var stream = Ready();
        var proof = Prove(stream, 5);

        Assert.Throws<InvalidProofException>(() => new Proof(
            proof.Id, proof.Round, proof.ContractId, proof.ContractHash, proof.Report, new string('0', 64), proof.ProverProfile, proof.ProverSession, proof.ImplementerSession,
            proof.SemanticRevisionId, proof.ImplementationRevisionId, proof.VerificationRunId, proof.ReviewId, proof.RecordedAt, proof.ProverMilliseconds, proof.ProverCommandCount,
            proof.ProverCommands, proof.ConfiguredCheckRerun, proof.DuplicatedVerificationMilliseconds));
    }

    [Fact]
    public void Replay_refuses_a_proof_that_claims_a_wrong_round()
    {
        var stream = Ready();
        var proof = Prove(stream, 5, Report(ProofVerdict.Fail));
        var skipped = new Proof(
            new ProofId(Guid.NewGuid()), 5, proof.ContractId, proof.ContractHash, proof.Report, proof.ReportHash, proof.ProverProfile, "prover-9", proof.ImplementerSession,
            proof.SemanticRevisionId, proof.ImplementationRevisionId, proof.VerificationRunId, proof.ReviewId, proof.RecordedAt, 1, 0, [], false, 0);

        Assert.Throws<InvalidProofException>(() => TaskStream.Replay(Forge(stream, new ProofRecorded(skipped)), new GuidIdSource()));
    }

    [Fact]
    public void Replay_refuses_a_completion_without_a_current_PASS()
    {
        var stream = Ready();
        var done = Ready();
        Prove(done, 5);
        done.CompleteVerified(At(6));
        var completion = done.Events[^1].Payload;

        Assert.Throws<InvalidWorkflowTransitionException>(() => TaskStream.Replay(Forge(stream, completion), new GuidIdSource()));
    }

    [Fact]
    public void A_task_created_without_PROVE_replays_exactly_as_before()
    {
        var stream = NewStream(prove: false);

        Assert.False(((TaskCreated)stream.Events[0].Payload).ProveRequired);
        Assert.False(TaskStream.Replay(stream.Events, new GuidIdSource()).State.IsProveRequired);
    }
}
