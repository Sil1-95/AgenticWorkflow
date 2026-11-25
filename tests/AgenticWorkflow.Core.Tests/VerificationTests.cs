using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

public sealed class VerificationTests
{
    private static VerificationCheckResult Pass(string id = "tests") => new(new VerificationCheckId(id), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail(string id = "tests") => new(new VerificationCheckId(id), VerificationOutcome.Failed, "exit 1", 1);

    private static TaskStream WithImplementation()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        return stream;
    }

    // ---- value types ------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_needs_at_least_one_check()
        => Assert.Throws<InvalidVerificationException>(() => new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, new ImplementationRevisionId(Guid.NewGuid()),
            VerificationOutcome.Passed, [], T0));

    [Fact]
    public void Check_ids_must_be_unique_within_a_run()
        => Assert.Throws<InvalidVerificationException>(() => new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, new ImplementationRevisionId(Guid.NewGuid()),
            VerificationOutcome.Passed, [Pass("a"), Pass("a")], T0));

    [Theory]
    [InlineData(VerificationOutcome.Passed)]
    [InlineData(VerificationOutcome.Failed)]
    public void The_aggregate_outcome_must_match_the_checks(VerificationOutcome stated)
    {
        // The statement contradicts the checks in both directions.
        var inconsistent = stated == VerificationOutcome.Passed
            ? new[] { Pass("a"), Fail("b") }
            : new[] { Pass("a"), Pass("b") };

        Assert.Throws<InvalidVerificationException>(() => new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, new ImplementationRevisionId(Guid.NewGuid()),
            stated, inconsistent, T0));
    }

    [Fact]
    public void Aggregate_is_passed_iff_every_check_passed()
    {
        Assert.Equal(VerificationOutcome.Passed, VerificationRun.AggregateOf([Pass("a"), Pass("b")]));
        Assert.Equal(VerificationOutcome.Failed, VerificationRun.AggregateOf([Pass("a"), Fail("b")]));
        Assert.Equal(VerificationOutcome.Failed, VerificationRun.AggregateOf([Fail("a")]));
    }

    [Fact]
    public void Check_results_validate_their_own_shape()
    {
        Assert.Throws<ArgumentException>(() => new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, " "));
        Assert.Throws<ArgumentException>(() => new VerificationCheckResult(
            new VerificationCheckId("tests"), VerificationOutcome.Passed, new string('x', VerificationCheckResult.MaxSummaryLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerificationCheckResult(new VerificationCheckId("tests"), (VerificationOutcome)99, "x"));
        Assert.Throws<ArgumentException>(() => new VerificationCheckId("Bad Id"));
    }

    // ---- command and state ------------------------------------------------------------------------------------

    [Fact]
    public void Recording_a_verification_binds_it_to_the_given_implementation_revision()
    {
        var stream = WithImplementation();
        var implementation = stream.State.CurrentImplementationRevisionId!.Value;

        var run = stream.RecordVerification(implementation, [Pass("tests"), Pass("symbols")], At(2));

        Assert.Equal(VerificationOutcome.Passed, run.Outcome);
        Assert.Equal(implementation, run.ImplementationRevisionId);
        Assert.Equal(At(2), run.Timestamp);
        Assert.Equal(EventKinds.VerificationRecorded, stream.Events[^1].Kind);
        Assert.Equal(run, stream.State.CurrentVerification);
        Assert.Equal(run, stream.State.LatestVerificationFor(implementation));
    }

    [Fact]
    public void A_failed_check_makes_the_run_failed()
    {
        var stream = WithImplementation();

        var run = stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [Pass("a"), Fail("b")], At(2));

        Assert.Equal(VerificationOutcome.Failed, run.Outcome);
    }

    [Fact]
    public void An_unknown_implementation_revision_is_rejected_and_nothing_changes()
    {
        var stream = WithImplementation();
        var before = stream.Events.Count;

        Assert.Throws<InvalidRevisionReferenceException>(
            () => stream.RecordVerification(new ImplementationRevisionId(Guid.NewGuid()), [Pass()], At(2)));

        Assert.Equal(before, stream.Events.Count);
        Assert.Empty(stream.State.Verifications);
    }

    [Fact]
    public void A_task_without_an_implementation_has_no_current_verification()
    {
        var stream = NewTask();

        Assert.Null(stream.State.CurrentVerification);
        Assert.Throws<InvalidRevisionReferenceException>(
            () => stream.RecordVerification(new ImplementationRevisionId(Guid.NewGuid()), [Pass()], At(1)));
    }

    [Fact]
    public void Old_verification_remains_history_but_is_not_current_after_a_new_implementation_revision()
    {
        var stream = WithImplementation();
        var first = stream.State.CurrentImplementationRevisionId!.Value;
        var oldRun = stream.RecordVerification(first, [Pass()], At(2));

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));

        Assert.Null(stream.State.CurrentVerification);
        Assert.Equal(oldRun, stream.State.LatestVerificationFor(first));
        Assert.Equal(oldRun, Assert.Single(stream.State.Verifications));
    }

    [Fact]
    public void The_latest_run_of_a_revision_wins()
    {
        var stream = WithImplementation();
        var implementation = stream.State.CurrentImplementationRevisionId!.Value;
        stream.RecordVerification(implementation, [Fail()], At(2));
        var second = stream.RecordVerification(implementation, [Pass()], At(3));

        Assert.Equal(second, stream.State.CurrentVerification);
        Assert.Equal(2, stream.State.Verifications.Length);
    }

    [Fact]
    public void A_terminal_task_accepts_no_verification()
    {
        var stream = AsLegacy(WithImplementation());
        stream.ChangeWorkflowState(WorkflowState.Completed, "done", At(2));

        Assert.Throws<TaskTerminatedException>(
            () => stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [Pass()], At(3)));
    }

    // ---- replay -----------------------------------------------------------------------------------------------

    [Fact]
    public void Replay_reconstructs_verification_state_exactly()
    {
        var ids = new SequentialIdSource();
        var stream = NewTask(ids);
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [Pass("a"), Fail("b")], At(2));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));
        stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [Pass("a"), Pass("b")], At(4));

        var replayed = TaskStream.Replay(stream.Events, new SequentialIdSource());

        Assert.Equal(stream.State, replayed.State);
        Assert.Equal(2, replayed.State.Verifications.Length);
        Assert.Equal(VerificationOutcome.Passed, replayed.State.CurrentVerification!.Outcome);
    }

    private static EventEnvelope Forge(TaskStream basis, VerificationRun run, DateTimeOffset? at = null)
        => new(
            new EventId(Guid.NewGuid()),
            basis.State.Id,
            basis.State.LastSequence + 1,
            at ?? run.Timestamp,
            new VerificationRecorded(run));

    [Fact]
    public void Replay_rejects_a_run_for_an_unknown_implementation_revision()
    {
        var stream = WithImplementation();
        var run = new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, new ImplementationRevisionId(Guid.NewGuid()),
            VerificationOutcome.Passed, [Pass()], At(2));

        var history = stream.Events.Append(Forge(stream, run)).ToList();

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_run_that_claims_another_task()
    {
        var stream = WithImplementation();
        var run = new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskTwo, stream.State.CurrentImplementationRevisionId!.Value,
            VerificationOutcome.Passed, [Pass()], At(2));

        var history = stream.Events.Append(Forge(stream, run)).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_run_stamped_differently_from_its_event()
    {
        var stream = WithImplementation();
        var run = new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, stream.State.CurrentImplementationRevisionId!.Value,
            VerificationOutcome.Passed, [Pass()], At(2));

        var history = stream.Events.Append(Forge(stream, run, at: At(3))).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_duplicate_run_id()
    {
        var stream = WithImplementation();
        var implementation = stream.State.CurrentImplementationRevisionId!.Value;
        var run = stream.RecordVerification(implementation, [Pass()], At(2));
        var duplicate = new VerificationRun(run.Id, TaskOne, implementation, VerificationOutcome.Passed, [Pass()], At(3));

        var history = stream.Events.Append(Forge(stream, duplicate)).ToList();

        Assert.Throws<InvalidVerificationException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_run_stamped_before_the_previous_event()
    {
        var stream = WithImplementation();
        var run = new VerificationRun(
            new VerificationRunId(Guid.NewGuid()), TaskOne, stream.State.CurrentImplementationRevisionId!.Value,
            VerificationOutcome.Passed, [Pass()], At(0));

        var history = stream.Events.Append(Forge(stream, run)).ToList();

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }
}
