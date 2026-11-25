using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgenticWorkflow.Core.Proofs;

/// <summary>The independent prover's judgment of one claim, or of the whole contract.</summary>
public enum ProofVerdict
{
    Pass,
    Fail,
    Inconclusive,
}

/// <summary>What a piece of cited evidence points at.</summary>
public enum ProofEvidenceRefKind
{
    /// <summary><c>verification:&lt;check-name&gt;</c>: a check of the current verification.</summary>
    Verification,

    /// <summary><c>review:&lt;review-id&gt;</c>: the current passing review.</summary>
    Review,

    /// <summary><c>command:&lt;ordinal&gt;</c>: the Nth command the prover ran in its own session, as the runtime observed it.</summary>
    Command,
}

/// <summary>A typed, stable reference to evidence a claim result cites. It resolves against the authoritative state or it is refused; prose is never evidence.</summary>
public sealed record ProofEvidenceRef
{
    public const int MaxValueLength = 128;

    public ProofEvidenceRef(ProofEvidenceRefKind kind, string value)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown evidence reference kind.");

        Guard.NotBlank(value, nameof(value));

        if (value.Length > MaxValueLength || value.Any(char.IsWhiteSpace))
            throw new ArgumentException($"An evidence reference value is a single token of at most {MaxValueLength} characters (was '{value}').", nameof(value));

        if (kind == ProofEvidenceRefKind.Command && !(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) && ordinal >= 1))
            throw new ArgumentException($"A command reference is the command's ordinal, from 1 (was '{value}').", nameof(value));

        Kind = kind;
        Value = value;
    }

    public ProofEvidenceRefKind Kind { get; }

    public string Value { get; }

    /// <summary>The ordinal of a command reference; 0 for any other kind.</summary>
    public int Ordinal => Kind == ProofEvidenceRefKind.Command ? int.Parse(Value, CultureInfo.InvariantCulture) : 0;

    public override string ToString() => $"{KindName(Kind)}:{Value}";

    public static string KindName(ProofEvidenceRefKind kind) => kind switch
    {
        ProofEvidenceRefKind.Verification => "verification",
        ProofEvidenceRefKind.Review => "review",
        _ => "command",
    };

    /// <summary>Parses <c>verification:tests</c>, <c>review:&lt;id&gt;</c> or <c>command:3</c>.</summary>
    public static ProofEvidenceRef Parse(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split(':', 2);

        if (parts.Length != 2)
            throw new ArgumentException($"An evidence reference looks like 'verification:<check>', 'review:<id>' or 'command:<n>' (was '{text}').", nameof(text));

        return parts[0].ToLowerInvariant() switch
        {
            "verification" => new ProofEvidenceRef(ProofEvidenceRefKind.Verification, parts[1]),
            "review" => new ProofEvidenceRef(ProofEvidenceRefKind.Review, parts[1]),
            "command" => new ProofEvidenceRef(ProofEvidenceRefKind.Command, parts[1]),
            _ => throw new ArgumentException($"Unknown evidence reference kind '{parts[0]}'; use 'verification', 'review' or 'command'.", nameof(text)),
        };
    }
}

/// <summary>The prover's verdict on one claim of the contract, the evidence it cites and a short bounded reason. No transcript, no hidden reasoning.</summary>
public sealed record ProofClaimResult
{
    public const int MaxSummaryLength = 1000;
    public const int MaxEvidenceRefs = 12;

    public ProofClaimResult(string claimId, ProofVerdict verdict, IEnumerable<ProofEvidenceRef> evidence, string summary)
    {
        if (!Enum.IsDefined(verdict))
            throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown proof verdict.");

        ClaimId = ProofText.Bounded(claimId, ProofClaim.MaxIdLength, nameof(claimId));
        Verdict = verdict;
        Evidence = [.. Guard.NotNull(evidence, nameof(evidence))];
        Summary = ProofText.Bounded(summary, MaxSummaryLength, nameof(summary));

        if (Evidence.Length > MaxEvidenceRefs)
            throw new ArgumentException($"A claim result cites at most {MaxEvidenceRefs} evidence references (was {Evidence.Length}).", nameof(evidence));

        if (Evidence.Distinct().Count() != Evidence.Length)
            throw new ArgumentException($"Claim '{ClaimId}' cites the same evidence reference twice.", nameof(evidence));
    }

    public string ClaimId { get; }

    public ProofVerdict Verdict { get; }

    public ImmutableArray<ProofEvidenceRef> Evidence { get; }

    public string Summary { get; }

