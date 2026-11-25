using System.Collections.Immutable;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Core.Reviews;

/// <summary>
/// The agent's own request that the task be independently reviewed: additive only. It never creates a semantic revision, invalidates nothing and cannot be undone:
/// once a task requires REVIEW, nothing the agent says removes the requirement. It holds bounded display text and operational correlation only.
/// </summary>
public sealed record ReviewRequest
{
    public const int MaxReasonLength = 500;
    public const int MaxSessionLength = 200;

    public ReviewRequest(ReviewRequestId id, string reason, int turn, string? session, SemanticRevisionId semanticRevisionId)
    {
        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        Id = id;
        Reason = ReviewText.Bounded(reason, MaxReasonLength, nameof(reason));
        Turn = turn;
        Session = session is null ? null : ReviewText.Bounded(session, MaxSessionLength, nameof(session));
        SemanticRevisionId = semanticRevisionId;
    }

    public ReviewRequestId Id { get; }

    /// <summary>Why the agent thinks an independent look is warranted.</summary>
    public string Reason { get; }

    /// <summary>The 1-based agent turn (within its session) that requested it. Operational correlation.</summary>
    public int Turn { get; }

    /// <summary>The implementer session that requested it (an opaque, bounded runtime identifier). Operational correlation.</summary>
    public string? Session { get; }

    /// <summary>The semantic revision that was current when it was requested. Requesting creates no revision.</summary>
    public SemanticRevisionId SemanticRevisionId { get; }

    /// <summary>The reason <paramref name="reason"/> could not be recorded, or null when it is well formed (the bound Core enforces), so a runtime can refuse it at its own boundary.</summary>
    public static string? TextProblem(string? reason)
    {
        try
        {
            _ = ReviewText.Bounded(reason, MaxReasonLength, nameof(reason));
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message.Split(" (Parameter", 2)[0];
        }
    }
}

/// <summary>
/// One independent review that was recorded: the <see cref="Report"/> the reviewer submitted and the provenance that makes it count. It is bound to the exact
/// state it judged (the semantic revision, the implementation revision and the verification run it was given); any change makes it historical, and a historical
/// review is never deleted and never satisfies the requirement. It records WHO reviewed (the reviewer's profile and its own native session, which must differ from the
/// implementer's and from every earlier reviewer's: a review is always a fresh conversation) and what the review cost. It holds no transcript and no hidden reasoning.
/// </summary>
public sealed record Review
{
    public const int MaxSessionLength = 200;
    public const int MaxCommands = 10;
    public const int MaxCommandLength = 200;

    public Review(
        ReviewId id,
        int round,
        ReviewReport report,
        string reportHash,
        ProfileId reviewerProfile,
        string reviewerSession,
        string implementerSession,
        SemanticRevisionId semanticRevisionId,
        ImplementationRevisionId implementationRevisionId,
        VerificationRunId verificationRunId,
        DateTimeOffset recordedAt,
        long reviewerMilliseconds,
        int reviewerCommandCount,
        IEnumerable<string> reviewerCommands,
        bool configuredCheckRerun)
    {
        if (round < 1)
            throw new ArgumentOutOfRangeException(nameof(round), round, "Review rounds are numbered from 1.");

        if (reviewerMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(reviewerMilliseconds), reviewerMilliseconds, "A duration cannot be negative.");

        if (reviewerCommandCount < 0)
            throw new ArgumentOutOfRangeException(nameof(reviewerCommandCount), reviewerCommandCount, "A command count cannot be negative.");

        Report = Guard.NotNull(report, nameof(report));

        if (!string.Equals(reportHash, report.ContentHash, StringComparison.Ordinal))
            throw new InvalidReviewException($"Review {id} records report hash '{reportHash}', but its report hashes to '{report.ContentHash}'.");

        Id = id;
        Round = round;
        ReportHash = reportHash;
        ReviewerProfile = Guard.NotNull(reviewerProfile, nameof(reviewerProfile));
        ReviewerSession = ReviewText.Bounded(reviewerSession, MaxSessionLength, nameof(reviewerSession));
        ImplementerSession = ReviewText.Bounded(implementerSession, MaxSessionLength, nameof(implementerSession));
        SemanticRevisionId = semanticRevisionId;
        ImplementationRevisionId = implementationRevisionId;
        VerificationRunId = verificationRunId;
        RecordedAt = recordedAt;
        ReviewerMilliseconds = reviewerMilliseconds;
        ReviewerCommandCount = reviewerCommandCount;
        ReviewerCommands = [.. Guard.NotNull(reviewerCommands, nameof(reviewerCommands)).Select(c => ReviewText.Bounded(c, MaxCommandLength, nameof(reviewerCommands)))];
        ConfiguredCheckRerun = configuredCheckRerun;

        if (ReviewerCommands.Length > MaxCommands || ReviewerCommands.Length > reviewerCommandCount)
            throw new ArgumentException($"A review lists at most {MaxCommands} reviewer commands and never more than it counted.", nameof(reviewerCommands));
    }

