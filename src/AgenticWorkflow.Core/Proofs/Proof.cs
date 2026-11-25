using System.Collections.Immutable;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Core.Proofs;

/// <summary>
/// One formal proof that was recorded: the <see cref="Report"/> the independent prover submitted over a Proof Contract, with the provenance that makes it count. It is
/// bound to exactly the state it judged (the contract, the semantic revision, the implementation revision, the verification run and, when REVIEW is required, the review
/// it was given); any change makes it historical, and a historical proof is never deleted and never satisfies the requirement. It records WHO proved (the prover's profile
/// and its own native session, which must differ from the implementer's, from every reviewer's and from every earlier prover's) and what the proof cost. It holds no
/// transcript and no hidden reasoning.
/// </summary>
public sealed record Proof
{
    public const int MaxSessionLength = 200;
    public const int MaxCommands = 20;
    public const int MaxCommandLength = 200;

    public Proof(
        ProofId id,
        int round,
        ProofContractId contractId,
        string contractHash,
        ProofReport report,
        string reportHash,
        ProfileId proverProfile,
        string proverSession,
        string implementerSession,
        SemanticRevisionId semanticRevisionId,
        ImplementationRevisionId implementationRevisionId,
        VerificationRunId verificationRunId,
        ReviewId? reviewId,
        DateTimeOffset recordedAt,
        long proverMilliseconds,
        int proverCommandCount,
        IEnumerable<string> proverCommands,
        bool configuredCheckRerun,
        long duplicatedVerificationMilliseconds)
    {
        if (round < 1)
            throw new ArgumentOutOfRangeException(nameof(round), round, "Proof rounds are numbered from 1.");

        if (proverMilliseconds < 0 || duplicatedVerificationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(proverMilliseconds), proverMilliseconds, "A duration cannot be negative.");

        if (proverCommandCount < 0)
            throw new ArgumentOutOfRangeException(nameof(proverCommandCount), proverCommandCount, "A command count cannot be negative.");

        Report = Guard.NotNull(report, nameof(report));

        if (!string.Equals(reportHash, report.ContentHash, StringComparison.Ordinal))
            throw new InvalidProofException($"Proof {id} records report hash '{reportHash}', but its report hashes to '{report.ContentHash}'.");

        Id = id;
        Round = round;
        ContractId = contractId;
        ContractHash = ProofText.Bounded(contractHash, 128, nameof(contractHash));
        ReportHash = reportHash;
        ProverProfile = Guard.NotNull(proverProfile, nameof(proverProfile));
        ProverSession = ProofText.Bounded(proverSession, MaxSessionLength, nameof(proverSession));
        ImplementerSession = ProofText.Bounded(implementerSession, MaxSessionLength, nameof(implementerSession));
        SemanticRevisionId = semanticRevisionId;
        ImplementationRevisionId = implementationRevisionId;
        VerificationRunId = verificationRunId;
        ReviewId = reviewId;
        RecordedAt = recordedAt;
        ProverMilliseconds = proverMilliseconds;
        ProverCommandCount = proverCommandCount;
        ProverCommands = [.. Guard.NotNull(proverCommands, nameof(proverCommands)).Select(c => ProofText.Bounded(c, MaxCommandLength, nameof(proverCommands)))];
        ConfiguredCheckRerun = configuredCheckRerun;
        DuplicatedVerificationMilliseconds = duplicatedVerificationMilliseconds;

        if (ProverCommands.Length > MaxCommands || ProverCommands.Length > proverCommandCount)
            throw new ArgumentException($"A proof lists at most {MaxCommands} prover commands and never more than it counted.", nameof(proverCommands));
    }

    public ProofId Id { get; }

    /// <summary>1-based, in the order recorded: the first proof of the task is round 1.</summary>
    public int Round { get; }

    /// <summary>The Proof Contract the prover evaluated.</summary>
    public ProofContractId ContractId { get; }

    public string ContractHash { get; }

    public ProofReport Report { get; }

