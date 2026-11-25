using System.Text.Json;

namespace AgenticWorkflow.Packaging.Tests;

/// <summary>`aw doctor` as an installed release runs it: the installer's work is what the doctor verifies.</summary>
[Collection(ReleaseCollection.Name)]
public sealed class InstalledDoctorTests(ReleaseFixture release)
{
    [Fact]
    public void A_good_installation_passes_the_checks_that_depend_on_the_installation()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        var doctor = sandbox.Aw("doctor", "--json");
        var checks = Checks(doctor);

        // git, Herdr and an agent harness depend on the machine running the test; everything the installer is responsible for must pass.
        foreach (var name in new[] { "version", "package", "evidence hook", "pi extension", "state", "controller", "cockpit palette", "cockpit aw", "cockpit version" })
            Assert.True(checks[name] == "pass", $"{name}: {checks[name]}\n{doctor}");
    }

    [Fact]
    public void A_damaged_installation_is_reported_as_a_failure_with_exit_code_33()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);
        File.Delete(Path.Combine(sandbox.BinDir, "AgenticWorkflow.ClaudeEvidenceHook.exe"));

        var doctor = sandbox.Aw("doctor", "--json");
        var checks = Checks(doctor);

        Assert.Equal(33, doctor.ExitCode);
        Assert.Equal("fail", checks["evidence hook"]);
        Assert.Equal("fail", checks["package"]);
    }

    [Fact]
    public void A_stale_cockpit_is_reported_and_a_missing_one_is_only_a_warning()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        File.WriteAllText(Path.Combine(sandbox.ModDir, "lib", "version.js"), "export const COCKPIT_VERSION = \"0.0.1\";\n");
        Assert.Equal("fail", Checks(sandbox.Aw("doctor", "--json"))["cockpit version"]);

        Directory.Delete(sandbox.ModDir, recursive: true);
        Assert.Equal("warning", Checks(sandbox.Aw("doctor", "--json"))["cockpit"]);
    }

    [Fact]
    public void A_cockpit_pointing_at_another_aw_is_reported()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);
        File.WriteAllText(Path.Combine(sandbox.SettingsDir, "config.json"), JsonSerializer.Serialize(new { aw = Path.Combine(sandbox.Root, "gone", "aw.exe") }));

        Assert.Equal("fail", Checks(sandbox.Aw("doctor", "--json"))["cockpit aw"]);
    }

    [Fact]
    public void The_report_leaks_no_secret_from_the_environment()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        var environment = new Dictionary<string, string?>(sandbox.Environment) { ["ANTHROPIC_API_KEY"] = "sk-test-secret-value", ["OPENAI_API_KEY"] = "sk-other-secret-value" };
        var doctor = ReleaseFixture.Run(sandbox.AwExe, ["doctor"], environment);

        Assert.DoesNotContain("sk-test-secret-value", doctor.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-other-secret-value", doctor.Output, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Checks(Outcome doctor)
    {
        using var document = JsonDocument.Parse(doctor.Output);

        return document.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString()!);
    }
}
