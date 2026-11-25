using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Cli.Diagnostics;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Runtime;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// <c>aw doctor</c>: every verdict is reached from a context the test builds (a fake machine, a fake install folder, a fake Herdr home), so no real tool, controller or user setting is involved.
/// </summary>
public sealed class DoctorTests : IDisposable
{
    private const string Version = "9.8.7";

    private readonly string _root = Directory.CreateTempSubdirectory("aw-doctor-").FullName;
    private readonly string _install;
    private readonly string _herdr;
    private readonly string _tools;
    private readonly string _workspace;
    private readonly FakeProbe _probe = new();

    public DoctorTests()
    {
        _install = Directory.CreateDirectory(Path.Combine(_root, "install dir")).FullName;
        _herdr = Directory.CreateDirectory(Path.Combine(_root, "herdr")).FullName;
        _tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

        WriteInstall();
        Tool("git", "git version 2.50.0");
        Tool("hc", "hc 0.1.22");
        Tool("claude", "2.1.0 (Claude Code)");
        WriteCockpit(Version);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_complete_installation_passes_every_check()
    {
        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Equal(0, report.Failures);
        Assert.Contains(report.Checks, c => c.Name == "package" && c.Status == DoctorStatus.Pass && c.Summary.Contains("verified", StringComparison.Ordinal));
        Assert.Contains(report.Checks, c => c.Name == "cockpit version" && c.Status == DoctorStatus.Pass);
        Assert.Contains(report.Checks, c => c.Name == "cockpit aw" && c.Status == DoctorStatus.Pass);
        Assert.Contains(report.Checks, c => c.Name == "controller" && c.Status == DoctorStatus.Pass);
    }

    [Fact]
    public async Task A_missing_optional_harness_is_a_warning_not_a_failure()
    {
        Remove("claude");
        Tool("pi", "pi 1.0.0");

        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "claude" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task Having_neither_harness_fails_because_a_task_cannot_run()
    {
        Remove("claude");

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "agent" && c.Status == DoctorStatus.Fail);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("hc")]
    public async Task A_missing_required_tool_fails(string tool)
    {
        Remove(tool);

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Status == DoctorStatus.Fail && c.Summary.Contains($"'{tool}' was not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_old_herdr_core_is_a_warning()
    {
        Tool("hc", "hc 0.1.10");

        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "herdr (hc)" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_cockpit_from_another_release_fails()
    {
        WriteCockpit("0.0.1");

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "cockpit version" && c.Status == DoctorStatus.Fail && c.Summary.Contains("0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unstamped_development_cockpit_is_a_warning()
    {
        File.WriteAllText(Path.Combine(_herdr, "mods", "agenticworkflow", "lib", "version.js"), "export const COCKPIT_VERSION = null;\n");

        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "cockpit version" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_cockpit_that_runs_a_program_that_does_not_exist_fails()
    {
        File.WriteAllText(Path.Combine(_herdr, "agenticworkflow", "config.json"), JsonSerializer.Serialize(new { aw = Path.Combine(_root, "gone", "aw.exe") }));

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "cockpit aw" && c.Status == DoctorStatus.Fail);
    }

    [Fact]
    public async Task A_cockpit_that_is_not_installed_is_only_a_warning()
    {
        Directory.Delete(Path.Combine(_herdr, "mods"), recursive: true);

        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "cockpit" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_cockpit_without_a_palette_entry_is_reported()
    {
        File.WriteAllText(Path.Combine(_herdr, "mods", "agenticworkflow", "mod.json"), """{ "title": "AgenticWorkflow" }""");

        var report = await RunAsync();

        Assert.Contains(report.Checks, c => c.Name == "cockpit palette" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_changed_packaged_file_fails_the_package_check()
    {
        File.AppendAllText(Path.Combine(_install, "payload.dll"), "tampered");

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "package" && c.Status == DoctorStatus.Fail && c.Summary.Contains("payload.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_packaged_file_fails_the_package_check()
    {
        File.Delete(Path.Combine(_install, "payload.dll"));

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "package" && c.Status == DoctorStatus.Fail && c.Summary.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_manifest_of_another_version_fails()
    {
        WriteManifest("1.0.0");

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "package" && c.Status == DoctorStatus.Fail);
    }

    [Fact]
    public async Task A_build_without_a_manifest_is_a_warning_because_it_is_a_development_build()
    {
        File.Delete(Path.Combine(_install, PackageManifest.FileName));

        var report = await RunAsync();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "package" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_missing_evidence_hook_fails()
    {
        File.Delete(Path.Combine(_install, "AgenticWorkflow.ClaudeEvidenceHook.exe"));

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "evidence hook" && c.Status == DoctorStatus.Fail);
    }

    [Fact]
    public async Task An_unreachable_controller_fails()
    {
        var report = await RunAsync(controller: new ControllerObservation(false, false, null, null, "No controller could be reached."));

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "controller" && c.Status == DoctorStatus.Fail);
    }

    [Fact]
    public async Task A_controller_that_could_not_own_work_durably_fails()
    {
        var report = await RunAsync(controller: Observation(version: Version, durable: false));

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "controller" && c.Status == DoctorStatus.Fail && c.Summary.Contains("refuses to own agent work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_controller_of_another_version_is_a_warning()
    {
        var report = await RunAsync(controller: Observation(version: "1.2.3", durable: true));

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "controller" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task An_invalid_project_configuration_fails()
    {
        File.WriteAllText(Path.Combine(_workspace, "agenticworkflow.json"), "{ not json");

        var report = await RunAsync();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "project" && c.Status == DoctorStatus.Fail);
    }

    [Fact]
    public async Task A_folder_without_a_project_configuration_is_fine_unless_one_was_asked_for()
    {
        var implicitFolder = await RunAsync();
        var asked = await RunAsync(workspaceGiven: true);

        Assert.Contains(implicitFolder.Checks, c => c.Name == "project" && c.Status == DoctorStatus.Pass);
        Assert.Contains(asked.Checks, c => c.Name == "project" && c.Status == DoctorStatus.Warning);
    }

    [Fact]
    public async Task A_check_that_throws_is_reported_as_a_failure_and_the_others_still_run()
    {
        var report = await RunAsync(observeController: _ => throw new InvalidOperationException("boom"));

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "controller" && c.Status == DoctorStatus.Fail && c.Summary.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(report.Checks, c => c.Name == "git" && c.Status == DoctorStatus.Pass);
    }

    [Fact]
    public async Task The_report_names_no_conversation_and_no_secret()
    {
        var report = await RunAsync(environment: name => name == "ANTHROPIC_API_KEY" ? "sk-secret-value" : null);

        Assert.DoesNotContain("sk-secret-value", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-value", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_text_report_labels_each_check_and_ends_with_a_conclusion()
    {
        Remove("claude");
        Tool("pi", "pi 1.0.0");

        var text = (await RunAsync()).ToText();

        Assert.Contains("PASS  ", text, StringComparison.Ordinal);
        Assert.Contains("WARN  ", text, StringComparison.Ordinal);
        Assert.Contains("Ready to use, with 1 warning", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_report_is_machine_readable()
    {
        Remove("claude");

        using var document = JsonDocument.Parse((await RunAsync()).ToJson());

        Assert.Equal(Version, document.RootElement.GetProperty("version").GetString());
        Assert.False(document.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Contains(document.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("status").GetString() == "fail");
    }

    [Fact]
    public void Doctor_is_a_known_command_that_takes_json_and_a_workspace_and_no_positionals()
    {
        Assert.IsType<DoctorRequest>(CommandLine.Parse(["doctor"]));
        Assert.True(Assert.IsType<DoctorRequest>(CommandLine.Parse(["doctor", "--json"])).Json);
        Assert.Equal("w", Assert.IsType<DoctorRequest>(CommandLine.Parse(["doctor", "--workspace", "w"])).Workspace);
        Assert.Throws<UsageException>(() => CommandLine.Parse(["doctor", "extra"]));
    }

    [Fact]
    public async Task The_command_exits_zero_when_healthy_and_33_when_a_check_fails()
    {
        using var harness = new CliHarness();
        await using var launcher = new InProcessControllerLauncher(harness);
        harness.ControllerLauncher = launcher;

        // The real machine's controller and tools are involved here, so only the exit-code contract is asserted, not which checks pass.
        var code = await harness.RunAsync("doctor", "--json");

        Assert.Contains(code, new[] { 0, 33 });
        Assert.Equal(code == 0, JsonDocument.Parse(harness.Out.ToString()).RootElement.GetProperty("healthy").GetBoolean());
    }

    private Task<DoctorReport> RunAsync(
        ControllerObservation? controller = null,
        bool workspaceGiven = false,
        Func<CancellationToken, Task<ControllerObservation>>? observeController = null,
        Func<string, string?>? environment = null)
    {
        var path = _tools;
        Func<string, string?> getEnvironmentVariable = environment ?? (name => name == "PATH" ? path : null);

        if (environment is not null)
        {
            var inner = environment;
            getEnvironmentVariable = name => name == "PATH" ? path : inner(name);
        }

        var observation = controller ?? Observation(Version, durable: true);

        var context = new DoctorContext(
            Version,
            _install,
            Path.Combine(_install, "aw.exe"),
            Path.Combine(_root, "state"),
            _herdr,
            _workspace,
            workspaceGiven,
            getEnvironmentVariable,
            new ExecutableLocator(getEnvironmentVariable),
            _probe,
            new ScriptedRuntimeFactory(),
            observeController ?? (_ => Task.FromResult(observation)));

        return Doctor.RunAsync(context, CancellationToken.None);
    }

    private static ControllerObservation Observation(string version, bool durable)
        => new(
            true,
            false,
            new ControllerInfo
            {
                InstanceId = Guid.NewGuid(),
                ProcessId = 1234,
                StartedAt = DateTimeOffset.UtcNow,
                Version = version,
                Transport = "named_pipe",
                MinimumProtocolVersion = 1,
                MaximumProtocolVersion = 1,
                ReadModelSchemaVersion = 1,
            },
            new SupervisionStatus { ActiveRuns = 0, Durable = durable },
            null);

    private void Tool(string name, string firstLine)
    {
        var file = Path.Combine(_tools, name + ".exe");
        File.WriteAllText(file, "not really a program");
        _probe.Answers[file] = firstLine;
    }

    private void Remove(string name) => File.Delete(Path.Combine(_tools, name + ".exe"));

    private void WriteInstall()
    {
        File.WriteAllText(Path.Combine(_install, "aw.exe"), "aw");
        File.WriteAllText(Path.Combine(_install, "AgenticWorkflow.ClaudeEvidenceHook.exe"), "hook");
        File.WriteAllText(Path.Combine(_install, "payload.dll"), "payload");
        WriteManifest(Version);
    }

    private void WriteManifest(string version)
    {
        var files = new[] { "aw.exe", "AgenticWorkflow.ClaudeEvidenceHook.exe", "payload.dll" }
            .Select(name => new { path = name, size = new FileInfo(Path.Combine(_install, name)).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_install, name)))).ToLowerInvariant() });

        File.WriteAllText(Path.Combine(_install, PackageManifest.FileName), JsonSerializer.Serialize(new { product = "AgenticWorkflow", version, platform = "win-x64", files }));
    }

    private void WriteCockpit(string version)
    {
        var mod = Directory.CreateDirectory(Path.Combine(_herdr, "mods", "agenticworkflow", "lib")).Parent!.FullName;
        File.WriteAllText(Path.Combine(mod, "mod.json"), """{ "title": "AgenticWorkflow", "palette": [ { "title": "Open AgenticWorkflow" } ] }""");
        File.WriteAllText(Path.Combine(mod, "lib", "version.js"), $"export const COCKPIT_VERSION = \"{version}\";\n");

        Directory.CreateDirectory(Path.Combine(_herdr, "agenticworkflow"));
        File.WriteAllText(Path.Combine(_herdr, "agenticworkflow", "config.json"), JsonSerializer.Serialize(new { aw = Path.Combine(_install, "aw.exe").Replace('\\', '/') }));
    }

    private sealed class FakeProbe : IToolProbe
    {
        public Dictionary<string, string> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? FirstLine(IReadOnlyList<string> command, TimeSpan timeout)
            => Answers.TryGetValue(command[0], out var line) ? line : null;
    }
}
