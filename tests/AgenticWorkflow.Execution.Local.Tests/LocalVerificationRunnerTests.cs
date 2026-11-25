using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Execution.Local.Tests;

public sealed class LocalVerificationRunnerTests : IDisposable
{
    private readonly TempRepo _repo = TempRepo.Create();
    private readonly LocalVerificationRunner _runner = new();

    public void Dispose() => _repo.Dispose();

    private static VerificationCheckSpec Check(
        string executable,
        string[] arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        int expectedExit = 0)
        => new(new VerificationCheckId("check"), "a check", executable, arguments, timeout ?? TimeSpan.FromSeconds(30), workingDirectory, expectedExit);

    /// <summary>The platform shell as an executable plus the arguments that run one command line.</summary>
    private static (string Exe, string[] Args) Shell(string commandLine)
        => OperatingSystem.IsWindows() ? ("cmd", ["/d", "/c", commandLine]) : ("sh", ["-c", commandLine]);

    [Fact]
    public async Task A_command_that_exits_zero_reports_exit_zero_and_its_output()
    {
        var (exe, args) = Shell("echo hello-from-check");

        var run = await _runner.RunAsync(_repo.Root, Check(exe, args));

        Assert.Equal(0, run.ExitCode);
        Assert.False(run.TimedOut);
        Assert.Contains("hello-from-check", run.StdOutTail);
    }

    [Fact]
    public async Task A_nonzero_exit_is_reported_as_is_deciding_pass_or_fail_is_not_this_ports_job()
    {
        var (exe, args) = Shell("exit 7");

        var run = await _runner.RunAsync(_repo.Root, Check(exe, args));

        Assert.Equal(7, run.ExitCode);
        Assert.Null(run.StartError);
    }

    [Fact]
    public async Task Git_grep_gives_the_absent_symbol_check_its_exit_one()
    {
        _repo.Write("src/Greeting.cs", "public string GetFood() => \"x\";\n");

        var old = await _runner.RunAsync(_repo.Root, Check("git", ["grep", "--untracked", "-q", "-F", "GetMeal"]));
        var renamed = await _runner.RunAsync(_repo.Root, Check("git", ["grep", "--untracked", "-q", "-F", "GetFood"]));

        Assert.Equal(1, old.ExitCode);
        Assert.Equal(0, renamed.ExitCode);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_killed_and_reported_as_timed_out()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >nul" : "sleep 60");

        var run = await _runner.RunAsync(_repo.Root, Check(exe, args, TimeSpan.FromSeconds(1)));

        Assert.True(run.TimedOut);
        Assert.Null(run.ExitCode);
        Assert.True(run.Duration < TimeSpan.FromSeconds(20), $"took {run.Duration}");
    }

    [Fact]
    public async Task Output_is_bounded_to_a_tail()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,3000) do @echo line-%i-padding-padding-padding"
            : "i=1; while [ $i -le 3000 ]; do echo line-$i-padding-padding-padding; i=$((i+1)); done");

        var run = await _runner.RunAsync(_repo.Root, Check(exe, args));

        Assert.Equal(0, run.ExitCode);
        Assert.True(run.StdOutTail.Length <= 4000);
        Assert.Contains("line-3000-", run.StdOutTail);
        Assert.DoesNotContain("line-1-", run.StdOutTail);
    }

    [Fact]
    public async Task A_missing_executable_is_a_start_error_not_an_exception()
    {
        var run = await _runner.RunAsync(_repo.Root, Check("definitely-not-a-real-program-xyz", []));

        Assert.NotNull(run.StartError);
        Assert.Null(run.ExitCode);
    }

    [Fact]
    public async Task Arguments_are_passed_as_a_list_not_interpreted_by_a_shell()
    {
        // If this went through a shell, "&" would run a second command. With an argument list it is just text.
        var run = await _runner.RunAsync(_repo.Root, Check("git", ["grep", "--untracked", "-q", "-F", "x & echo injected"]));

        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("injected", run.StdOutTail);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("nonexistent")]
    public async Task A_working_directory_outside_or_missing_in_the_workspace_is_refused_without_running(string directory)
    {
        var run = await _runner.RunAsync(_repo.Root, Check("git", ["status"], workingDirectory: directory));

        Assert.NotNull(run.StartError);
        Assert.Null(run.ExitCode);
    }

    [Fact]
    public async Task A_relative_working_directory_inside_the_workspace_is_honoured()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "cd" : "pwd");

        var run = await _runner.RunAsync(_repo.Root, Check(exe, args, workingDirectory: "src"));

        Assert.Equal(0, run.ExitCode);
        Assert.EndsWith("src", run.StdOutTail.Trim().Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Claude_environment_variables_are_not_passed_to_checks()
    {
        const string name = "CLAUDE_M2B_TEST_PROBE";
        Environment.SetEnvironmentVariable(name, "leaked");

        try
        {
            var (exe, args) = Shell(OperatingSystem.IsWindows() ? $"echo [%{name}%]" : $"echo [${name}]");

            var run = await _runner.RunAsync(_repo.Root, Check(exe, args));

            Assert.DoesNotContain("leaked", run.StdOutTail);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Cancelling_kills_the_process_and_propagates()
    {
        var (exe, args) = Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >nul" : "sleep 60");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync(_repo.Root, Check(exe, args, TimeSpan.FromMinutes(5)), cts.Token));
    }
}
