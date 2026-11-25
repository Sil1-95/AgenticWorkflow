using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// the acceptance requirement is the task's own persisted semantics. With none required the controller completes verified work itself, with no gate; with the
/// human required the M4A gate is unchanged. Neither policy lets failed or stale verification, or the agent's own report, complete a task.
/// </summary>
public sealed class DirectAcceptancePolicyTests
{
    private static readonly ImplementationFingerprint FingerprintA = new("fp-a");

    private static VerificationCheckResult Pass(string id = "tests")
        => new(new VerificationCheckId(id), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail(string id = "tests")
        => new(new VerificationCheckId(id), VerificationOutcome.Failed, "broken", 1);

    private static async Task<DirectHarness> VerifiedAsync(AcceptanceRequirement acceptance)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;
        return h;
    }

    private static string[] Kinds(DirectHarness h) => [.. h.Store.History(Fixture.TaskOne).Select(e => e.Payload.Kind)];

    // ---- the requirement is the task's own, persisted semantics -------------------------------------------------------------

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired)]
    [InlineData(AcceptanceRequirement.HumanRequired)]
    public async Task The_requirement_given_at_creation_is_recorded_in_the_first_event_and_reloaded(AcceptanceRequirement acceptance)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);

        Assert.Equal(acceptance, ((TaskCreated)h.Store.History(Fixture.TaskOne)[0].Payload).Acceptance);
        Assert.Equal(acceptance, (await h.ReloadAsync()).Acceptance);
    }

    [Fact]
    public async Task A_task_created_without_naming_a_requirement_does_not_require_acceptance()
    {
        var service = Fixture.NewService(new FakeTaskEventStore());

        var created = await service.CreateAsync(Fixture.TaskOne, "intent", CompletionBoundary.WorkingTree);

        Assert.Equal(AcceptanceRequirement.NotRequired, created.Task.Acceptance);
    }

    // ---- no acceptance required: the controller completes ----------------------------------------------------------------

    [Fact]
    public async Task Verified_work_completes_with_no_gate_no_awaiting_human_and_no_further_session()
    {
        var h = await DirectHarness.CreateAsync(acceptance: AcceptanceRequirement.NotRequired);
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.True(result.IsCompleted);
        Assert.Equal(1, result.TurnsUsed);

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Completed, task.WorkflowState);
        Assert.Empty(task.Gates);
        Assert.Empty(task.GateDecisions);

        // the history says: revision, verification, and then the controller's own completion; never a gate or an awaiting-human state
        Assert.Equal(
            [EventKinds.ImplementationRevisionRecorded, EventKinds.VerificationRecorded, EventKinds.WorkflowStateChanged],
            Kinds(h)[^3..]);
        Assert.DoesNotContain(EventKinds.GateOpened, Kinds(h));
        Assert.DoesNotContain(EventKinds.GateDecided, Kinds(h));
        Assert.DoesNotContain(h.Store.History(Fixture.TaskOne), e => e.Payload is WorkflowStateChanged { To: WorkflowState.AwaitingHuman });

        // the completion rests on the current passing verification
        Assert.Equal(task.CurrentImplementationRevisionId, task.CurrentVerification!.ImplementationRevisionId);
        Assert.Equal(VerificationOutcome.Passed, task.CurrentVerification.Outcome);
        Assert.Single(h.Runtime.Starts);
        Assert.Single(h.Runtime.Stops);
        Assert.Equal([DirectCompletionReasons.TaskTerminal], DirectPathCompletion.Evaluate(task).Reasons);
    }

    [Fact]
    public async Task Repair_happens_in_the_same_session_before_the_controller_completes()
    {
        var h = await DirectHarness.CreateAsync(acceptance: AcceptanceRequirement.NotRequired);
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Single(h.Runtime.Starts);

        var task = await h.ReloadAsync();
        Assert.Equal(VerificationOutcome.Failed, task.Verifications[0].Outcome);
        Assert.Equal(VerificationOutcome.Passed, task.Verifications[1].Outcome);
        Assert.Equal(task.CurrentImplementationRevisionId, task.Verifications[1].ImplementationRevisionId);

        // exactly one completion, after the repairing verification and nothing earlier
        var completion = Assert.Single(h.Store.History(Fixture.TaskOne), e => e.Payload is WorkflowStateChanged { To: WorkflowState.Completed });
        Assert.Equal(h.Store.History(Fixture.TaskOne)[^1], completion);
        Assert.Equal(EventKinds.VerificationRecorded, h.Store.History(Fixture.TaskOne)[^2].Payload.Kind);
    }

    [Theory]
    [InlineData(AcceptanceRequirement.NotRequired)]
    [InlineData(AcceptanceRequirement.HumanRequired)]
    public async Task Failing_verification_never_completes_either_policy_and_never_opens_a_gate(AcceptanceRequirement acceptance)
    {
        var h = await DirectHarness.CreateAsync(acceptance: acceptance);
        h.Runtime.Enqueue(DirectHarness.DoesNothing(), DirectHarness.DoesNothing(), DirectHarness.DoesNothing());

        // the agent reports "done" on every turn; only the controller's checks matter
        var result = await h.RunAsync(h.Definition(maxTurns: 3));

        Assert.Equal(DirectRunStatus.VerificationUnresolved, result.Status);

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Blocked, task.WorkflowState);
        Assert.True(UnresolvedVerificationBlock.IsBlockedBy(task));
        Assert.Empty(task.Gates);
        Assert.DoesNotContain(h.Store.History(Fixture.TaskOne), e => e.Payload is WorkflowStateChanged { To: WorkflowState.Completed });
    }

    [Fact]
    public async Task A_workspace_that_moves_while_the_checks_run_does_not_complete_the_task_on_that_verification()
    {
        var h = await DirectHarness.CreateAsync(acceptance: AcceptanceRequirement.NotRequired);
        h.Runtime.Enqueue(h.Renames(), DirectHarness.DoesNothing());

        var edited = false;
        h.Verifier.Override = _ =>
        {
            if (!edited)
            {
                edited = true;
                h.Workspace.Files["src/Greeting.cs"] += "\n// edited while the checks ran";
            }

            return new CheckExecution(0, false, TimeSpan.FromMilliseconds(5), "Passed!", string.Empty);
        };

        var result = await h.RunAsync();
        var task = await h.ReloadAsync();

        // the first (unstable) verification failed and could not complete the task; only the verification of the re-observed revision did
        Assert.Equal(VerificationOutcome.Failed, task.Verifications[0].Outcome);
        Assert.Equal(DirectRunStatus.Completed, result.Status);
        Assert.Equal(2, task.ImplementationRevisions.Length);
        Assert.Equal(task.ImplementationRevisions[^1].Id, task.CurrentVerification!.ImplementationRevisionId);
    }

    // ---- the controller's completion operation -----------------------------------------------------------------------------

    [Fact]
    public async Task The_controller_completes_a_verified_task_that_requires_no_acceptance()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);

        var change = await h.Service.CompleteVerifiedAsync(h.Snapshot);

        Assert.Equal(WorkflowState.Completed, change.Task.WorkflowState);
        Assert.Empty(change.Task.Gates);
    }

    [Fact]
    public async Task Completion_is_refused_without_a_current_passing_verification_and_writes_nothing()
    {
        var h = await DirectHarness.CreateAsync(acceptance: AcceptanceRequirement.NotRequired);
        var before = h.Store.History(Fixture.TaskOne).Count;

        // no implementation at all
        await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));

        // implemented but unverified
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        var unverified = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));
        Assert.Contains(DirectCompletionReasons.CurrentImplementationNotVerified, unverified.Eligibility.Reasons);

        // verified, but failing
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass("a"), Fail("b")])).Snapshot;
        var failing = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));
        Assert.Contains(DirectCompletionReasons.CurrentVerificationFailed, failing.Eligibility.Reasons);

        Assert.Equal(before + 2, h.Store.History(Fixture.TaskOne).Count);
        Assert.Equal(WorkflowState.Running, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task An_old_pass_does_not_complete_a_newer_implementation_or_a_newer_meaning()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        var newer = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, new ImplementationFingerprint("fp-b"))).Snapshot;

        var unverified = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(newer));
        Assert.Contains(DirectCompletionReasons.CurrentImplementationNotVerified, unverified.Eligibility.Reasons);

        var verified = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        var reworded = (await verified.Service.CreateSemanticRevisionAsync(verified.Snapshot, SemanticChangeKind.RequirementChange, "changed")).Snapshot;

        var stale = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => verified.Service.CompleteVerifiedAsync(reworded));
        Assert.Contains(DirectCompletionReasons.ImplementationTargetsStaleSemantics, stale.Eligibility.Reasons);
    }

    [Fact]
    public async Task Completion_on_a_stale_basis_is_refused()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        await h.Service.RecordImplementationRevisionAsync(h.Snapshot, new ImplementationFingerprint("fp-b"));

        await Assert.ThrowsAsync<StaleWorkflowStateException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));
    }

    [Fact]
    public async Task Only_the_working_tree_boundary_completes()
    {
        var h = await DirectHarness.CreateAsync(CompletionBoundary.LocalCommit, acceptance: AcceptanceRequirement.NotRequired);
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;

        var refused = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));

        Assert.Contains(DirectCompletionReasons.UnsupportedCompletionBoundary, refused.Eligibility.Reasons);
    }

    [Fact]
    public async Task A_task_waiting_for_a_native_interaction_is_not_completed_by_the_controller()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        h.Snapshot = (await h.Service.ChangeWorkflowStateAsync(h.Snapshot, WorkflowState.AwaitingHuman, "The agent waits in its own interface.")).Snapshot;

        var refused = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));

        Assert.Contains(DirectCompletionReasons.TaskNotRunning, refused.Eligibility.Reasons);
    }

    [Fact]
    public async Task A_completed_task_is_terminal()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        var done = await h.Service.CompleteVerifiedAsync(h.Snapshot);

        var again = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.CompleteVerifiedAsync(done.Snapshot));
        Assert.Contains(DirectCompletionReasons.TaskTerminal, again.Eligibility.Reasons);
        await Assert.ThrowsAsync<TaskTerminatedException>(() => h.Service.ChangeWorkflowStateAsync(done.Snapshot, WorkflowState.Running, "Reopen."));
        Assert.Contains(DirectCompletionReasons.TaskTerminal, DirectPathCompletion.Evaluate(done.Task).Reasons);
    }

    [Fact]
    public async Task The_generic_state_change_still_cannot_reach_completed_under_either_policy()
    {
        foreach (var acceptance in new[] { AcceptanceRequirement.NotRequired, AcceptanceRequirement.HumanRequired })
        {
            var h = await VerifiedAsync(acceptance);

            await Assert.ThrowsAsync<ArgumentException>(() => h.Service.ChangeWorkflowStateAsync(h.Snapshot, WorkflowState.Completed, "done"));
        }
    }

    // ---- acceptance required: the M4A gate, unchanged -------------------------------------------------------------------------

    [Fact]
    public async Task The_controller_cannot_complete_a_task_that_requires_the_human()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.HumanRequired);

        await Assert.ThrowsAsync<AcceptanceRequiredException>(() => h.Service.CompleteVerifiedAsync(h.Snapshot));

        Assert.Equal(WorkflowState.Running, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task Verified_work_that_requires_acceptance_opens_the_gate_and_does_not_complete()
    {
        var h = await DirectHarness.CreateAsync(acceptance: AcceptanceRequirement.HumanRequired);
        h.Runtime.Enqueue(h.Renames());

        var result = await h.RunAsync();

        Assert.Equal(DirectRunStatus.AwaitingAcceptance, result.Status);
        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.NotNull(task.PendingGate);
        Assert.DoesNotContain(h.Store.History(Fixture.TaskOne), e => e.Payload is WorkflowStateChanged { To: WorkflowState.Completed });
    }

    [Fact]
    public async Task A_task_that_requires_acceptance_cannot_complete_before_its_gate_is_accepted()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.HumanRequired);

        // verified and current, but no gate has been accepted: not eligible
        Assert.Contains(DirectCompletionReasons.NoPendingAcceptGate, DirectPathCompletion.Evaluate(h.Snapshot.Task).Reasons);
        // the shared evidence is all there; what is missing is the human
        Assert.True(DirectPathCompletion.EvaluateVerifiedImplementation(h.Snapshot.Task).IsEligible);

        var opened = (await h.Service.OpenAcceptGateAsync(h.Snapshot)).Snapshot;
        Assert.True(DirectPathCompletion.Evaluate(opened.Task).IsEligible);
        Assert.Equal(WorkflowState.AwaitingHuman, opened.Task.WorkflowState);

        var accepted = await h.Service.AcceptGateAsync(opened, opened.Task.PendingGate!.Id);
        Assert.Equal(WorkflowState.Completed, accepted.Task.WorkflowState);
    }

    [Fact]
    public async Task The_eligibility_of_a_task_without_acceptance_is_the_shared_evidence_plus_running()
    {
        var verified = await VerifiedAsync(AcceptanceRequirement.NotRequired);

        Assert.True(DirectPathCompletion.Evaluate(verified.Snapshot.Task).IsEligible);
        Assert.Equal(
            DirectPathCompletion.EvaluateVerifiedImplementation(verified.Snapshot.Task).Reasons,
            DirectPathCompletion.Evaluate(verified.Snapshot.Task).Reasons);

        // for the same evidence, a human-required task is not eligible to complete until its gate exists
        var human = await VerifiedAsync(AcceptanceRequirement.HumanRequired);
        Assert.False(DirectPathCompletion.Evaluate(human.Snapshot.Task).IsEligible);
    }

    // ---- no gate exists for a task without acceptance --------------------------------------------------------------------------

    [Fact]
    public async Task No_gate_can_be_opened_for_a_task_that_requires_no_acceptance()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);

        await Assert.ThrowsAsync<InvalidGateException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));

        Assert.Empty((await h.ReloadAsync()).Gates);
    }

    [Fact]
    public async Task Every_gate_decision_on_a_task_without_acceptance_is_refused_because_no_gate_exists()
    {
        var h = await VerifiedAsync(AcceptanceRequirement.NotRequired);
        await h.Service.CompleteVerifiedAsync(h.Snapshot);
        var gates = new AcceptGateService(h.Service, h.Workspace);
        var before = h.Store.History(Fixture.TaskOne);

        var accept = await Assert.ThrowsAsync<GateRefusedException>(() => gates.AcceptAsync(Fixture.TaskOne, null, @"C:\fixture"));
        var request = await Assert.ThrowsAsync<GateRefusedException>(() => gates.RequestChangesAsync(Fixture.TaskOne, null, "please change it"));
        var cancel = await Assert.ThrowsAsync<GateRefusedException>(() => gates.CancelAsync(Fixture.TaskOne, null));
        var named = await Assert.ThrowsAsync<GateRefusedException>(() => gates.AcceptAsync(Fixture.TaskOne, new Core.Identity.GateId(Guid.NewGuid()), @"C:\fixture"));

        Assert.All([accept, request, cancel, named], e => Assert.Equal(GateRefusal.AcceptanceNotRequired, e.Reason));
        Assert.Equal(before, h.Store.History(Fixture.TaskOne));
    }
}
