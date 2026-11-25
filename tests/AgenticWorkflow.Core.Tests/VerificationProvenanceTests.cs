using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

/// <summary>Where a passing check came from: provenance is bounded, only passing checks can be agent-observed, and it binds to the exact state.</summary>
public sealed class VerificationProvenanceTests
{
    private static readonly ImplementationFingerprint State = new("tree:1");

    private static AgentObservation Observation(ImplementationFingerprint? state = null, long? duration = 1200)
        => new("claude-code", "toolu_01", "dotnet test Fixture.sln", duration, state ?? State);

    private static TaskStream WithImplementation()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(State, At(1));
        return stream;
    }

    [Fact]
    public void A_check_without_an_observation_is_controller_executed()
    {
        var check = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0);

        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, check.Source);
        Assert.Null(check.Observation);
    }

    [Fact]
    public void A_check_with_an_observation_is_agent_observed()
    {
        var check = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation());

        Assert.Equal(VerificationEvidenceSource.AgentObserved, check.Source);
        Assert.Equal("toolu_01", check.Observation!.ToolUseId);
    }

    [Fact]
    public void A_check_keeps_the_reason_it_was_reused_or_executed_without_it_changing_its_provenance()
    {
        var reused = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation(), "agent_observed_current_state");
        var executed = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, evidenceReason: "state_mismatch");
        var unrecorded = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0);

        Assert.Equal("agent_observed_current_state", reused.EvidenceReason);
        Assert.Equal(VerificationEvidenceSource.AgentObserved, reused.Source);
        Assert.Equal("state_mismatch", executed.EvidenceReason);
        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, executed.Source);
        Assert.Null(unrecorded.EvidenceReason);
        Assert.NotEqual(executed, unrecorded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("two\nlines")]
    public void An_evidence_reason_must_be_a_non_blank_single_line(string reason)
        => Assert.Throws<ArgumentException>(() => new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, evidenceReason: reason));

    [Fact]
    public void An_evidence_reason_is_bounded_so_output_cannot_be_smuggled_into_history()
    {
        _ = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, evidenceReason: new string('r', VerificationCheckResult.MaxEvidenceReasonLength));

        Assert.Throws<ArgumentException>(
            () => new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, evidenceReason: new string('r', VerificationCheckResult.MaxEvidenceReasonLength + 1)));
    }

    [Fact]
    public void A_failed_check_cannot_be_agent_observed()
        => Assert.Throws<InvalidVerificationException>(
            () => new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Failed, "exit 1", 1, Observation()));

    [Fact]
    public void Provenance_is_part_of_a_checks_value_equality()
    {
        var controller = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0);
        var observed = new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation());

        Assert.NotEqual(controller, observed);
        Assert.Equal(observed, new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation()));
    }

    [Theory]
    [InlineData("", "toolu_01", "dotnet test")]
    [InlineData("claude-code", " ", "dotnet test")]
    [InlineData("claude-code", "toolu_01", "")]
    public void Observation_text_must_not_be_blank(string runtime, string toolUse, string command)
        => Assert.Throws<ArgumentException>(() => new AgentObservation(runtime, toolUse, command, null, State));

    [Fact]
    public void Observation_text_is_bounded_so_output_cannot_be_smuggled_into_history()
    {
        Assert.Throws<ArgumentException>(() => new AgentObservation(new string('r', AgentObservation.MaxRuntimeLength + 1), "t", "c", null, State));
        Assert.Throws<ArgumentException>(() => new AgentObservation("r", new string('t', AgentObservation.MaxToolUseIdLength + 1), "c", null, State));
        Assert.Throws<ArgumentException>(() => new AgentObservation("r", "t", new string('c', AgentObservation.MaxCommandIdentityLength + 1), null, State));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentObservation("r", "t", "c", -1, State));
    }

    [Fact]
    public void An_observation_of_the_verified_state_is_recorded_and_keeps_its_provenance()
    {
        var stream = WithImplementation();

        var run = stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [
                new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation()),
                new VerificationCheckResult(new VerificationCheckId("smoke"), VerificationOutcome.Passed, "ok", 0),
            ],
            At(2));

        Assert.Equal(VerificationOutcome.Passed, run.Outcome);
        Assert.Equal(VerificationEvidenceSource.AgentObserved, stream.State.CurrentVerification!.Checks[0].Source);
        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, stream.State.CurrentVerification.Checks[1].Source);
    }

    [Fact]
    public void An_observation_of_a_different_state_than_the_verified_revision_is_refused()
    {
        var stream = WithImplementation();

        Assert.Throws<InvalidVerificationException>(() => stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation(new ImplementationFingerprint("tree:other")))],
            At(2)));

        Assert.Empty(stream.State.Verifications);
    }

    [Fact]
    public void An_observation_of_an_older_revisions_state_cannot_verify_a_newer_revision()
    {
        var stream = WithImplementation();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(2));

        Assert.Throws<InvalidVerificationException>(() => stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation())],
            At(3)));
    }

    [Fact]
    public void Replay_refuses_a_history_whose_observation_does_not_match_its_revision()
    {
        var stream = WithImplementation();
        var run = stream.RecordVerification(
            stream.State.CurrentImplementationRevisionId!.Value,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation())],
            At(2));

        // The same history, but the stored observation names another state.
        var tampered = new VerificationRun(
            run.Id,
            run.TaskId,
            run.ImplementationRevisionId,
            run.Outcome,
            [new VerificationCheckResult(new VerificationCheckId("tests"), VerificationOutcome.Passed, "ok", 0, Observation(new ImplementationFingerprint("tree:forged")))],
            run.Timestamp);

        var events = stream.Events.ToList();
        var last = events[^1];
        events[^1] = new EventEnvelope(last.Id, last.TaskId, last.Sequence, last.Timestamp, new VerificationRecorded(tampered));

        Assert.Throws<InvalidVerificationException>(() => TaskStream.Replay(events, new SequentialIdSource()));
    }
}
