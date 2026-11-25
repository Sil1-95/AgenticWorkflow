using System.Security.Cryptography;
using System.Text;

namespace AgenticWorkflow.Core.Gates;

/// <summary>
/// The artifact of a WHAT gate: a bounded, structured statement of what the task is to achieve, written by the agent and approved or sent back by a human
/// before anything is implemented. It states intent, never implementation strategy: it has no sections for files, designs or steps.
/// <para>
/// It is immutable and carried by the gate that asks for approval, so the proposal a human approved is exactly the one in history, replayable from any
/// process. Every section is bounded (individually and in total) and checked for control characters; nothing here is summarized or truncated, an oversized
/// proposal is refused. <see cref="ContentHash"/> identifies the contract itself (the sections, in a fixed canonical form), independent of the wording of the
/// rationale and of which turn raised it.
/// </para>
/// </summary>
public sealed record WhatProposal
{
    public const int MaxSectionLength = 1500;
    public const int MaxRationaleLength = 500;
    public const int MaxTotalLength = 6000;

    public WhatProposal(
        string objective,
        string requiredBehavior,
        string scope,
        string? constraints,
        string? nonGoals,
        string acceptanceConditions,
        string rationale,
        int turn)
    {
        Objective = Section(objective, nameof(objective), MaxSectionLength);
        RequiredBehavior = Section(requiredBehavior, nameof(requiredBehavior), MaxSectionLength);
        Scope = Section(scope, nameof(scope), MaxSectionLength);
        Constraints = OptionalSection(constraints, nameof(constraints));
        NonGoals = OptionalSection(nonGoals, nameof(nonGoals));
        AcceptanceConditions = Section(acceptanceConditions, nameof(acceptanceConditions), MaxSectionLength);
        Rationale = Section(rationale, nameof(rationale), MaxRationaleLength);

        var total = Sections().Sum(s => s.Text.Length);
        if (total > MaxTotalLength)
            throw new ArgumentException($"A WHAT proposal must not exceed {MaxTotalLength} characters in total (was {total}).", nameof(objective));

        if (turn < 1)
            throw new ArgumentOutOfRangeException(nameof(turn), turn, "Turns are numbered from 1.");

        Turn = turn;
    }

    /// <summary>What the task is for, in one coherent statement.</summary>
    public string Objective { get; }

    /// <summary>The externally visible behavior that must hold when the task is done.</summary>
    public string RequiredBehavior { get; }

    /// <summary>What is in play: the parts of the product or repository the task is about.</summary>
    public string Scope { get; }

    /// <summary>Limits the result must respect; null when the agent had none to state.</summary>
    public string? Constraints { get; }

    /// <summary>What the task deliberately does not do; null when the agent had none to state.</summary>
    public string? NonGoals { get; }

    /// <summary>The conditions under which the result counts as meeting the requirement.</summary>
    public string AcceptanceConditions { get; }

    /// <summary>Why a human decision on this contract is warranted. Not part of the contract itself.</summary>
    public string Rationale { get; }

    /// <summary>The 1-based agent turn (within its session) that proposed it. Operational correlation; it identifies no conversation.</summary>
    public int Turn { get; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the contract: the six sections, each named and length-prefixed in a fixed order, so the same sections always hash the same and no
    /// two different sets collide through concatenation. The rationale and the turn are not part of it.
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
            ("Objective", Objective),
            ("Required behavior", RequiredBehavior),
            ("Scope", Scope),
        };

        if (Constraints is not null)
            sections.Add(("Constraints", Constraints));

        if (NonGoals is not null)
            sections.Add(("Non-goals", NonGoals));

        sections.Add(("Acceptance conditions", AcceptanceConditions));

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
