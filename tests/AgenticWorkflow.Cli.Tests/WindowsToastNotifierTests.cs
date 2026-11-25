using System.ComponentModel;
using System.Text;
using AgenticWorkflow.Cli.Reporting;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The optional desktop toast (<c>AGENTICWORKFLOW_NOTIFY=toast</c>) is off by default, carries its text in environment variables rather than in the script, is
/// bounded, and can never fail a run. No test starts a process: the launcher is a recording fake.
/// </summary>
public sealed class WindowsToastNotifierTests
{
    private sealed class FakeLauncher : IToastProcessLauncher
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

        public Func<CancellationToken, Task<int>> Behaviour { get; set; } = _ => Task.FromResult(0);

        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments, environment));
            return Behaviour(cancellationToken);
        }
    }

    private static readonly HumanAttentionNotice Notice = new("AgenticWorkflow: your agent is waiting for you", "Run abcd1234: permission: approve Bash\n$env:HC_SESSION = \"s\"; hc attach t");

    private static WindowsToastNotifier OnWindows(FakeLauncher launcher, TimeSpan? timeout = null)
        => new(launcher, timeout, () => true);

    // ---- selection --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("toasts")]
    public void Notifications_are_off_unless_the_variable_says_toast(string? value)
    {
        var notifier = HumanAttentionNotifiers.Select(name => name == "AGENTICWORKFLOW_NOTIFY" ? value : null);

        Assert.Same(NoHumanAttentionNotifier.Instance, notifier);
    }

    [Theory]
    [InlineData("toast")]
    [InlineData("TOAST")]
    [InlineData("  Toast ")]
    public void The_toast_notifier_is_chosen_only_by_AGENTICWORKFLOW_NOTIFY_equal_to_toast(string value)
    {
        var notifier = HumanAttentionNotifiers.Select(name => name == "AGENTICWORKFLOW_NOTIFY" ? value : null);

        Assert.IsType<WindowsToastNotifier>(notifier);
    }

    [Fact]
    public async Task The_default_notifier_shows_nothing_and_reports_nothing()
    {
        Assert.Null(await NoHumanAttentionNotifier.Instance.NotifyAsync(Notice, CancellationToken.None));
    }

    // ---- what is run ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_text_travels_in_environment_variables_and_the_script_is_the_same_fixed_text_every_time()
    {
        var launcher = new FakeLauncher();
        var notifier = OnWindows(launcher);

        Assert.Null(await notifier.NotifyAsync(Notice, CancellationToken.None));
        Assert.Null(await notifier.NotifyAsync(new HumanAttentionNotice("other title", "other body"), CancellationToken.None));

        Assert.Equal(2, launcher.Calls.Count);
        var (executable, arguments, environment) = launcher.Calls[0];
        Assert.Equal("powershell.exe", executable);
        Assert.Equal(Notice.Title, environment["AW_TOAST_TITLE"]);
        Assert.Equal(Notice.Body, environment["AW_TOAST_BODY"]);

        // hidden, no profile, no prompt; the script is one encoded constant
        Assert.Equal(["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand"], arguments.Take(5));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[5]));
        Assert.Equal(WindowsToastNotifier.Script, script);
        Assert.Equal(arguments, launcher.Calls[1].Arguments);   // nothing of the notice is in the arguments

        // the notice text is nowhere in the script or the arguments
        Assert.DoesNotContain("hc attach", script);
        Assert.DoesNotContain("approve Bash", script);
        Assert.All(arguments, a => Assert.DoesNotContain("approve Bash", a));
        Assert.Contains("$env:AW_TOAST_TITLE", script);
        Assert.Contains("$env:AW_TOAST_BODY", script);
        Assert.Contains("SecurityElement]::Escape", script);   // escaped as XML text, never interpolated as markup
    }

    [Fact]
    public async Task The_title_and_the_body_are_bounded_single_line_text_without_control_characters()
    {
        var launcher = new FakeLauncher();
        var notice = new HumanAttentionNotice(
            new string('T', 500) + "\u001b[2J",
            "line one \u0007 with bell\n\n" + new string('b', 2000));

        await OnWindows(launcher).NotifyAsync(notice, CancellationToken.None);

        var environment = launcher.Calls.Single().Environment;
        Assert.True(environment["AW_TOAST_TITLE"].Length <= WindowsToastNotifier.MaxTitleLength);
        Assert.True(environment["AW_TOAST_BODY"].Length <= WindowsToastNotifier.MaxBodyLength);
        Assert.EndsWith("...", environment["AW_TOAST_BODY"]);
        Assert.DoesNotContain(environment.Values, v => v.Any(c => char.IsControl(c) && c != '\n'));
        Assert.Contains("line one ? with bell", environment["AW_TOAST_BODY"]);
        Assert.DoesNotContain("\n\n", environment["AW_TOAST_BODY"]);
    }

    [Fact]
    public async Task Off_windows_nothing_is_started()
    {
        var launcher = new FakeLauncher();

        var reason = await new WindowsToastNotifier(launcher, null, () => false).NotifyAsync(Notice, CancellationToken.None);

        Assert.Null(reason);
        Assert.Empty(launcher.Calls);
    }

    // ---- failures never escape --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_zero_exit_is_a_short_reason_not_an_exception()
    {
        var launcher = new FakeLauncher { Behaviour = _ => Task.FromResult(5) };

        Assert.Equal("powershell.exe exited with code 5", await OnWindows(launcher).NotifyAsync(Notice, CancellationToken.None));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_launcher_that_cannot_start_the_program_is_a_short_reason(Type failure)
    {
        var launcher = new FakeLauncher { Behaviour = _ => throw (Exception)Activator.CreateInstance(failure)! };

        Assert.Equal("powershell.exe could not be run", await OnWindows(launcher).NotifyAsync(Notice, CancellationToken.None));
    }

    [Fact]
    public async Task Any_other_failure_is_reported_by_type_only_never_thrown()
    {
        var launcher = new FakeLauncher { Behaviour = _ => throw new FormatException("secret text") };

        var reason = await OnWindows(launcher).NotifyAsync(Notice, CancellationToken.None);

        Assert.Equal("unexpected FormatException", reason);
    }

    [Fact]
    public async Task A_child_that_outlives_the_bound_is_cancelled_and_reported_as_timed_out()
    {
        CancellationToken seen = default;
        var launcher = new FakeLauncher
        {
            Behaviour = async token =>
            {
                seen = token;
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            },
        };

        var reason = await OnWindows(launcher, TimeSpan.FromMilliseconds(100)).NotifyAsync(Notice, CancellationToken.None);

        Assert.StartsWith("timed out after", reason);
        Assert.True(seen.IsCancellationRequested);   // so the real launcher kills the process tree
    }

    [Fact]
    public async Task The_caller_giving_up_cancels_the_child_without_an_error_message()
    {
        using var cancel = new CancellationTokenSource();
        var launcher = new FakeLauncher
        {
            Behaviour = async token =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            },
        };

        var reason = await OnWindows(launcher).NotifyAsync(Notice, cancel.Token);

        Assert.Null(reason);
    }

    [Fact]
    public void The_default_bound_is_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), WindowsToastNotifier.DefaultTimeout);
    }
}
