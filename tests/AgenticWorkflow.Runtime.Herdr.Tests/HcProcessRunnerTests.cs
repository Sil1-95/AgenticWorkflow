using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

public sealed class HcProcessRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Captures_exit_code_stdout_and_stderr()
    {
        var runner = new HcProcessRunner("cmd.exe");

        var result = await runner.RunAsync(["/d", "/c", "echo out& echo err 1>&2& exit /b 7"], Generous);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.StdOut.Trim());
        Assert.Equal("err", result.StdErr.Trim());
    }

    [Fact]
    public async Task Passes_arguments_with_spaces_quotes_and_backslashes_without_a_shell()
    {
        using var directory = new FakeHcDirectory("rem unused");
        var scriptPath = Path.Combine(Path.GetDirectoryName(directory.ExecutablePath)!, "echo args.ps1");
        await File.WriteAllTextAsync(scriptPath, "[Console]::Out.Write((ConvertTo-Json -Compress -InputObject @($args)))");

        string[] arguments = ["plain", "has space", "C:\\Program Files\\x y\\", "say \"hi\"", "a&b|c"];
        var runner = new HcProcessRunner("powershell.exe");

        var result = await runner.RunAsync(["-NoProfile", "-NonInteractive", "-File", scriptPath, .. arguments], Generous);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(result.StdOut));
    }

    [Fact]
    public async Task Applies_environment_overrides()
    {
        var runner = new HcProcessRunner("cmd.exe", new Dictionary<string, string> { ["AW_M0_PROBE"] = "value-1" });

        var result = await runner.RunAsync(["/d", "/c", "echo %AW_M0_PROBE%"], Generous);

        Assert.Equal("value-1", result.StdOut.Trim());
    }

    [Fact]
    public async Task Scrubs_environment_variables_by_prefix_case_insensitively_but_keeps_explicit_overrides()
    {
        Environment.SetEnvironmentVariable("AW_M0_SCRUB_ME", "secret");
        Environment.SetEnvironmentVariable("AW_M0_KEEP_ME", "kept");

        try
        {
            var runner = new HcProcessRunner(
                "cmd.exe",
                new Dictionary<string, string> { ["AW_M0_SCRUB_OVERRIDE"] = "explicit" },
                scrubbedEnvironmentPrefixes: ["aw_m0_scrub"]);

            var result = await runner.RunAsync(
                ["/d", "/c", "echo [%AW_M0_SCRUB_ME%][%AW_M0_KEEP_ME%][%AW_M0_SCRUB_OVERRIDE%]"], Generous);

            Assert.Equal("[%AW_M0_SCRUB_ME%][kept][explicit]", result.StdOut.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AW_M0_SCRUB_ME", null);
            Environment.SetEnvironmentVariable("AW_M0_KEEP_ME", null);
        }
    }

    [Fact]
    public async Task Throws_not_found_when_the_executable_does_not_exist()
    {
        var runner = new HcProcessRunner("aw-m0-definitely-not-installed");

        await Assert.ThrowsAsync<HcNotFoundException>(() => runner.RunAsync(["--version"], Generous));
    }

    [Fact]
    public async Task Throws_timeout_and_kills_the_process()
    {
        var runner = new HcProcessRunner("cmd.exe");

        var exception = await Assert.ThrowsAsync<HcTimeoutException>(
            () => runner.RunAsync(["/d", "/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(500)));

        Assert.Contains("did not finish", exception.Message);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var runner = new HcProcessRunner("cmd.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(["/d", "/c", "ping -n 30 127.0.0.1 >nul"], Generous, cancellation.Token));
    }
}
