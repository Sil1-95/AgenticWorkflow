using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AgenticWorkflow.Core.Reviews;

/// <summary>The independent reviewer's judgment of the current implementation. There is no severity taxonomy: a change is either required or it is not.</summary>
public enum ReviewVerdict
{
    Pass,
    ChangesRequired,
}

/// <summary>
/// One concrete problem the reviewer found: what it is, the evidence for it, why it matters and what to do. Bounded display text only; it never holds a transcript,
/// hidden reasoning or a diff.
/// </summary>
public sealed record ReviewFinding
{
    public const int MaxTitleLength = 150;
    public const int MaxEvidenceLength = 1000;
    public const int MaxImpactLength = 500;
    public const int MaxRecommendationLength = 500;
    public const int MaxLocationLength = 200;

    public ReviewFinding(string title, string evidence, string impact, string recommendation, string? location = null)
    {
        Title = ReviewText.Bounded(title, MaxTitleLength, nameof(title));
        Evidence = ReviewText.Bounded(evidence, MaxEvidenceLength, nameof(evidence));
        Impact = ReviewText.Bounded(impact, MaxImpactLength, nameof(impact));
        Recommendation = ReviewText.Bounded(recommendation, MaxRecommendationLength, nameof(recommendation));
        Location = string.IsNullOrWhiteSpace(location) ? null : ReviewText.Bounded(location, MaxLocationLength, nameof(location));
    }

    public string Title { get; }

    /// <summary>What the reviewer observed, concretely and verifiably.</summary>
    public string Evidence { get; }

    /// <summary>Why it matters for the approved semantics or the repository.</summary>
    public string Impact { get; }

    public string Recommendation { get; }

    /// <summary>Where it was seen (a path, optionally with a line); null when it is not tied to one place.</summary>
    public string? Location { get; }
}

/// <summary>
/// What the reviewer submitted: a verdict, a summary, the findings that justify a request for changes and optional non-blocking notes. Bounded per item and in total,
/// and identified by a content hash over its canonical form, so a recorded report can be shown to be the one that was submitted. Notes never block completion.
/// </summary>
public sealed record ReviewReport
{
    public const int MaxSummaryLength = 1000;
    public const int MaxNotesLength = 1000;
    public const int MaxFindings = 10;
    public const int MaxTotalLength = 12000;

    public ReviewReport(ReviewVerdict verdict, string summary, IEnumerable<ReviewFinding> findings, string? notes = null)
    {
        if (!Enum.IsDefined(verdict))
            throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown review verdict.");

        Verdict = verdict;
        Summary = ReviewText.Bounded(summary, MaxSummaryLength, nameof(summary));
        Findings = [.. Guard.NotNull(findings, nameof(findings))];
        Notes = string.IsNullOrWhiteSpace(notes) ? null : ReviewText.Bounded(notes, MaxNotesLength, nameof(notes));

        if (verdict == ReviewVerdict.ChangesRequired && Findings.IsEmpty)
            throw new ArgumentException("A review that requires changes states at least one finding.", nameof(findings));

        if (verdict == ReviewVerdict.Pass && !Findings.IsEmpty)
            throw new ArgumentException("A passing review states no findings; anything worth saying that does not block goes in the notes.", nameof(findings));

        if (Findings.Length > MaxFindings)
            throw new ArgumentException($"A review states at most {MaxFindings} findings (was {Findings.Length}).", nameof(findings));

        var total = Summary.Length + (Notes?.Length ?? 0)
                    + Findings.Sum(f => f.Title.Length + f.Evidence.Length + f.Impact.Length + f.Recommendation.Length + (f.Location?.Length ?? 0));

        if (total > MaxTotalLength)
            throw new ArgumentException($"A review report must not exceed {MaxTotalLength} characters in total (was {total}).", nameof(findings));
    }

    public ReviewVerdict Verdict { get; }

    public string Summary { get; }

    public ImmutableArray<ReviewFinding> Findings { get; }

    /// <summary>Optional observations that do not block anything; null when there are none.</summary>
    public string? Notes { get; }

    /// <summary>SHA-256 (lowercase hex) of the report in a fixed canonical form: every field named and length-prefixed, so equal reports hash equally and no two differ by concatenation.</summary>
    public string ContentHash
    {
        get
        {
            var canonical = new StringBuilder();

            void Field(string name, string? text)
                => canonical.Append(name).Append(':').Append(text?.Length ?? -1).Append(':').Append(text).Append('\n');

            Field("verdict", Verdict == ReviewVerdict.Pass ? "pass" : "changes_required");
            Field("summary", Summary);

            foreach (var finding in Findings)
            {
                Field("title", finding.Title);
                Field("evidence", finding.Evidence);
                Field("impact", finding.Impact);
                Field("recommendation", finding.Recommendation);
                Field("location", finding.Location);
            }

            Field("notes", Notes);

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        }
    }

    /// <summary>The reason this report could not be recorded, or null when it is well formed: the very bounds Core enforces, for a runtime to refuse a report at its own boundary.</summary>
    public static string? Problem(ReviewVerdict verdict, string? summary, IEnumerable<ReviewFinding> findings, string? notes)
    {
        try
        {
            _ = new ReviewReport(verdict, summary!, findings, notes);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message.Split(" (Parameter", 2)[0];
        }
    }

    public bool Equals(ReviewReport? other)
        => other is not null
           && Verdict == other.Verdict
           && Summary == other.Summary
           && Notes == other.Notes
           && Findings.SequenceEqual(other.Findings);

    public override int GetHashCode() => HashCode.Combine(Verdict, Summary);
}

internal static class ReviewText
{
    public static string Bounded(string? value, int maxLength, string name)
    {
        Guard.NotBlank(value, name);

        if (value!.Length > maxLength)
            throw new ArgumentException($"The {name} must not exceed {maxLength} characters (was {value.Length}).", name);

        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"The {name} must not contain control characters.", name);

        return value;
    }
}
