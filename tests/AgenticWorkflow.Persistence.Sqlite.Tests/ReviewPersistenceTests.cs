using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// The independent REVIEW is stored as additive versioned payloads (<c>review_requested</c> v1, <c>review_recorded</c> v1, and <c>task_created</c> v5 only when the requirement was
/// forced), so every earlier history replays byte for byte. A review replays in a fresh store to exactly the live state; no transcript is ever stored; and a damaged or forged
/// record (a report that does not hash, a review of another implementation, a duplicate) fails loudly instead of satisfying a requirement.
/// </summary>
public sealed class ReviewPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static ReviewReport Pass() => new(ReviewVerdict.Pass, "The implementation does what the task asks.", [], "A non-blocking remark.");

    private static ReviewReport Changes()
        => new(ReviewVerdict.ChangesRequired, "One real defect.", [new ReviewFinding("Empty input returns null", "Importer.cs:12 returns null for an empty file.", "Callers crash.", "Return an empty list.", "Importer.cs:12")]);

    private static void Verify(TaskStream stream, int at)
        => stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(at));

    /// <summary>REVIEW forced; implemented and verified; CHANGES_REQUIRED, a repair, then PASS; completed. Both roles explicitly bound.</summary>
    private static TaskStream Reviewed(bool forced = true)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, false, false, forced);
        stream.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        stream.BindRole(Role("reviewer", "repository_tools"), Profile("p-reviewer", null, "repository_tools"), At(1));

        if (!forced)
            stream.RequestReview(new ReviewRequestId(Guid.NewGuid()), "A subtle change to shared behaviour.", 1, "session-1", At(2));

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(3));
        Verify(stream, 4);
        stream.RecordReview(new ReviewId(Guid.NewGuid()), Changes(), new ProfileId("p-reviewer"), "reviewer-1", "implementer-1", 1500, 2, ["git diff", "git show HEAD"], false, At(5));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(6));
        Verify(stream, 7);
        stream.RecordReview(new ReviewId(Guid.NewGuid()), Pass(), new ProfileId("p-reviewer"), "reviewer-2", "implementer-1", 900, 0, [], false, At(8));
        stream.CompleteVerified(At(9));
        return stream;
    }

    private async Task<SqliteEventStore> SeedAsync(TaskStream stream)
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private void Damage(string sql)
        => Assert.True(_db.Execute(sql, []) > 0, "The damaging statement must change something, or the test proves nothing.");

    private string LastReviewPayload() => _db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'review_recorded' ORDER BY sequence DESC LIMIT 1");

    private long LastReviewSequence() => _db.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'review_recorded' ORDER BY sequence DESC LIMIT 1");

    // ---- stable, additive storage --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_events_are_stored_with_stable_kinds_versions_and_names()
    {
        await SeedAsync(Reviewed(forced: false));

        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind = 'review_requested' AND payload_schema_version = 1"));
        Assert.Equal(2L, _db.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind = 'review_recorded' AND payload_schema_version = 1"));

        var json = LastReviewPayload();
        foreach (var name in new[] { "\"verdict\":\"pass\"", "\"report_hash\":\"", "\"reviewer_profile\":\"p-reviewer\"", "\"reviewer_session\":\"reviewer-2\"", "\"implementer_session\":\"implementer-1\"", "\"semantic_revision_id\":\"", "\"implementation_revision_id\":\"", "\"verification_run_id\":\"", "\"configured_check_rerun\":false" })
            Assert.Contains(name, json.Replace("\"verdict\":\"PASS\"", "\"verdict\":\"pass\""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_forced_requirement_is_in_the_first_event_at_version_5_and_a_task_without_it_is_written_exactly_as_before()
    {
        await SeedAsync(Reviewed(forced: true));
        Assert.Equal(5L, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.Contains("review_required", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));

        using var other = new TempDatabase();
        var plain = NewTask();
        plain.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        plain.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2));
        Verify(plain, 3);
        await PersistAsync(await other.OpenStoreAsync(), plain);

        // no review member, no new version: an older history's bytes are untouched by M7
        Assert.True(other.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1") < 5);
        Assert.DoesNotContain("review", other.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, other.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind LIKE 'review%'"));
    }

    // ---- replay ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reviewed_history_replays_in_a_fresh_store_to_exactly_the_live_state(bool forced)
    {
        var original = Reviewed(forced);
        await SeedAsync(original);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(2, replayed.Reviews.Length);
        Assert.Equal(ReviewStatus.PassedCurrent, replayed.ReviewStatus);
        Assert.True(replayed.IsReviewSatisfied);
        Assert.Equal(forced, replayed.ReviewRequired);
        Assert.Equal(!forced, replayed.ReviewEscalated);
        Assert.Equal(replayed.Reviews[0].Report.ContentHash, replayed.Reviews[0].ReportHash);
        Assert.False(replayed.IsReviewCurrent(replayed.Reviews[0]));   // the first report is history after the repair
        Assert.True(replayed.IsReviewCurrent(replayed.Reviews[1]));
    }

    [Fact]
    public async Task The_provenance_survives_the_round_trip_and_holds_no_transcript_or_reasoning()
    {
        await SeedAsync(Reviewed());
        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;
        var first = replayed.Reviews[0];

        Assert.Equal(new ProfileId("p-reviewer"), first.ReviewerProfile);
        Assert.Equal("reviewer-1", first.ReviewerSession);
        Assert.Equal("implementer-1", first.ImplementerSession);
        Assert.Equal(1, first.Round);
        Assert.Equal(1500, first.ReviewerMilliseconds);
        Assert.Equal(2, first.ReviewerCommandCount);
        Assert.Equal(["git diff", "git show HEAD"], first.ReviewerCommands.ToArray());

        var all = string.Concat(_db.Scalar<string>("SELECT group_concat(payload_json, ' ') FROM task_events WHERE event_kind LIKE 'review%'"));
        foreach (var forbidden in new[] { "transcript", "reasoning", "thinking", "conversation_text", "messages" })
            Assert.DoesNotContain(forbidden, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_history_continues_after_a_restart_and_a_stale_pass_is_kept_not_deleted()
    {
        var stream = Reviewed();
        await SeedAsync(stream);
        var store = await _db.OpenStoreAsync();
        var resumed = await LoadAndReplayAsync(store, TaskOne);

        Assert.Equal(WorkflowState.Completed, resumed.State.WorkflowState);   // a completed task stays completed; the review that allowed it is still there
        Assert.Equal(2, resumed.State.Reviews.Length);
        Assert.Equal(ReviewVerdict.Pass, resumed.State.Reviews[^1].Verdict);
    }

    // ---- damage and forgery ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_that_no_longer_matches_its_recorded_hash_is_corruption()
    {
        var stream = Reviewed();
        var store = await SeedAsync(stream);
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.review.summary', 'Everything is fine, trust me.') WHERE sequence = {LastReviewSequence()}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_verdict_or_a_missing_member_is_corruption()
    {
        var store = await SeedAsync(Reviewed());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.review.verdict', 'maybe') WHERE sequence = {LastReviewSequence()}");
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        using var second = new TempDatabase();
        var other = Reviewed();
        await PersistAsync(await second.OpenStoreAsync(), other);
        var sequence = second.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'review_recorded' ORDER BY sequence DESC LIMIT 1");
        Assert.True(second.Execute($"UPDATE task_events SET payload_json = json_remove(payload_json, '$.review.reviewer_session') WHERE sequence = {sequence}", []) > 0);
        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await second.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_finding_beyond_its_bound_is_corruption()
    {
        var stream = Reviewed();
        var store = await SeedAsync(stream);
        var sequence = _db.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'review_recorded' ORDER BY sequence ASC LIMIT 1");
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.review.findings[0].evidence', '{new string('x', ReviewFinding.MaxEvidenceLength + 1)}') WHERE sequence = {sequence}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_forged_review_of_a_different_implementation_fails_replay()
    {
        var stream = Reviewed();
        var store = await SeedAsync(stream);
        var elsewhere = Guid.NewGuid();
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.review.implementation_revision_id', '{elsewhere}') WHERE sequence = {LastReviewSequence()}");

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_forged_review_that_names_the_implementers_conversation_as_the_reviewer_fails_replay()
    {
        var stream = Reviewed();
        var store = await SeedAsync(stream);
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.review.reviewer_session', 'implementer-1') WHERE sequence = {LastReviewSequence()}");

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_duplicated_review_event_fails_replay()
    {
        var stream = Reviewed();
        var store = await SeedAsync(stream);
        var duplicate = (ReviewRecorded)stream.Events.Last(e => e.Payload is ReviewRecorded).Payload;

        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(20), duplicate)]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_review_recorded_in_a_history_that_never_required_one_fails_replay()
    {
        var plain = NewTask();
        plain.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        plain.BindRole(Role("reviewer", "repository_tools"), Profile("p-reviewer", null, "repository_tools"), At(1));
        plain.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2));
        Verify(plain, 3);

        var forgedReview = Reviewed().State.Reviews[^1];
        var history = plain.Events.Append(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, plain.Events.Count + 1, At(9), new ReviewRecorded(forgedReview))).ToList();

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }
}
