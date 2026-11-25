using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Core.Proofs;

/// <summary>The closed vocabulary of evidence a claim can require. There is deliberately no generic evidence language.</summary>
public enum ProofEvidenceKind
{
    /// <summary>A named configured check of the project's verification, which must be present in the current verification.</summary>
    Verification,

    /// <summary>The current independent REVIEW, which must have passed. Requiring it never makes REVIEW required: without a current pass the claim cannot pass.</summary>
    Review,

    /// <summary>A bounded description of a claim-specific observation the prover must establish itself, by read-only repository inspection or a targeted command.</summary>
    Observation,
}

/// <summary>
/// One thing a claim needs as evidence: a configured check, the current review, or a targeted observation. It states what must be demonstrated, never a conclusion.
/// </summary>
public sealed record ProofEvidenceRequirement
{
    public const int MaxObservationLength = 300;

    private ProofEvidenceRequirement(ProofEvidenceKind kind, string? subject)
    {
        Kind = kind;
        Subject = subject;
    }

    public ProofEvidenceKind Kind { get; }

    /// <summary>The check name for <see cref="ProofEvidenceKind.Verification"/>, the observation for <see cref="ProofEvidenceKind.Observation"/>, null for the review.</summary>
    public string? Subject { get; }

    public static ProofEvidenceRequirement Verification(string checkName)
        => new(ProofEvidenceKind.Verification, new VerificationCheckId(checkName).Value);

    public static ProofEvidenceRequirement CurrentReviewPass() => new(ProofEvidenceKind.Review, null);

    public static ProofEvidenceRequirement Observation(string description)
        => new(ProofEvidenceKind.Observation, ProofText.Bounded(description, MaxObservationLength, nameof(description)));

    /// <summary>The persisted/displayed form: <c>verification:tests</c>, <c>review:current-pass</c>, <c>observation:...</c>.</summary>
    public override string ToString() => Kind switch
    {
        ProofEvidenceKind.Verification => $"verification:{Subject}",
        ProofEvidenceKind.Review => "review:current-pass",
        _ => $"observation:{Subject}",
    };

    /// <summary>The requirement from its persisted/typed pieces: kind name (<c>verification</c>, <c>review</c>, <c>observation</c>) and subject.</summary>
    public static ProofEvidenceRequirement Parse(string kind, string? subject) => kind switch
    {
        "verification" => Verification(subject ?? string.Empty),
        "review" when string.IsNullOrWhiteSpace(subject) || subject == "current-pass" => CurrentReviewPass(),
        "observation" => Observation(subject ?? string.Empty),
        _ => throw new ArgumentException($"Unknown evidence requirement '{kind}'; use 'verification', 'review' or 'observation'.", nameof(kind)),
    };

    public static string KindName(ProofEvidenceKind kind) => kind switch
    {
        ProofEvidenceKind.Verification => "verification",
        ProofEvidenceKind.Review => "review",
        _ => "observation",
    };
}

/// <summary>One consequential claim of a Proof Contract: what must be true, what evidence must show it, and the criterion that decides it.</summary>
public sealed record ProofClaim
{
    public const int MaxIdLength = 20;
    public const int MaxStatementLength = 1000;
    public const int MaxAcceptanceCriterionLength = 1000;
    public const int MaxEvidence = 4;

    public ProofClaim(string id, string statement, IEnumerable<ProofEvidenceRequirement> evidence, string acceptanceCriterion)
    {
        Id = ValidId(id);
        Statement = ProofText.Bounded(statement, MaxStatementLength, nameof(statement));
        Evidence = [.. Guard.NotNull(evidence, nameof(evidence))];
        AcceptanceCriterion = ProofText.Bounded(acceptanceCriterion, MaxAcceptanceCriterionLength, nameof(acceptanceCriterion));

        if (Evidence.IsEmpty)
            throw new ArgumentException($"Claim '{Id}' requires at least one piece of evidence.", nameof(evidence));

        if (Evidence.Length > MaxEvidence)
            throw new ArgumentException($"Claim '{Id}' lists at most {MaxEvidence} evidence requirements (was {Evidence.Length}).", nameof(evidence));

        if (Evidence.Distinct().Count() != Evidence.Length)
            throw new ArgumentException($"Claim '{Id}' lists the same evidence requirement twice.", nameof(evidence));
    }

    /// <summary>A short stable identifier (<c>C1</c>, <c>api-compat</c>): a letter, then letters, digits, '-' or '_'.</summary>
    public string Id { get; }

    public string Statement { get; }

    public ImmutableArray<ProofEvidenceRequirement> Evidence { get; }

    public string AcceptanceCriterion { get; }

    public bool Equals(ProofClaim? other)
        => other is not null
           && Id == other.Id
           && Statement == other.Statement
           && AcceptanceCriterion == other.AcceptanceCriterion
           && Evidence.SequenceEqual(other.Evidence);

    public override int GetHashCode() => HashCode.Combine(Id, Statement);

