using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M7 at the controller: after the controller's own verification passes, a fresh reviewer conversation judges the verified implementation and submits one structured
/// report; PASS lets the task complete (or open ACCEPT), CHANGES_REQUIRED resumes the original implementer with the findings only and is followed by a NEW fresh reviewer.
/// The reviewer is read-only, never the implementer, never silently the implementer's profile, and never receives a transcript.
/// </summary>
public sealed class ReviewRunnerTests
{
    private const string ReviewerProfileName = "claude-reviewer";

    private static AgentReviewSubmission Pass(string summary = "The rename is complete and correct.", string? notes = null)
        => new("PASS", summary, [], notes);

    private static AgentReviewSubmission Changes(string title = "The forwarding alias is missing", string? location = "src/Greeting.cs:1")
        => new("CHANGES_REQUIRED", "One real defect.", [new AgentReviewFinding(title, "Callers of GetMeal() no longer compile.", "Downstream builds break.", "Keep a forwarding GetMeal().", location)], null);

    private sealed class Rig
    {
        public required DirectHarness H { get; init; }

        public required FakeConversationRuntime Conversations { get; init; }

        public required InMemoryConversationStore Store { get; init; }

        public FakeSignals Signals { get; } = new();

        public FakeBaselineSink Baselines { get; } = new();

        public DirectPathRunner Runner()
            => new(H.Service, H.Runtime, H.Workspace, H.Verifier, options: null, evidence: null, new AgentConversationSupport(Conversations, Store), new AgentClarificationSupport(Signals, Baselines));

        public Task<DirectRunResult> RunAsync(int maxTurns = 4, Profile? reviewer = null)
            => Runner().RunAsync(Fixture.TaskOne, Definition(maxTurns, reviewer));

        public DirectTaskDefinition Definition(int maxTurns = 4, Profile? reviewer = null)
            => H.Definition(maxTurns) with { ReviewerProfile = reviewer };

        public async Task<WorkflowTask> LoadAsync() => (await H.Service.LoadAsync(Fixture.TaskOne)).Task;

        public int Events<T>() where T : DomainEvent => H.Store.History(Fixture.TaskOne).Count(e => e is { } && e.Payload is T);

        public IEnumerable<AgentSessionRequest> ReviewerStarts => H.Runtime.Starts.Where(s => s.Purpose == AgentSessionPurpose.Review);

        public IEnumerable<AgentSessionRequest> ImplementerStarts => H.Runtime.Starts.Where(s => s.Purpose != AgentSessionPurpose.Review);

        public IEnumerable<string> PromptsOf(AgentSessionId session) => H.Runtime.Sends.Where(s => s.Session == session).Select(s => s.Prompt);

        public string ReviewerPrompt(int round) => H.Runtime.Sends.Where(s => s.Prompt.StartsWith("You are the independent REVIEWER", StringComparison.Ordinal)).ElementAt(round - 1).Prompt;
    }

