using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// M2D in the runner: reuse of the runtime's record of agent-executed checks, mixed with controller execution, and every
/// fallback. All with fakes; the controller's runner is the <see cref="FakeVerifier"/> whose call list proves what it executed.
/// </summary>
public sealed class EvidenceReuseRunnerTests
{
    private static async Task<(DirectHarness Harness, ScriptedEvidenceSource Evidence)> ArrangeAsync()
    {
        var harness = await DirectHarness.CreateAsync();
        return (harness, new ScriptedEvidenceSource());
    }

    private static VerificationCheckResult CheckOf(WorkflowTask task, string id)
        => task.CurrentVerification!.Checks.Single(c => c.CheckId.Value == id);

    /// <summary>The evidence of a well-behaved agent: it edits, builds, tests and reads, one tool per batch.</summary>
    private static Func<Task<ObservedTurnEvidence?>> WellBehaved(DirectHarness h)
        => async () =>
        {
            var state = await h.CurrentFingerprintAsync();

            return Obs.Evidence(
                Obs.Edit("tu-edit", 0),
                Obs.Command("tu-build", 1, Obs.BuildCommand, state, milliseconds: 9000),
                Obs.Command("tu-tests", 2, Obs.TestCommand, state, milliseconds: 35000),
                Obs.Read("tu-read", 3));
        };