    public ReviewId Id { get; }

    /// <summary>1-based, in the order recorded: the first review of the task is round 1.</summary>
    public int Round { get; }

    public ReviewReport Report { get; }

    public ReviewVerdict Verdict => Report.Verdict;

    /// <summary>The content hash of <see cref="Report"/>, as recorded; replay refuses a review whose report does not hash to it.</summary>
    public string ReportHash { get; }

    /// <summary>The profile the Reviewer role was explicitly bound to.</summary>
    public ProfileId ReviewerProfile { get; }

    /// <summary>The reviewer's own native conversation. Never the implementer's, never an earlier reviewer's.</summary>
    public string ReviewerSession { get; }

    /// <summary>The implementer's native conversation at the time, recorded so independence is checkable from the history alone.</summary>
    public string ImplementerSession { get; }

    public SemanticRevisionId SemanticRevisionId { get; }

    public ImplementationRevisionId ImplementationRevisionId { get; }

    /// <summary>The verification run the reviewer was given: the review judges the implementation as that run verified it.</summary>
    public VerificationRunId VerificationRunId { get; }

    public DateTimeOffset RecordedAt { get; }

    /// <summary>Wall-clock time of the reviewer's active turn, for cost discipline. Operational, not part of any judgment.</summary>
    public long ReviewerMilliseconds { get; }

    /// <summary>How many shell commands the reviewer ran (every one is a cost; a configured check rerun is called out separately).</summary>
    public int ReviewerCommandCount { get; }

    /// <summary>The first commands the reviewer ran, bounded, so a rerun of the configured checks is visible. No output.</summary>
    public ImmutableArray<string> ReviewerCommands { get; }

    /// <summary>True when the reviewer re-ran one of the project's configured checks (allowed for a concrete concern, never by default).</summary>
    public bool ConfiguredCheckRerun { get; }

    public bool Equals(Review? other)
        => other is not null
           && Id == other.Id
           && Round == other.Round
           && Report.Equals(other.Report)
           && ReportHash == other.ReportHash
           && ReviewerProfile == other.ReviewerProfile
           && ReviewerSession == other.ReviewerSession
           && ImplementerSession == other.ImplementerSession
           && SemanticRevisionId == other.SemanticRevisionId
           && ImplementationRevisionId == other.ImplementationRevisionId
           && VerificationRunId == other.VerificationRunId
           && RecordedAt == other.RecordedAt
           && ReviewerMilliseconds == other.ReviewerMilliseconds
           && ReviewerCommandCount == other.ReviewerCommandCount
           && ReviewerCommands.SequenceEqual(other.ReviewerCommands)
           && ConfiguredCheckRerun == other.ConfiguredCheckRerun;

    public override int GetHashCode() => HashCode.Combine(Id, Round, ReportHash);
}

/// <summary>Where a task stands against its REVIEW requirement. Only meaningful when REVIEW is required.</summary>
public enum ReviewStatus
{
    /// <summary>No review has been recorded.</summary>
    None,

    /// <summary>The latest review passed and still describes the task: it satisfies the requirement.</summary>
    PassedCurrent,

    /// <summary>The latest review passed but the task changed since (semantics, implementation or verification): historical, it satisfies nothing.</summary>
    PassedStale,

    /// <summary>The latest review requires changes and still describes the task: the implementer has not yet answered it.</summary>
    ChangesRequired,

    /// <summary>The latest review required changes, and the task has changed since: a fresh review is due once the work is verified.</summary>
    ChangesRequiredStale,
}
