using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Core.Authority;

/// <summary>
/// An extensible semantic/action category (<c>dependency_change</c>, <c>public_api_change</c>,
/// <c>destructive_action</c>, <c>external_side_effect</c>, <c>deployment</c>, or any project extension).
/// Categories describe meaning, never file paths.
/// </summary>
public sealed record ActionId : StableIdentifier
{
    public ActionId(string value)
        : base(value)
    {
    }
}

public enum AuthorityClassification
{
    /// <summary>The envelope says nothing about this action. This is NOT permission.</summary>
    Unclassified,

    /// <summary>Delegated: may proceed without additional human authority.</summary>
    AllowedWithoutHuman,

    /// <summary>Reserved: requires human authority.</summary>
    ReservedForHuman,
}

/// <summary>
/// Delegated authority for a task: which action categories are allowed without additional human authority and
/// which are reserved for a human. Separate from policy decisions, which record what a rule did. An action can
/// be in at most one set; anything in neither is <see cref="AuthorityClassification.Unclassified"/>, which the
/// kernel reports explicitly and never treats as allowed. What to do about it is a later policy concern.
/// </summary>
public sealed record AuthorityEnvelope
{
    public AuthorityEnvelope(IdentifierSet<ActionId> allowedWithoutHuman, IdentifierSet<ActionId> reservedForHuman)
    {
        Guard.NotNull(allowedWithoutHuman, nameof(allowedWithoutHuman));
        Guard.NotNull(reservedForHuman, nameof(reservedForHuman));

        if (allowedWithoutHuman.Overlaps(reservedForHuman))
        {
            var both = allowedWithoutHuman.Where(reservedForHuman.Contains);
            throw new ArgumentException(
                $"An action cannot be both allowed and reserved: {string.Join(", ", both)}.",
                nameof(reservedForHuman));
        }

        AllowedWithoutHuman = allowedWithoutHuman;
        ReservedForHuman = reservedForHuman;
    }

    /// <summary>Nothing classified yet.</summary>
    public static AuthorityEnvelope Empty { get; } = new(IdentifierSet<ActionId>.Empty, IdentifierSet<ActionId>.Empty);

    public IdentifierSet<ActionId> AllowedWithoutHuman { get; }

    public IdentifierSet<ActionId> ReservedForHuman { get; }

    public AuthorityClassification Classify(ActionId action)
    {
        Guard.NotNull(action, nameof(action));

        if (ReservedForHuman.Contains(action))
            return AuthorityClassification.ReservedForHuman;

        return AllowedWithoutHuman.Contains(action)
            ? AuthorityClassification.AllowedWithoutHuman
            : AuthorityClassification.Unclassified;
    }
}
