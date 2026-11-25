namespace AgenticWorkflow.Core;

/// <summary>Argument validation for values that are malformed regardless of any task state.</summary>
internal static class Guard
{
    public static string NotBlank(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must not be null, empty or whitespace.", paramName);

        return value;
    }

    public static Guid NotEmpty(Guid value, string paramName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Identity must not be the empty GUID.", paramName);

        return value;
    }

    public static T NotNull<T>(T? value, string paramName)
        where T : class
        => value ?? throw new ArgumentNullException(paramName);
}
