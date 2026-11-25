using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;

namespace AgenticWorkflow.Application.ReadModel;

/// <summary>The independent REVIEW: the requirement, the explicitly bound reviewer, every round and where the task stands against it.</summary>
public sealed record ReviewView
{
    public required ReviewStatus Status { get; init; }

    /// <summary>True when a fresh reviewer is due now.</summary>
    public required bool IsDue { get; init; }

    /// <summary>True when the requirement asks nothing more: not required, or a current review passed.</summary>
    public required bool IsSatisfied { get; init; }

    /// <summary>The profile the reviewer role is bound to; null when it is unbound (the implementer's profile is never used instead).</summary>
    public required string? ReviewerProfile { get; init; }

    /// <summary>The agent's own requests for a review, oldest first.</summary>
    public required IReadOnlyList<ReviewRequestView> Requests { get; init; }

    public required IReadOnlyList<ReviewRoundView> Rounds { get; init; }

    /// <summary>The review that describes the task now (whatever its verdict), or null. It is one of <see cref="Rounds"/>.</summary>
    public required Guid? CurrentReviewId { get; init; }
}

public sealed record ReviewRequestView
{
    public required Guid Id { get; init; }

    public required string Reason { get; init; }

    public required int Turn { get; init; }

    public required string? Session { get; init; }

    public required int SemanticOrdinal { get; init; }
}

public sealed record ReviewRoundView
{
    public required Guid Id { get; init; }

    public required int Round { get; init; }

    public required ReviewVerdict Verdict { get; init; }

    /// <summary>True when this review still describes the task: it satisfies nothing otherwise.</summary>
    public required bool IsCurrent { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<ReviewFindingView> Findings { get; init; }

    /// <summary>Non-blocking observations; null when there are none.</summary>
    public required string? Notes { get; init; }

    public required string ReportHash { get; init; }

    public required string ReviewerProfile { get; init; }

    /// <summary>The reviewer's own native conversation: never the implementer's, never an earlier reviewer's.</summary>
    public required string ReviewerSession { get; init; }

    public required string ImplementerSession { get; init; }

    public required int SemanticOrdinal { get; init; }

    public required Guid ImplementationRevisionId { get; init; }

    public required Guid VerificationRunId { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required long ReviewerMilliseconds { get; init; }

    public required int ReviewerCommandCount { get; init; }

    public required IReadOnlyList<string> ReviewerCommands { get; init; }

    public required bool ConfiguredCheckRerun { get; init; }
}

public sealed record ReviewFindingView
{
    public required string Title { get; init; }

    public required string Evidence { get; init; }

    public required string Impact { get; init; }

    public required string Recommendation { get; init; }

    public required string? Location { get; init; }
}

/// <summary>Formal PROVE: the requirement, the explicitly bound prover, the Proof Contracts, every proof round and where the task stands against it.</summary>
public sealed record ProofView
{
    public required ProofStatus Status { get; init; }

    /// <summary>True when a fresh prover is due now.</summary>
    public required bool IsDue { get; init; }

    /// <summary>True when PROVE is required and the implementer owes a Proof Contract for the current semantic revision.</summary>
    public required bool IsContractMissing { get; init; }

    /// <summary>True when the requirement asks nothing more: not required, or a current proof passed.</summary>
    public required bool IsSatisfied { get; init; }

    /// <summary>The profile the prover role is bound to; null when it is unbound.</summary>
    public required string? ProverProfile { get; init; }

    /// <summary>Every Proof Contract ever submitted, oldest first (at most one per semantic revision).</summary>
    public required IReadOnlyList<ProofContractView> Contracts { get; init; }

    public required IReadOnlyList<ProofRoundView> Rounds { get; init; }

    /// <summary>The proof that describes the task now (whatever its verdict), or null. It is one of <see cref="Rounds"/>.</summary>
    public required Guid? CurrentProofId { get; init; }
}

public sealed record ProofContractView
{
    public required Guid Id { get; init; }

    public required string ContentHash { get; init; }

    public required int SemanticOrdinal { get; init; }

    /// <summary>True for the contract written for the current semantic revision.</summary>
    public required bool IsCurrent { get; init; }

    public required string Rationale { get; init; }

    public required int Turn { get; init; }

    public required string? Session { get; init; }

    public required IReadOnlyList<ProofClaimView> Claims { get; init; }
}

public sealed record ProofClaimView
{
    public required string Id { get; init; }

    public required string Statement { get; init; }

    public required string AcceptanceCriterion { get; init; }

    public required IReadOnlyList<ProofEvidenceRequirementView> Evidence { get; init; }
}

public sealed record ProofEvidenceRequirementView
{
    public required ProofEvidenceKind Kind { get; init; }

    /// <summary>The check name, or the observation to establish; null for the current review.</summary>
    public required string? Subject { get; init; }
}

public sealed record ProofRoundView
{
    public required Guid Id { get; init; }

    public required int Round { get; init; }

    public required Guid ContractId { get; init; }

    public required string ContractHash { get; init; }

    public required ProofVerdict Verdict { get; init; }

    /// <summary>True when this proof still describes the task: it satisfies nothing otherwise.</summary>
    public required bool IsCurrent { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<ProofClaimResultView> Claims { get; init; }

    public required string ReportHash { get; init; }

    public required string ProverProfile { get; init; }

    /// <summary>The prover's own native conversation: never the implementer's, never a reviewer's, never an earlier prover's.</summary>
    public required string ProverSession { get; init; }

    public required string ImplementerSession { get; init; }

    public required int SemanticOrdinal { get; init; }

    public required Guid ImplementationRevisionId { get; init; }

    public required Guid VerificationRunId { get; init; }

    /// <summary>The passing review the prover was given when REVIEW is required.</summary>
    public required Guid? ReviewId { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required long ProverMilliseconds { get; init; }

    public required int ProverCommandCount { get; init; }

    public required IReadOnlyList<string> ProverCommands { get; init; }

    public required bool ConfiguredCheckRerun { get; init; }

    public required long DuplicatedVerificationMilliseconds { get; init; }
}

public sealed record ProofClaimResultView
{
    public required string ClaimId { get; init; }

    public required ProofVerdict Verdict { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<ProofEvidenceReferenceView> Evidence { get; init; }
}

public sealed record ProofEvidenceReferenceView
{
    public required ProofEvidenceRefKind Kind { get; init; }

    /// <summary>The check name, review id or command ordinal the reference points at.</summary>
    public required string Value { get; init; }
}
