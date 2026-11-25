using System.Runtime.InteropServices;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Runtime.Herdr.Pi;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>The platform this build was made and verified for: Windows 10 or newer, x64.</summary>
internal sealed class PlatformCheck : IDoctorCheck
{
    private const int MinimumWindowsBuild = 17763;

    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var system = $"{RuntimeInformation.OSDescription.Trim()} {RuntimeInformation.OSArchitecture}; {RuntimeInformation.FrameworkDescription}";

        DoctorCheck result;

        if (!OperatingSystem.IsWindows())
            result = DoctorCheck.Warn("platform", system, "Only Windows x64 is verified. Linux and macOS may work but are untested.");
        else if (RuntimeInformation.OSArchitecture != Architecture.X64)
            result = DoctorCheck.Warn("platform", system, "Only Windows x64 is verified.");
        else if (Environment.OSVersion.Version.Build < MinimumWindowsBuild)
            result = DoctorCheck.Fail("platform", system, "Windows 10 (build 17763) or newer is required.");
        else
            result = DoctorCheck.Pass("platform", system);

        return Task.FromResult<IReadOnlyList<DoctorCheck>>([result]);
    }
}

/// <summary>
/// That the installed application is whole: the packaged files are all there and unchanged, the evidence hook sits next to <c>aw</c>, and the Pi extension is part of this build.
/// A build that carries no manifest (a source checkout) is reported as such rather than failed.
/// </summary>
internal sealed class InstallationCheck : IDoctorCheck
{
    private const int MaxListed = 4;

    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck> { DoctorCheck.Pass("version", $"aw {context.Version} ({context.AwPath})") };

        checks.Add(CheckPackage(context));

        var hook = Path.Combine(context.InstallDirectory, HerdrAgentRuntimeFactory.HookFileName);

        checks.Add(File.Exists(hook)
            ? DoctorCheck.Pass("evidence hook", hook)
            : DoctorCheck.Fail("evidence hook", $"{hook} is missing", "Agents' tool evidence cannot be recorded without it. Reinstall AgenticWorkflow."));

        checks.Add(PiChannelHarness.IsExtensionEmbedded
            ? DoctorCheck.Pass("pi extension", "present in this build")
            : DoctorCheck.Fail("pi extension", "the Pi extension is missing from this build", "Reinstall AgenticWorkflow."));

        return Task.FromResult<IReadOnlyList<DoctorCheck>>(checks);
    }

    private static DoctorCheck CheckPackage(DoctorContext context)
    {
        var manifest = PackageManifest.TryRead(context.InstallDirectory);

        if (manifest is null)
            return DoctorCheck.Warn("package", "no package manifest next to aw: this is a development build, not an installed release");

        if (!string.Equals(manifest.Version, context.Version, StringComparison.Ordinal))
            return DoctorCheck.Fail("package", $"the manifest says version {manifest.Version} but this aw is {context.Version}", "The installation mixes two releases. Run install.ps1 from one release package.");

        var (missing, changed) = manifest.Verify(context.InstallDirectory);

        if (missing.Count > 0)
            return DoctorCheck.Fail("package", $"{missing.Count} file{(missing.Count == 1 ? string.Empty : "s")} missing: {Listed(missing)}", "Reinstall AgenticWorkflow.");

        if (changed.Count > 0)
            return DoctorCheck.Fail("package", $"{changed.Count} file{(changed.Count == 1 ? string.Empty : "s")} differ from the release: {Listed(changed)}", "Reinstall AgenticWorkflow.");

        return DoctorCheck.Pass("package", $"{manifest.Files.Count} files verified ({manifest.Platform})");
    }

    private static string Listed(IReadOnlyList<string> files)
        => string.Join(", ", files.Take(MaxListed)) + (files.Count > MaxListed ? $" and {files.Count - MaxListed} more" : string.Empty);
}
