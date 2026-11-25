using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>
/// an independent REVIEW is a conditional requirement of the workflow, judged by a fresh reviewer conversation. Core derives from the history alone whether a review is
/// required, due, current, stale or satisfied; refuses a review that is not independent, not bound to the current state or not made by an explicitly bound reviewer;
/// never lets completion or the ACCEPT gate pass without a current PASS; and refuses to replay a history that claims otherwise.
/// </summary>
public sealed class ReviewTests
{
    private const string ImplementerSession = "implementer-conversation";

    private static TaskStream NewStream(bool review = true, bool what = false, bool how = false, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
        => TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new SequentialIdSource(), acceptance, what, how, review);

    private static void BindReviewer(TaskStream stream, string profile = "p-reviewer", int at = 1)
        => stream.BindRole(Role("reviewer", "repository_tools"), Profile(profile, "repository_tools"), At(at));

    private static void BindImplementer(TaskStream stream, string profile = "p-impl", int at = 1)
        => stream.BindRole(Role("root", "code_edit", "repository_tools"), Profile(profile, "code_edit", "repository_tools"), At(at));

    private static VerificationRun Implement(TaskStream stream, int at, string fingerprint = "tree:1", VerificationOutcome outcome = VerificationOutcome.Passed)
    {
        stream.RecordImplementationRevision(new ImplementationFingerprint(fingerprint), At(at));
        return stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), outcome, "ok", 0)], At(at + 1));
    }

    private static VerificationRun Reverify(TaskStream stream, int at)
        => stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(at));

    private static ReviewReport Pass() => new(ReviewVerdict.Pass, "The implementation does what the task asks.", []);

    private static ReviewReport Changes(string title = "Empty input returns null")
        => new(ReviewVerdict.ChangesRequired, "One real defect.", [new ReviewFinding(title, "Importer.cs:12 returns null for an empty file.", "Callers crash.", "Return an empty list.", "Importer.cs:12")]);

    private static Review Record(TaskStream stream, int at, ReviewReport? report = null, string reviewerSession = "reviewer-1", string implementerSession = ImplementerSession, string profile = "p-reviewer")
        => stream.RecordReview(new ReviewId(Guid.NewGuid()), report ?? Pass(), new ProfileId(profile), reviewerSession, implementerSession, 1200, 1, ["git diff"], false, At(at));

    /// <summary>REVIEW required, both roles bound, implemented and verified (passing): the moment a fresh reviewer is due.</summary>
    private static TaskStream Verified(bool review = true, AcceptanceRequirement acceptance = AcceptanceRequirement.NotRequired)
    {
        var stream = NewStream(review, acceptance: acceptance);
        BindImplementer(stream);
        BindReviewer(stream);
        Implement(stream, 2);
        return stream;
    }

    private static List<EventEnvelope> Forge(TaskStream stream, params DomainEvent[] more)
    {
        var events = stream.Events.ToList();

        foreach (var payload in more)
            events.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, events.Count + 1, At(80 + events.Count), payload));

        return events;
    }

    // ---- role and composition -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_forced_REVIEW_requirement_is_recorded_in_the_first_event_and_replays()
    {
        var stream = NewStream(review: true);

        Assert.True(((TaskCreated)stream.Events[0].Payload).ReviewRequired);
        Assert.True(stream.State.ReviewRequired);
        Assert.True(stream.State.IsReviewRequired);
        Assert.False(stream.State.ReviewEscalated);

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;
        Assert.True(replayed.ReviewRequired);
        Assert.True(replayed.IsReviewRequired);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void REVIEW_neither_implies_nor_is_implied_by_WHAT_or_HOW(bool what, bool how, bool review)
    {
        var state = NewStream(review, what, how).State;

        Assert.Equal(review, state.IsReviewRequired);
        Assert.Equal(what, state.WhatRequired);
        Assert.Equal(how, state.HowRequired);
    }

    [Fact]
    public void A_review_without_an_explicit_reviewer_binding_is_refused_and_the_implementers_profile_is_never_substituted()
    {
        var stream = NewStream();
        BindImplementer(stream);
        Implement(stream, 2);

        // The Reviewer role is unbound; the implementer's own profile is bound to the root role only.
        var unbound = Assert.Throws<InvalidReviewException>(() => Record(stream, 4, profile: "p-impl"));
        Assert.Contains("not explicitly bound", unbound.Message);
        Assert.False(stream.State.RoleBindings.ContainsKey(new RoleId("reviewer")));
        Assert.Empty(stream.State.Reviews);
    }

    [Fact]
    public void An_explicit_binding_of_the_reviewer_role_to_the_implementers_profile_is_allowed()
    {
        var stream = NewStream();
        BindImplementer(stream, profile: "p-both");
        BindReviewer(stream, profile: "p-both");
        Implement(stream, 2);

        var review = Record(stream, 4, profile: "p-both");

        Assert.Equal(new ProfileId("p-both"), review.ReviewerProfile);
        Assert.True(stream.State.HasCurrentReviewPass);
    }

    [Fact]
    public void A_review_from_the_implementers_own_conversation_is_refused()
    {
        var stream = Verified();

        var refused = Assert.Throws<InvalidReviewException>(() => Record(stream, 4, reviewerSession: ImplementerSession));

        Assert.Contains("implementer's own conversation", refused.Message);
        Assert.Empty(stream.State.Reviews);
    }

    [Fact]
    public void Every_review_is_a_conversation_of_its_own_and_an_earlier_reviewers_is_never_reused()
    {
        var stream = Verified();
        Record(stream, 4, Changes(), reviewerSession: "reviewer-1");
        Implement(stream, 6, "tree:2");

        var reused = Assert.Throws<InvalidReviewException>(() => Record(stream, 8, reviewerSession: "reviewer-1"));

        Assert.Contains("fresh conversation", reused.Message);
    }

    [Fact]
    public void A_dynamic_request_is_durable_and_makes_REVIEW_required()
    {
        var stream = NewStream(review: false);
        Assert.False(stream.State.IsReviewRequired);

        var request = stream.RequestReview(new ReviewRequestId(Guid.NewGuid()), "A subtle change to shared behaviour.", 2, "session-1", At(3));

        Assert.True(stream.State.IsReviewRequired);
        Assert.True(stream.State.ReviewEscalated);
        Assert.False(stream.State.ReviewRequired);   // the human did not force it; the agent's request is recorded as such
        Assert.Equal(request, Assert.Single(stream.State.ReviewRequests));
        Assert.Equal(stream.State.CurrentSemanticRevisionId, request.SemanticRevisionId);

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;
        Assert.True(replayed.IsReviewRequired);
        Assert.Equal(request, Assert.Single(replayed.ReviewRequests));
    }

    [Fact]
    public void A_request_for_REVIEW_is_additive_it_creates_no_semantic_revision_and_invalidates_nothing()
    {
        var stream = NewStream(review: false, what: true, how: true);
        var what = stream.OpenWhatGate(new GateId(Guid.NewGuid()), new WhatProposal("Retries on 429.", "A failed import is retried.", "The importer only.", null, null, "A 429 is retried.", "Two policies are plausible.", 1), At(1));
        stream.ApproveWhat(what.Id, At(2));
        var revisionBefore = stream.State.CurrentSemanticRevisionId;
        var revisionsBefore = stream.State.SemanticRevisions.Length;

        stream.RequestReview(new ReviewRequestId(Guid.NewGuid()), "Worth a second look.", 1, null, At(3));

        Assert.Equal(revisionBefore, stream.State.CurrentSemanticRevisionId);
        Assert.Equal(revisionsBefore, stream.State.SemanticRevisions.Length);
        Assert.Equal(GateStatus.Approved, stream.State.StatusOf(what));
        Assert.Empty(stream.State.Reclassifications);
    }

    [Fact]
    public void The_requirement_cannot_be_lowered_or_requested_twice()
    {
        var forced = NewStream(review: true);
        Assert.NotNull(forced.State.ReviewRequestRefusal());   // already required: nothing to add

        var requested = NewStream(review: false);
        requested.RequestReview(new ReviewRequestId(Guid.NewGuid()), "First.", 1, null, At(1));
        Assert.NotNull(requested.State.ReviewRequestRefusal());
        Assert.Throws<InvalidReviewException>(() => requested.RequestReview(new ReviewRequestId(Guid.NewGuid()), "Second.", 2, null, At(2)));

        // No de-escalation exists: the requirement stays however the task moves on.
        BindImplementer(requested);
        Implement(requested, 3);
        Assert.True(requested.State.IsReviewRequired);
        Assert.Contains(typeof(TaskStream).GetMethods().Select(m => m.Name), name => name == nameof(TaskStream.RequestReview));
        Assert.DoesNotContain(typeof(TaskStream).GetMethods().Select(m => m.Name), name => name.Contains("LowerReview") || name.Contains("WithdrawReview") || name.Contains("RemoveReview"));
    }

    [Fact]
    public void A_history_without_review_events_replays_unchanged_and_completes_as_before()
    {
        var stream = NewStream(review: false);
        BindImplementer(stream);
        Implement(stream, 2);
        stream.CompleteVerified(At(5));

        var replayed = TaskStream.Replay(stream.Events, new GuidIdSource()).State;

        Assert.False(replayed.IsReviewRequired);
        Assert.False(replayed.ReviewRequired);
        Assert.Empty(replayed.Reviews);
        Assert.Empty(replayed.ReviewRequests);
        Assert.True(replayed.IsReviewSatisfied);   // nothing is asked of a task that never required it
        Assert.Equal(ReviewStatus.None, replayed.ReviewStatus);
        Assert.Equal(WorkflowState.Completed, replayed.WorkflowState);
    }

    // ---- sequencing ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_review_can_be_recorded_before_there_is_an_implementation_or_a_verification()
    {
        var stream = NewStream();
        BindReviewer(stream);

        Assert.False(stream.State.IsReviewDue);
        Assert.Throws<InvalidReviewException>(() => Record(stream, 3));   // no implementation

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(4));
        Assert.False(stream.State.IsReviewDue);
        Assert.Throws<InvalidReviewException>(() => Record(stream, 5));   // implemented, never verified
    }

    [Fact]
    public void A_failing_verification_is_never_reviewed_the_implementer_repairs_first()
    {
        var stream = NewStream();
        BindReviewer(stream);
        Implement(stream, 2, outcome: VerificationOutcome.Failed);

        Assert.False(stream.State.IsReviewDue);
        Assert.Throws<InvalidReviewException>(() => Record(stream, 4));

        Implement(stream, 5, "tree:2");   // the repair is verified and passes
        Assert.True(stream.State.IsReviewDue);
    }

    [Fact]
    public void A_current_PASS_makes_completion_possible_and_without_it_completion_is_refused()
    {
        var stream = Verified();

        Assert.True(stream.State.IsReviewDue);
        Assert.False(stream.State.IsReviewSatisfied);
        Assert.ThrowsAny<DomainException>(() => stream.CompleteVerified(At(4)));

        Record(stream, 4, Pass());
        Assert.False(stream.State.IsReviewDue);
        Assert.True(stream.State.IsReviewSatisfied);

        stream.CompleteVerified(At(5));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void CHANGES_REQUIRED_blocks_completion_until_a_later_review_passes()
    {
        var stream = Verified();
        Record(stream, 4, Changes(), reviewerSession: "reviewer-1");

        Assert.Equal(ReviewStatus.ChangesRequired, stream.State.ReviewStatus);
        Assert.False(stream.State.IsReviewSatisfied);
        Assert.False(stream.State.IsReviewDue);   // answered by the implementer first, not by another review of the same state
        Assert.ThrowsAny<DomainException>(() => stream.CompleteVerified(At(5)));

        Implement(stream, 6, "tree:2");
        Assert.True(stream.State.IsReviewDue);
        Record(stream, 8, Pass(), reviewerSession: "reviewer-2");

        stream.CompleteVerified(At(9));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void A_stale_PASS_never_completes_and_is_never_deleted()
    {
        var stream = Verified();
        var passed = Record(stream, 4, Pass());

        Implement(stream, 6, "tree:2");   // the workspace moved on: the pass describes an earlier implementation

        Assert.Equal(ReviewStatus.PassedStale, stream.State.ReviewStatus);
        Assert.False(stream.State.HasCurrentReviewPass);
        Assert.False(stream.State.IsReviewSatisfied);
        Assert.True(stream.State.IsReviewDue);
        Assert.ThrowsAny<DomainException>(() => stream.CompleteVerified(At(8)));
        Assert.Contains(passed, stream.State.Reviews);   // kept as history
    }

    [Fact]
    public void With_human_acceptance_the_ACCEPT_gate_opens_only_after_a_current_review_PASS()
    {
        var stream = Verified(acceptance: AcceptanceRequirement.HumanRequired);

        Assert.ThrowsAny<DomainException>(() => stream.OpenAcceptGate(At(4)));
        Assert.Null(stream.State.PendingGate);

        Record(stream, 4, Pass());
        var gate = stream.OpenAcceptGate(At(5));

        Assert.Equal(GateType.Accept, gate.Type);
        Assert.Equal(WorkflowState.AwaitingHuman, stream.State.WorkflowState);

        // REVIEW PASS is never ACCEPT: only the human's gate decision completes a task that requires acceptance.
        Assert.ThrowsAny<DomainException>(() => stream.CompleteVerified(At(6)));
        stream.AcceptGate(gate.Id, At(7));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    [Fact]
    public void Without_a_requirement_nothing_waits_for_a_review()
    {
        var stream = Verified(review: false);

        Assert.False(stream.State.IsReviewDue);
        Assert.True(stream.State.IsReviewSatisfied);
        stream.CompleteVerified(At(4));
        Assert.Equal(WorkflowState.Completed, stream.State.WorkflowState);
    }

    // ---- report, binding and currentness ------------------------------------------------------------------------------------------

    [Fact]
    public void A_review_is_bound_to_the_current_semantic_revision_implementation_revision_and_verification()
    {
        var stream = Verified();
        var verification = stream.State.CurrentVerification!;

        var review = Record(stream, 4, Pass(), reviewerSession: "reviewer-9");

        Assert.Equal(stream.State.CurrentSemanticRevisionId, review.SemanticRevisionId);
        Assert.Equal(stream.State.CurrentImplementationRevisionId, review.ImplementationRevisionId);
        Assert.Equal(verification.Id, review.VerificationRunId);
        Assert.Equal(1, review.Round);
        Assert.Equal(review.Report.ContentHash, review.ReportHash);
        Assert.Equal(ImplementerSession, review.ImplementerSession);
        Assert.Equal("reviewer-9", review.ReviewerSession);
        Assert.Equal(new ProfileId("p-reviewer"), review.ReviewerProfile);
        Assert.True(stream.State.IsReviewCurrent(review));
    }

    [Fact]
    public void A_new_implementation_revision_makes_the_review_stale()
    {
        var stream = Verified();
        var review = Record(stream, 4, Pass());

        Implement(stream, 6, "tree:2");

        Assert.False(stream.State.IsReviewCurrent(review));
        Assert.Null(stream.State.CurrentReview);
    }

    [Fact]
    public void A_reclassification_makes_the_review_stale_and_keeps_REVIEW_required()
    {
        var stream = NewStream(review: true, how: true);
        BindImplementer(stream);
        BindReviewer(stream);
        var how = stream.OpenHowGate(new GateId(Guid.NewGuid()), new HowProposal("Introduce a retry policy object.", "RetryPolicy decides.", null, null, "One more class.", null, "Two shapes are plausible.", 1), At(1));
        stream.ApproveHow(how.Id, At(2));
        Implement(stream, 3);
        var review = Record(stream, 5, Pass());

        stream.Reclassify(new ReclassificationId(Guid.NewGuid()), ReclassificationStage.How, "The retry budget cannot be met.", "The approved strategy does not hold.", 2, "session-1", new ImplementationFingerprint("ws:1"), false, At(6));

        Assert.False(stream.State.IsReviewCurrent(review));
        Assert.Equal(ReviewStatus.PassedStale, stream.State.ReviewStatus);
        Assert.True(stream.State.IsReviewRequired);
        Assert.False(stream.State.IsReviewSatisfied);
        Assert.Contains(review, stream.State.Reviews);
    }

    [Fact]
    public void A_new_verification_run_requires_a_new_review_even_for_an_unchanged_implementation()
    {
        var stream = Verified();
        var review = Record(stream, 4, Pass());

        Reverify(stream, 6);   // same implementation revision, a new verification run

        Assert.False(stream.State.IsReviewCurrent(review));
        Assert.True(stream.State.IsReviewDue);

        // ...and a no-change repair can be re-reviewed over that new run, by a new conversation.
        var again = Record(stream, 8, Pass(), reviewerSession: "reviewer-2");
        Assert.Equal(2, again.Round);
        Assert.Equal(review.ImplementationRevisionId, again.ImplementationRevisionId);
        Assert.NotEqual(review.VerificationRunId, again.VerificationRunId);
    }

    [Fact]
    public void A_second_review_over_the_same_verification_is_a_duplicate()
    {
        var stream = Verified();
        Record(stream, 4, Pass(), reviewerSession: "reviewer-1");

        var duplicate = Assert.Throws<InvalidReviewException>(() => Record(stream, 5, Pass(), reviewerSession: "reviewer-2"));

        Assert.Contains("duplicate", duplicate.Message);
    }

    [Fact]
    public void A_forged_review_of_a_different_implementation_fails_replay()
    {
        var stream = Verified();
        var other = Verified();
        Implement(other, 6, "tree:elsewhere");
        var forged = new Review(
            new ReviewId(Guid.NewGuid()),
            1,
            Pass(),
            Pass().ContentHash,
            new ProfileId("p-reviewer"),
            "reviewer-1",
            ImplementerSession,
            stream.State.CurrentSemanticRevisionId,
            other.State.CurrentImplementationRevisionId!.Value,   // an implementation this history never made current
            stream.State.CurrentVerification!.Id,
            At(80 + stream.Events.Count + 1),
            10,
            0,
            [],
            false);

        var history = Forge(stream, new ReviewRecorded(forged));

        var refused = Assert.Throws<InvalidReviewException>(() => TaskStream.Replay(history, new GuidIdSource()));
        Assert.Contains("stale", refused.Message);
    }

    [Fact]
    public void A_forged_review_from_the_implementers_session_fails_replay()
    {
        var stream = Verified();
        var forged = new Review(
            new ReviewId(Guid.NewGuid()),
            1,
            Pass(),
            Pass().ContentHash,
            new ProfileId("p-reviewer"),
            ImplementerSession,                                   // the reviewer "is" the implementer
            ImplementerSession,
            stream.State.CurrentSemanticRevisionId,
            stream.State.CurrentImplementationRevisionId!.Value,
            stream.State.CurrentVerification!.Id,
            At(80 + stream.Events.Count + 1),
            10,
            0,
            [],
            false);

        var history = Forge(stream, new ReviewRecorded(forged));

        Assert.Throws<InvalidReviewException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    [Fact]
    public void A_review_whose_report_does_not_match_its_recorded_hash_is_refused()
    {
        var report = Pass();

        var refused = Assert.Throws<InvalidReviewException>(() => new Review(
            new ReviewId(Guid.NewGuid()), 1, report, new string('0', 64), new ProfileId("p-reviewer"), "reviewer-1", ImplementerSession,
            new SemanticRevisionId(Guid.NewGuid()), new ImplementationRevisionId(Guid.NewGuid()), new VerificationRunId(Guid.NewGuid()), At(1), 1, 0, [], false));

        Assert.Contains("hash", refused.Message);
    }

    [Fact]
    public void A_stale_forged_replay_of_a_PASS_cannot_complete_the_task()
    {
        // The completion itself is forged after a pass that no longer describes the task: replay refuses it.
        var stream = Verified();
        Record(stream, 4, Pass());
        Implement(stream, 6, "tree:2");

        var history = Forge(stream, new WorkflowStateChanged(WorkflowState.Running, WorkflowState.Completed, "forged completion over a stale review"));

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }

    // ---- the report itself ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_report_that_requires_changes_must_state_a_finding_and_a_pass_must_not()
    {
        Assert.Throws<ArgumentException>(() => new ReviewReport(ReviewVerdict.ChangesRequired, "Needs work.", []));
        Assert.Throws<ArgumentException>(() => new ReviewReport(ReviewVerdict.Pass, "Fine.", [new ReviewFinding("t", "e", "i", "r")]));
        Assert.Equal(ReviewVerdict.Pass, new ReviewReport(ReviewVerdict.Pass, "Fine.", [], "A non-blocking remark.").Verdict);
    }

    [Fact]
    public void A_report_is_bounded_per_item_and_in_total()
    {
        Assert.Throws<ArgumentException>(() => new ReviewReport(ReviewVerdict.Pass, new string('x', ReviewReport.MaxSummaryLength + 1), []));
        Assert.Throws<ArgumentException>(() => new ReviewFinding(new string('x', ReviewFinding.MaxTitleLength + 1), "e", "i", "r"));
        Assert.Throws<ArgumentException>(() => new ReviewFinding("t", new string('x', ReviewFinding.MaxEvidenceLength + 1), "i", "r"));

        var tooMany = Enumerable.Range(0, ReviewReport.MaxFindings + 1).Select(i => new ReviewFinding($"t{i}", "e", "i", "r"));
        Assert.Throws<ArgumentException>(() => new ReviewReport(ReviewVerdict.ChangesRequired, "Many.", tooMany));

        var large = Enumerable.Range(0, ReviewReport.MaxFindings)
            .Select(i => new ReviewFinding($"t{i}", new string('e', ReviewFinding.MaxEvidenceLength), new string('i', ReviewFinding.MaxImpactLength), new string('r', ReviewFinding.MaxRecommendationLength)));
        Assert.Throws<ArgumentException>(() => new ReviewReport(ReviewVerdict.ChangesRequired, new string('s', ReviewReport.MaxSummaryLength), large, new string('n', ReviewReport.MaxNotesLength)));
    }

    [Fact]
    public void Equal_reports_hash_equally_and_a_different_report_hashes_differently()
    {
        Assert.Equal(Pass().ContentHash, Pass().ContentHash);
        Assert.NotEqual(Changes("a").ContentHash, Changes("b").ContentHash);
        Assert.Equal(64, Pass().ContentHash.Length);
    }

    [Fact]
    public void A_review_and_its_report_hold_no_transcript_or_hidden_reasoning()
    {
        var names = typeof(Review).GetProperties().Concat(typeof(ReviewReport).GetProperties()).Concat(typeof(ReviewFinding).GetProperties()).Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n.Contains("transcript") || n.Contains("reasoning") || n.Contains("thinking") || n.Contains("conversationtext") || n.Contains("messages"));
        Assert.DoesNotContain(names, n => n.Contains("severity"));   // no severity taxonomy
    }

    [Fact]
    public void Review_status_distinguishes_current_stale_and_changes_required()
    {
        var stream = Verified();
        Assert.Equal(ReviewStatus.None, stream.State.ReviewStatus);

        Record(stream, 4, Changes(), reviewerSession: "reviewer-1");
        Assert.Equal(ReviewStatus.ChangesRequired, stream.State.ReviewStatus);

        Implement(stream, 6, "tree:2");
        Assert.Equal(ReviewStatus.ChangesRequiredStale, stream.State.ReviewStatus);

        Record(stream, 8, Pass(), reviewerSession: "reviewer-2");
        Assert.Equal(ReviewStatus.PassedCurrent, stream.State.ReviewStatus);

        Implement(stream, 10, "tree:3");
        Assert.Equal(ReviewStatus.PassedStale, stream.State.ReviewStatus);
    }
}