    public ProofVerdict Verdict => Report.Verdict;

    public string ReportHash { get; }

    /// <summary>The profile the Prover role was explicitly bound to.</summary>
    public ProfileId ProverProfile { get; }

    /// <summary>The prover's own native conversation. Never the implementer's, never a reviewer's, never an earlier prover's.</summary>
    public string ProverSession { get; }

    /// <summary>The implementer's native conversation at the time, recorded so independence is checkable from the history alone.</summary>
    public string ImplementerSession { get; }

    public SemanticRevisionId SemanticRevisionId { get; }

    public ImplementationRevisionId ImplementationRevisionId { get; }

    /// <summary>The verification run the prover was given: the proof judges the implementation as that run verified it.</summary>
    public VerificationRunId VerificationRunId { get; }

    /// <summary>The current passing review the prover was given when REVIEW is required; null when it is not.</summary>
    public ReviewId? ReviewId { get; }

    public DateTimeOffset RecordedAt { get; }

    /// <summary>Wall-clock time of the prover's active turn(s), for cost discipline. Operational, not part of any judgment.</summary>
    public long ProverMilliseconds { get; }

    /// <summary>How many shell commands the prover ran (every one is a cost; a configured check rerun is called out separately).</summary>
    public int ProverCommandCount { get; }

    /// <summary>The first commands the prover ran, in order, bounded: command references are ordinals into this list. No output.</summary>
    public ImmutableArray<string> ProverCommands { get; }

    /// <summary>True when the prover re-ran one of the project's configured checks (allowed for a concrete concern, never by default).</summary>
    public bool ConfiguredCheckRerun { get; }

    /// <summary>Time the prover spent on commands that were configured checks: verification that was already current evidence.</summary>
    public long DuplicatedVerificationMilliseconds { get; }

    /// <summary>True when any claim cites the current review as evidence.</summary>
    public bool ReviewEvidenceReused => Report.Claims.Any(c => c.Evidence.Any(e => e.Kind == ProofEvidenceRefKind.Review));

    public bool Equals(Proof? other)
        => other is not null
           && Id == other.Id
           && Round == other.Round
           && ContractId == other.ContractId
           && ContractHash == other.ContractHash
           && Report.Equals(other.Report)
           && ReportHash == other.ReportHash
           && ProverProfile == other.ProverProfile
           && ProverSession == other.ProverSession
           && ImplementerSession == other.ImplementerSession
           && SemanticRevisionId == other.SemanticRevisionId
           && ImplementationRevisionId == other.ImplementationRevisionId
           && VerificationRunId == other.VerificationRunId
           && ReviewId == other.ReviewId
           && RecordedAt == other.RecordedAt
           && ProverMilliseconds == other.ProverMilliseconds
           && ProverCommandCount == other.ProverCommandCount
           && ProverCommands.SequenceEqual(other.ProverCommands)
           && ConfiguredCheckRerun == other.ConfiguredCheckRerun
           && DuplicatedVerificationMilliseconds == other.DuplicatedVerificationMilliseconds;

    public override int GetHashCode() => HashCode.Combine(Id, Round, ReportHash);
}

/// <summary>Where a task stands against its PROVE requirement. Only meaningful when PROVE is required.</summary>
public enum ProofStatus
{
    /// <summary>No proof has been recorded.</summary>
    None,

    /// <summary>The latest proof passed and still describes the task: it satisfies the requirement.</summary>
    PassedCurrent,

    /// <summary>The latest proof passed but the task changed since (contract, semantics, implementation, verification or review): historical, it satisfies nothing.</summary>
    PassedStale,

    /// <summary>The latest proof failed and still describes the task: the implementer has not yet answered it.</summary>
    Failed,

    /// <summary>The latest proof was inconclusive and still describes the task: the implementer has not yet answered it.</summary>
    Inconclusive,

    /// <summary>The latest proof did not pass, and the task has changed since: a fresh proof is due once the work is verified (and reviewed, when required).</summary>
    NotPassedStale,
}
