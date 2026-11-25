using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>The direct-path loop against a fake runtime, workspace and verifier (no Herdr, Claude, Git or SQLite).</summary>
public sealed class DirectPathRunnerTests
{
    private static int Events(DirectHarness h, string kind)
        => h.Store.History(Fixture.TaskOne).Count(e => e.Payload.Kind == kind);

    // ---- success -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Operating_notes_are_sent_in_the_first_prompt_only_when_supplied()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());
        await h.RunAsync(definition: h.Definition() with { OperatingNotes = "Use git grep; run dotnet test alone." });
        Assert.Contains("Use git grep; run dotnet test alone.", h.Runtime.Sends[0].Prompt);

        var plain = await DirectHarness.CreateAsync();
        plain.Runtime.Enqueue(plain.Renames());
        await plain.RunAsync();
        Assert.DoesNotContain("git grep", plain.Runtime.Sends[0].Prompt);
    }

    [Fact]
    public async Task One_turn_success_records_revision_then_verification_then_opens_the_accept_gate()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(1, result.TurnsUsed);
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);

        var kinds = h.Store.History(Fixture.TaskOne).Select(e => e.Payload.Kind).TakeLast(3).ToArray();
        Assert.Equal(
            [EventKinds.ImplementationRevisionRecorded, EventKinds.VerificationRecorded, EventKinds.GateOpened],
            kinds);

        var task = (await h.ReloadAsync());
        Assert.Equal(task.CurrentImplementationRevisionId, task.CurrentVerification!.ImplementationRevisionId);
        Assert.Contains(h.Verifier.Ran, id => id == "tests");
    }

    [Fact]
    public async Task The_first_prompt_carries_the_objective_limits_and_checks_but_not_the_workflow_spec()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        await h.RunAsync();

        var prompt = Assert.Single(h.Runtime.Sends).Prompt;
        Assert.Contains("GetMeal", prompt);
        Assert.Contains("src/", prompt);
        Assert.Contains("Fixture.sln", prompt);
        Assert.Contains("tests: dotnet test passes", prompt);
        Assert.Contains("Do not commit, push", prompt);
        Assert.DoesNotContain("Workflow V1.1", prompt);
        Assert.False(prompt.TrimStart().StartsWith('/'));
    }

    // ---- the agent's "done" is not completion ------------------------------------------------------------------------

    [Fact]
    public async Task Agent_done_without_a_real_change_is_not_completion()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing());

        var result = await h.RunAsync(h.Definition(maxTurns: 1));

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);
        Assert.Equal(WorkflowState.Blocked, (await h.ReloadAsync()).WorkflowState);
        Assert.Equal(1, Events(h, EventKinds.WorkflowStateChanged));
        Assert.Equal(VerificationOutcome.Failed, (await h.ReloadAsync()).CurrentVerification!.Outcome);
    }

    [Fact]
    public async Task The_runner_opens_the_accept_gate_instead_of_using_a_generic_state_change()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        await h.RunAsync();

        Assert.Equal(0, Events(h, EventKinds.WorkflowStateChanged));
        var opened = Assert.Single(h.Store.History(Fixture.TaskOne).Select(e => e.Payload).OfType<GateOpened>());
        var task = (await h.ReloadAsync());
        Assert.Equal(opened.Gate, task.PendingGate);
        Assert.Equal(task.CurrentImplementationRevision!.Id, opened.Gate.ImplementationRevisionId);
        Assert.Equal(task.CurrentVerification!.Id, opened.Gate.VerificationRunId);
        Assert.StartsWith("ACCEPT gate", task.StateReason);
    }

    // ---- repair in the same session ---------------------------------------------------------------------------------

    [Fact]
    public async Task Failed_verification_sends_concise_evidence_to_the_same_session_and_then_completes()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Equal(2, result.TurnsUsed);

        Assert.Single(h.Runtime.Starts);
        Assert.Equal(2, h.Runtime.Sends.Count);
        Assert.Equal(h.Runtime.Sends[0].Session, h.Runtime.Sends[1].Session);

        var repair = h.Runtime.Sends[1].Prompt;
        Assert.Contains("Controller verification failed", repair);
        Assert.Contains("check: tests", repair);
        Assert.Contains("GreetingTests.ExpectedGreeting failed.", repair);
        Assert.True(repair.Length < 800, "repair feedback must stay concise");

        var task = await h.ReloadAsync();
        Assert.Equal(2, task.Verifications.Length);
        Assert.Equal(VerificationOutcome.Failed, task.Verifications[0].Outcome);
        Assert.Equal(VerificationOutcome.Passed, task.Verifications[1].Outcome);
        Assert.NotEqual(task.Verifications[0].ImplementationRevisionId, task.Verifications[1].ImplementationRevisionId);
        Assert.Equal(task.CurrentImplementationRevisionId, task.Verifications[1].ImplementationRevisionId);
    }

    [Fact]
    public async Task Repair_feedback_is_bounded_even_when_the_check_output_is_huge()
    {
        var h = await DirectHarness.CreateAsync();
        h.Verifier.Override = check => new CheckExecution(1, false, TimeSpan.Zero, new string('x', 100_000), string.Empty);
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), DirectHarness.DoesNothing());

        await h.RunAsync(h.Definition(maxTurns: 2));

        Assert.True(h.Runtime.Sends[1].Prompt.Length < 3000);
    }

    [Fact]
    public async Task The_turn_limit_ends_unresolved_never_completed_or_failed()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());

        var result = await h.RunAsync(h.Definition(maxTurns: 3));

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);
        Assert.Equal(3, result.TurnsUsed);
        Assert.Equal(3, h.Runtime.Sends.Count);
        Assert.StartsWith("turn_limit_reached", result.Reason);

        // not completed, not failed, and not RUNNING with nobody working: BLOCKED once, with a reason that is neither M4D's marker nor a runtime failure
        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(UnresolvedVerificationBlock.IsBlockedBy(task));
        Assert.False(RuntimeFailureBlock.IsBlockedBy(task));
        Assert.Null(RecoverableContinuation.Find(task));
        Assert.Equal(1, Events(h, EventKinds.WorkflowStateChanged));
        Assert.Single(h.Runtime.Stops);
    }

    // ---- agent error / cancelled ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentTurnStatus.Error, DirectRunStatus.AgentError)]
    [InlineData(AgentTurnStatus.Cancelled, DirectRunStatus.AgentCancelled)]
    public async Task Error_and_cancelled_turns_stop_the_run_blocked_without_fabricating_workflow_meaning(
        AgentTurnStatus status,
        DirectRunStatus expected)
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(Outcome: new AgentTurnOutcome(status, "needs a human")));
        var before = h.Store.History(Fixture.TaskOne).Count;

        var result = await h.RunAsync();

        Assert.Equal(expected, result.Status);
        Assert.Contains("needs a human", result.Reason);

        // no revision, no verification, no Failed: the only thing recorded is that nothing can progress any more (BLOCKED, once)
        var task = await h.ReloadAsync();
        Assert.Equal(before + 1, h.Store.History(Fixture.TaskOne).Count);
        Assert.Equal(1, Events(h, EventKinds.WorkflowStateChanged));
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(RuntimeFailureBlock.IsBlockedBy(task));
        Assert.Contains("needs a human", task.StateReason);
        Assert.Null(RecoverableContinuation.Find(task));
        Assert.Empty(h.Verifier.Ran);
        Assert.Single(h.Runtime.Stops);
    }

    // ---- refusals happen before the runtime is touched -----------------------------------------------------------------

    [Fact]
    public async Task An_unbound_role_is_rejected_and_nothing_is_launched()
    {
        var h = await DirectHarness.CreateAsync(bindRoot: false);

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("role_unbound", result.Reason);
        Assert.Empty(h.Runtime.Starts);
        Assert.Equal(0, h.Workspace.Captures);
    }

    [Fact]
    public async Task A_profile_mismatch_is_rejected_with_no_substitution()
    {
        var h = await DirectHarness.CreateAsync(boundProfile: "some-other-profile");

        var result = await h.RunAsync(h.Definition(profile: DirectHarness.ProfileName));

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("profile_mismatch", result.Reason);
        Assert.Empty(h.Runtime.Starts);
        Assert.Empty(h.Runtime.Sends);
    }

    [Theory]
    [InlineData("local_commit")]
    [InlineData("remote_branch")]
    [InlineData("deployment")]
    public async Task An_unsupported_completion_boundary_is_rejected_before_launch(string boundary)
    {
        var h = await DirectHarness.CreateAsync(new CompletionBoundary(boundary));

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("unsupported_completion_boundary", result.Reason);
        Assert.Empty(h.Runtime.Starts);
    }

    [Theory]
    [InlineData(WorkflowState.AwaitingHuman)]
    [InlineData(WorkflowState.Blocked)]
    public async Task A_task_that_is_not_running_is_rejected(WorkflowState state)
    {
        var h = await DirectHarness.CreateAsync();
        await h.Service.ChangeWorkflowStateAsync(h.Snapshot, state, "waiting");

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("task_not_running", result.Reason);
        Assert.Empty(h.Runtime.Starts);
    }

    [Fact]
    public async Task An_unknown_task_is_rejected()
    {
        var h = await DirectHarness.CreateAsync();

        var result = await h.Runner().RunAsync(Fixture.TaskTwo, h.Definition());

        Assert.Equal(DirectRunStatus.Rejected, result.Status);
        Assert.StartsWith("task_not_found", result.Reason);
        Assert.Empty(h.Runtime.Starts);
    }

    // ---- scope ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_protected_path_change_blocks_completion_even_when_every_check_passes()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(() =>
        {
            h.Workspace.RenameToGetFood();
            h.Workspace.Files["Fixture.sln"] = "tampered";
        }));

        var result = await h.RunAsync(h.Definition(maxTurns: 1));

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);
        var violation = Assert.Single(Assert.Single(result.Turns).ScopeViolations);
        Assert.Equal("Fixture.sln", violation.Path);

        var verification = (await h.ReloadAsync()).CurrentVerification!;
        Assert.Equal(VerificationOutcome.Failed, verification.Outcome);
        Assert.Contains(verification.Checks, c => c.CheckId == DirectPathRunner.ScopeCheckId && c.Outcome == VerificationOutcome.Failed);
        Assert.Contains(verification.Checks, c => c.CheckId == DirectHarness.TestsCheck && c.Outcome == VerificationOutcome.Passed);
    }

    [Fact]
    public async Task An_out_of_scope_new_file_is_reported_and_repaired_in_the_same_session()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(
            new TurnScript(() =>
            {
                h.Workspace.RenameToGetFood();
                h.Workspace.Files["notes/scratch.txt"] = "x";
            }),
            new TurnScript(() => h.Workspace.Files.Remove("notes/scratch.txt")));

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Contains("notes/scratch.txt", h.Runtime.Sends[1].Prompt);
        Assert.Contains("outside the allowed scope", h.Runtime.Sends[1].Prompt);
    }

    [Fact]
    public async Task Moving_head_is_a_scope_violation()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(() =>
        {
            h.Workspace.RenameToGetFood();
            h.Workspace.Head = "HEAD-2";
        }));

        var result = await h.RunAsync(h.Definition(maxTurns: 1));

        Assert.NotEqual(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Contains(Assert.Single(result.Turns).ScopeViolations, v => v.Path == "(repository history)");
    }

    [Fact]
    public async Task Pre_existing_changes_are_not_attributed_to_the_agent()
    {
        var h = await DirectHarness.CreateAsync();
        h.Workspace.Files["Fixture.sln"] = "already dirty before the run";
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Empty(Assert.Single(result.Turns).ScopeViolations);
    }

    // ---- currentness ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_existing_revision_for_identical_files_is_not_duplicated_across_turns()
    {
        var h = await DirectHarness.CreateAsync();
        h.Verifier.Override = check => new CheckExecution(1, false, TimeSpan.Zero, "no", string.Empty);
        h.Runtime.Enqueue(h.Renames(), DirectHarness.DoesNothing());

        await h.RunAsync(h.Definition(maxTurns: 2));

        Assert.Equal(1, Events(h, EventKinds.ImplementationRevisionRecorded));
        Assert.Equal(2, Events(h, EventKinds.VerificationRecorded));
    }

    [Fact]
    public async Task A_new_implementation_revision_is_recorded_when_files_change_between_turns()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());

        await h.RunAsync();

        Assert.Equal(2, Events(h, EventKinds.ImplementationRevisionRecorded));
    }

    // ---- stale decisions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_task_that_moves_while_the_agent_works_stops_the_run_with_nothing_applied()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(
            h.Workspace.RenameToGetFood,
            WhileWorking: async () => await h.Service.CreateSemanticRevisionAsync(
                (await h.Service.LoadAsync(Fixture.TaskOne)),
                SemanticChangeKind.RequirementChange,
                "A human changed the requirement mid-run.")));

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.StaleWorkflowState, result.Status);
        Assert.Equal(0, Events(h, EventKinds.ImplementationRevisionRecorded));
        Assert.Equal(0, Events(h, EventKinds.VerificationRecorded));
        Assert.Equal(WorkflowState.Running, (await h.ReloadAsync()).WorkflowState);
        Assert.Single(h.Runtime.Stops);

        // the reported snapshot is the reloaded, newer state, not the stale one
        Assert.True(result.Task!.Version > h.Snapshot.Version);
    }

    [Fact]
    public async Task A_competing_writer_between_verification_and_completion_stops_the_run_stale()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        // the third append of the run is the completion; another writer gets there first
        var appends = 0;
        h.Store.BeforeNextAppend = Hook();
        Action Hook() => () =>
        {
            appends++;
            if (appends < 3)
            {
                h.Store.BeforeNextAppend = Hook();
                return;
            }

            var rival = new WorkflowTaskService(h.Store, new RandomIds(), new SteppingClock(Fixture.T0.AddDays(1)));
            var current = rival.LoadAsync(Fixture.TaskOne).GetAwaiter().GetResult();
            rival.RecordImplementationRevisionAsync(current, new ImplementationFingerprint("someone-else")).GetAwaiter().GetResult();
        };

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.StaleWorkflowState, result.Status);
        Assert.NotEqual(WorkflowState.Completed, (await h.ReloadAsync()).WorkflowState);
    }

    // ---- unknown persistence outcome ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_storage_failure_is_indeterminate_reloaded_and_never_blindly_repeated()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(new TurnScript(
            h.Workspace.RenameToGetFood,
            WhileWorking: () =>
            {
                h.Store.FailNextAppend = new IOException("disk went away");
                return Task.CompletedTask;
            }));
        var appendsBefore = h.Store.AppendCalls;

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Indeterminate, result.Status);
        Assert.Contains("not repeated", result.Reason);
        Assert.NotNull(result.Task);
        Assert.Equal(1, h.Store.AppendCalls - appendsBefore);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task Cancellation_during_a_workflow_mutation_is_indeterminate_not_a_failure_and_not_repeated()
    {
        var h = await DirectHarness.CreateAsync();
        using var cts = new CancellationTokenSource();
        h.Runtime.Enqueue(h.Renames());
        h.Store.BeforeNextAppend = cts.Cancel;
        var appendsBefore = h.Store.AppendCalls;

        var result = await h.RunAsync(ct: cts.Token);

        Assert.Equal(DirectRunStatus.Indeterminate, result.Status);
        Assert.Equal(1, h.Store.AppendCalls - appendsBefore);
        Assert.NotNull(result.Task);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task Cancellation_while_the_agent_works_is_interrupted_and_the_run_is_blocked_not_running()
    {
        var h = await DirectHarness.CreateAsync();
        using var cts = new CancellationTokenSource();
        h.Runtime.Enqueue(new TurnScript(h.Workspace.RenameToGetFood, WhileWorking: () =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        }));
        var before = h.Store.History(Fixture.TaskOne).Count;

        var result = await h.RunAsync(ct: cts.Token);

        Assert.Equal(DirectRunStatus.Interrupted, result.Status);
        Assert.Equal(before + 1, h.Store.History(Fixture.TaskOne).Count);
        Assert.True(RuntimeFailureBlock.IsBlockedBy(await h.ReloadAsync()));
        Assert.Single(h.Runtime.Stops);
    }

    // ---- session ownership and cleanup -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_session_started_by_the_run_is_stopped_exactly_once_and_nothing_else_is_touched()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal([result.Session!.Value], h.Runtime.Stops);
        Assert.DoesNotContain(h.Runtime.Unrelated[0], h.Runtime.Stops);
        Assert.Equal(h.Runtime.Starts.Single().Label, $"task {Fixture.TaskOne}");
        Assert.Equal(h.Runtime.Starts.Single().Profile, new ProfileId(DirectHarness.ProfileName));
    }

    [Fact]
    public async Task A_failing_stop_is_reported_as_a_note_and_does_not_change_the_result()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());
        h.Runtime.FailStop = new AgentRuntimeException("hc kill failed");

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Contains(result.Notes, n => n.Contains("hc kill failed"));
    }

    [Fact]
    public async Task A_runtime_that_cannot_start_ends_as_an_execution_failure_with_no_session_to_stop()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.FailStart = new AgentRuntimeException("herdr unavailable");

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.Contains("herdr unavailable", result.Reason);
        Assert.Empty(h.Runtime.Stops);

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.StartsWith(RuntimeFailureBlock.BlockedReasonPrefix, task.StateReason);
        Assert.Contains("herdr unavailable", task.StateReason);
    }

    [Fact]
    public async Task A_runtime_failure_mid_turn_still_stops_the_session()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.FailWait = new AgentRuntimeException("event connection lost");

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.ExecutionFailure, result.Status);
        Assert.Single(h.Runtime.Stops);
    }

    [Fact]
    public async Task Keeping_the_session_alive_is_an_explicit_option()
    {
        var h = await DirectHarness.CreateAsync();
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync(options: new DirectRunOptions { StopSessionWhenDone = false });

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        Assert.Empty(h.Runtime.Stops);
    }

    // ---- definition validation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_definition_needs_checks_a_sane_turn_budget_and_unique_check_ids()
    {
        var h = await DirectHarness.CreateAsync();
        var good = h.Definition();

        Assert.Throws<ArgumentException>(() => new DirectTaskDefinition(
            "x", @"C:\w", good.RootRole, good.ExecutionProfile, good.Scope, [], 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Definition(maxTurns: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Definition(maxTurns: DirectTaskDefinition.MaxAllowedTurns + 1));
        Assert.Throws<ArgumentException>(() => new DirectTaskDefinition(
            "x", @"C:\w", good.RootRole, good.ExecutionProfile, good.Scope, [good.Checks[0], good.Checks[0]], 3));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/rooted")]
    [InlineData("C:/abs")]
    public void Scope_entries_must_stay_inside_the_workspace(string entry)
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceScope([entry]));
    }

    [Fact]
    public void Protected_paths_win_over_the_allow_list_and_matching_ignores_case_and_separators()
    {
        var scope = new WorkspaceScope(["src/"], ["src/Secrets/"]);

        var violations = scope.Evaluate(["SRC/ok.cs", @"src\secrets\key.txt", "other.txt"]);

        Assert.Equal(["other.txt", "src/secrets/key.txt"], violations.Select(v => v.Path).Order(StringComparer.Ordinal).ToArray());
    }
}

internal sealed class RandomIds : IIdSource
{
    public Guid Next() => Guid.NewGuid();
}
