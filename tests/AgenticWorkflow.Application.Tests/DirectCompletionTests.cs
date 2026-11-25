using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Tests;

/// <summary>The deterministic completion rule (M2B, extended by the M4A ACCEPT gate) and the Application operations around it.</summary>
public sealed class DirectCompletionTests
{
    private static readonly ImplementationFingerprint FingerprintA = new("fp-a");
    private static readonly ImplementationFingerprint FingerprintB = new("fp-b");

    private static VerificationCheckResult Pass(string id = "tests")
        => new(new VerificationCheckId(id), VerificationOutcome.Passed, "ok", 0);

    private static VerificationCheckResult Fail(string id = "tests")
        => new(new VerificationCheckId(id), VerificationOutcome.Failed, "broken", 1);

    private static async Task<DirectHarness> WithImplementationAsync()
    {
        var h = await DirectHarness.CreateAsync();
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        return h;
    }

    private static async Task<DirectHarness> WithOpenGateAsync()
    {
        var h = await WithImplementationAsync();
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;
        h.Snapshot = (await h.Service.OpenAcceptGateAsync(h.Snapshot)).Snapshot;
        return h;
    }

    // ---- verification recording (unchanged by M4A) ------------------------------------------------------------------

    [Fact]
    public async Task Recording_verification_without_an_implementation_is_rejected_explicitly()
    {
        var h = await DirectHarness.CreateAsync();

        await Assert.ThrowsAsync<NoCurrentImplementationException>(
            () => h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()]));

        Assert.Empty((await h.ReloadAsync()).Verifications);
    }

    [Fact]
    public async Task Verification_binds_to_the_current_implementation_revision_only()
    {
        var h = await WithImplementationAsync();

        var change = await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()]);

        var run = Assert.Single(change.Snapshot.Task.Verifications);
        Assert.Equal(h.Snapshot.Task.CurrentImplementationRevisionId, run.ImplementationRevisionId);
        Assert.Equal(VerificationOutcome.Passed, run.Outcome);
    }

    [Fact]
    public async Task Verification_recorded_against_a_stale_basis_is_refused()
    {
        var h = await WithImplementationAsync();
        await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintB);

        await Assert.ThrowsAsync<StaleWorkflowStateException>(
            () => h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()]));
    }

    // ---- the gate only opens over current passing verification ------------------------------------------------------

    [Fact]
    public async Task An_agent_done_signal_without_verification_cannot_open_the_gate()
    {
        var h = await WithImplementationAsync();

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Empty(task.Gates);
    }

    [Fact]
    public async Task A_task_without_any_implementation_cannot_open_the_gate()
    {
        var h = await DirectHarness.CreateAsync();

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));
        Assert.Empty((await h.ReloadAsync()).Gates);
    }

    [Fact]
    public async Task A_failed_current_verification_cannot_open_the_gate()
    {
        var h = await WithImplementationAsync();
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass("a"), Fail("b")])).Snapshot;

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));
        Assert.Empty((await h.ReloadAsync()).Gates);
    }

    [Fact]
    public async Task An_old_pass_followed_by_a_new_implementation_revision_cannot_open_the_gate()
    {
        var h = await WithImplementationAsync();
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintB)).Snapshot;

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));
        Assert.Empty((await h.ReloadAsync()).Gates);
    }

    [Fact]
    public async Task The_latest_verification_of_the_current_revision_decides_not_an_earlier_pass()
    {
        var h = await WithImplementationAsync();
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Fail()])).Snapshot;

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.OpenAcceptGateAsync(h.Snapshot));
    }

    [Fact]
    public async Task Opening_the_gate_makes_the_workflow_durably_await_the_human_and_does_not_complete_it()
    {
        var h = await WithOpenGateAsync();

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.False(task.IsTerminal);
        var gate = Assert.IsType<Gate>(task.PendingGate);
        Assert.Equal(GateType.Accept, gate.Type);
        Assert.True(task.IsCurrent(gate));
        Assert.Equal(task.CurrentImplementationRevisionId, gate.ImplementationRevisionId);
    }

    // ---- the completion rule ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_an_open_gate_nothing_is_eligible_even_with_a_passing_verification()
    {
        var h = await WithImplementationAsync();
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;

        var eligibility = DirectPathCompletion.Evaluate(h.Snapshot.Task);

        Assert.Equal([DirectCompletionReasons.NoPendingAcceptGate], eligibility.Reasons);
    }

    [Fact]
    public async Task A_current_gate_over_a_current_passing_verification_is_eligible_to_be_accepted()
    {
        var h = await WithOpenGateAsync();

        Assert.True(DirectPathCompletion.Evaluate(h.Snapshot.Task).IsEligible);
    }

    public static TheoryData<string> StrongerBoundaries => new() { "local_commit", "remote_branch", "release", "deployment", "staging_deploy" };

    [Theory]
    [MemberData(nameof(StrongerBoundaries))]
    public async Task A_boundary_stronger_than_the_working_tree_is_never_satisfied(string boundaryId)
    {
        var h = await DirectHarness.CreateAsync(new CompletionBoundary(boundaryId));
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        h.Snapshot = (await h.Service.RecordLightweightVerificationAsync(h.Snapshot, [Pass()])).Snapshot;

        Assert.Contains(DirectCompletionReasons.UnsupportedCompletionBoundary, DirectPathCompletion.Evaluate(h.Snapshot.Task).Reasons);
    }

    [Fact]
    public async Task Every_unmet_condition_is_reported_not_just_the_first()
    {
        var h = await DirectHarness.CreateAsync(CompletionBoundary.RemoteBranch);
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintA)).Snapshot;
        h.Snapshot = (await h.Service.CreateSemanticRevisionAsync(h.Snapshot, SemanticChangeKind.RequirementChange, "changed")).Snapshot;

        Assert.Equal(
            [
                DirectCompletionReasons.UnsupportedCompletionBoundary,
                DirectCompletionReasons.ImplementationTargetsStaleSemantics,
                DirectCompletionReasons.CurrentImplementationNotVerified,
                DirectCompletionReasons.NoPendingAcceptGate,
            ],
            DirectPathCompletion.Evaluate(h.Snapshot.Task).Reasons);
    }

    // ---- accepting --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_explicit_accept_of_the_current_gate_completes_the_task()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;

        var change = await h.Service.AcceptGateAsync(h.Snapshot, gate.Id);

        Assert.Equal(WorkflowState.Completed, change.Snapshot.Task.WorkflowState);
        Assert.Equal(GateAction.Accept, change.Snapshot.Task.DecisionFor(gate.Id)!.Action);
        Assert.Null(change.Snapshot.Task.PendingGate);
    }

    [Fact]
    public async Task Accepting_an_unknown_gate_is_refused_and_records_nothing()
    {
        var h = await WithOpenGateAsync();

        await Assert.ThrowsAsync<InvalidGateException>(() => h.Service.AcceptGateAsync(h.Snapshot, new GateId(Guid.NewGuid())));

        var task = await h.ReloadAsync();
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.NotNull(task.PendingGate);
    }

    [Fact]
    public async Task A_second_accept_of_the_same_gate_is_refused()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        var accepted = await h.Service.AcceptGateAsync(h.Snapshot, gate.Id);

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.AcceptGateAsync(accepted.Snapshot, gate.Id));

        Assert.Single((await h.ReloadAsync()).GateDecisions);
    }

    [Fact]
    public async Task A_gate_made_stale_by_a_later_implementation_revision_cannot_be_accepted()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        h.Snapshot = (await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintB)).Snapshot;

        var error = await Assert.ThrowsAsync<CompletionNotEligibleException>(() => h.Service.AcceptGateAsync(h.Snapshot, gate.Id));

        Assert.Contains(DirectCompletionReasons.AcceptGateNotCurrent, error.Eligibility.Reasons);
        Assert.NotEqual(WorkflowState.Completed, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task Accepting_on_a_stale_basis_is_refused_even_if_the_stale_state_looked_eligible()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        await h.Service.RecordImplementationRevisionAsync(h.Snapshot, FingerprintB);

        await Assert.ThrowsAsync<StaleWorkflowStateException>(() => h.Service.AcceptGateAsync(h.Snapshot, gate.Id));
        Assert.Equal(WorkflowState.AwaitingHuman, (await h.ReloadAsync()).WorkflowState);
    }

    [Fact]
    public async Task The_generic_state_change_cannot_reach_completed()
    {
        var h = await WithOpenGateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.ChangeWorkflowStateAsync(h.Snapshot, WorkflowState.Completed, "done"));
    }

    [Fact]
    public async Task The_generic_state_change_cannot_move_a_task_off_its_pending_gate()
    {
        var h = await WithOpenGateAsync();

        await Assert.ThrowsAsync<InvalidGateException>(
            () => h.Service.ChangeWorkflowStateAsync(h.Snapshot, WorkflowState.Running, "carry on"));
        Assert.NotNull((await h.ReloadAsync()).PendingGate);
    }

    // ---- requesting changes and cancelling --------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_changes_closes_the_gate_as_not_approved_and_returns_to_running_under_a_new_semantic_revision()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        var before = h.Snapshot.Task.CurrentSemanticRevisionId;

        var change = await h.Service.RequestChangesAsync(h.Snapshot, gate.Id, "Also rename the docs.");

        var task = change.Snapshot.Task;
        Assert.Equal(WorkflowState.Running, task.WorkflowState);
        Assert.Null(task.PendingGate);
        Assert.Equal(GateAction.RequestChanges, task.DecisionFor(gate.Id)!.Action);
        Assert.Equal("Also rename the docs.", task.DecisionFor(gate.Id)!.Feedback);
        Assert.NotEqual(before, task.CurrentSemanticRevisionId);
        Assert.False(task.IsCurrent(gate));
    }

    [Fact]
    public async Task A_gate_closed_by_requested_changes_can_never_be_accepted_afterwards()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        var changed = await h.Service.RequestChangesAsync(h.Snapshot, gate.Id, "Needs more.");

        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.AcceptGateAsync(changed.Snapshot, gate.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Requesting_changes_needs_feedback(string feedback)
    {
        var h = await WithOpenGateAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => h.Service.RequestChangesAsync(h.Snapshot, h.Snapshot.Task.PendingGate!.Id, feedback));
        Assert.NotNull((await h.ReloadAsync()).PendingGate);
    }

    [Fact]
    public async Task Cancelling_the_gate_closes_it_without_approval_and_cancels_the_task()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;

        var change = await h.Service.CancelGateAsync(h.Snapshot, gate.Id);

        Assert.Equal(WorkflowState.Cancelled, change.Snapshot.Task.WorkflowState);
        Assert.Equal(GateAction.Cancel, change.Snapshot.Task.DecisionFor(gate.Id)!.Action);
    }

    [Fact]
    public async Task A_terminal_task_has_no_gate_to_decide()
    {
        var h = await WithOpenGateAsync();
        var gate = h.Snapshot.Task.PendingGate!;
        var accepted = await h.Service.AcceptGateAsync(h.Snapshot, gate.Id);

        Assert.Contains(DirectCompletionReasons.TaskTerminal, DirectPathCompletion.Evaluate(accepted.Snapshot.Task).Reasons);
        await Assert.ThrowsAnyAsync<DomainException>(() => h.Service.CancelGateAsync(accepted.Snapshot, gate.Id));
    }
}
