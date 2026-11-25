using System.Diagnostics;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Execution.Local.Tests;

/// <summary>
/// Real processes through the real Windows process stack (no mocked resolution): Windows toolchains such as Flutter are .bat/.cmd files
/// that CreateProcess does not find by bare name, and that must never let an argument become a second cmd.exe command.
/// </summary>
public sealed class WindowsScriptExecutionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aw-exec-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static bool NotWindows => !OperatingSystem.IsWindows();

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\n", "\r\n"));

        return path;
    }

    private IReadOnlyDictionary<string, string?> PathWithScripts(params string[] more)
        => new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, [_directory, .. more, Environment.GetEnvironmentVariable("PATH")]),
        };

    private Task<ProcessResult> RunAsync(
        string executable,
        string[]? arguments = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => ProcessExecutor.RunAsync(
            new ProcessSpec(executable, arguments ?? [], workingDirectory ?? _directory, timeout ?? TimeSpan.FromSeconds(30), environment ?? PathWithScripts()),
            cancellationToken);

    // ---- resolution and execution ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_native_executable_still_runs_directly_by_bare_name_and_by_explicit_path()
    {
        if (NotWindows)
            return;

        var bare = await RunAsync("cmd", ["/d", "/c", "echo native-bare"]);
        var explicitPath = await RunAsync(WindowsScripts.CommandInterpreter, ["/d", "/c", "echo native-path"]);

        Assert.Equal(0, bare.ExitCode);
        Assert.Contains("native-bare", bare.StdOut);
        Assert.Equal(0, explicitPath.ExitCode);
        Assert.Contains("native-path", explicitPath.StdOut);
    }

    [Fact]
    public async Task A_native_executable_is_never_replaced_by_a_batch_script_of_the_same_name_on_path()
    {
        if (NotWindows)
            return;

        var marker = Path.Combine(_directory, "decoy-ran.txt");
        Write("cmd.cmd", $"@echo off\necho x> \"{marker}\"\n");
        Write("cmd.bat", $"@echo off\necho x> \"{marker}\"\n");

        var result = await RunAsync("cmd", ["/d", "/c", "echo the-real-one"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("the-real-one", result.StdOut);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_bare_name_backed_by_a_cmd_script_on_path_runs()
    {
        if (NotWindows)
            return;

        Write("awtool.cmd", "@echo off\necho from-cmd\n");

        var result = await RunAsync("awtool");

        Assert.Null(result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("from-cmd", result.StdOut);
    }

    [Fact]
    public async Task A_bare_name_backed_by_a_bat_script_on_path_runs()
    {
        if (NotWindows)
            return;

        Write("awbat.bat", "@echo off\necho from-bat\n");

        var result = await RunAsync("awbat");

        Assert.Null(result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("from-bat", result.StdOut);
    }

    [Fact]
    public async Task A_bare_name_backed_by_a_com_file_runs_natively()
    {
        if (NotWindows)
            return;

        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(_directory, "awcom.com"));

        var result = await RunAsync("awcom");

        Assert.Null(result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut));
    }

    [Theory]
    [InlineData("explicit.cmd")]
    [InlineData("explicit.bat")]
    [InlineData("EXPLICIT.CMD")]
    public async Task An_explicit_script_path_runs(string name)
    {
        if (NotWindows)
            return;

        var path = Write(name, "@echo off\necho explicit-ok\n");

        var result = await RunAsync(path);

        Assert.Null(result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("explicit-ok", result.StdOut);
    }

    [Fact]
    public async Task A_bare_file_name_with_a_script_extension_is_searched_on_path()
    {
        if (NotWindows)
            return;

        Write("named.cmd", "@echo off\necho by-file-name\n");

        var result = await RunAsync("named.cmd");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("by-file-name", result.StdOut);
    }

    [Fact]
    public async Task A_script_in_a_directory_with_spaces_runs_by_explicit_path_and_by_path_lookup()
    {
        if (NotWindows)
            return;

        var path = Write(@"Program Files (x86)\My Tool\bin\spaced.bat", "@echo off\necho spaced-ok\n");

        var byPath = await RunAsync(path);
        var byLookup = await RunAsync("spaced", environment: PathWithScripts(Path.GetDirectoryName(path)!));

        Assert.Equal(0, byPath.ExitCode);
        Assert.Contains("spaced-ok", byPath.StdOut);
        Assert.Equal(0, byLookup.ExitCode);
        Assert.Contains("spaced-ok", byLookup.StdOut);
    }

    [Fact]
    public async Task A_relative_explicit_script_path_resolves_against_the_controller_not_the_child_working_directory()
    {
        if (NotWindows)
            return;

        Write("rel.cmd", "@echo off\necho relative-ok\n");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_directory, "elsewhere")).FullName;
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_directory);

            var result = await RunAsync(@".\rel.cmd", workingDirectory: elsewhere);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("relative-ok", result.StdOut);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    // ---- the process contract -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ordinary_arguments_arrive_exactly_including_spaces_and_cmd_token_separators()
    {
        if (NotWindows)
            return;

        Write("args.cmd", "@echo off\necho [%~1][%~2][%~3][%~4][%~5][%~6]\n");

        var result = await RunAsync(
            "args",
            ["test", "--no-pub", "path with spaces/a_test.dart", "--dart-define=A=B,C;D", "(paren)", ""]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[test][--no-pub][path with spaces/a_test.dart][--dart-define=A=B,C;D][(paren)][]", result.StdOut);
    }

    [Fact]
    public async Task The_arguments_are_forwarded_whole_to_the_program_the_script_runs()
    {
        if (NotWindows)
            return;

        // %* is how real toolchain scripts (flutter.bat, npm.cmd) hand their arguments on; here to a real native program.
        Write("forward.cmd", "@echo off\ncmd /d /c echo %*\n");

        var result = await RunAsync("forward", ["one", "two words"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("one \"two words\"", result.StdOut);
    }

    [Fact]
    public async Task The_working_directory_is_the_requested_one_not_the_scripts()
    {
        if (NotWindows)
            return;

        Write("cwd.cmd", "@echo off\necho cwd=%CD%\n");
        var work = Directory.CreateDirectory(Path.Combine(_directory, "work dir")).FullName;

        var result = await RunAsync("cwd", workingDirectory: work);

        Assert.Contains($"cwd={work}", result.StdOut);
    }

    [Fact]
    public async Task Environment_variables_are_set_overridden_and_removed_for_the_script()
    {
        if (NotWindows)
            return;

        // Not named "env": a native executable always wins over a script of the same bare name, and Git for Windows puts an env.exe on PATH.
        Write("awenv.cmd", "@echo off\necho set=[%AW_SET%] gone=[%AW_GONE%]\n");
        Environment.SetEnvironmentVariable("AW_GONE", "inherited");

        try
        {
            var environment = new Dictionary<string, string?>(PathWithScripts()) { ["AW_SET"] = "value 1", ["AW_GONE"] = null };

            var result = await RunAsync("awenv", environment: environment);

            Assert.Contains("set=[value 1] gone=[]", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AW_GONE", null);
        }
    }

    [Fact]
    public async Task Stdout_and_stderr_are_captured_separately()
    {
        if (NotWindows)
            return;

        Write("streams.cmd", "@echo off\necho to-out\necho to-err 1>&2\n");

        var result = await RunAsync("streams");

        Assert.Contains("to-out", result.StdOut);
        Assert.DoesNotContain("to-err", result.StdOut);
        Assert.Contains("to-err", result.StdErr);
        Assert.DoesNotContain("to-out", result.StdErr);
    }

    [Theory]
    [InlineData("exit /b 3", 3)]
    [InlineData("exit /b 0", 0)]
    [InlineData("cmd /d /c exit 42", 42)] // the script's last command fails: that status is the script's status
    public async Task A_script_exit_code_is_the_controller_exit_code(string body, int expected)
    {
        if (NotWindows)
            return;

        Write("exits.cmd", $"@echo off\n{body}\n");

        var result = await RunAsync("exits");

        Assert.Null(result.StartError);
        Assert.False(result.TimedOut);
        Assert.Equal(expected, result.ExitCode);
    }

    [Fact]
    public async Task A_failing_script_is_never_reported_as_success_even_through_the_interpreter()
    {
        if (NotWindows)
            return;

        Write("fails.bat", "@echo off\necho broken 1>&2\nexit /b 9\n");

        var result = await RunAsync(Path.Combine(_directory, "fails.bat"));

        Assert.Equal(9, result.ExitCode);
        Assert.Contains("broken", result.StdErr);
    }

    [Fact]
    public async Task A_script_that_runs_too_long_is_killed_with_its_children_and_reported_as_timed_out()
    {
        if (NotWindows)
            return;

        Write("slow.cmd", "@echo off\nping -n 60 127.0.0.1 >nul\n");

        var result = await RunAsync("slow", timeout: TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        // An orphaned ping would keep the inherited pipes open and push the result out by the stream grace period.
        Assert.True(result.Duration < TimeSpan.FromSeconds(3.5), $"took {result.Duration}");
    }

    [Fact]
    public async Task Cancelling_stops_the_script_and_surfaces_the_cancellation()
    {
        if (NotWindows)
            return;

        Write("slow.cmd", "@echo off\nping -n 60 127.0.0.1 >nul\n");
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();

        var run = RunAsync("slow", cancellationToken: cancel.Token);
        await Task.Delay(700);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    // ---- safety ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a&echo injected> {marker}")]
    [InlineData("a&&echo injected> {marker}")]
    [InlineData("a|echo injected> {marker}")]
    [InlineData("a>{marker}")]
    [InlineData("a<{marker}")]
    [InlineData("a^&echo injected> {marker}")]
    [InlineData("%COMSPEC%")]
    [InlineData("!COMSPEC!")]
    [InlineData("a\"&echo injected> {marker}&\"")]
    [InlineData("a\r\necho injected> {marker}")]
    public async Task An_argument_cmd_would_interpret_is_rejected_and_the_script_is_not_started(string template)
    {
        if (NotWindows)
            return;

        var marker = Path.Combine(_directory, "injected.txt");
        var started = Path.Combine(_directory, "started.txt");
        Write("victim.cmd", $"@echo off\necho x> \"{started}\"\n");

        var byLookup = await RunAsync("victim", ["ok", template.Replace("{marker}", marker)]);
        var byPath = await RunAsync(Path.Combine(_directory, "victim.cmd"), [template.Replace("{marker}", marker)]);

        foreach (var result in new[] { byLookup, byPath })
        {
            Assert.NotNull(result.StartError);
            Assert.Contains("cmd.exe would interpret", result.StartError);
            Assert.Null(result.ExitCode);
        }

        Assert.False(File.Exists(started));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_script_whose_own_path_cmd_would_interpret_is_rejected()
    {
        if (NotWindows)
            return;

        var path = Write(@"a&b\victim.cmd", "@echo off\necho should-not-run\n");

        var result = await RunAsync(path);

        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
        Assert.DoesNotContain("should-not-run", result.StdOut);
    }

    [Theory]
    [InlineData("noscript.ps1")]
    [InlineData("noscript.js")]
    [InlineData("noscript.vbs")]
    public async Task Other_script_types_are_never_executed_by_the_fallback(string file)
    {
        if (NotWindows)
            return;

        var marker = Path.Combine(_directory, "ran.txt");
        Write(file, $"echo x > \"{marker}\"\n");

        var result = await RunAsync("noscript");

        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task The_target_directory_is_not_searched_for_scripts_unless_it_is_on_path()
    {
        if (NotWindows)
            return;

        Write("onlyhere.cmd", "@echo off\necho should-not-run\n");

        var result = await RunAsync("onlyhere", environment: new Dictionary<string, string?> { ["PATH"] = Environment.SystemDirectory });

        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
    }

    // ---- failure ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_command_is_a_normal_start_failure()
    {
        var result = await RunAsync("aw-definitely-not-a-tool");

        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Theory]
    [InlineData(@"missing\tool.cmd")]
    [InlineData("missing-tool.bat")]
    [InlineData(@"missing\tool.exe")]
    public async Task A_missing_explicit_path_is_a_normal_start_failure(string path)
    {
        var result = await RunAsync(Path.Combine(_directory, path));

        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
    }

    // ---- through the controller's real check runner ----------------------------------------------------------------------

    [Fact]
    public async Task A_check_whose_executable_is_a_script_runs_through_the_local_verification_runner()
    {
        if (NotWindows)
            return;

        var script = Write("check.cmd", "@echo off\necho checking %1 %2 in %CD%\nexit /b 0\n");
        var failing = Write("failing check.bat", "@echo off\necho tests failed 1>&2\nexit /b 1\n");
        var workspace = Directory.CreateDirectory(Path.Combine(_directory, "workspace")).FullName;
        var runner = new LocalVerificationRunner();

        var passed = await runner.RunAsync(workspace, Check(script, ["analyze", "--no-pub"]));
        var failed = await runner.RunAsync(workspace, Check(failing, ["test"]));

        Assert.Equal(0, passed.ExitCode);
        Assert.Contains($"checking analyze --no-pub in {workspace}", passed.StdOutTail);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("tests failed", failed.StdErrTail);
    }

    private static VerificationCheckSpec Check(string executable, string[] arguments)
        => new(new VerificationCheckId("check"), "a check", executable, arguments, TimeSpan.FromSeconds(30));

    // ---- other platforms --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Off_windows_a_cmd_named_file_is_executed_natively_and_no_interpreter_is_involved()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_directory, "unix.cmd");
        File.WriteAllText(path, "#!/bin/sh\necho native-unix \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await RunAsync(path, ["a&b"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("native-unix a&b", result.StdOut);
    }
}