    private static async Task<Rig> NewRigAsync(
        AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired,
        bool review = true,
        string? reviewerProfile = ReviewerProfileName,
        bool howRequired = false)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance, howRequired: howRequired, reviewRequired: review, reviewerProfile: reviewerProfile);
        var rig = new Rig { H = h, Conversations = new FakeConversationRuntime(h.Runtime), Store = new InMemoryConversationStore() };
        rig.Baselines.GatesNow = () => rig.H.Store.History(Fixture.TaskOne).Count(e => e.Payload is GateOpened);
        return rig;
    }

    // ---- sequencing and completion -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_passing_review_of_the_verified_implementation_completes_the_task_without_rerunning_the_checks()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        var review = Assert.Single(task.Reviews);
        Assert.Equal(ReviewVerdict.Pass, review.Verdict);
        Assert.Equal(ReviewStatus.PassedCurrent, task.ReviewStatus);
        Assert.Equal(task.CurrentVerification!.Id, review.VerificationRunId);
        Assert.Equal(task.CurrentImplementationRevisionId, review.ImplementationRevisionId);
        Assert.Equal(1, rig.Events<ReviewRecorded>());

        // The controller verified once, before the review; the reviewer cost no second full run.
        Assert.Single(rig.H.Verifier.Ran);
        Assert.False(review.ConfiguredCheckRerun);
    }

    [Fact]
    public async Task Verification_runs_before_the_review_and_a_failing_verification_is_repaired_before_any_reviewer_starts()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing(), rig.H.Renames(), DirectHarness.DoesNothing());   // turn 1: nothing done (verification fails), turn 2: renamed
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Equal(2, rig.H.Verifier.Ran.Count);              // fail, then pass: the reviewer only ever saw the passing one
        Assert.Single(rig.ReviewerStarts);
        var implementer = Assert.Single(rig.ImplementerStarts);
        var order = rig.H.Runtime.Sends.Select(s => s.Session).ToList();
        Assert.Equal(implementer.Profile, new ProfileId(DirectHarness.ProfileName));
        Assert.Equal([new AgentSessionId("session-1"), new AgentSessionId("session-1"), new AgentSessionId("session-2")], order);   // implementer twice, then the reviewer
        Assert.Single((await rig.LoadAsync()).Reviews);
    }

    [Fact]
    public async Task With_human_acceptance_a_passing_review_opens_the_ACCEPT_gate_and_is_not_the_acceptance()
    {
        var rig = await NewRigAsync(acceptance: AcceptanceRequirement.HumanRequired);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Accept, task.PendingGate!.Type);
        Assert.True(task.HasCurrentReviewPass);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_task_that_does_not_require_a_review_runs_exactly_as_before_with_one_session_and_no_reviewer()
    {
        var rig = await NewRigAsync(review: false, reviewerProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Empty(rig.ReviewerStarts);
        Assert.Empty((await rig.LoadAsync()).Reviews);
        Assert.Equal(0, rig.Events<ReviewRecorded>());
        Assert.Empty(rig.Signals.ReviewCalls);
    }

    // ---- reviewer binding -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_reviewer_binding_the_run_stops_unreviewed_and_the_implementers_profile_is_never_used()
    {
        var rig = await NewRigAsync(reviewerProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ReviewerUnbound, result.Status);
        Assert.Contains("reviewer_unbound", result.Reason);
        Assert.Contains("bind", result.Reason, StringComparison.OrdinalIgnoreCase);   // the way out is stated: bind the role explicitly, then continue
        Assert.Single(rig.H.Runtime.Starts);                                          // no reviewer was started, least of all with the implementer's profile
        var task = await rig.LoadAsync();
        Assert.False(task.RoleBindings.ContainsKey(WellKnownRoles.Reviewer));
        Assert.Empty(task.Reviews);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.True(task.IsReviewRequired);   // the requirement is still recorded
    }

    [Fact]
    public async Task The_projects_configured_reviewer_profile_is_bound_when_the_review_is_first_needed()
    {
        var rig = await NewRigAsync(reviewerProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync(reviewer: Fixture.Profile(ReviewerProfileName, "repository_tools"));

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var binding = (await rig.LoadAsync()).RoleBindings[WellKnownRoles.Reviewer];
        Assert.Equal(new ProfileId(ReviewerProfileName), binding.Profile.Id);
        Assert.Equal(new ProfileId(ReviewerProfileName), Assert.Single(rig.ReviewerStarts).Profile);
    }

    [Fact]
    public async Task An_explicit_binding_to_the_implementers_own_profile_is_a_separate_fresh_review_session()
    {
        var rig = await NewRigAsync(reviewerProfile: DirectHarness.ProfileName);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var reviewer = Assert.Single(rig.ReviewerStarts);
        var implementer = Assert.Single(rig.ImplementerStarts);
        Assert.Equal(implementer.Profile, reviewer.Profile);        // allowed: the project said so
        Assert.Equal(AgentSessionPurpose.Review, reviewer.Purpose);  // still its own restricted launch
        var review = Assert.Single((await rig.LoadAsync()).Reviews);
        Assert.NotEqual(review.ImplementerSession, review.ReviewerSession);
    }

    [Fact]
    public async Task The_reviewer_is_a_new_conversation_started_for_review_and_the_implementers_is_never_resumed_for_it()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        await rig.RunAsync();

        Assert.Equal(2, rig.H.Runtime.Starts.Count);
        Assert.Equal(AgentSessionPurpose.Review, rig.H.Runtime.Starts[1].Purpose);
        Assert.Empty(rig.Conversations.Resumes);
        var review = Assert.Single((await rig.LoadAsync()).Reviews);
        Assert.NotEqual(review.ImplementerSession, review.ReviewerSession);
        Assert.Contains(new AgentSessionId("session-2"), rig.H.Runtime.Stops);   // the reviewer session is stopped when its report is in
    }

    // ---- what the reviewer is given -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_reviewer_context_is_the_authoritative_state_and_never_the_implementers_transcript()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass());

        await rig.RunAsync();

        var packet = rig.ReviewerPrompt(1);
        var implementerPrompt = rig.PromptsOf(new AgentSessionId("session-1")).First();

        Assert.Contains("Rename GetMeal() to GetFood()", packet);                // the task
        Assert.Contains("Semantic revision 1", packet);
        Assert.Contains("Implementation revision:", packet);
        Assert.Contains("tests:", packet);                                       // the verification the controller already holds
        Assert.Contains("Do NOT run the configured checks again", packet);
        Assert.Contains("src/Greeting.cs", packet);                              // the files changed
        Assert.Contains(AgentReviewSubmission.ToolName, packet);                 // the output schema's capability
        Assert.DoesNotContain("Rename the public method", packet);               // the implementer's own working prompt
        Assert.DoesNotContain(implementerPrompt, packet);
        Assert.DoesNotContain("Done.", packet);
    }

    // ---- reports -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_structured_CHANGES_REQUIRED_is_recorded_with_its_findings_and_provenance()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());
        rig.Signals.RaiseReview(2, Pass());

        await rig.RunAsync();

        var first = (await rig.LoadAsync()).Reviews[0];
        Assert.Equal(ReviewVerdict.ChangesRequired, first.Verdict);
        var finding = Assert.Single(first.Report.Findings);
        Assert.Equal("The forwarding alias is missing", finding.Title);
        Assert.Equal("src/Greeting.cs:1", finding.Location);
        Assert.Equal(new ProfileId(ReviewerProfileName), first.ReviewerProfile);
        Assert.Equal(1, first.Round);
        Assert.Equal(first.Report.ContentHash, first.ReportHash);
    }

    [Theory]
    [InlineData("MAYBE", "x", 0)]            // unknown verdict
    [InlineData("CHANGES_REQUIRED", "x", 0)] // changes required without a finding
    [InlineData("PASS", "x", 1)]             // a pass with a finding
    public async Task A_malformed_report_is_rejected_and_nothing_is_recorded(string verdict, string summary, int findings)
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, new AgentReviewSubmission(verdict, summary, Enumerable.Range(0, findings).Select(i => new AgentReviewFinding("t", "e", "i", "r", null)).ToList(), null));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("review_report_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reviews);
        Assert.Equal(0, rig.Events<ReviewRecorded>());
    }

    [Fact]
    public async Task An_oversized_report_is_rejected_and_nothing_is_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass(summary: new string('x', ReviewReport.MaxSummaryLength + 1)));

        var result = await rig.RunAsync();

        Assert.StartsWith("review_report_invalid", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reviews);
    }

    [Fact]
    public async Task Two_reports_in_one_turn_are_a_conflict_and_neither_is_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Pass(), Changes());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("review_report_conflict", result.Reason, StringComparison.Ordinal);
        Assert.Empty((await rig.LoadAsync()).Reviews);
    }

    [Fact]
    public async Task A_reviewer_that_never_submits_gets_one_reminder_in_its_own_session_and_then_fails_with_nothing_recorded()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.StartsWith("review_not_submitted", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, rig.PromptsOf(new AgentSessionId("session-2")).Count());   // the packet and one reminder, nothing else
        Assert.Empty((await rig.LoadAsync()).Reviews);
    }

    // ---- read-only ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_that_changes_the_workspace_blocks_the_run_and_nothing_is_absorbed_recorded_or_reverted()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// the reviewer fixed it"));
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ReviewerModifiedWorkspace, result.Status);
        Assert.StartsWith("reviewer_modified_workspace", result.Reason, StringComparison.Ordinal);
        var task = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(ReviewerWorkspaceBlock.IsBlockedBy(task));
        Assert.Empty(task.Reviews);                                                       // its report was not recorded
        Assert.Single(task.ImplementationRevisions);                                      // the reviewer's change is not an implementation revision
        Assert.Contains("the reviewer fixed it", rig.H.Workspace.Files["src/Greeting.cs"]);   // and was not reverted
        Assert.Null(RecoverableContinuation.Find(task));                                  // 'aw continue' is not a generic retry
    }

    // ---- the repair loop ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Findings_resume_the_original_implementer_with_only_the_delta_then_a_NEW_fresh_reviewer_judges_the_repair()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),                                                                                   // implementer turn 1
            DirectHarness.DoesNothing(),                                                                       // reviewer 1
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"),   // implementer repairs
            DirectHarness.DoesNothing());                                                                      // reviewer 2
        rig.Signals.RaiseReview(1, Changes());
        rig.Signals.RaiseReview(2, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();

        // the same implementer session heard the findings; two reviewers, each its own session
        var implementer = new AgentSessionId("session-1");
        var implementerPrompts = rig.PromptsOf(implementer).ToList();
        Assert.Equal(2, implementerPrompts.Count);
        Assert.StartsWith("Independent REVIEW found:", implementerPrompts[1], StringComparison.Ordinal);
        Assert.Contains("The forwarding alias is missing", implementerPrompts[1]);
        Assert.Contains("use the existing reclassification mechanism rather than silently changing semantics", implementerPrompts[1]);
        Assert.DoesNotContain("Rename the public method", implementerPrompts[1]);   // only the delta: the task is not restated

        Assert.Equal(2, rig.ReviewerStarts.Count());
        Assert.Single(rig.ImplementerStarts);
        Assert.Equal(2, task.Reviews.Length);
        Assert.NotEqual(task.Reviews[0].ReviewerSession, task.Reviews[1].ReviewerSession);
        Assert.Equal(task.Reviews[0].ImplementerSession, task.Reviews[1].ImplementerSession);   // the original implementer throughout
        Assert.Equal(2, task.Reviews[1].Round);
        Assert.Equal(ReviewStatus.PassedCurrent, task.ReviewStatus);
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
    }

    [Fact]
    public async Task A_repair_that_changes_files_makes_a_new_implementation_revision_and_is_verified_again_before_the_next_review()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"),
            DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());
        rig.Signals.RaiseReview(2, Pass());

        await rig.RunAsync();

        var task = await rig.LoadAsync();
        Assert.Equal(2, task.ImplementationRevisions.Length);                 // the repair is a fresh revision
        Assert.Equal(2, rig.H.Verifier.Ran.Count);                            // and verified once more
        Assert.NotEqual(task.Reviews[0].ImplementationRevisionId, task.Reviews[1].ImplementationRevisionId);
        Assert.NotEqual(task.Reviews[0].VerificationRunId, task.Reviews[1].VerificationRunId);
        Assert.False(task.IsReviewCurrent(task.Reviews[0]));                  // the first report is history now
        Assert.True(task.IsReviewCurrent(task.Reviews[1]));
    }

    [Fact]
    public async Task A_repair_that_changes_nothing_can_be_disputed_and_a_fresh_review_decides()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());
        rig.Signals.RaiseReview(2, Pass("On inspection the finding does not hold."));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        Assert.Single(task.ImplementationRevisions);            // the implementer left the workspace as it was
        Assert.Equal(2, task.Reviews.Length);                   // and a NEW review, over the new verification run, decided
        Assert.Equal(task.Reviews[0].ImplementationRevisionId, task.Reviews[1].ImplementationRevisionId);
        Assert.NotEqual(task.Reviews[0].VerificationRunId, task.Reviews[1].VerificationRunId);
        Assert.NotEqual(task.Reviews[0].ReviewerSession, task.Reviews[1].ReviewerSession);
    }

    [Fact]
    public async Task The_second_reviewer_receives_the_previous_findings_but_never_a_transcript_of_the_previous_reviewer_or_the_implementer()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(
            rig.H.Renames(),
            DirectHarness.DoesNothing(),
            new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"),
            DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());
        rig.Signals.RaiseReview(2, Pass());

        await rig.RunAsync();

        var second = rig.ReviewerPrompt(2);
        var first = rig.ReviewerPrompt(1);

        Assert.Contains("review round 2", second);
        Assert.Contains("The previous review (round 1, CHANGES_REQUIRED)", second);
        Assert.Contains("The forwarding alias is missing", second);
        Assert.DoesNotContain("Independent REVIEW found:", second);                 // the implementer's repair prompt
        Assert.DoesNotContain("The previous review", first);                         // nothing to carry on the first round
        Assert.NotEqual(rig.H.Runtime.Sends.First(s => s.Prompt == first).Session, rig.H.Runtime.Sends.First(s => s.Prompt == second).Session);
        Assert.Equal(1, rig.H.Runtime.Sends.Count(s => s.Prompt == first));          // the first reviewer was never sent anything else
    }

    [Fact]
    public async Task A_review_that_still_requires_changes_when_the_implementer_has_no_turn_left_is_unresolved_and_not_complete()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());

        var result = await rig.RunAsync(maxTurns: 1);

        Assert.Equal(DirectRunStatus.ReviewUnresolved, result.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(ReviewStatus.ChangesRequired, task.ReviewStatus);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.True(ReviewUnresolvedBlock.IsBlockedBy(task));
    }

    // ---- agent-requested review -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_request_for_review_is_recorded_durably_does_not_stop_the_turn_and_makes_the_review_run()
    {
        var rig = await NewRigAsync(review: false);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReviewRequest(1, new AgentReviewRequestSignal("This changes a public API."));
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        var task = await rig.LoadAsync();
        var request = Assert.Single(task.ReviewRequests);
        Assert.Equal("This changes a public API.", request.Reason);
        Assert.True(task.ReviewEscalated);
        Assert.False(task.ReviewRequired);
        Assert.Single(task.Reviews);                              // the agent's rename in the same turn was still implemented, verified and reviewed
        Assert.Single(task.SemanticRevisions);                    // additive: no semantic revision
        Assert.Equal(1, rig.Events<ReviewRequested>());
    }

    [Fact]
    public async Task A_second_request_for_a_review_that_is_already_required_records_nothing_more()
    {
        var rig = await NewRigAsync(review: true);
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReviewRequest(1, new AgentReviewRequestSignal("Please."));
        rig.Signals.RaiseReview(1, Pass());

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Equal(0, rig.Events<ReviewRequested>());
        Assert.Contains(result.Notes, n => n.Contains("nothing was recorded"));
    }

    [Fact]
    public async Task A_dynamic_request_with_no_reviewer_binding_stops_safely_with_an_actionable_state()
    {
        var rig = await NewRigAsync(review: false, reviewerProfile: null);
        rig.H.Runtime.Enqueue(rig.H.Renames());
        rig.Signals.RaiseReviewRequest(1, new AgentReviewRequestSignal("Worth a second look."));

        var result = await rig.RunAsync();

        Assert.Equal(DirectRunStatus.ReviewerUnbound, result.Status);
        var task = await rig.LoadAsync();
        Assert.True(task.IsReviewRequired);
        Assert.Single(task.ReviewRequests);
        Assert.NotEqual(WorkflowState.Completed, task.WorkflowState);
        Assert.Single(rig.H.Runtime.Starts);
        Assert.Contains("bind", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- recovery -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reviewer_that_died_before_a_durable_report_is_retried_with_a_fresh_reviewer_and_nothing_is_duplicated()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        var failures = 0;
        rig.H.Runtime.OnWait = _ =>
        {
            // the reviewer's runtime fails while the implementer's turn went fine
            if (rig.H.Runtime.Starts.Count == 2 && failures++ == 0)
                throw new InvalidOperationException("the reviewer pane crashed");

            return Task.CompletedTask;
        };

        var first = await rig.RunAsync();

        Assert.NotEqual(DirectRunStatus.Completed, first.Status);
        var stranded = await rig.LoadAsync();
        Assert.Equal(WorkflowState.Blocked, stranded.WorkflowState);
        Assert.Empty(stranded.Reviews);
        var pending = RecoverableContinuation.Find(stranded);
        Assert.NotNull(pending);
        Assert.Equal(HumanInputKind.ReviewPending, pending!.Kind);
        var implementationRevisions = stranded.ImplementationRevisions.Length;
        var verifications = stranded.Verifications.Length;

        rig.Signals.RaiseReview(1, Pass());
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        await new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne);
        var current = await rig.H.Workspace.CaptureAsync(@"C:\fixture");
        var recovered = await rig.Runner().RecoverHumanInputAsync(Fixture.TaskOne, rig.Definition(), current);

        Assert.Equal(DirectRunStatus.Completed, recovered.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(implementationRevisions, task.ImplementationRevisions.Length);   // not duplicated
        Assert.Equal(verifications, task.Verifications.Length);                       // not re-run
        Assert.Single(task.Reviews);
        Assert.Equal(1, rig.Events<ReviewRecorded>());
        Assert.Equal(2, rig.ReviewerStarts.Count());                                  // a fresh reviewer over the same packet
        Assert.Single(rig.ImplementerStarts);
    }

    [Fact]
    public async Task A_failed_repair_after_the_report_was_recorded_retries_the_continuation_in_the_original_conversation_and_not_the_report()
    {
        var rig = await NewRigAsync();
        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes());
        var sends = 0;
        rig.H.Runtime.OnWait = _ =>
        {
            // implementer turn 1 and reviewer 1 pass; the implementer's repair turn (the third wait) fails
            if (++sends == 3)
                throw new InvalidOperationException("the implementer pane crashed");

            return Task.CompletedTask;
        };

        var first = await rig.RunAsync();

        Assert.NotEqual(DirectRunStatus.Completed, first.Status);
        var stranded = await rig.LoadAsync();
        var pending = RecoverableContinuation.Find(stranded);
        Assert.NotNull(pending);
        Assert.Equal(HumanInputKind.ReviewFindings, pending!.Kind);
        Assert.Equal(1, rig.Events<ReviewRecorded>());

        rig.H.Runtime.OnWait = null;
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(2, Pass());
        await new ContinuationRecoveryService(rig.H.Service).ResumeAsync(Fixture.TaskOne);
        var baseline = rig.Baselines.Saved.Last(s => s.Gate == pending.BaselineKey).Baseline;
        var recovered = await rig.Runner().RecoverHumanInputAsync(Fixture.TaskOne, rig.Definition(), baseline);

        Assert.Equal(DirectRunStatus.Completed, recovered.Status);
        var task = await rig.LoadAsync();
        Assert.Equal(0, rig.Events<ReviewRequested>());                       // no request was involved; the forced requirement stands
        Assert.Equal(2, task.Reviews.Length);                                 // the first report was kept once, the second review is the new reviewer's
        Assert.Single(rig.Conversations.Resumes);                             // the ORIGINAL implementer conversation was resumed, not a new one started
        Assert.Single(rig.ImplementerStarts);
        var resumedPrompts = rig.H.Runtime.Sends.Select(s => s.Prompt).Where(p => p.Contains("Independent REVIEW found:")).ToList();
        Assert.Equal(2, resumedPrompts.Count);                                // once before the failure, once as the recovered delta
        Assert.Contains("interrupted by a runtime failure", resumedPrompts[1]);
    }

    // ---- M6 interaction -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reclassification_during_a_review_repair_stales_the_review_keeps_REVIEW_required_and_a_new_fresh_review_follows()
    {
        var rig = await NewRigAsync(howRequired: true);

        // HOW v1 proposed and approved, then the work is verified and reviewed: CHANGES_REQUIRED.
        rig.Signals.RaiseHow(1, new AgentHowSignal("Rename in place.", "Greeting owns it.", "Callers use GetFood().", null, "Simplest.", "Existing tests.", "A forwarding alias is also plausible."));
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing());
        var proposed = await rig.RunAsync();
        Assert.Equal(DirectRunStatus.AwaitingHow, proposed.Status);
        await new HowService(rig.H.Service).ApproveAsync(Fixture.TaskOne, proposed.Task!.Task.PendingGate!.Id);

        rig.H.Runtime.Enqueue(rig.H.Renames(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(1, Changes("The approved strategy cannot keep old callers working"));
        // the repair turn reports that the approved HOW does not hold, then proposes HOW v2 in the next turn
        rig.H.Runtime.Enqueue(DirectHarness.DoesNothing(), DirectHarness.DoesNothing());
        rig.Signals.RaiseReclassification(3, new AgentReclassificationSignal("Old callers need a forwarding alias; renaming in place breaks them.", ReclassificationStage.How, "The approved strategy has no alias."));
        rig.Signals.RaiseHow(4, new AgentHowSignal("Rename and keep a forwarding alias.", "Greeting owns it.", "GetMeal() forwards to GetFood().", null, "Old callers keep working.", "Existing tests plus one for the alias.", "Reviewed evidence changed the trade-off."));
        var baseline = rig.Baselines.Saved.Last().Baseline;

        var rerouted = await rig.Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, rig.Definition(), baseline);

        Assert.Equal(DirectRunStatus.AwaitingHow, rerouted.Status);
        var task = await rig.LoadAsync();
        var review = Assert.Single(task.Reviews);
        Assert.False(task.IsReviewCurrent(review));                 // the review describes a semantic revision that is gone
        Assert.True(task.IsReviewRequired);                         // REVIEW stays required
        Assert.False(task.IsReviewSatisfied);
        Assert.Single(task.Reclassifications);
        Assert.Equal(ReviewStatus.ChangesRequiredStale, task.ReviewStatus);
        Assert.Equal(1, rig.Events<ReviewRecorded>());              // nothing was re-recorded or duplicated

        // after the new HOW is approved and implemented, a NEW fresh reviewer decides, and only a current PASS completes
        await new HowService(rig.H.Service).ApproveAsync(Fixture.TaskOne, task.PendingGate!.Id);
        rig.H.Runtime.Enqueue(new TurnScript(() => rig.H.Workspace.Files["src/Greeting.cs"] += "\n// obsolete forwarder for old callers"), DirectHarness.DoesNothing());
        rig.Signals.RaiseReview(2, Pass());
        var next = await rig.Runner().ContinueAfterHumanInputAsync(Fixture.TaskOne, rig.Definition(), rig.Baselines.Saved.Last().Baseline);

        Assert.Equal(DirectRunStatus.Completed, next.Status);
        var finished = await rig.LoadAsync();
        Assert.Equal(2, finished.Reviews.Length);
        Assert.Equal(ReviewStatus.PassedCurrent, finished.ReviewStatus);
        Assert.NotEqual(finished.Reviews[0].ReviewerSession, finished.Reviews[1].ReviewerSession);
        Assert.NotEqual(finished.Reviews[0].SemanticRevisionId, finished.Reviews[1].SemanticRevisionId);
    }
}
