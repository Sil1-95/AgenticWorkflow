using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.ReadModel;

public static partial class TaskReadModelProjector
{
    private static GateView Gate(WorkflowTask task, Gate gate)
    {
        var decision = task.DecisionFor(gate.Id);
        var invalidation = task.InvalidationOf(gate);

        return new GateView
        {
            Id = gate.Id.Value,
            Type = gate.Type,
            Status = task.StatusOf(gate),
            OpenedAt = gate.OpenedAt,
            Binding = new GateBindingView
            {
                SemanticRevisionId = gate.SemanticRevisionId.Value,
                SemanticOrdinal = Ordinal(task, gate.SemanticRevisionId),
                ImplementationRevisionId = gate.ImplementationRevisionId?.Value,
                VerificationRunId = gate.VerificationRunId?.Value,
            },
            IsCurrent = task.IsCurrent(gate),
            StaleReasons = StaleReasons(task, gate),
            Invalidation = invalidation is null
                ? null
                : new GateInvalidationView
                {
                    Cause = invalidation.Cause,
                    Reason = invalidation.Reason,
                    ReclassificationId = invalidation.Reclassification?.Value,
                    SupersededByGateId = invalidation.SupersededBy?.Value,
                },
            Clarification = gate.Clarification is { } asked
                ? new ClarificationView { Question = asked.Question, Reason = asked.Reason, Turn = asked.Turn }
                : null,
            What = gate.What is { } what
                ? new WhatProposalView
                {
                    Objective = what.Objective,
                    RequiredBehavior = what.RequiredBehavior,
                    Scope = what.Scope,
                    Constraints = what.Constraints,
                    NonGoals = what.NonGoals,
                    AcceptanceConditions = what.AcceptanceConditions,
                    Rationale = what.Rationale,
                    ContentHash = what.ContentHash,
                    Turn = what.Turn,
                }
                : null,
            How = gate.How is { } how
                ? new HowProposalView
                {
                    Strategy = how.Strategy,
                    Components = how.Components,
                    DataAndControlFlow = how.DataAndControlFlow,
                    Compatibility = how.Compatibility,
                    RisksAndTradeoffs = how.RisksAndTradeoffs,
                    VerificationApproach = how.VerificationApproach,
                    Rationale = how.Rationale,
                    ContentHash = how.ContentHash,
                    Turn = how.Turn,
                }
                : null,
            Decision = decision is null
                ? null
                : new GateDecisionView { Action = decision.Action, DecidedAt = decision.DecidedAt, Feedback = decision.Feedback, Answer = decision.Answer },
        };
    }

    /// <summary>
    /// Each reason a gate no longer describes the task, one per clause of <c>WorkflowTask.IsCurrent</c>. <see cref="GateView.IsCurrent"/> is that predicate itself; the list only
    /// explains it, and the two are tested to agree.
    /// </summary>
    private static IReadOnlyList<GateStaleReason> StaleReasons(WorkflowTask task, Gate gate)
    {
        var reasons = new List<GateStaleReason>();

        if (gate.SemanticRevisionId != task.CurrentSemanticRevisionId)
            reasons.Add(GateStaleReason.SemanticRevisionSuperseded);

        if (task.InvalidationOf(gate) is not null)
            reasons.Add(GateStaleReason.Invalidated);

        if (gate.Type == GateType.Accept)
        {
            if (gate.ImplementationRevisionId != task.CurrentImplementationRevisionId)
                reasons.Add(GateStaleReason.ImplementationSuperseded);

            if (task.CurrentVerification is not { Outcome: VerificationOutcome.Passed } verification || verification.Id != gate.VerificationRunId)
                reasons.Add(GateStaleReason.VerificationNotCurrent);

            if (!task.IsReviewSatisfied)
                reasons.Add(GateStaleReason.ReviewNotSatisfied);

            if (!task.IsProveSatisfied)
                reasons.Add(GateStaleReason.ProofNotSatisfied);
        }

        return reasons;
    }

