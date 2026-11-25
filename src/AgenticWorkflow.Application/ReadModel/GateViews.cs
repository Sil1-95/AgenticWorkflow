using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.ReadModel;

/// <summary>Why a gate no longer describes the task. A gate is current exactly when it has none of these.</summary>
public enum GateStaleReason
{
    /// <summary>The task moved to a later semantic revision.</summary>
    SemanticRevisionSuperseded,

    /// <summary>An approval that new evidence (a reclassification, or a later WHAT) invalidated; see <see cref="GateView.Invalidation"/>.</summary>
    Invalidated,

    /// <summary>An ACCEPT gate: the task has a newer implementation revision.</summary>
    ImplementationSuperseded,

    /// <summary>An ACCEPT gate: the verification it binds is no longer the current passing verification.</summary>
    VerificationNotCurrent,

    /// <summary>An ACCEPT gate: the independent review is required and no current review passed.</summary>
    ReviewNotSatisfied,

    /// <summary>An ACCEPT gate: formal PROVE is required and no current proof passed.</summary>
    ProofNotSatisfied,
}

/// <summary>One workflow-owned gate: what it binds, whether it still describes the task, what it asks and how it was decided.</summary>
public sealed record GateView
{
    public required Guid Id { get; init; }

    public required GateType Type { get; init; }

    /// <summary>Where the gate is in its life. An approved gate that is no longer current reads <see cref="GateStatus.Invalidated"/>.</summary>
    public required GateStatus Status { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public required GateBindingView Binding { get; init; }

    /// <summary>True when the gate still describes the task. This is the controller's own predicate, not a recomputation.</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>Why <see cref="IsCurrent"/> is false. Empty exactly when it is true for an open gate; a decided gate is history and may list reasons too.</summary>
    public required IReadOnlyList<GateStaleReason> StaleReasons { get; init; }

    /// <summary>Why an approved gate is no longer current; null while it is, and for a gate that was never approved.</summary>
    public required GateInvalidationView? Invalidation { get; init; }

    /// <summary>The question of a CLARIFY gate; null for any other gate.</summary>
    public required ClarificationView? Clarification { get; init; }

    /// <summary>The proposal of a WHAT gate; null for any other gate.</summary>
    public required WhatProposalView? What { get; init; }

    /// <summary>The proposal of a HOW gate; null for any other gate.</summary>
    public required HowProposalView? How { get; init; }

    /// <summary>The decision that closed the gate; null while it is open.</summary>
    public required GateDecisionView? Decision { get; init; }
}

/// <summary>What a gate binds. The binding never changes; a gate whose binding is not the task's current state is stale.</summary>
public sealed record GateBindingView
{
    public required Guid SemanticRevisionId { get; init; }

    public required int SemanticOrdinal { get; init; }

    /// <summary>The implementation an ACCEPT gate asks the human to review; null for a gate that precedes implementation.</summary>
    public required Guid? ImplementationRevisionId { get; init; }

    /// <summary>The passing verification of that implementation; null for a gate that precedes implementation.</summary>
    public required Guid? VerificationRunId { get; init; }
}

public sealed record GateInvalidationView
{
    public required GateInvalidationCause Cause { get; init; }

    public required string Reason { get; init; }

    public required Guid? ReclassificationId { get; init; }

    /// <summary>The later approved WHAT gate that superseded the WHAT a HOW was derived from; null for the other causes.</summary>
    public required Guid? SupersededByGateId { get; init; }
}

public sealed record GateDecisionView
{
    public required GateAction Action { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>The human's feedback of a request for changes; null for any other action.</summary>
    public required string? Feedback { get; init; }

    /// <summary>The human's answer to a CLARIFY question, exactly as given: task context, never an approval. Null for any other action.</summary>
    public required string? Answer { get; init; }
}

public sealed record ClarificationView
{
    public required string Question { get; init; }

    /// <summary>Why the answer materially affects the task.</summary>
    public required string Reason { get; init; }

    /// <summary>The 1-based agent turn that raised it. Operational correlation.</summary>
    public required int Turn { get; init; }
}

public sealed record WhatProposalView
{
    public required string Objective { get; init; }

    public required string RequiredBehavior { get; init; }

    public required string Scope { get; init; }

    public required string? Constraints { get; init; }

    public required string? NonGoals { get; init; }

    public required string AcceptanceConditions { get; init; }

    /// <summary>Why a human decision is warranted. Not part of the contract itself.</summary>
    public required string Rationale { get; init; }

    /// <summary>SHA-256 of the contract's sections: it identifies exactly what an approval binds.</summary>
    public required string ContentHash { get; init; }

    public required int Turn { get; init; }
}

public sealed record HowProposalView
{
    public required string Strategy { get; init; }

    public required string Components { get; init; }

    public required string? DataAndControlFlow { get; init; }

    public required string? Compatibility { get; init; }

    public required string RisksAndTradeoffs { get; init; }

    public required string? VerificationApproach { get; init; }

    /// <summary>Why a human decision is warranted. Not part of the strategy itself.</summary>
    public required string Rationale { get; init; }

    /// <summary>SHA-256 of the strategy's sections: it identifies exactly what an approval binds.</summary>
    public required string ContentHash { get; init; }

    public required int Turn { get; init; }
}
