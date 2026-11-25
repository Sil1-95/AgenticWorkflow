using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticWorkflow.Cli.Diagnostics;

internal enum DoctorStatus
{
    /// <summary>The thing works, or is not needed.</summary>
    Pass,

    /// <summary>An optional capability is missing, or something is unusual but does not stop normal operation.</summary>
    Warning,

    /// <summary>Something that prevents normal operation.</summary>
    Fail,
}

/// <param name="Name">A short, stable name for what was checked.</param>
/// <param name="Status">The verdict.</param>
/// <param name="Summary">One line: what was found.</param>
/// <param name="Hint">What to do about it, when there is something to do.</param>
internal sealed record DoctorCheck(string Name, DoctorStatus Status, string Summary, string? Hint = null)
{
    public static DoctorCheck Pass(string name, string summary) => new(name, DoctorStatus.Pass, summary);

    public static DoctorCheck Warn(string name, string summary, string? hint = null) => new(name, DoctorStatus.Warning, summary, hint);

    public static DoctorCheck Fail(string name, string summary, string? hint = null) => new(name, DoctorStatus.Fail, summary, hint);
}

internal sealed record DoctorReport(string Version, IReadOnlyList<DoctorCheck> Checks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public int Warnings => Checks.Count(check => check.Status == DoctorStatus.Warning);

    public int Failures => Checks.Count(check => check.Status == DoctorStatus.Fail);

    public bool Healthy => Failures == 0;

    public string ToText()
    {
        var width = Checks.Count == 0 ? 0 : Checks.Max(check => check.Name.Length);
        var lines = new List<string> { $"AgenticWorkflow {Version} - diagnosis", string.Empty };

        foreach (var check in Checks)
        {
            lines.Add($"{Label(check.Status)}  {check.Name.PadRight(width)}  {check.Summary}");

            if (check.Hint is not null)
                lines.Add($"      {new string(' ', width)}  -> {check.Hint}");
        }

        lines.Add(string.Empty);
        lines.Add(Conclusion());

        return string.Join('\n', lines) + "\n";
    }

    public string ToJson() => JsonSerializer.Serialize(new { version = Version, healthy = Healthy, warnings = Warnings, failures = Failures, checks = Checks }, Json) + "\n";

    private string Conclusion()
    {
        if (!Healthy)
            return $"{Failures} check{Plural(Failures)} failed: normal operation is not possible until {(Failures == 1 ? "it is" : "they are")} fixed.";

        return Warnings == 0
            ? "Everything needed for normal operation is in place."
            : $"Ready to use, with {Warnings} warning{Plural(Warnings)} (optional things that are missing or unusual).";
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private static string Label(DoctorStatus status) => status switch
    {
        DoctorStatus.Pass => "PASS",
        DoctorStatus.Warning => "WARN",
        _ => "FAIL",
    };
}
