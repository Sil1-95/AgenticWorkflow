using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The structured read model of a task. Each state is built through the real service (so the events are the real ones), then projected; the model is compared with the
/// predicates the controller itself asks, so it can never describe a state the controller would not.
/// </summary>
public sealed class TaskReadModelTests
{
    private static readonly ImplementationFingerprint FingerprintA = new("fp-a");
    private static readonly ImplementationFingerprint FingerprintB = new("fp-b");

    private static VerificationCheckResult Pass() => new(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail() => new(new VerificationCheckId("tests"), VerificationOutcome.Failed, "broken", 1);

    private static WhatProposal What() => new("Rename GetMeal() to GetFood().", "GetFood() behaves as GetMeal() did.", "src/ and tests/.", null, null, "The tests pass.", "Two readings are plausible.", 1);

    private static HowProposal How() => new("Rename with the IDE refactoring.", "Meal.cs and its callers.", null, null, "A reflection caller would be missed.", null, "A strategy is worth a decision.", 1);

    private static TaskReadModel Project(DirectHarness h) => TaskReadModelProjector.Project(h.Snapshot.Task);

    private static async Task Do(DirectHarness h, Func<TaskSnapshot, Task<TaskChange>> step) => h.Snapshot = (await step(h.Snapshot)).Snapshot;

    private static Task Implement(DirectHarness h, ImplementationFingerprint? fingerprint = null) => Do(h, s => h.Service.RecordImplementationRevisionAsync(s, fingerprint ?? FingerprintA));

    private static Task Verify(DirectHarness h, bool passed = true) => Do(h, s => h.Service.RecordLightweightVerificationAsync(s, [passed ? Pass() : Fail()]));

    private static async Task<DirectHarness> VerifiedAsync(bool whatRequired = false, bool howRequired = false, bool reviewRequired = false, string? reviewer = null, bool proveRequired = false, string? prover = null, AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance, whatRequired: whatRequired, howRequired: howRequired, reviewRequired: reviewRequired, reviewerProfile: reviewer, proveRequired: proveRequired, proverProfile: prover);
        await Approve(h, GateType.What, whatRequired);
        await Approve(h, GateType.How, howRequired);
        await Implement(h);
        await Verify(h);
        return h;
    }

    private static async Task Approve(DirectHarness h, GateType type, bool required)
    {
        if (!required)
            return;

        var id = h.Service.NewGateId();

        if (type == GateType.What)
        {
            await Do(h, s => h.Service.OpenWhatGateAsync(s, id, What()));
            await Do(h, s => h.Service.ApproveWhatAsync(s, id));
        }
        else
        {
            await Do(h, s => h.Service.OpenHowGateAsync(s, id, How()));
            await Do(h, s => h.Service.ApproveHowAsync(s, id));
        }
    }

    private static ReviewReport Passing() => new(ReviewVerdict.Pass, "Nothing blocks.", []);

    private static ReviewReport Failing() => new(ReviewVerdict.ChangesRequired, "One defect.", [new ReviewFinding("Null caller", "Meal.cs:12 dereferences null.", "Crash at startup.", "Guard the call.", "Meal.cs:12")]);

    private static Task Review(DirectHarness h, ReviewReport report) => Do(h, s => h.Service.RecordReviewAsync(s, report, new ProfileId("reviewer-profile"), "reviewer-session", "implementer-session", 900, ["git diff"], 1, false));

    private static ProofClaim Claim() => new("c1", "GetMeal no longer exists.", [ProofEvidenceRequirement.Verification("tests")], "No GetMeal reference remains.");

    private static Task Contract(DirectHarness h) => Do(h, s => h.Service.RecordProofContractAsync(s, "These claims matter.", [Claim()], 1, "implementer-session"));

