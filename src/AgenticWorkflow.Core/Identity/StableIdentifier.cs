namespace AgenticWorkflow.Core.Identity;

/// <summary>
/// A human-authored, extensible, stable identifier (capability, action category, rule, role, ...).
/// Lowercase ASCII letters, digits and <c>_ . : -</c>; must start with a letter or digit. Comparison is
/// ordinal, so ordering and equality are culture-independent and deterministic.
/// Distinct derived types are never equal to each other even if their text is.
/// </summary>
public abstract record StableIdentifier
{
    public const int MaxLength = 128;

    protected StableIdentifier(string value) => Value = Validate(value);

    public string Value { get; }

    public sealed override string ToString() => Value;

    private static string Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Identifier must not be empty.", nameof(value));

        if (value.Length > MaxLength)
            throw new ArgumentException($"Identifier must not exceed {MaxLength} characters.", nameof(value));

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isAlphanumeric = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            var isSeparator = c is '_' or '.' or ':' or '-';

            if (!isAlphanumeric && !(isSeparator && i > 0))
                throw new ArgumentException(
                    $"Identifier '{value}' is invalid: use lowercase letters, digits and '_', '.', ':', '-' (not first).",
                    nameof(value));
        }

        return value;
    }
}
