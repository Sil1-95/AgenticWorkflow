using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.ReadModel;

public static partial class TaskReadModelProjector
{
    private static ReviewView Review(WorkflowTask task) => new()
    {
        Status = task.ReviewStatus,
        IsDue = task.IsReviewDue,
        IsSatisfied = task.IsReviewSatisfied,
        ReviewerProfile = task.RoleBindings.TryGetValue(WellKnownRoles.Reviewer, out var reviewer) ? reviewer.Profile.Id.Value : null,
        Requests = [.. task.ReviewRequests.Select(r => new ReviewRequestView
        {
            Id = r.Id.Value,
            Reason = r.Reason,
            Turn = r.Turn,
            Session = r.Session,
            SemanticOrdinal = Ordinal(task, r.SemanticRevisionId),
        })],
        Rounds = [.. task.Reviews.Select(r => ReviewRound(task, r))],
        CurrentReviewId = task.CurrentReview?.Id.Value,
    };

    private static ReviewRoundView ReviewRound(WorkflowTask task, Review review) => new()
    {
        Id = review.Id.Value,
        Round = review.Round,
        Verdict = review.Verdict,
        IsCurrent = task.IsReviewCurrent(review),
        Summary = review.Report.Summary,
        Findings = [.. review.Report.Findings.Select(f => new ReviewFindingView
        {
            Title = f.Title,
            Evidence = f.Evidence,
            Impact = f.Impact,
            Recommendation = f.Recommendation,
            Location = f.Location,
        })],
        Notes = review.Report.Notes,
        ReportHash = review.ReportHash,
        ReviewerProfile = review.ReviewerProfile.Value,
        ReviewerSession = review.ReviewerSession,
        ImplementerSession = review.ImplementerSession,
        SemanticOrdinal = Ordinal(task, review.SemanticRevisionId),
        ImplementationRevisionId = review.ImplementationRevisionId.Value,
        VerificationRunId = review.VerificationRunId.Value,
        RecordedAt = review.RecordedAt,
        ReviewerMilliseconds = review.ReviewerMilliseconds,
        ReviewerCommandCount = review.ReviewerCommandCount,
        ReviewerCommands = [.. review.ReviewerCommands],
        ConfiguredCheckRerun = review.ConfiguredCheckRerun,
    };

    private static ProofView Proof(WorkflowTask task) => new()
    {
        Status = task.ProofStatus,
        IsDue = task.IsProveDue,
        IsContractMissing = task.IsProofContractMissing,
        IsSatisfied = task.IsProveSatisfied,
        ProverProfile = task.RoleBindings.TryGetValue(WellKnownRoles.Prover, out var prover) ? prover.Profile.Id.Value : null,
        Contracts = [.. task.ProofContracts.Select(c => ProofContract(task, c))],
        Rounds = [.. task.Proofs.Select(p => ProofRound(task, p))],
        CurrentProofId = task.CurrentProof?.Id.Value,
    };

    private static ProofContractView ProofContract(WorkflowTask task, ProofContract contract) => new()
    {
        Id = contract.Id.Value,
        ContentHash = contract.ContentHash,
        SemanticOrdinal = Ordinal(task, contract.SemanticRevisionId),
        IsCurrent = contract.Id == task.CurrentProofContract?.Id,
        Rationale = contract.Rationale,
        Turn = contract.Turn,
        Session = contract.Session,
        Claims = [.. contract.Claims.Select(c => new ProofClaimView
        {
            Id = c.Id,
            Statement = c.Statement,
            AcceptanceCriterion = c.AcceptanceCriterion,
            Evidence = [.. c.Evidence.Select(e => new ProofEvidenceRequirementView { Kind = e.Kind, Subject = e.Subject })],
        })],
    };

    private static ProofRoundView ProofRound(WorkflowTask task, Proof proof) => new()
    {
        Id = proof.Id.Value,
        Round = proof.Round,
        ContractId = proof.ContractId.Value,
        ContractHash = proof.ContractHash,
        Verdict = proof.Verdict,
        IsCurrent = task.IsProofCurrent(proof),
        Summary = proof.Report.Summary,
        Claims = [.. proof.Report.Claims.Select(c => new ProofClaimResultView
        {
            ClaimId = c.ClaimId,
            Verdict = c.Verdict,
            Summary = c.Summary,
            Evidence = [.. c.Evidence.Select(e => new ProofEvidenceReferenceView { Kind = e.Kind, Value = e.Value })],
        })],
        ReportHash = proof.ReportHash,
        ProverProfile = proof.ProverProfile.Value,
        ProverSession = proof.ProverSession,
        ImplementerSession = proof.ImplementerSession,
        SemanticOrdinal = Ordinal(task, proof.SemanticRevisionId),
        ImplementationRevisionId = proof.ImplementationRevisionId.Value,
        VerificationRunId = proof.VerificationRunId.Value,
        ReviewId = proof.ReviewId?.Value,
        RecordedAt = proof.RecordedAt,
        ProverMilliseconds = proof.ProverMilliseconds,
        ProverCommandCount = proof.ProverCommandCount,
        ProverCommands = [.. proof.ProverCommands],
        ConfiguredCheckRerun = proof.ConfiguredCheckRerun,
        DuplicatedVerificationMilliseconds = proof.DuplicatedVerificationMilliseconds,
    };
}
