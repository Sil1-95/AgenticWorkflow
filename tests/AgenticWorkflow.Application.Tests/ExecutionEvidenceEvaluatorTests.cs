using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>When the runtime's record of the agent running a check may stand in for the controller running it, and when it may not.</summary>
public sealed class ExecutionEvidenceEvaluatorTests
{
    private static readonly ImplementationFingerprint Current = Obs.Fingerprint("current");

    private static readonly VerificationCheckSpec[] Declared = [Obs.Build, Obs.Tests, Obs.ControllerOnly];

    private static EvidenceDecision Evaluate(
        ObservedTurnEvidence? evidence,
        VerificationCheckSpec? check = null,
        ISet<string>? used = null,
        ImplementationFingerprint? current = null)
        => new ExecutionEvidenceEvaluator(Declared).Evaluate(
            Obs.Workspace,
            check ?? Obs.Tests,
            evidence,
            current ?? Current,
            used ?? new HashSet<string>());

    private static void AssertRerun(EvidenceDecision decision, string reason)
    {
        Assert.False(decision.Reusable);
        Assert.Null(decision.Observation);
        Assert.Equal(reason, decision.Reason);
    }

    // ---- reuse -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_exact_successful_command_in_the_right_directory_against_the_current_state_is_reused()
    {
        var decision = Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, milliseconds: 4200)));

        Assert.True(decision.Reusable);
        Assert.Equal("agent_observed_current_state", decision.Reason);

        var observation = decision.Observation!;
        Assert.Equal("fake-harness", observation.Runtime);
        Assert.Equal("tu-1", observation.ToolUseId);
        Assert.Equal("dotnet test Fixture.sln", observation.CommandIdentity);
        Assert.Equal(4200, observation.DurationMilliseconds);
        Assert.Equal(Current, observation.StateFingerprint);
    }

    [Fact]
    public void Later_reads_searches_and_known_read_only_commands_do_not_invalidate_the_evidence()
    {
        var decision = Evaluate(Obs.Evidence(
            Obs.Command("tu-1", 0, Obs.TestCommand, Current),
            Obs.Read("tu-2", 1),
            Obs.Command("tu-3", 2, "git status", state: null),
            Obs.Command("tu-4", 3, "git diff --stat", state: null)));

        Assert.True(decision.Reusable);
    }

    [Fact]
    public void A_later_configured_check_that_captured_the_same_state_does_not_invalidate_an_earlier_one()
    {
        var evidence = Obs.Evidence(
            Obs.Command("tu-1", 0, Obs.BuildCommand, Current),
            Obs.Command("tu-2", 1, Obs.TestCommand, Current));

        Assert.True(Evaluate(evidence, Obs.Build).Reusable);
        Assert.True(Evaluate(evidence, Obs.Tests).Reusable);
    }

    [Fact]
    public void When_the_agent_ran_the_check_twice_the_latest_valid_run_is_used()
    {
        var decision = Evaluate(Obs.Evidence(
            Obs.Command("tu-old", 0, Obs.TestCommand, Obs.Fingerprint("before-the-fix")),
            Obs.Edit("tu-edit", 1),
            Obs.Command("tu-new", 2, Obs.TestCommand, Current)));

        Assert.True(decision.Reusable);
        Assert.Equal("tu-new", decision.Observation!.ToolUseId);
    }

    // ---- fallbacks -------------------------------------------------------------------------------------------------

    [Fact]
    public void No_evidence_at_all_means_the_controller_runs_the_check()
        => AssertRerun(Evaluate(null), "no_evidence_available");

    [Fact]
    public void An_empty_record_means_the_controller_runs_the_check()
        => AssertRerun(Evaluate(Obs.Evidence()), "command_mismatch");

    [Fact]
    public void An_untrustworthy_record_is_ignored_as_a_whole_even_when_it_contains_a_perfect_match()
    {
        var evidence = new ObservedTurnEvidence(
            "fake-harness",
            [Obs.Command("tu-1", 0, Obs.TestCommand, Current)],
            ["malformed_hook_payload: JsonReaderException"]);

        AssertRerun(Evaluate(evidence), "evidence_untrusted: malformed_hook_payload: JsonReaderException");
    }

    [Theory]
    [InlineData("dotnet test Fixture.sln --verbosity quiet")]
    [InlineData("dotnet test")]
    [InlineData("cd src && dotnet test Fixture.sln")]
    [InlineData("dotnet test Fixture.sln | tail -20")]
    [InlineData("dotnet test Fixture.sln ; echo done")]
    [InlineData("powershell -Command \"dotnet test Fixture.sln\"")]
    [InlineData("cmd /c dotnet test Fixture.sln")]
    public void A_command_that_is_not_exactly_the_check_is_never_reused(string command)
        => AssertRerun(Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, command, Current))), "command_mismatch");

    [Fact]
    public void The_wrong_working_directory_is_never_reused()
        => AssertRerun(
            Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, directory: @"C:\fixture\src"))),
            "working_directory_mismatch");

    [Fact]
    public void A_missing_working_directory_is_never_reused()
        => AssertRerun(
            Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, directory: null))),
            "working_directory_mismatch");

    [Fact]
    public void A_check_with_its_own_directory_requires_that_directory()
    {
        var check = new VerificationCheckSpec(new VerificationCheckId("sub"), "sub", "dotnet", ["test"], TimeSpan.FromMinutes(1), workingDirectory: "src");
        var evaluator = new ExecutionEvidenceEvaluator([check]);

        var right = evaluator.Evaluate(Obs.Workspace, check, Obs.Evidence(Obs.Command("tu-1", 0, "dotnet test", Current, directory: @"C:\fixture\src")), Current, new HashSet<string>());
        var wrong = evaluator.Evaluate(Obs.Workspace, check, Obs.Evidence(Obs.Command("tu-1", 0, "dotnet test", Current, directory: Obs.Workspace)), Current, new HashSet<string>());

        Assert.True(right.Reusable);
        AssertRerun(wrong, "working_directory_mismatch");
    }

    [Fact]
    public void A_state_other_than_the_one_being_verified_is_never_reused()
        => AssertRerun(
            Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Obs.Fingerprint("some-other-state")))),
            "state_mismatch");

    [Fact]
    public void A_command_with_no_captured_state_is_never_reused()
        => AssertRerun(Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, state: null))), "no_state_captured");

    [Fact]
    public void A_failed_command_is_never_reused()
        => AssertRerun(Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, succeeded: false))), "agent_command_failed");

    [Fact]
    public void A_command_whose_exit_status_the_runtime_cannot_prove_is_never_reused_and_is_not_called_failed()
        => AssertRerun(
            Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, succeeded: false, exitUnproven: true))),
            "exit_unproven");

    [Fact]
    public void An_unproven_exit_is_reported_over_a_failed_attempt_because_it_got_further()
    {
        var decision = Evaluate(Obs.Evidence(
            Obs.Command("tu-1", 0, Obs.TestCommand, Current, succeeded: false),
            Obs.Command("tu-2", 1, Obs.TestCommand, Current, succeeded: false, exitUnproven: true)));

        AssertRerun(decision, "exit_unproven");
    }

    [Fact]
    public void A_check_that_expects_a_non_zero_exit_code_is_never_taken_from_the_runtime()
    {
        var expectsFailure = new VerificationCheckSpec(
            new VerificationCheckId("absent"), "absent", "git", ["grep", "-q", "GetMeal"], TimeSpan.FromSeconds(30), expectedExitCode: 1);

        var decision = Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, "git grep -q GetMeal", Current)), expectsFailure);

        AssertRerun(decision, "expected_exit_code_not_zero");
    }

    [Fact]
    public void A_run_that_took_longer_than_the_checks_own_timeout_is_never_reused()
        => AssertRerun(
            Evaluate(Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current, milliseconds: (long)TimeSpan.FromMinutes(6).TotalMilliseconds))),
            "agent_run_exceeded_check_timeout");

    // ---- ordering and ambiguity -------------------------------------------------------------------------------------

    [Fact]
    public void A_command_in_a_parallel_batch_is_never_reused()
        => AssertRerun(
            Evaluate(Obs.Evidence(
                Obs.Command("tu-1", 0, Obs.TestCommand, Current, batchSize: 2),
                Obs.Edit("tu-2", 0, batchSize: 2))),
            "ambiguous_parallel_batch");

    [Fact]
    public void A_later_edit_invalidates_the_evidence_even_if_the_state_happens_to_match()
        => AssertRerun(
            Evaluate(Obs.Evidence(
                Obs.Command("tu-1", 0, Obs.TestCommand, Current),
                Obs.Edit("tu-2", 1))),
            "state_may_have_changed_later");

    [Theory]
    [InlineData("sed -i s/a/b/ src/Menu.cs")]
    [InlineData("git checkout -- src/Menu.cs")]
    [InlineData("dotnet format")]
    [InlineData("rm src/Old.cs")]
    [InlineData("echo x > src/New.cs")]
    [InlineData("powershell -Command \"Set-Content a b\"")]
    public void A_later_command_that_could_change_state_invalidates_the_evidence(string later)
        => AssertRerun(
            Evaluate(Obs.Evidence(
                Obs.Command("tu-1", 0, Obs.TestCommand, Current),
                Obs.Command("tu-2", 1, later, state: null))),
            "state_may_have_changed_later");

    [Fact]
    public void A_later_unclassified_tool_invalidates_the_evidence()
        => AssertRerun(
            Evaluate(Obs.Evidence(
                Obs.Command("tu-1", 0, Obs.TestCommand, Current),
                new ObservedActivity { ToolUseId = "tu-2", Batch = 1, BatchSize = 1, Kind = ObservedActivityKind.Unknown })),
            "state_may_have_changed_later");

    [Fact]
    public void A_later_configured_check_that_ended_in_a_different_state_invalidates_the_evidence()
        => AssertRerun(
            Evaluate(
                Obs.Evidence(
                    Obs.Command("tu-1", 0, Obs.BuildCommand, Current),
                    Obs.Command("tu-2", 1, Obs.TestCommand, Obs.Fingerprint("tests-regenerated-a-file"))),
                Obs.Build),
            "state_may_have_changed_later");

    [Fact]
    public void A_later_configured_check_in_a_parallel_batch_invalidates_the_evidence()
        => AssertRerun(
            Evaluate(
                Obs.Evidence(
                    Obs.Command("tu-1", 0, Obs.BuildCommand, Current),
                    Obs.Command("tu-2", 1, Obs.TestCommand, state: null, batchSize: 2),
                    Obs.Read("tu-3", 1, batchSize: 2)),
                Obs.Build),
            "state_may_have_changed_later");

    [Fact]
    public void A_duplicated_tool_use_id_creates_no_authority()
        => AssertRerun(
            Evaluate(Obs.Evidence(
                Obs.Command("tu-1", 0, Obs.TestCommand, Current),
                Obs.Command("tu-1", 0, Obs.TestCommand, Current))),
            "duplicate_event");

    [Fact]
    public void One_observed_execution_serves_at_most_one_check()
    {
        var twin = new VerificationCheckSpec(new VerificationCheckId("tests_again"), "the same command, declared twice", "dotnet", ["test", "Fixture.sln"], TimeSpan.FromMinutes(5));
        var evaluator = new ExecutionEvidenceEvaluator([Obs.Tests, twin]);
        var evidence = Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current));
        var used = new HashSet<string>();

        var first = evaluator.Evaluate(Obs.Workspace, Obs.Tests, evidence, Current, used);
        var second = evaluator.Evaluate(Obs.Workspace, twin, evidence, Current, used);

        Assert.True(first.Reusable);
        AssertRerun(second, "already_used_by_another_check");
    }

    [Fact]
    public void Evidence_that_belongs_to_an_older_state_is_stale_for_a_newer_one()
    {
        var older = Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Obs.Fingerprint("turn-1-state")));

        AssertRerun(Evaluate(older, current: Obs.Fingerprint("turn-2-state")), "state_mismatch");
    }

    // ---- a configured .NET filter that contains `!~` ------------------------------------------------------------------------

    private const string FilteredTestCommand =
        "dotnet test --no-build --filter FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script";

    private static readonly VerificationCheckSpec FilteredTests = new(
        new VerificationCheckId("tests"),
        "the suite passes, minus one environment-dependent test",
        "dotnet",
        [
            "test",
            "--no-build",
            "--filter",
            "FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script",
        ],
        TimeSpan.FromMinutes(5));

    private static EvidenceDecision EvaluateFiltered(VerificationCheckSpec check, ObservedTurnEvidence evidence, ISet<string>? used = null)
        => new ExecutionEvidenceEvaluator([Obs.Build, FilteredTests]).Evaluate(Obs.Workspace, check, evidence, Current, used ?? new HashSet<string>());

    [Fact]
    public void The_exact_build_and_the_exact_filtered_test_after_the_final_edit_are_both_reused()
    {
        var evidence = Obs.Evidence(
            Obs.Edit("tu-edit", 0),
            Obs.Command("tu-build", 1, Obs.BuildCommand, Current),
            Obs.Command("tu-test", 2, FilteredTestCommand, Current));
        var used = new HashSet<string>();

        var build = EvaluateFiltered(Obs.Build, evidence, used);
        var tests = EvaluateFiltered(FilteredTests, evidence, used);

        Assert.True(build.Reusable);
        Assert.Equal("agent_observed_current_state", build.Reason);
        Assert.Equal("tu-build", build.Observation!.ToolUseId);
        Assert.True(tests.Reusable);
        Assert.Equal("agent_observed_current_state", tests.Reason);
        Assert.Equal("tu-test", tests.Observation!.ToolUseId);
        Assert.Equal(FilteredTestCommand, tests.Observation.CommandIdentity);
    }

    [Fact]
    public void A_different_filter_is_a_command_mismatch_and_still_invalidates_the_build_conservatively()
    {
        var evidence = Obs.Evidence(
            Obs.Command("tu-build", 0, Obs.BuildCommand, Current),
            Obs.Command("tu-test", 1, "dotnet test --no-build --filter FullyQualifiedName!~SomethingElse", Current));

        AssertRerun(EvaluateFiltered(FilteredTests, evidence), "command_mismatch");
        AssertRerun(EvaluateFiltered(Obs.Build, evidence), "state_may_have_changed_later");
    }

    [Theory]
    [InlineData(" && echo pwned")]
    [InlineData("; echo pwned")]
    [InlineData(" | tee x")]
    [InlineData(" > x")]
    [InlineData(" < x")]
    [InlineData(" $(echo x)")]
    [InlineData(" `echo x`")]
    [InlineData(" & echo pwned")]
    public void A_shell_composed_command_containing_the_filtered_check_is_never_reused_and_never_inert(string suffix)
    {
        var composed = FilteredTestCommand + suffix;
        var evidence = Obs.Evidence(
            Obs.Command("tu-build", 0, Obs.BuildCommand, Current),
            Obs.Command("tu-test", 1, composed, Current));

        AssertRerun(EvaluateFiltered(FilteredTests, evidence), "command_mismatch");
        AssertRerun(EvaluateFiltered(Obs.Build, evidence), "state_may_have_changed_later");
    }

    [Theory]
    [InlineData("cd src && " + FilteredTestCommand)]
    [InlineData("bash -c \"" + FilteredTestCommand + "\"")]
    [InlineData("powershell -Command \"" + FilteredTestCommand + "\"")]
    [InlineData("echo x; " + FilteredTestCommand)]
    public void The_filtered_check_embedded_in_another_shell_form_is_never_reused(string command)
        => AssertRerun(EvaluateFiltered(FilteredTests, Obs.Evidence(Obs.Command("tu-test", 0, command, Current))), "command_mismatch");

    [Fact]
    public void A_later_edit_still_invalidates_the_filtered_test()
        => AssertRerun(
            EvaluateFiltered(
                FilteredTests,
                Obs.Evidence(
                    Obs.Command("tu-test", 0, FilteredTestCommand, Current),
                    Obs.Edit("tu-edit", 1))),
            "state_may_have_changed_later");

    [Fact]
    public void A_check_that_the_agent_never_ran_has_no_evidence_even_when_others_do()
    {
        var evidence = Obs.Evidence(Obs.Command("tu-1", 0, Obs.TestCommand, Current));

        AssertRerun(Evaluate(evidence, Obs.ControllerOnly), "command_mismatch");
    }
}