    private static Task Prove(DirectHarness h, ProofVerdict verdict)
        => Do(h, s => h.Service.RecordProofAsync(
            s,
            new ProofReport("Judged.", [new ProofClaimResult("c1", verdict, [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "tests")], "Because.")]),
            new ProfileId("prover-profile"),
            "prover-session",
            "implementer-session",
            1200,
            ["git grep GetMeal"],
            1,
            false,
            0));

    // ---- stages -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_running_task_with_no_work_owes_the_first_implementation()
    {
        var h = await DirectHarness.CreateAsync();

        var model = Project(h);

        Assert.Equal(TaskReadModel.CurrentSchemaVersion, model.SchemaVersion);
        Assert.Equal(Fixture.TaskOne.Value, model.TaskId);
        Assert.Equal(WorkflowState.Running, model.State);
        Assert.Equal((WorkflowStage.Implement, StageReason.InitialImplementation), (model.Stage, model.StageReason));
        Assert.Equal(EvidenceCurrency.None, model.Implementation.Currency);
        Assert.Equal(EvidenceCurrency.None, model.Verification.Currency);
        Assert.Null(model.PendingRequest);
        Assert.Null(model.Block);
        Assert.Empty(model.Gates);
        Assert.Equal(1, model.SemanticRevision.Ordinal);
        Assert.Equal(h.Snapshot.Task.LastSequence, model.StreamVersion);
        Assert.Equal("root", Assert.Single(model.Bindings).Role);
    }

    [Fact]
    public async Task An_implementation_without_verification_awaits_verification_and_a_failed_one_goes_back_to_implementation()
    {
        var h = await DirectHarness.CreateAsync();
        await Implement(h);

        var awaiting = Project(h);
        Assert.Equal((WorkflowStage.Verify, StageReason.AwaitingVerification), (awaiting.Stage, awaiting.StageReason));
        Assert.Equal(EvidenceCurrency.Current, awaiting.Implementation.Currency);

        await Verify(h, passed: false);

        var failed = Project(h);
        Assert.Equal((WorkflowStage.Implement, StageReason.VerificationFailed), (failed.Stage, failed.StageReason));
        Assert.Equal(VerificationOutcome.Failed, failed.Verification.Outcome);
        Assert.Equal(EvidenceCurrency.Current, failed.Verification.Currency);
    }

    [Fact]
    public async Task A_clarify_gate_is_a_pending_answer_with_only_answer_and_abort()
    {
        var h = await DirectHarness.CreateAsync();
        var id = h.Service.NewGateId();
        await Do(h, s => h.Service.OpenClarifyGateAsync(s, id, new ClarificationRequest("Which GetMeal?", "Two classes define it.", 1)));

        var model = Project(h);

        Assert.Equal(WorkflowState.AwaitingHuman, model.State);
        Assert.Equal((WorkflowStage.Clarify, StageReason.AwaitingHumanAnswer), (model.Stage, model.StageReason));
        var request = Assert.IsType<HumanRequestView>(model.PendingRequest);
        Assert.Equal(HumanRequestKind.ClarifyAnswer, request.Kind);
        Assert.Equal(id.Value, request.GateId);
        Assert.Equal([HumanAction.Answer, HumanAction.Abort], request.AllowedActions);
        var gate = Assert.Single(model.Gates);
        Assert.Equal("Which GetMeal?", gate.Clarification!.Question);
        Assert.Null(gate.What);
        Assert.Null(gate.Decision);
    }

    [Fact]
    public async Task A_what_gate_holds_the_proposal_and_offers_approve_request_changes_and_abort()
    {
        var h = await DirectHarness.CreateAsync(whatRequired: true);
        var id = h.Service.NewGateId();
        await Do(h, s => h.Service.OpenWhatGateAsync(s, id, What()));

        var model = Project(h);

        Assert.Equal((WorkflowStage.What, StageReason.AwaitingHumanApproval), (model.Stage, model.StageReason));
        Assert.Equal(RequirementOrigin.Human, model.Requirements.What.Origin);
        Assert.False(model.Requirements.What.Satisfied);
        Assert.Equal([HumanAction.Approve, HumanAction.RequestChanges, HumanAction.Abort], model.PendingRequest!.AllowedActions);
        var gate = Assert.Single(model.Gates);
        Assert.Equal(GateType.What, gate.Type);
        Assert.Equal(h.Snapshot.Task.PendingGate!.What!.ContentHash, gate.What!.ContentHash);
        Assert.True(gate.IsCurrent);
        Assert.Empty(gate.StaleReasons);
    }

    [Fact]
    public async Task An_approved_what_moves_the_stage_to_how_and_then_to_the_implementation()
    {
        var h = await DirectHarness.CreateAsync(whatRequired: true, howRequired: true);
        await Approve(h, GateType.What, true);

        Assert.Equal((WorkflowStage.How, StageReason.ProposalNotYetMade), (Project(h).Stage, Project(h).StageReason));

        var id = h.Service.NewGateId();
        await Do(h, s => h.Service.OpenHowGateAsync(s, id, How()));

        var how = Project(h);
        Assert.Equal((WorkflowStage.How, StageReason.AwaitingHumanApproval), (how.Stage, how.StageReason));
        Assert.Equal(HumanRequestKind.HowApproval, how.PendingRequest!.Kind);
        Assert.Equal("Rename with the IDE refactoring.", how.Gates.Last().How!.Strategy);

        await Do(h, s => h.Service.ApproveHowAsync(s, id));

        var approved = Project(h);
        Assert.Equal(WorkflowStage.Implement, approved.Stage);
        Assert.Null(approved.PendingRequest);
        Assert.All(approved.Gates, g => Assert.Equal(GateAction.Approve, g.Decision!.Action));
    }

    [Fact]
    public async Task A_reclassification_invalidates_the_approved_how_and_the_model_says_why()
    {
        var h = await DirectHarness.CreateAsync(whatRequired: true, howRequired: true);
        await Approve(h, GateType.What, true);
        await Approve(h, GateType.How, true);
        await Implement(h);
        var reclassification = h.Service.NewReclassificationId();
        await Do(h, s => h.Service.ReclassifyAsync(s, reclassification, ReclassificationStage.What, "The API is also used by plugins.", "Scope is wider than approved.", 2, "session-1", FingerprintA, provisionalWork: true));

        var model = Project(h);

        Assert.Equal(WorkflowState.Running, model.State);
        Assert.Equal((WorkflowStage.What, StageReason.ApprovalInvalidated), (model.Stage, model.StageReason));
        Assert.False(model.Requirements.What.Satisfied);
        Assert.Equal(4, model.SemanticRevision.Ordinal);
        Assert.Equal(EvidenceCurrency.Stale, model.Implementation.Currency);
        Assert.True(model.Implementation.HasProvisionalWork);

        var notice = Assert.Single(model.Reclassifications);
        Assert.Equal(reclassification.Value, notice.Id);
        Assert.True(notice.IsActive);
        Assert.Equal((3, 4), (notice.PriorSemanticOrdinal, notice.SemanticOrdinal));
        Assert.Equal(2, notice.InvalidatedGateIds.Count);

        var how = model.Gates.Single(g => g.Type == GateType.How);
        Assert.Equal(GateStatus.Invalidated, how.Status);
        Assert.False(how.IsCurrent);
        Assert.Contains(GateStaleReason.Invalidated, how.StaleReasons);
        Assert.Equal(reclassification.Value, how.Invalidation!.ReclassificationId);
        Assert.Equal(h.Snapshot.Task.Gates.Single(g => g.Type == GateType.How).SemanticRevisionId.Value, how.Binding.SemanticRevisionId);
        Assert.True(how.Binding.SemanticOrdinal < model.SemanticRevision.Ordinal);
    }

    [Fact]
    public async Task Review_is_due_after_a_passing_verification_and_findings_send_the_task_back_to_implementation()
    {
        var h = await VerifiedAsync(reviewRequired: true, reviewer: "reviewer-profile");

        var due = Project(h);
        Assert.Equal((WorkflowStage.Review, StageReason.ReviewDue), (due.Stage, due.StageReason));
        Assert.True(due.Review.IsDue);
        Assert.Equal("reviewer-profile", due.Review.ReviewerProfile);
        Assert.Equal(RequirementOrigin.Human, due.Requirements.Review.Origin);

        await Review(h, Failing());

        var findings = Project(h);
        Assert.Equal((WorkflowStage.Implement, StageReason.ReviewFindingsOwed), (findings.Stage, findings.StageReason));
        var round = Assert.Single(findings.Review.Rounds);
        Assert.Equal(ReviewVerdict.ChangesRequired, round.Verdict);
        Assert.Equal("Null caller", Assert.Single(round.Findings).Title);
        Assert.True(round.IsCurrent);
        Assert.Equal(round.Id, findings.Review.CurrentReviewId);
        Assert.Equal("reviewer-session", round.ReviewerSession);
    }

    [Fact]
    public async Task Prove_owes_a_contract_first_then_is_due_and_a_passing_proof_opens_the_acceptance()
    {
        var h = await VerifiedAsync(proveRequired: true, prover: "prover-profile");

        var owed = Project(h);
        Assert.Equal((WorkflowStage.Prove, StageReason.ProofContractOwed), (owed.Stage, owed.StageReason));
        Assert.True(owed.Proof.IsContractMissing);

        await Contract(h);

        var due = Project(h);
        Assert.Equal((WorkflowStage.Prove, StageReason.ProveDue), (due.Stage, due.StageReason));
        var contract = Assert.Single(due.Proof.Contracts);
        Assert.True(contract.IsCurrent);
        Assert.Equal(ProofEvidenceKind.Verification, Assert.Single(Assert.Single(contract.Claims).Evidence).Kind);

        await Prove(h, ProofVerdict.Pass);

        var proved = Project(h);
        Assert.Equal((WorkflowStage.Accept, StageReason.AcceptGateNotYetOpened), (proved.Stage, proved.StageReason));
        Assert.True(proved.Proof.IsSatisfied);
        var round = Assert.Single(proved.Proof.Rounds);
        Assert.Equal(ProofVerdict.Pass, round.Verdict);
        Assert.Equal(contract.Id, round.ContractId);
        Assert.Equal(round.Id, proved.Proof.CurrentProofId);
    }

    [Fact]
    public async Task An_open_accept_gate_is_the_pending_request_and_binds_the_verified_implementation()
    {
        var h = await VerifiedAsync();
        await Do(h, s => h.Service.OpenAcceptGateAsync(s));

        var model = Project(h);

        Assert.Equal((WorkflowStage.Accept, StageReason.AwaitingHumanAcceptance), (model.Stage, model.StageReason));
        Assert.Equal(HumanRequestKind.AcceptDecision, model.PendingRequest!.Kind);
        Assert.Equal([HumanAction.Accept, HumanAction.RequestChanges, HumanAction.Abort], model.PendingRequest.AllowedActions);
        Assert.True(model.PendingRequest.IsCurrent);
        Assert.Equal(AcceptState.AwaitingDecision, model.Completion.Accept);
        Assert.True(model.Completion.IsEligible);
        Assert.Empty(model.Completion.BlockingReasons);

        var gate = Assert.Single(model.Gates);
        Assert.Equal(model.Implementation.Latest!.Id, gate.Binding.ImplementationRevisionId);
        Assert.Equal(model.Verification.CurrentRunId, gate.Binding.VerificationRunId);
        Assert.Equal(model.Completion.AcceptGateId, gate.Id);
    }

    [Fact]
    public async Task Requesting_changes_creates_a_revision_and_the_closed_gate_is_history_the_new_work_is_owed()
    {
        var h = await VerifiedAsync();
        await Do(h, s => h.Service.OpenAcceptGateAsync(s));
        var gateId = h.Snapshot.Task.PendingGate!.Id;
        await Do(h, s => h.Service.RequestChangesAsync(s, gateId, "Also rename the doc comments."));

        var model = Project(h);

        Assert.Equal(WorkflowState.Running, model.State);
        Assert.Equal(2, model.SemanticRevision.Ordinal);
        Assert.Equal(2, model.SemanticRevisions.Count);
        Assert.Equal((WorkflowStage.Implement, StageReason.HumanInputBeingActedOn), (model.Stage, model.StageReason));
        Assert.Null(model.PendingRequest);
        Assert.Equal(EvidenceCurrency.Stale, model.Implementation.Currency);
        Assert.False(model.Implementation.Latest!.TargetsCurrentSemantics);

        var gate = Assert.Single(model.Gates);
        Assert.Equal(GateAction.RequestChanges, gate.Decision!.Action);
        Assert.Equal("Also rename the doc comments.", gate.Decision.Feedback);
        Assert.False(gate.IsCurrent);
        Assert.Contains(GateStaleReason.SemanticRevisionSuperseded, gate.StaleReasons);
    }

    [Fact]
    public async Task A_new_implementation_after_a_passing_verification_makes_the_verification_not_current_and_the_gate_stale()
    {
        var h = await VerifiedAsync();
        await Implement(h, FingerprintB);

        var model = Project(h);

        Assert.Equal((WorkflowStage.Verify, StageReason.AwaitingVerification), (model.Stage, model.StageReason));
        Assert.Equal(h.Snapshot.Task.IsVerificationStale, model.Verification.Currency == EvidenceCurrency.Stale);
        Assert.Equal(2, model.Implementation.Revisions.Count);
        Assert.Equal(FingerprintB.Value, model.Implementation.Latest!.Fingerprint);
        Assert.Equal(FingerprintA.Value, model.Implementation.Revisions[0].Fingerprint);
        Assert.False(model.Implementation.Revisions[0].IsLatest);
    }

    [Fact]
    public async Task A_blocked_task_names_the_kind_and_the_recovery_actions()
    {
        var h = await DirectHarness.CreateAsync();
        await Do(h, s => h.Service.ChangeWorkflowStateAsync(s, WorkflowState.Blocked, RuntimeFailureBlock.BlockedReason("implementation", "the agent process crashed", sessionConfirmedStopped: true)));

        var model = Project(h);

        Assert.Equal(WorkflowState.Blocked, model.State);
        Assert.Equal(BlockKind.RuntimeFailure, model.Block!.Kind);
        Assert.Equal(h.Snapshot.Task.StateReason, model.Block.Reason);
        Assert.Equal([RecoveryAction.Abort], model.Recovery!.Actions);
        Assert.Null(model.Recovery.PendingWork);
        Assert.Null(model.PendingRequest);
    }

    [Fact]
    public async Task A_blocked_task_with_an_unclassified_reason_is_other()
    {
        var h = await DirectHarness.CreateAsync();
        await Do(h, s => h.Service.ChangeWorkflowStateAsync(s, WorkflowState.Blocked, "Waiting for a person to fix the machine."));

        Assert.Equal(BlockKind.Other, Project(h).Block!.Kind);
    }

    [Fact]
    public async Task A_completed_task_is_terminal_with_nothing_pending()
    {
        var h = await VerifiedAsync();
        await Do(h, s => h.Service.OpenAcceptGateAsync(s));
        await Do(h, s => h.Service.AcceptGateAsync(s, h.Snapshot.Task.PendingGate!.Id));

        var model = Project(h);

        Assert.Equal(WorkflowState.Completed, model.State);
        Assert.Equal((WorkflowStage.None, StageReason.Terminal), (model.Stage, model.StageReason));
        Assert.Equal(AcceptState.Accepted, model.Completion.Accept);
        Assert.Null(model.Completion.IsEligible);
        Assert.Null(model.PendingRequest);
        Assert.Null(model.Block);
        Assert.Null(model.Recovery);
        Assert.Equal(GateAction.Accept, Assert.Single(model.Gates).Decision!.Action);
    }

    [Fact]
    public async Task A_task_that_needs_no_acceptance_completes_without_a_gate()
    {
        var h = await VerifiedAsync(acceptance: AcceptanceRequirement.NotRequired);

        var eligible = Project(h);
        Assert.Equal((WorkflowStage.Completion, StageReason.EligibleToComplete), (eligible.Stage, eligible.StageReason));
        Assert.Equal(AcceptState.NotApplicable, eligible.Completion.Accept);

        await Do(h, s => h.Service.CompleteVerifiedAsync(s));

        Assert.Equal(WorkflowState.Completed, Project(h).State);
    }

    // ---- agreement with the controller's own predicates -------------------------------------------------------------

    [Fact]
    public async Task Every_gate_is_current_exactly_when_it_has_no_stale_reason()
    {
        var h = await VerifiedAsync(whatRequired: true, howRequired: true, reviewRequired: true, reviewer: "reviewer-profile");
        await Review(h, Passing());
        await Do(h, s => h.Service.OpenAcceptGateAsync(s));
        var gateId = h.Snapshot.Task.PendingGate!.Id;
        await Do(h, s => h.Service.RequestChangesAsync(s, gateId, "Again."));

        var task = h.Snapshot.Task;
        var model = Project(h);

        Assert.Equal(task.Gates.Length, model.Gates.Count);

        foreach (var (gate, view) in task.Gates.Zip(model.Gates))
        {
            Assert.Equal(task.IsCurrent(gate), view.IsCurrent);
            Assert.Equal(view.IsCurrent, view.StaleReasons.Count == 0);
            Assert.Equal(task.StatusOf(gate), view.Status);
        }
    }

    [Fact]
    public async Task The_model_agrees_with_the_completion_rule_and_the_predicates_in_each_state()
    {
        var h = await VerifiedAsync(reviewRequired: true, reviewer: "reviewer-profile");

        foreach (var step in new Func<Task>[] { () => Task.CompletedTask, () => Review(h, Passing()), () => Do(h, s => h.Service.OpenAcceptGateAsync(s)) })
        {
            await step();

            var task = h.Snapshot.Task;
            var model = Project(h);

            Assert.Equal(task.WorkflowState, model.State);
            Assert.Equal(task.StateReason, model.StateReason);
            Assert.Equal(task.IsReviewDue, model.Review.IsDue);
            Assert.Equal(task.IsReviewSatisfied, model.Review.IsSatisfied);
            Assert.Equal(task.ReviewStatus, model.Review.Status);
            Assert.Equal(task.IsProveSatisfied, model.Proof.IsSatisfied);
            Assert.Equal(task.MayImplement, model.Requirements.What.Satisfied && model.Requirements.How.Satisfied);

            var eligibility = DirectPathCompletion.Evaluate(task);
            Assert.Equal(eligibility.IsEligible, model.Completion.IsEligible);
            Assert.Equal(eligibility.Reasons, model.Completion.BlockingReasons);
        }
    }

    [Fact]
    public async Task Projection_is_deterministic_and_bindings_are_sorted_by_role()
    {
        var h = await VerifiedAsync(reviewRequired: true, reviewer: "reviewer-profile", proveRequired: true, prover: "prover-profile");

        var first = TaskReadModelJson.Serialize(Project(h));
        var second = TaskReadModelJson.Serialize(Project(h));

        Assert.Equal(first, second);
        Assert.Equal(["prover", "reviewer", "root"], Project(h).Bindings.Select(b => b.Role));
        Assert.All(Project(h).Bindings, b => Assert.Null(b.Configuration));
    }

    // ---- execution configuration and missing bindings --------------------------------------------------------------

    [Fact]
    public async Task A_binding_with_a_recorded_execution_configuration_shows_harness_model_reasoning_and_fingerprint()
    {
        var h = await DirectHarness.CreateAsync(bindRoot: false);
        var execution = new ExecutionConfiguration("claude-code", "opus", "high", "fp-1");
        await Do(h, s => h.Service.BindRoleAsync(s, DirectPathRoles.Root, Fixture.Profile("claude-main", "code_edit", "repository_tools"), CancellationToken.None, execution));

        var binding = Assert.Single(Project(h).Bindings);

        Assert.Equal("root", binding.Role);
        Assert.Equal("claude-main", binding.Profile.Id);
        Assert.Equal(new ExecutionConfigurationView { Harness = "claude-code", Model = "opus", Reasoning = "high", Fingerprint = "fp-1" }, binding.Configuration);
    }

    [Fact]
    public async Task A_binding_recorded_without_an_execution_configuration_replays_with_null_configuration()
    {
        var h = await DirectHarness.CreateAsync();

        var binding = Assert.Single(Project(h).Bindings);

        Assert.Null(binding.Configuration);

        using var document = System.Text.Json.JsonDocument.Parse(TaskReadModelJson.Serialize(Project(h)));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("bindings")[0].GetProperty("configuration").ValueKind);
    }

    [Fact]
    public async Task A_required_review_with_no_reviewer_binding_is_reported_as_a_missing_binding_and_nothing_is_chosen()
    {
        var h = await VerifiedAsync(reviewRequired: true, reviewer: null);

        var missing = Assert.Single(Project(h).MissingBindings);

        Assert.Equal("reviewer", missing.Role);
        Assert.Equal("review_required", missing.Reason);
        Assert.Equal(["repository_tools"], missing.RequiredCapabilities);
        Assert.DoesNotContain(Project(h).Bindings, b => b.Role == "reviewer");
    }

    [Fact]
    public async Task A_required_proof_with_no_prover_binding_is_reported_and_a_bound_role_is_not()
    {
        var h = await VerifiedAsync(proveRequired: true, prover: null, reviewRequired: true, reviewer: "reviewer-profile");

        var missing = Project(h).MissingBindings;

        Assert.Equal(["prover"], missing.Select(m => m.Role));
        Assert.Equal("prove_required", Assert.Single(missing).Reason);
    }

    [Fact]
    public async Task A_task_whose_roles_are_all_bound_or_not_required_has_no_missing_bindings()
    {
        var h = await VerifiedAsync();

        Assert.Empty(Project(h).MissingBindings);
    }

    [Fact]
    public async Task A_task_without_a_root_binding_reports_the_root_as_missing()
    {
        var h = await DirectHarness.CreateAsync(bindRoot: false);

        var missing = Assert.Single(Project(h).MissingBindings);

        Assert.Equal(("root", "work_requires_root"), (missing.Role, missing.Reason));
    }

    // ---- JSON -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_json_has_the_documented_shape_and_round_trips()
    {
        var h = await VerifiedAsync(whatRequired: true);
        await Do(h, s => h.Service.OpenAcceptGateAsync(s));

        var json = TaskReadModelJson.Serialize(Project(h));

        Assert.EndsWith("\n", json);
        Assert.DoesNotContain("\r", json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("awaiting_human", root.GetProperty("state").GetString());
        Assert.Equal("accept", root.GetProperty("stage").GetString());
        Assert.Equal("awaiting_human_acceptance", root.GetProperty("stageReason").GetString());
        Assert.Equal(Fixture.TaskOne.Value.ToString("D"), root.GetProperty("taskId").GetString());
        Assert.Equal("accept_decision", root.GetProperty("pendingRequest").GetProperty("kind").GetString());
        Assert.Equal("approve", root.GetProperty("gates")[0].GetProperty("decision").GetProperty("action").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("block").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("operational").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("review").GetProperty("rounds").ValueKind);

        var back = TaskReadModelJson.Deserialize(json);
        Assert.Equal(TaskReadModelJson.Serialize(back), json);
    }

    [Fact]
    public void A_failure_document_carries_the_schema_version_and_a_stable_code()
    {
        var json = TaskReadModelJson.Serialize(new TaskReadFailure(TaskReadFailureCodes.NotFound, "No run 'x'."));

        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("No run 'x'.", document.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("state", out _));
    }
}