    private static string ValidId(string? id)
    {
        Guard.NotBlank(id, nameof(id));

        if (id!.Length > MaxIdLength || !char.IsAsciiLetter(id[0]) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"A claim id is a short identifier of at most {MaxIdLength} characters: a letter, then letters, digits, '-' or '_' (was '{id}').", nameof(id));

        return id;
    }
}

/// <summary>
/// The Proof Contract: the implementer's explicit statement of WHAT must be demonstrated for this task, written before any proof evidence is gathered and judged
/// afterwards by an independent prover. It is bound to the semantic revision under which it was written: it stays current across implementation repairs and becomes
/// history the moment the semantics change, and it is immutable for that revision (a failed proof cannot weaken it). It holds claims and evidence requirements, never
/// a conclusion, and is identified by a content hash over its canonical form. Recording it is also how the agent adds PROVE to a run (additive; nothing removes it).
/// </summary>
public sealed record ProofContract
{
    public const int MaxRationaleLength = 1000;
    public const int MaxClaims = 8;
    public const int MaxTotalLength = 12000;
    public const int MaxSessionLength = 200;

    public ProofContract(
        ProofContractId id,
        SemanticRevisionId semanticRevisionId,
        int turn,
        string? session,
        string rationale,
        IEnumerable<ProofClaim> claims,
        string? expectedHash = null)
    {
        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        Id = id;
        SemanticRevisionId = semanticRevisionId;
        Turn = turn;
        Session = session is null ? null : ProofText.Bounded(session, MaxSessionLength, nameof(session));
        Rationale = ProofText.Bounded(rationale, MaxRationaleLength, nameof(rationale));
        Claims = [.. Guard.NotNull(claims, nameof(claims))];

        if (Claims.IsEmpty)
            throw new ArgumentException("A Proof Contract states at least one claim.", nameof(claims));

        if (Claims.Length > MaxClaims)
            throw new ArgumentException($"A Proof Contract states at most {MaxClaims} claims (was {Claims.Length}).", nameof(claims));

        if (Claims.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != Claims.Length)
            throw new ArgumentException("A Proof Contract's claim ids are unique.", nameof(claims));

        var total = Rationale.Length + Claims.Sum(c => c.Id.Length + c.Statement.Length + c.AcceptanceCriterion.Length + c.Evidence.Sum(e => e.ToString().Length));

        if (total > MaxTotalLength)
            throw new ArgumentException($"A Proof Contract must not exceed {MaxTotalLength} characters in total (was {total}).", nameof(claims));

        ContentHash = ComputeHash();

        if (expectedHash is not null && !string.Equals(expectedHash, ContentHash, StringComparison.Ordinal))
            throw new InvalidProofException($"Proof Contract {id} records hash '{expectedHash}', but its content hashes to '{ContentHash}'.");
    }

    public ProofContractId Id { get; }

    /// <summary>The semantic revision the contract was written for. It is current only while that revision is.</summary>
    public SemanticRevisionId SemanticRevisionId { get; }

    /// <summary>The 1-based agent turn that submitted it. Operational correlation.</summary>
    public int Turn { get; }

    /// <summary>The implementer session that submitted it (opaque, bounded). Operational correlation.</summary>
    public string? Session { get; }

    public string Rationale { get; }

    public ImmutableArray<ProofClaim> Claims { get; }

    /// <summary>SHA-256 (lowercase hex) of the rationale and claims in a fixed canonical form (every field named and length-prefixed). Equal contracts hash equally, whatever revision or session they were written under.</summary>
    public string ContentHash { get; }

    public ProofClaim? Find(string claimId) => Claims.FirstOrDefault(c => string.Equals(c.Id, claimId, StringComparison.Ordinal));

    /// <summary>The reason this contract could not be recorded, or null when it is well formed: the very bounds Core enforces, for a runtime to refuse it whole at its own boundary.</summary>
    public static string? Problem(string? rationale, IEnumerable<ProofClaim> claims)
    {
        try
        {
            _ = new ProofContract(new ProofContractId(Guid.NewGuid()), new SemanticRevisionId(Guid.NewGuid()), 1, null, rationale!, claims);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message.Split(" (Parameter", 2)[0];
        }
    }

    public bool Equals(ProofContract? other)
        => other is not null
           && Id == other.Id
           && SemanticRevisionId == other.SemanticRevisionId
           && Turn == other.Turn
           && Session == other.Session
           && Rationale == other.Rationale
           && ContentHash == other.ContentHash
           && Claims.SequenceEqual(other.Claims);

    public override int GetHashCode() => HashCode.Combine(Id, ContentHash);

    private string ComputeHash()
    {
        var canonical = new StringBuilder();

        void Field(string name, string? text)
            => canonical.Append(name).Append(':').Append(text?.Length ?? -1).Append(':').Append(text).Append('\n');

        Field("rationale", Rationale);

        foreach (var claim in Claims)
        {
            Field("claim", claim.Id);
            Field("statement", claim.Statement);
            Field("criterion", claim.AcceptanceCriterion);

            foreach (var requirement in claim.Evidence)
                Field("evidence", requirement.ToString());
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

internal static class ProofText
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