    // ---- reuse ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_exact_agent_checks_are_reused_and_only_the_controller_only_check_is_executed()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(WellBehaved(h));

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["controller_only"], h.Verifier.Ran); // the controller did NOT run build or tests

        var task = await h.ReloadAsync();
        Assert.Equal(VerificationOutcome.Passed, task.CurrentVerification!.Outcome);
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);

        var build = CheckOf(task, "build");
        Assert.Equal(VerificationEvidenceSource.AgentObserved, build.Source);
        Assert.Equal("tu-build", build.Observation!.ToolUseId);
        Assert.Equal("fake-harness", build.Observation.Runtime);
        Assert.Equal("dotnet build Fixture.sln --no-incremental", build.Observation.CommandIdentity);
        Assert.Equal(9000, build.Observation.DurationMilliseconds);
        Assert.Equal(task.CurrentImplementationRevision!.Fingerprint, build.Observation.StateFingerprint);
        Assert.Equal(0, build.ExitCode);

        var tests = CheckOf(task, "tests");
        Assert.Equal(VerificationEvidenceSource.AgentObserved, tests.Source);
        Assert.Equal("tu-tests", tests.Observation!.ToolUseId);

        var controllerOnly = CheckOf(task, "controller_only");
        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, controllerOnly.Source);
        Assert.Null(controllerOnly.Observation);

        Assert.Equal(
            [("build", true), ("tests", true), ("controller_only", false)],
            result.EvidenceDecisions.Select(d => (d.CheckId.Value, d.Reusable)).ToArray());
    }

    [Fact]
    public async Task The_reason_for_reusing_or_executing_each_check_is_recorded_with_its_result()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(async () =>
        {
            var state = await h.CurrentFingerprintAsync();

            // the build is reusable (and last); the tests were run with an extra flag, so they are not the configured command
            return Obs.Evidence(
                Obs.Command("tu-tests", 0, "dotnet test Fixture.sln --verbosity quiet", state),
                Obs.Command("tu-build", 1, Obs.BuildCommand, state));
        });

        var result = await h.RunWithAsync(evidence);

        var task = await h.ReloadAsync();
        Assert.Equal("agent_observed_current_state", CheckOf(task, "build").EvidenceReason);
        Assert.Equal("command_mismatch", CheckOf(task, "tests").EvidenceReason);
        Assert.Equal(VerificationEvidenceSource.ControllerExecuted, CheckOf(task, "tests").Source);

        // what is recorded is exactly what the controller decided
        Assert.Equal(
            result.EvidenceDecisions.Select(d => (d.CheckId.Value, d.Reason)),
            task.CurrentVerification!.Checks.Where(c => c.EvidenceReason is not null).Select(c => (c.CheckId.Value, c.EvidenceReason!)));
    }

    [Fact]
    public async Task Every_fallback_reason_is_recorded_with_the_check_the_controller_executed_instead()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(Scenario(h, "wrong_session"));

        await h.RunWithAsync(evidence);

        var task = await h.ReloadAsync();
        Assert.All(
            task.CurrentVerification!.Checks,
            c =>
            {
                Assert.Equal(VerificationEvidenceSource.ControllerExecuted, c.Source);
                Assert.Equal("evidence_untrusted: foreign_session: a record does not belong to the owned session", c.EvidenceReason);
            });
    }

    [Fact]
    public async Task A_reason_is_bounded_and_single_line_even_when_the_runtime_reports_an_unbounded_problem()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(Obs.Untrusted($"unreadable_record: {new string('x', 2000)}\nsecond line\u001b[31m"));

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);

        var reasons = (await h.ReloadAsync()).CurrentVerification!.Checks.Select(c => c.EvidenceReason).ToList();
        Assert.All(reasons, r =>
        {
            Assert.StartsWith("evidence_untrusted: unreadable_record:", r);
            Assert.True(r!.Length <= VerificationCheckResult.MaxEvidenceReasonLength);
            Assert.DoesNotContain(r, char.IsControl);
        });
    }

    [Fact]
    public async Task Without_an_evidence_source_the_controller_executes_every_check_exactly_as_before()
    {
        var (h, _) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunWithAsync(evidence: null);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["build", "tests", "controller_only"], h.Verifier.Ran);
        Assert.All((await h.ReloadAsync()).CurrentVerification!.Checks, c => Assert.Equal(VerificationEvidenceSource.ControllerExecuted, c.Source));
        Assert.All(result.EvidenceDecisions, d => Assert.Equal("no_evidence_available", d.Reason));
        Assert.All((await h.ReloadAsync()).CurrentVerification!.Checks.Where(c => c.CheckId.Value is "build" or "tests" or "controller_only"), c => Assert.Equal("no_evidence_available", c.EvidenceReason));
        Assert.Empty(h.Runtime.Starts.Single().ObservableChecks);
    }

    [Fact]
    public async Task A_runtime_session_without_the_capability_reports_nothing_and_the_controller_runs_everything()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue((ObservedTurnEvidence?)null);

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["build", "tests", "controller_only"], h.Verifier.Ran);
    }

    [Fact]
    public async Task The_checks_are_offered_to_the_runtime_only_when_an_evidence_source_exists()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(WellBehaved(h));

        await h.RunWithAsync(evidence);

        Assert.Equal(["build", "tests", "controller_only"], h.Runtime.Starts.Single().ObservableChecks.Select(c => c.Id.Value));
    }

    [Fact]
    public async Task The_turn_window_is_opened_before_each_prompt_and_collected_after_each_finished_turn()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());
        evidence.Enqueue(Obs.Evidence());
        evidence.Enqueue(WellBehaved(h));

        await h.RunWithAsync(evidence);

        Assert.Equal(["begin", "collect", "begin", "collect"], evidence.Calls);
    }

    // ---- fallbacks: the controller executes, the task is still verified normally ----------------------------------------

    public static TheoryData<string> Fallbacks() => new()
    {
        "command_mismatch",
        "working_directory",
        "fingerprint_mismatch",
        "failed_tool",
        "later_edit",
        "later_mutating_bash",
        "parallel_batch",
        "duplicate_event",
        "malformed_record",
        "wrong_session",
        "empty_record",
        "ordering_problem",
    };

    private static Func<Task<ObservedTurnEvidence?>> Scenario(DirectHarness h, string name)
        => async () =>
        {
            var state = await h.CurrentFingerprintAsync();

            return name switch
            {
                "command_mismatch" => Obs.Evidence(Obs.Command("tu-1", 0, "dotnet test Fixture.sln --verbosity quiet", state), Obs.Command("tu-2", 1, "cd src && dotnet build Fixture.sln --no-incremental", state)),
                "working_directory" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state, directory: @"C:\fixture\src"), Obs.Command("tu-2", 1, Obs.BuildCommand, state, directory: @"C:\elsewhere")),
                "fingerprint_mismatch" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Obs.Fingerprint("not-this-state")), Obs.Command("tu-2", 1, Obs.BuildCommand, Obs.Fingerprint("not-this-state"))),
                "failed_tool" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state, succeeded: false), Obs.Command("tu-2", 1, Obs.BuildCommand, state, succeeded: false)),
                "later_edit" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state), Obs.Edit("tu-3", 2)),
                "later_mutating_bash" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state), Obs.Command("tu-3", 2, "sed -i s/a/b/ src/Greeting.cs", null)),
                "parallel_batch" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state, batchSize: 2), Obs.Edit("tu-2", 0, batchSize: 2), Obs.Command("tu-3", 1, Obs.BuildCommand, state, batchSize: 2), Obs.Read("tu-4", 1, batchSize: 2)),
                "duplicate_event" => Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state)),
                "malformed_record" => new ObservedTurnEvidence("fake-harness", [Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state)], ["unreadable_record: 'e-1.json' (JsonException)"]),
                "wrong_session" => new ObservedTurnEvidence("fake-harness", [Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state)], ["foreign_session: a record does not belong to the owned session"]),
                "empty_record" => Obs.Evidence(),
                "ordering_problem" => new ObservedTurnEvidence("fake-harness", [Obs.Command("tu-1", 0, Obs.TestCommand, state), Obs.Command("tu-2", 1, Obs.BuildCommand, state)], ["out_of_order: the outcome of tool use tu-1 was recorded after its batch resolved"]),
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
        };

    [Theory]
    [MemberData(nameof(Fallbacks))]
    public async Task Evidence_that_is_wrong_ambiguous_or_untrustworthy_is_ignored_and_the_controller_runs_every_check(string scenario)
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(Scenario(h, scenario));

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status); // the work really is correct, so the controller's own runs pass
        Assert.Equal(["build", "tests", "controller_only"], h.Verifier.Ran);
        Assert.All(result.EvidenceDecisions, d => Assert.False(d.Reusable, $"{d.CheckId}: {d.Reason}"));
        Assert.All((await h.ReloadAsync()).CurrentVerification!.Checks, c => Assert.Equal(VerificationEvidenceSource.ControllerExecuted, c.Source));
    }

    [Fact]
    public async Task Fabricated_passing_evidence_for_wrong_work_cannot_complete_the_task()
    {
        // The controller-only check still fails on the unchanged workspace, so reuse of the two agent-run checks changes nothing.
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing());
        evidence.Enqueue(WellBehaved(h));

        var result = await h.RunWithAsync(evidence, maxTurns: 1);

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);   // verification unresolved: not completed, and nothing works on it
        Assert.Equal(VerificationOutcome.Failed, task.CurrentVerification!.Outcome);
        Assert.Equal(VerificationOutcome.Failed, CheckOf(task, "controller_only").Outcome);
        Assert.Equal(VerificationEvidenceSource.AgentObserved, CheckOf(task, "tests").Source);
        Assert.False(DirectPathCompletion.Evaluate(task).IsEligible);
    }

    [Fact]
    public async Task A_source_that_throws_never_fails_the_task()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.FailBegin = new IOException("channel directory vanished");
        evidence.FailCollect = new InvalidOperationException("hook channel corrupted");

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["build", "tests", "controller_only"], h.Verifier.Ran);
        Assert.Contains(result.Notes, n => n.Contains("evidence window could not be opened"));
        Assert.Contains(result.Notes, n => n.Contains("evidence could not be collected"));
    }

    [Fact]
    public async Task A_workspace_that_moves_while_the_controller_runs_its_own_checks_is_still_caught_with_reused_evidence()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(h.Renames());
        evidence.Enqueue(WellBehaved(h));
        h.Verifier.Override = _ =>
        {
            h.Workspace.Files["src/Greeting.cs"] += "\n// touched while the checks ran";
            return new CheckExecution(0, false, TimeSpan.FromMilliseconds(5), "Passed!", string.Empty);
        };

        var result = await h.RunWithAsync(evidence, maxTurns: 1);

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);
        Assert.Contains(result.LastVerification, d => d.CheckId == DirectPathRunner.WorkspaceStableCheckId && d.Outcome == VerificationOutcome.Failed);
        Assert.False((await h.ReloadAsync()).CurrentVerification!.Outcome == VerificationOutcome.Passed);

        // The stability sensor is a controller-only check, not subject to reuse, so it has no reuse reason; the reused ones keep theirs.
        var checks = (await h.ReloadAsync()).CurrentVerification!.Checks;
        Assert.Null(checks.Single(c => c.CheckId == DirectPathRunner.WorkspaceStableCheckId).EvidenceReason);
        Assert.Equal("agent_observed_current_state", checks.Single(c => c.CheckId.Value == "build").EvidenceReason);
    }

    // ---- repair: the older turn's evidence never verifies the newer implementation ----------------------------------------

    [Fact]
    public async Task Same_session_repair_uses_only_the_new_turns_evidence_for_the_new_implementation()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());

        // turn 1: the agent built and tested the unfinished state; turn 2: it finished the rename and built and tested again
        evidence.Enqueue(async () =>
        {
            var state = await h.CurrentFingerprintAsync();
            return Obs.Evidence(Obs.Command("t1-build", 0, Obs.BuildCommand, state), Obs.Command("t1-tests", 1, Obs.TestCommand, state));
        });
        evidence.Enqueue(async () =>
        {
            var state = await h.CurrentFingerprintAsync();
            return Obs.Evidence(Obs.Command("t2-build", 0, Obs.BuildCommand, state), Obs.Command("t2-tests", 1, Obs.TestCommand, state));
        });

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Equal(["controller_only", "controller_only"], h.Verifier.Ran); // the controller-only check ran in both turns, nothing else

        var task = await h.ReloadAsync();
        var first = task.Verifications[0];
        var second = task.Verifications[1];

        Assert.Equal(VerificationOutcome.Failed, first.Outcome);
        Assert.Equal(VerificationOutcome.Passed, second.Outcome);

        var firstRevision = task.ImplementationRevisions.Single(r => r.Id == first.ImplementationRevisionId);
        var secondRevision = task.ImplementationRevisions.Single(r => r.Id == second.ImplementationRevisionId);
        Assert.NotEqual(firstRevision.Fingerprint, secondRevision.Fingerprint);

        Assert.Equal("t1-tests", first.Checks.Single(c => c.CheckId.Value == "tests").Observation!.ToolUseId);
        Assert.Equal(firstRevision.Fingerprint, first.Checks.Single(c => c.CheckId.Value == "tests").Observation!.StateFingerprint);
        Assert.Equal("t2-tests", second.Checks.Single(c => c.CheckId.Value == "tests").Observation!.ToolUseId);
        Assert.Equal(secondRevision.Fingerprint, second.Checks.Single(c => c.CheckId.Value == "tests").Observation!.StateFingerprint);
    }

    [Fact]
    public async Task Previous_turn_evidence_offered_again_cannot_verify_the_repaired_implementation()
    {
        var (h, evidence) = await ArrangeAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());

        ObservedTurnEvidence? turnOne = null;
        evidence.Enqueue(async () =>
        {
            var state = await h.CurrentFingerprintAsync();
            return turnOne = Obs.Evidence(Obs.Command("t1-build", 0, Obs.BuildCommand, state), Obs.Command("t1-tests", 1, Obs.TestCommand, state));
        });

        // A misbehaving source repeats turn 1's record in turn 2: its state is not the repaired one.
        evidence.Enqueue(() => Task.FromResult(turnOne));

        var result = await h.RunWithAsync(evidence);

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(["controller_only", "build", "tests", "controller_only"], h.Verifier.Ran);

        var task = await h.ReloadAsync();
        Assert.All(task.Verifications[1].Checks, c => Assert.Equal(VerificationEvidenceSource.ControllerExecuted, c.Source));
        Assert.Equal("state_mismatch", result.EvidenceDecisions.Single(d => d.CheckId.Value == "tests").Reason);
    }
}