    /// <summary>
    /// What a person is asked for now, with the decisions that are legal at the workflow level: a current gate offers its own decisions and the abort, a stale one only the
    /// abort. A task that waits in the agent's own interface (AWAITING_HUMAN with no gate) owns no decision of the workflow; it can only be aborted.
    /// </summary>
    private static HumanRequestView? PendingRequest(WorkflowTask task)
    {
        if (task.PendingGate is { } gate)
        {
            var current = task.IsCurrent(gate);

            IReadOnlyList<HumanAction> actions = gate.Type switch
            {
                GateType.Clarify => current ? [HumanAction.Answer, HumanAction.Abort] : [HumanAction.Abort],
                GateType.Accept => current ? [HumanAction.Accept, HumanAction.RequestChanges, HumanAction.Abort] : [HumanAction.RequestChanges, HumanAction.Abort],
                _ => current ? [HumanAction.Approve, HumanAction.RequestChanges, HumanAction.Abort] : [HumanAction.Abort],
            };

            return new HumanRequestView
            {
                Kind = gate.Type switch
                {
                    GateType.Clarify => HumanRequestKind.ClarifyAnswer,
                    GateType.What => HumanRequestKind.WhatApproval,
                    GateType.How => HumanRequestKind.HowApproval,
                    _ => HumanRequestKind.AcceptDecision,
                },
                GateId = gate.Id.Value,
                GateType = gate.Type,
                IsCurrent = current,
                AllowedActions = actions,
            };
        }

        return task.WorkflowState == WorkflowState.AwaitingHuman
            ? new HumanRequestView { Kind = HumanRequestKind.NativeAgentInteraction, GateId = null, GateType = null, IsCurrent = null, AllowedActions = [HumanAction.Abort] }
            : null;
    }

    private static CompletionView Completion(WorkflowTask task)
    {
        var gate = task.PendingGate is { Type: GateType.Accept } pending ? pending : null;

        var accept = task.Acceptance == AcceptanceRequirement.NotRequired
            ? AcceptState.NotApplicable
            : task.WorkflowState == WorkflowState.Completed
                ? task.Gates.IsEmpty ? AcceptState.NotRecorded : AcceptState.Accepted
                : task.IsTerminal
                    ? AcceptState.NotAccepted
                    : gate is null ? AcceptState.NotReady : task.IsCurrent(gate) ? AcceptState.AwaitingDecision : AcceptState.AwaitingDecisionStale;

        var eligibility = task.IsTerminal ? null : DirectPathCompletion.Evaluate(task);

        return new CompletionView
        {
            Accept = accept,
            AcceptGateId = gate?.Id.Value,
            IsEligible = eligibility?.IsEligible,
            BlockingReasons = eligibility is null ? [] : [.. eligibility.Reasons],
        };
    }

    private static BlockView? Block(WorkflowTask task)
    {
        if (task.WorkflowState != WorkflowState.Blocked)
            return null;

        var kind = RecoverableContinuation.Find(task) is not null ? BlockKind.RecoverableContinuation
            : RuntimeFailureBlock.IsBlockedBy(task) ? BlockKind.RuntimeFailure
            : UnresolvedVerificationBlock.IsBlockedBy(task) ? BlockKind.UnresolvedVerification
            : WhatStageBlock.IsBlockedBy(task) ? BlockKind.WhatStageViolated
            : HowStageBlock.IsBlockedBy(task) ? BlockKind.HowStageViolated
            : ReviewerWorkspaceBlock.IsBlockedBy(task) ? BlockKind.ReviewerModifiedWorkspace
            : ReviewUnresolvedBlock.IsBlockedBy(task) ? BlockKind.ReviewUnresolved
            : ProverWorkspaceBlock.IsBlockedBy(task) ? BlockKind.ProverModifiedWorkspace
            : ProofUnresolvedBlock.IsBlockedBy(task) ? BlockKind.ProofUnresolved
            : NativeInteractionBlock.IsBlockedBy(task) ? BlockKind.NativeInteractionUnavailable
            : BlockKind.Other;

        return new BlockView { Kind = kind, Reason = task.StateReason };
    }

    private static RecoveryView? Recovery(WorkflowTask task)
    {
        if (task.WorkflowState != WorkflowState.Blocked)
            return null;

        var pending = RecoverableContinuation.Find(task);

        return new RecoveryView
        {
            Actions = pending is null ? [RecoveryAction.Abort] : [RecoveryAction.Continue, RecoveryAction.Abort],
            PendingWork = pending is null
                ? null
                : new PendingWorkView
                {
                    Kind = pending.Kind.ToString(),
                    SemanticOrdinal = pending.Revision.Ordinal,
                    DecisionGateId = pending.Decision?.GateId.Value,
                    ReclassificationId = pending.Reclassification?.Id.Value,
                    ReviewId = pending.Review?.Id.Value,
                    ProofId = pending.Proof?.Id.Value,
                },
        };
    }
}
