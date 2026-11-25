using System.Security.Cryptography;
using System.Text;

namespace AgenticWorkflow.Core.Gates;

/// <summary>
/// The artifact of a HOW gate: a bounded, structured statement of the implementation strategy the task is to use, written by the agent and approved or
/// sent back by a human before anything is implemented. Where the WHAT states intent, this states consequential technical strategy (architecture, component
/// responsibilities, data and control flow, migration and compatibility, tradeoffs); it is deliberately not source code, a file-by-file patch plan or a task
/// checklist, and it has no sections for them.
/// <para>
/// It is immutable and carried by the gate that asks for approval, so the strategy a human approved is exactly the one in history, replayable from any
/// process. Every section is bounded (individually and in total) and checked for control characters; nothing here is summarized or truncated, an oversized
/// proposal is refused. <see cref="ContentHash"/> identifies the strategy itself (the sections, in a fixed canonical form), independent of the wording of the
/// rationale and of which turn raised it.
/// </para>
/// </summary>
public sealed record HowProposal
{
    public const int MaxSectionLength = 1500;
    public const int MaxRationaleLength = 500;
    public const int MaxTotalLength = 6000;

    public HowProposal(
        string strategy,
        string components,
        string? dataAndControlFlow,
        string? compatibility,
        string risksAndTradeoffs,
        string? verificationApproach,
        string rationale,
        int turn)
    {
        Strategy = Section(strategy, nameof(strategy), MaxSectionLength);
        Components = Section(components, nameof(components), MaxSectionLength);
        DataAndControlFlow = OptionalSection(dataAndControlFlow, nameof(dataAndControlFlow));
        Compatibility = OptionalSection(compatibility, nameof(compatibility));
        RisksAndTradeoffs = Section(risksAndTradeoffs, nameof(risksAndTradeoffs), MaxSectionLength);
        VerificationApproach = OptionalSection(verificationApproach, nameof(verificationApproach));
        Rationale = Section(rationale, nameof(rationale), MaxRationaleLength);

        var total = Sections().Sum(s => s.Text.Length);
        if (total > MaxTotalLength)
            throw new ArgumentException($"A HOW proposal must not exceed {MaxTotalLength} characters in total (was {total}).", nameof(strategy));

        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        Turn = turn;
    }

    /// <summary>The chosen approach, in one coherent statement, and the architectural shape it gives the change.</summary>
    public string Strategy { get; }

    /// <summary>The components or interfaces involved and what each is responsible for.</summary>
    public string Components { get; }

    /// <summary>The important data or control flow, where it is part of the design; null when there is nothing to state.</summary>
    public string? DataAndControlFlow { get; }

    /// <summary>The persistence, migration or compatibility strategy; null when it does not apply.</summary>
    public string? Compatibility { get; }

    /// <summary>The main risks and the tradeoffs made, including the principal alternative that was not chosen.</summary>
    public string RisksAndTradeoffs { get; }

    /// <summary>How the strategy will be verified, where that is part of the design; null when it is not.</summary>
    public string? VerificationApproach { get; }

    /// <summary>Why a human decision on this strategy is warranted. Not part of the strategy itself.</summary>
    public string Rationale { get; }

    /// <summary>The 1-based agent turn (within its session) that proposed it. Operational correlation; it identifies no conversation.</summary>
    public int Turn { get; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the strategy: the sections that are present, each named and length-prefixed in a fixed order, so the same sections always
    /// hash the same and no two different sets collide through concatenation. The rationale and the turn are not part of it.
    /// </summary>
    public string ContentHash
    {
        get
        {
            var canonical = new StringBuilder();

            foreach (var (name, text) in Sections())
                canonical.Append(name).Append(':').Append(text.Length).Append(':').Append(text).Append('\n');

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        }
    }

    /// <summary>The sections that are present, in their fixed order, with their display names.</summary>
    public IReadOnlyList<(string Name, string Text)> Sections()
    {
        var sections = new List<(string, string)>
        {
            ("Strategy", Strategy),
            ("Components and responsibilities", Components),
        };

        if (DataAndControlFlow is not null)
            sections.Add(("Data and control flow", DataAndControlFlow));

        if (Compatibility is not null)
            sections.Add(("Compatibility and migration", Compatibility));

        sections.Add(("Risks and tradeoffs", RisksAndTradeoffs));

        if (VerificationApproach is not null)
            sections.Add(("Verification approach", VerificationApproach));

        return sections;
    }

    private static string? OptionalSection(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? null : Section(value, name, MaxSectionLength);

    private static string Section(string? value, string name, int maxLength)
    {
        Guard.NotBlank(value, name);

        if (value!.Length > maxLength)
            throw new ArgumentException($"The {name} must not exceed {maxLength} characters (was {value.Length}).", name);

        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"The {name} must not contain control characters.", name);

        return value;
    }
}
