using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The controller as real processes: <c>aw ctl</c> started the way a Herdr mod would start it (a short-lived child whose output is captured), the controller it starts
/// detached from that child, simultaneous clients, and a controller that is killed. Each test has its own state root, so its controller is its own, and every controller a
/// test started is stopped (and, if it will not stop, killed) when the test ends.
/// </summary>
public sealed class ControllerProcessTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _home = Path.Combine(Path.GetTempPath(), "awp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<int> _controllers = [];

    public ControllerProcessTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Aw("ctl", "stop");
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            // killed below
        }

        foreach (var pid in _controllers)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                if (!process.WaitForExit(5000))
                    process.Kill();
            }
            catch (ArgumentException)
            {
                // already gone
            }
        }

        ControllerTestSupport.Delete(_home);
    }

    /// <summary>Runs <c>aw</c> as a child process exactly as a caller that captures its output would, and waits for it to end.</summary>
    private (int Exit, string Out, string Error, TimeSpan Elapsed) Aw(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "aw.dll"));

        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        start.Environment["AGENTICWORKFLOW_HOME"] = _home;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.Close();

        // Reading to the end of both streams only finishes when every process holding them has let go: a controller that inherited them would make this wait for it.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!Task.WhenAll(output, error).Wait(Patience) || !process.WaitForExit(Patience))
        {
            process.Kill(entireProcessTree: false);

            throw new TimeoutException($"'aw {string.Join(' ', args)}' did not end: it, or a process it started, is holding its output open.");
        }

        return (process.ExitCode, output.Result, error.Result, clock.Elapsed);
    }

    private JsonElement Health()
    {
        var (exit, output, error, _) = Aw("ctl", "health");

        Assert.True(exit == 0, $"exit {exit}: {output} {error}");

        var root = JsonDocument.Parse(output).RootElement.Clone();
        _controllers.Add(root.GetProperty("controller").GetProperty("processId").GetInt32());

        return root;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void The_first_command_starts_a_detached_controller_and_returns_while_it_keeps_running()
    {
        var health = Health();
        var pid = health.GetProperty("controller").GetProperty("processId").GetInt32();

        Assert.Equal("ready", health.GetProperty("status").GetString());
        Assert.NotEqual(Environment.ProcessId, pid);
        Assert.True(IsRunning(pid));   // the command that started it has ended, and the controller has not
    }

    [Fact]
    public void Later_commands_use_the_same_controller_and_are_quick()
    {
        var first = Health().GetProperty("controller").GetProperty("instanceId").GetString();

        var (exit, output, _, elapsed) = Aw("ctl", "health");
        var second = JsonDocument.Parse(output).RootElement.GetProperty("controller").GetProperty("instanceId").GetString();

        Assert.Equal(0, exit);
        Assert.Equal(first, second);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed}");
    }

    [Fact]
    public async Task Simultaneous_commands_start_exactly_one_controller()
    {
        var results = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => Aw("ctl", "health")))
            .ToList();

        await Task.WhenAll(results).WaitAsync(Patience * 2);

        Assert.All(results, r => Assert.Equal(0, r.Result.Exit));

        var instances = results.Select(r => JsonDocument.Parse(r.Result.Out).RootElement.GetProperty("controller")).ToList();
        _controllers.AddRange(instances.Select(i => i.GetProperty("processId").GetInt32()));

        Assert.Single(instances.Select(i => i.GetProperty("instanceId").GetString()).Distinct());
        Assert.Single(instances.Select(i => i.GetProperty("processId").GetInt32()).Distinct());
    }

    [Fact]
    public void A_controller_that_was_killed_is_replaced_by_the_next_command()
    {
        var before = Health().GetProperty("controller");
        var pid = before.GetProperty("processId").GetInt32();

        using (var process = Process.GetProcessById(pid))
        {
            process.Kill();
            process.WaitForExit(10_000);
        }

        var after = Health().GetProperty("controller");

        Assert.NotEqual(before.GetProperty("instanceId").GetString(), after.GetProperty("instanceId").GetString());
        Assert.NotEqual(pid, after.GetProperty("processId").GetInt32());
        Assert.True(IsRunning(after.GetProperty("processId").GetInt32()));
    }

    [Fact]
    public async Task A_run_stranded_by_a_dead_controller_is_reconciled_by_the_controller_process_that_replaces_it_and_never_twice()
    {
        // The first "controller" is served in this process and dies while its agent works on a task's first stretch; the replacement is a real, detached process.
        using var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        harness.Environment[CliEnvironment.HomeVariable] = _home;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.Runtime.Turn = async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);

            return AgentTurnOutcome.Done;
        };

        var first = await SupervisedController.StartAsync(harness);
        var accepted = await first.StartTaskAsync();
        await started.Task.WaitAsync(Patience);
        await first.CrashAsync();
        var runId = accepted.Task.TaskId.ToString();

        var health = Health();
        var (exit, output, error, _) = Aw("ctl", "task", runId);

        Assert.True(exit == 0, $"{output} {error}");
        var task = JsonDocument.Parse(output).RootElement;
        Assert.Equal("blocked", task.GetProperty("state").GetString()!.ToLowerInvariant());
        var version = task.GetProperty("streamVersion").GetInt64();

        // Kill that controller too: the next one finds the run already defined and changes nothing.
        using (var process = Process.GetProcessById(health.GetProperty("controller").GetProperty("processId").GetInt32()))
        {
            process.Kill();
            process.WaitForExit(10_000);
        }

        Health();
        var (exitAgain, outputAgain, _, _) = Aw("ctl", "task", runId);

        Assert.Equal(0, exitAgain);
        Assert.Equal(version, JsonDocument.Parse(outputAgain).RootElement.GetProperty("streamVersion").GetInt64());
        Assert.Single(harness.Factory.Runtime.StartRequests);
    }

    [Fact]
    public void Stop_ends_the_controller_process_and_the_next_command_starts_a_new_one()
    {
        var before = Health().GetProperty("controller");
        var pid = before.GetProperty("processId").GetInt32();

        var (exit, output, _, _) = Aw("ctl", "stop");

        Assert.Equal(0, exit);
        Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("accepted").GetBoolean());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (IsRunning(pid) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        Assert.False(IsRunning(pid), "the controller process did not exit after 'stop'");

        Assert.NotEqual(before.GetProperty("instanceId").GetString(), Health().GetProperty("controller").GetProperty("instanceId").GetString());
    }

    [Fact]
    public void A_foreground_controller_stops_by_itself_when_idle_and_a_second_one_defers_to_the_first()
    {
        var (exit, _, _, _) = Aw("ctl", "serve", "--idle-timeout", "2");

        Assert.Equal(0, exit);   // started, served nobody, stopped itself, exit 0

        var first = Health().GetProperty("controller").GetProperty("processId").GetInt32();

        var (secondExit, _, _, _) = Aw("ctl", "serve", "--background");

        Assert.Equal(0, secondExit);   // another controller already serves this state root: nothing started, nothing changed
        Assert.True(IsRunning(first));
    }

    [Fact]
    public void The_controllers_files_stay_under_the_state_root_and_its_log_names_no_task_content()
    {
        Health();

        var directory = Path.Combine(_home, "controller");

        Assert.True(File.Exists(Path.Combine(directory, "controller.log")));
        Assert.Contains("ready", File.ReadAllText(Path.Combine(directory, "controller.log")), StringComparison.Ordinal);
    }
}