    public bool Equals(ProofClaimResult? other)
        => other is not null
           && ClaimId == other.ClaimId
           && Verdict == other.Verdict
           && Summary == other.Summary
           && Evidence.SequenceEqual(other.Evidence);

    public override int GetHashCode() => HashCode.Combine(ClaimId, Verdict);
}

/// <summary>
/// What the prover submitted: one result per claim of the contract and a summary. The overall verdict is not the prover's to choose: it is derived (every claim PASS gives
/// PASS; any FAIL gives FAIL; otherwise INCONCLUSIVE), so a report can never say "looks good" over a failing claim. Bounded per item and in total and identified by a
/// content hash over its canonical form.
/// </summary>
public sealed record ProofReport
{
    public const int MaxSummaryLength = 1000;
    public const int MaxTotalLength = 16000;

    public ProofReport(string summary, IEnumerable<ProofClaimResult> claims)
    {
        Summary = ProofText.Bounded(summary, MaxSummaryLength, nameof(summary));
        Claims = [.. Guard.NotNull(claims, nameof(claims))];

        if (Claims.IsEmpty)
            throw new ArgumentException("A proof report states a result for every claim of the contract (at least one).", nameof(claims));

        if (Claims.Length > ProofContract.MaxClaims)
            throw new ArgumentException($"A proof report states at most {ProofContract.MaxClaims} claim results (was {Claims.Length}).", nameof(claims));

        if (Claims.Select(c => c.ClaimId).Distinct(StringComparer.Ordinal).Count() != Claims.Length)
            throw new ArgumentException("A proof report states each claim once.", nameof(claims));

        var total = Summary.Length + Claims.Sum(c => c.ClaimId.Length + c.Summary.Length + c.Evidence.Sum(e => e.ToString().Length));

        if (total > MaxTotalLength)
            throw new ArgumentException($"A proof report must not exceed {MaxTotalLength} characters in total (was {total}).", nameof(claims));
    }

    public string Summary { get; }

    public ImmutableArray<ProofClaimResult> Claims { get; }

    /// <summary>Derived: PASS only when every claim is PASS; any FAIL makes it FAIL; no FAIL but an INCONCLUSIVE makes it INCONCLUSIVE.</summary>
    public ProofVerdict Verdict => Derive(Claims.Select(c => c.Verdict));

    public static ProofVerdict Derive(IEnumerable<ProofVerdict> claimVerdicts)
    {
        var verdicts = claimVerdicts.ToList();

        if (verdicts.Contains(ProofVerdict.Fail))
            return ProofVerdict.Fail;

        return verdicts.Count > 0 && verdicts.All(v => v == ProofVerdict.Pass) ? ProofVerdict.Pass : ProofVerdict.Inconclusive;
    }

    /// <summary>SHA-256 (lowercase hex) of the report in a fixed canonical form, so a recorded report can be shown to be the one that was submitted.</summary>
    public string ContentHash
    {
        get
        {
            var canonical = new StringBuilder();

            void Field(string name, string? text)
                => canonical.Append(name).Append(':').Append(text?.Length ?? -1).Append(':').Append(text).Append('\n');

            Field("summary", Summary);

            foreach (var claim in Claims)
            {
                Field("claim", claim.ClaimId);
                Field("verdict", VerdictName(claim.Verdict));
                Field("claim_summary", claim.Summary);

                foreach (var evidence in claim.Evidence)
                    Field("evidence", evidence.ToString());
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        }
    }

    public static string VerdictName(ProofVerdict verdict) => verdict switch
    {
        ProofVerdict.Pass => "pass",
        ProofVerdict.Fail => "fail",
        _ => "inconclusive",
    };

    public static ProofVerdict ParseVerdict(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "pass" => ProofVerdict.Pass,
        "fail" => ProofVerdict.Fail,
        "inconclusive" => ProofVerdict.Inconclusive,
        _ => throw new ArgumentException($"The verdict must be PASS, FAIL or INCONCLUSIVE (was '{text}')."),
    };

    /// <summary>The reason this report could not be recorded, or null when its own shape is well formed: the bounds Core enforces, for a runtime to refuse it at its own boundary. (Whether it fits the contract and resolves against the state is <c>WorkflowTask.ProofReportProblem</c>.)</summary>
    public static string? Problem(string? summary, IEnumerable<ProofClaimResult> claims)
    {
        try
        {
            _ = new ProofReport(summary!, claims);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message.Split(" (Parameter", 2)[0];
        }
    }

    public bool Equals(ProofReport? other)
        => other is not null
           && Summary == other.Summary
           && Claims.SequenceEqual(other.Claims);

    public override int GetHashCode() => HashCode.Combine(Summary, Claims.Length);
}
