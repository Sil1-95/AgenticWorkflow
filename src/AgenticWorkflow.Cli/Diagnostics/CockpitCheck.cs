using System.Text.Json;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>
/// That the Herdr cockpit is installed as a real copy that runs this <c>aw</c>: its manifest declares the command-palette entry, its settings name this program, and its version stamp is
/// this release's. A missing cockpit is only a warning: the command line works without it.
/// </summary>
internal sealed class CockpitCheck : IDoctorCheck
{
    private const string ModName = "agenticworkflow";
    private const string ReinstallHint = "Run install.ps1 from the release package to refresh the cockpit.";

    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DoctorCheck>>(Check(context));

    private static List<DoctorCheck> Check(DoctorContext context)
    {
        var mod = Path.Combine(context.HerdrHome, "mods", ModName);

        if (!Directory.Exists(mod))
            return [DoctorCheck.Warn("cockpit", $"not installed in {context.HerdrHome}", "Optional: the command line works without it. install.ps1 installs it.")];

        var checks = new List<DoctorCheck>();

        if (new DirectoryInfo(mod).LinkTarget is not null)
            checks.Add(DoctorCheck.Warn("cockpit", $"{mod} is a link", "Herdr lists a mod's command-palette entry only for a real folder. install.ps1 installs a copy."));

        checks.Add(CheckPalette(mod));
        checks.Add(CheckSettings(context));
        checks.Add(CheckVersion(context, mod));

        return checks;
    }

    private static DoctorCheck CheckPalette(string mod)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "mod.json")));

            return document.RootElement.TryGetProperty("palette", out var palette) && palette.ValueKind == JsonValueKind.Array && palette.GetArrayLength() > 0
                ? DoctorCheck.Pass("cockpit palette", "'Open AgenticWorkflow' is declared")
                : DoctorCheck.Warn("cockpit palette", "the cockpit's mod.json declares no command-palette entry", ReinstallHint);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return DoctorCheck.Fail("cockpit palette", $"the cockpit's mod.json cannot be read: {e.Message}", ReinstallHint);
        }
    }

    private static DoctorCheck CheckSettings(DoctorContext context)
    {
        var path = Path.Combine(context.HerdrHome, "agenticworkflow", "config.json");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("aw", out var aw) || aw.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(aw.GetString()))
                return DoctorCheck.Warn("cockpit aw", "the cockpit settings name no aw (it will look for 'aw' on PATH)", ReinstallHint);

            var configured = aw.GetString()!;

            if (SamePath(configured, context.AwPath))
                return DoctorCheck.Pass("cockpit aw", $"runs {configured}");

            return File.Exists(configured)
                ? DoctorCheck.Warn("cockpit aw", $"runs {configured}, not this aw ({context.AwPath})", "Two installations exist. Run install.ps1 from the release you want.")
                : DoctorCheck.Fail("cockpit aw", $"runs {configured}, which does not exist", ReinstallHint);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return DoctorCheck.Warn("cockpit aw", $"no readable cockpit settings ({e.GetType().Name}); the cockpit will look for 'aw' on PATH", ReinstallHint);
        }
    }

    private static DoctorCheck CheckVersion(DoctorContext context, string mod)
    {
        try
        {
            var stamp = File.ReadAllText(Path.Combine(mod, "lib", "version.js"));
            var marker = "COCKPIT_VERSION = ";
            var at = stamp.IndexOf(marker, StringComparison.Ordinal);

            if (at < 0)
                return DoctorCheck.Warn("cockpit version", "the cockpit carries no version stamp", ReinstallHint);

            var rest = stamp[(at + marker.Length)..].TrimStart();

            if (rest.StartsWith("null", StringComparison.Ordinal))
                return DoctorCheck.Warn("cockpit version", "this cockpit is an unstamped development copy", ReinstallHint);

            var version = rest.Split('"') is { Length: > 1 } parts ? parts[1] : string.Empty;

            return string.Equals(version, context.Version, StringComparison.Ordinal)
                ? DoctorCheck.Pass("cockpit version", $"{version}, the same as aw")
                : DoctorCheck.Fail("cockpit version", $"the cockpit is {version} but aw is {context.Version}", ReinstallHint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DoctorCheck.Warn("cockpit version", "the cockpit's version stamp cannot be read", ReinstallHint);
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
