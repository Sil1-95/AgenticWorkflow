using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>Stable machine-readable reasons a direct-path task is not (yet) eligible to complete.</summary>
public static class DirectCompletionReasons
{
    public const string TaskTerminal = "task_terminal";
    public const string TaskNotRunning = "task_not_running";
    public const string NoPendingAcceptGate = "no_pending_accept_gate";
    public const string UnsupportedCompletionBoundary = "unsupported_completion_boundary";
    public const string NoCurrentImplementation = "no_current_implementation";
    public const string ImplementationTargetsStaleSemantics = "implementation_targets_stale_semantics";
    public const string CurrentImplementationNotVerified = "current_implementation_not_verified";
    public const string CurrentVerificationFailed = "current_verification_failed";
    public const string AcceptGateNotCurrent = "accept_gate_not_current";
    public const string RequiredStageNotApproved = "required_stage_not_approved";

    /// <summary>The task requires an independent REVIEW and no review of exactly the current implementation and verification passed.</summary>
    public const string ReviewNotCurrentPass = "review_not_current_pass";

    /// <summary>The task requires formal PROVE and no proof of exactly the current contract, implementation and verification passed.</summary>
    public const string ProofNotCurrentPass = "proof_not_current_pass";
}

/// <summary>The result of the deterministic direct-path completion check. Empty <see cref="Reasons"/> means eligible.</summary>
public sealed record DirectCompletionEligibility(IReadOnlyList<string> Reasons)
{
    public bool IsEligible => Reasons.Count == 0;
}

/// <summary>
/// The completion rule for the direct path, derived purely from authoritative task state. It is not the future workflow policy engine: there are no
/// proof obligations or findings here, and no model is consulted. Both acceptance policies (<see cref="AcceptanceRequirement"/>, fixed when the task was
/// created) rest on the same evidence, <see cref="EvaluateVerifiedImplementation"/>:
/// <list type="number">
/// <item>the task is not terminal;</item>
/// <item>its completion boundary is <see cref="CompletionBoundary.WorkingTree"/> (nothing stronger is implemented, and working-tree semantics are never
/// treated as satisfying one);</item>
/// <item>a current implementation revision exists, produced against the <em>current</em> semantic revision;</item>
/// <item>and the latest verification of exactly that revision exists and passed.</item>
/// </list>
/// What that evidence entitles the task to depends on the policy, and <see cref="Evaluate"/> adds exactly that:
/// <list type="bullet">
/// <item><see cref="AcceptanceRequirement.NotRequired"/>: the verified implementation is eligible to <em>complete</em>, recorded by the controller while the
/// task is RUNNING. There is no gate.</item>
/// <item><see cref="AcceptanceRequirement.HumanRequired"/>: the verified implementation is eligible for the human's ACCEPT. Eligible here means "may be
/// accepted": an ACCEPT gate is awaiting the decision, it is <em>current</em> (it binds that semantic revision, that implementation revision and that
/// verification run), and only the human's explicit decision completes the task.</item>
/// </list>
/// </summary>
public static class DirectPathCompletion
{
    /// <summary>The evidence every completion path shares. Eligible when the task is not terminal and its current implementation is verified and passing.</summary>
    public static DirectCompletionEligibility EvaluateVerifiedImplementation(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new DirectCompletionEligibility(VerifiedImplementationReasons(task));
    }

    /// <summary>Whether the task may complete now, under its own acceptance policy.</summary>
    public static DirectCompletionEligibility Evaluate(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var reasons = VerifiedImplementationReasons(task);

        if (task.Acceptance == AcceptanceRequirement.NotRequired)
        {
            // The controller records the completion itself, so the task must be the one the controller is driving: RUNNING, with no gate to wait for.
            if (!task.IsTerminal && task.WorkflowState != WorkflowState.Running)
                reasons.Add(DirectCompletionReasons.TaskNotRunning);

            return new DirectCompletionEligibility(reasons);
        }

        var gate = task.PendingGate is { Type: GateType.Accept } pending ? pending : null;
        if (gate is null && !task.IsTerminal)
            reasons.Add(DirectCompletionReasons.NoPendingAcceptGate);

        if (gate is not null && !task.IsCurrent(gate))
            reasons.Add(DirectCompletionReasons.AcceptGateNotCurrent);

        return new DirectCompletionEligibility(reasons);
    }

    private static List<string> VerifiedImplementationReasons(WorkflowTask task)
    {
        var reasons = new List<string>();

        if (task.IsTerminal)
            reasons.Add(DirectCompletionReasons.TaskTerminal);

        if (task.CompletionBoundary != CompletionBoundary.WorkingTree)
            reasons.Add(DirectCompletionReasons.UnsupportedCompletionBoundary);

        // a stage whose approval was invalidated (or that a reclassification added) must be approved again; nothing implemented or verified before that counts.
        if (!task.MayImplement)
            reasons.Add(DirectCompletionReasons.RequiredStageNotApproved);

        var implementation = task.CurrentImplementationRevision;
        if (implementation is null)
        {
            reasons.Add(DirectCompletionReasons.NoCurrentImplementation);
            return reasons;
        }

        // a stale pass, a review that required changes, and no review at all are the same thing here: the requirement is not satisfied.
        if (!task.IsReviewSatisfied)
            reasons.Add(DirectCompletionReasons.ReviewNotCurrentPass);

        // likewise for formal PROVE; a stale pass, a failed or inconclusive proof and no proof at all are the same thing here.
        if (!task.IsProveSatisfied)
            reasons.Add(DirectCompletionReasons.ProofNotCurrentPass);

        if (implementation.SemanticRevisionId != task.CurrentSemanticRevisionId)
            reasons.Add(DirectCompletionReasons.ImplementationTargetsStaleSemantics);

        var verification = task.LatestVerificationFor(implementation.Id);
        if (verification is null)
            reasons.Add(DirectCompletionReasons.CurrentImplementationNotVerified);
        else if (verification.Outcome != VerificationOutcome.Passed)
            reasons.Add(DirectCompletionReasons.CurrentVerificationFailed);

        return reasons;
    }
}
