using System.Globalization;

namespace AgenticWorkflow.Persistence.Sqlite.Serialization;

/// <summary>
/// The one canonical stored form of an instant: UTC, ISO-8601, fixed 7 fractional digits (100 ns ticks), literal
/// <c>Z</c> suffix, e.g. <c>2026-01-01T12:00:00.0000000Z</c>. Fixed width makes the text sort chronologically.
/// The original offset is not preserved; the instant is.
/// </summary>
internal static class Timestamps
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset value)
        => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateTimeOffset value)
        => DateTimeOffset.TryParseExact(
            text,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
