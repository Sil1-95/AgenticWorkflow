using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using static AgenticWorkflow.Cli.Tests.ControllerTestSupport;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The controller's lifecycle over the real operating-system transport of the machine running the tests: single instance, attach, automatic start, simultaneous start,
/// incompatible or broken controllers, clean shutdown, idle shutdown, and clients that misbehave. Controllers run as tasks of the test process (<see cref="InProcessControllerLauncher"/>);
/// <c>ControllerProcessTests</c> repeats the essentials with real processes.
/// </summary>
public sealed class ControllerLifecycleTests : IAsyncLifetime
{
    private readonly CliHarness _harness = new(CliHarness.ValidConfig());
    private readonly InProcessControllerLauncher _launcher;
    private readonly string _root;
    private readonly ControllerPaths _paths;
    private readonly IControllerTransport _transport;

    public ControllerLifecycleTests()
    {
        _launcher = new InProcessControllerLauncher(_harness);

        var services = _harness.Services();
        _root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        _paths = new ControllerPaths(_root);
        _transport = ControllerEndpoints.For(_root, services.GetEnvironmentVariable);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _launcher.DisposeAsync();
        _harness.Dispose();
    }

    private ControllerConnector Connector(IControllerLauncher? launcher = null, ControllerConnectorOptions? options = null)
        => new(_transport, launcher ?? _launcher, _paths, _root, options);

    private RunningHost StartHost(TimeSpan? idle = null, ITaskCatalog? catalog = null)
        => new(_transport, _paths.InstanceLock, catalog, idle);

    private static async Task<ControllerReply> AskAsync(ControllerClientSession session, ControllerRequest request)
        => await session.SendAsync(request, new CancellationTokenSource(Patience).Token);

    // ---- single instance ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_one_controller_serves_a_state_root_and_a_second_one_leaves_without_touching_the_first()
    {
        await using var first = StartHost();
        var info = await first.Ready;

        var second = new ControllerHost(
            _transport,
            new EmptyCatalog(),
            new ControllerHostOptions { InstanceLockPath = _paths.InstanceLock, Version = "second" },
            new ControllerLog(_paths.Log));

        Assert.Equal(ControllerExit.AlreadyRunning, await second.RunAsync(null, CancellationToken.None).WaitAsync(Patience));

        await using var session = await ConnectAsync(_transport);
        Assert.Equal(info.InstanceId, session.Controller.InstanceId);
        Assert.True((await AskAsync(session, new HealthRequest())).Ok);
    }

    [Fact]
    public async Task Controllers_started_at_the_same_moment_leave_exactly_one_serving()
    {
        var hosts = Enumerable.Range(0, 6).Select(_ => StartHost()).ToList();

        try
        {
            var served = await Task.WhenAll(hosts.Select(async host =>
            {
                try
                {
                    await host.Ready;

                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;   // it lost the instance lock and left
                }
            }));

            Assert.Single(served, true);
            Assert.Equal(5, (await Task.WhenAll(hosts.Select(h => h.Exit).Where(e => e.IsCompleted))).Count(exit => exit == ControllerExit.AlreadyRunning));
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    // ---- the client's automatic lifecycle -----------------------------------------------------------------------------

    [Fact]
    public async Task A_client_attaches_to_a_running_controller_without_starting_another()
    {
        await using var host = StartHost();
        var info = await host.Ready;

        await using var session = await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException();

        Assert.Equal(info.InstanceId, session.Controller.InstanceId);
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task A_client_starts_the_controller_when_none_runs_and_the_next_client_reuses_it()
    {
        await using var first = await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException();
        await using var second = await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException();

        Assert.Equal(1, _launcher.Launches);
        Assert.Equal(first.Controller.InstanceId, second.Controller.InstanceId);
        Assert.True((await AskAsync(first, new HealthRequest())).Ok);
    }

    [Fact]
    public async Task A_client_that_may_not_start_a_controller_finds_none_and_starts_nothing()
    {
        Assert.Null(await Connector().ConnectAsync(startIfAbsent: false, CancellationToken.None));
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Many_clients_arriving_together_start_one_controller_and_all_use_it()
    {
        var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException()));

        try
        {
            Assert.Single(sessions.Select(s => s.Controller.InstanceId).Distinct());

            // Any controller that was started beyond the first has already left because the first held the instance lock; none is serving.
            Assert.All(await _launcher.ExitsOfFinishedAsync(), exit => Assert.Equal(ControllerExit.AlreadyRunning, exit));
        }
        finally
        {
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_client_waits_only_a_bounded_time_for_a_controller_that_never_becomes_ready()
    {
        var options = new ControllerConnectorOptions { StartTimeout = TimeSpan.FromMilliseconds(700) };
        var launcher = new NeverReadyLauncher();

        var e = await Assert.ThrowsAsync<ControllerUnavailableException>(() => Connector(launcher, options).ConnectAsync(startIfAbsent: true, CancellationToken.None));

        Assert.Equal(ControllerErrorCodes.ControllerUnavailable, e.Code);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task A_client_does_not_wait_for_a_controller_that_died_while_starting()
    {
        var launcher = new NeverReadyLauncher(exitCode: 3);

        var e = await Assert.ThrowsAsync<ControllerUnavailableException>(() => Connector(launcher).ConnectAsync(startIfAbsent: true, CancellationToken.None));

        Assert.Equal(ControllerErrorCodes.ControllerUnavailable, e.Code);
        Assert.Contains("exit 3", e.Message, StringComparison.Ordinal);
    }

    private sealed class NeverReadyLauncher(int? exitCode = null) : IControllerLauncher
    {
        public int Launches { get; private set; }

        public IStartedController Launch(string stateRoot)
        {
            Launches++;

            return new Process(exitCode);
        }

        private sealed class Process(int? exitCode) : IStartedController
        {
            public bool HasExited => exitCode is not null;

            public int? ExitCode => exitCode;

            public void Dispose()
            {
            }
        }
    }

    // ---- what is already at the endpoint ------------------------------------------------------------------------------

    /// <summary>Something listening at the controller's endpoint that answers the handshake with <paramref name="respond"/>.</summary>
    private async Task<IAsyncDisposable> ImpostorAsync(Func<Stream, Task> respond)
    {
        var listener = _transport.Listen(_ => { });
        var cancel = new CancellationTokenSource();

        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var stream = await listener.AcceptAsync(cancel.Token);
                    _ = Task.Run(async () =>
                    {
                        await using (stream)
                            await respond(stream);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.CompletedTask;

        return new Disposer(async () =>
        {
            await cancel.CancelAsync();
            await serving;
            await listener.DisposeAsync();
        });
    }

    private sealed class Disposer(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }

    [Fact]
    public async Task A_controller_that_speaks_another_protocol_is_reported_not_replaced()
    {
        await using var impostor = await ImpostorAsync(async stream =>
        {
            await ControllerWire.ReadFrameAsync(stream, ControllerProtocol.MaxRequestBytes, CancellationToken.None);
            await ControllerWire.WriteFrameAsync(
                stream,
                ControllerWire.Encode(new ControllerWelcome
                {
                    Ok = false,
                    ProtocolVersion = null,
                    ServerMinimumProtocolVersion = 8,
                    ServerMaximumProtocolVersion = 9,
                    Controller = null,
                    Error = new ControllerError(ControllerErrorCodes.IncompatibleProtocol, "unsupported"),
                }),
                ControllerProtocol.MaxReplyBytes,
                CancellationToken.None);
        });

        var e = await Assert.ThrowsAsync<ControllerUnavailableException>(() => Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None));

        Assert.Equal(ControllerErrorCodes.IncompatibleProtocol, e.Code);
        Assert.Contains("8..9", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Something_at_the_endpoint_that_is_not_a_controller_is_reported_and_not_spoken_to()
    {
        await using var impostor = await ImpostorAsync(async stream =>
        {
            await ControllerWire.ReadFrameAsync(stream, ControllerProtocol.MaxRequestBytes, CancellationToken.None);

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, 0x7FFFFFFF);   // a frame header no controller would send
            await stream.WriteAsync(header);
        });

        var e = await Assert.ThrowsAsync<ControllerUnavailableException>(() => Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None));

        Assert.Equal(ControllerErrorCodes.ControllerUnresponsive, e.Code);
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task A_controller_that_accepts_but_never_answers_is_reported_as_unresponsive()
    {
        await using var impostor = await ImpostorAsync(stream => Task.Delay(TimeSpan.FromSeconds(3)));

        var options = new ControllerConnectorOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(250) };
        var e = await Assert.ThrowsAsync<ControllerUnavailableException>(() => Connector(options: options).ConnectAsync(startIfAbsent: true, CancellationToken.None));

        Assert.Equal(ControllerErrorCodes.ControllerUnresponsive, e.Code);
        Assert.Equal(0, _launcher.Launches);
    }

    // ---- stopping -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_request_shuts_the_controller_down_cleanly_and_frees_the_endpoint()
    {
        await using var host = StartHost();
        await host.Ready;
        await using var session = await ConnectAsync(_transport);

        var reply = await AskAsync(session, new ShutdownRequest());

        Assert.True(Assert.IsType<ShutdownResult>(reply.Result).Accepted);
        Assert.Equal(ControllerExit.Stopped, await host.Exit.WaitAsync(Patience));

        Assert.Null(await Connector().ConnectAsync(startIfAbsent: false, CancellationToken.None));   // nothing listens
        using var free = ControllerFileLock.TryAcquire(_paths.InstanceLock);
        Assert.NotNull(free);                                                                          // and the instance lock is released
    }

    [Fact]
    public async Task A_controller_started_after_another_stopped_is_a_new_instance()
    {
        Guid first;

        await using (var host = StartHost())
            first = (await host.Ready).InstanceId;

        await using var restarted = StartHost();

        Assert.NotEqual(first, (await restarted.Ready).InstanceId);
    }

    [Fact]
    public async Task A_command_issued_right_after_stop_gets_a_new_controller_rather_than_the_one_that_is_leaving()
    {
        await CtlAsync("health");
        var stopped = await CtlAsync("stop");
        var (exit, output) = await CtlAsync("health");

        Assert.Equal(0, stopped.Exit);
        Assert.Equal(0, exit);
        Assert.Equal("ready", JsonDocument.Parse(output).RootElement.GetProperty("status").GetString());
        Assert.Equal(2, _launcher.Launches);
    }

    [Fact]
    public async Task A_controller_with_no_clients_stops_itself_after_the_idle_timeout()
    {
        await using var host = StartHost(idle: TimeSpan.FromMilliseconds(300));
        await host.Ready;

        Assert.Equal(ControllerExit.Idle, await host.Exit.WaitAsync(Patience));
    }

    [Fact]
    public async Task An_open_client_keeps_the_controller_alive_past_the_idle_timeout()
    {
        await using var host = StartHost(idle: TimeSpan.FromMilliseconds(300));
        await host.Ready;
        await using var session = await ConnectAsync(_transport);

        await Task.Delay(900);

        Assert.False(host.Exit.IsCompleted);
        Assert.True((await AskAsync(session, new HealthRequest())).Ok);
    }

    [Fact]
    public async Task Asking_the_process_to_stop_ends_the_controller_and_its_open_connections()
    {
        var host = StartHost();
        await host.Ready;
        await using var session = await ConnectAsync(_transport);

        await host.DisposeAsync();   // cancels, as Ctrl+C or SIGTERM does

        Assert.Equal(ControllerExit.Cancelled, await host.Exit);
        await Assert.ThrowsAnyAsync<Exception>(() => AskAsync(session, new HealthRequest()));
    }

    // ---- clients that misbehave ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_client_that_disconnects_in_the_middle_of_its_hello_does_not_disturb_the_controller()
    {
        await using var host = StartHost();
        await host.Ready;

        var rude = await _transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await rude.WriteAsync(new byte[] { 0, 0, 0, 50, (byte)'{' });
        await rude.DisposeAsync();

        await using var session = await ConnectAsync(_transport);

        Assert.True((await AskAsync(session, new HealthRequest())).Ok);
        Assert.False(host.Exit.IsCompleted);
    }

    [Fact]
    public async Task A_silent_client_does_not_keep_other_clients_waiting()
    {
        await using var host = new RunningHost(_transport, _paths.InstanceLock, sessions: new ControllerSessionOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(400) });
        await host.Ready;

        await using var silent = await _transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await using var session = await ConnectAsync(_transport);

        Assert.True((await AskAsync(session, new HealthRequest())).Ok);

        // The silent connection is closed by the controller once its handshake time is up.
        var read = await silent.ReadAsync(new byte[1]).AsTask().WaitAsync(Patience);
        Assert.Equal(0, read);
    }

    // ---- what the controller touches ----------------------------------------------------------------------------------

    private static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Reading_through_the_controller_changes_nothing_in_a_run()
    {
        var runId = await RunToAcceptanceAsync();
        var store = new RunStore(_root);
        var database = store.DatabasePath(runId);
        var before = (Fingerprint(database), Fingerprint(Path.Combine(store.RunDirectory(runId), RunStore.MetadataFileName)), Directory.GetFiles(store.RunDirectory(runId)).Length);

        await using var session = await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException();
        Assert.True((await AskAsync(session, new ListTasksRequest())).Ok);
        Assert.True((await AskAsync(session, new ReadTaskRequest { TaskId = runId.Value.ToString("D") })).Ok);

        var after = (Fingerprint(database), Fingerprint(Path.Combine(store.RunDirectory(runId), RunStore.MetadataFileName)), Directory.GetFiles(store.RunDirectory(runId)).Length);

        Assert.Equal(before, after);
        Assert.False(store.IsLive(runId));   // the controller takes no run lock in this version
    }

    [Fact]
    public async Task The_controller_lists_unreadable_runs_instead_of_failing_the_list()
    {
        var runId = await RunToAcceptanceAsync();
        var broken = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, "runs", broken.ToString("D")));   // a run directory with no event stream

        await using var session = await Connector().ConnectAsync(startIfAbsent: true, CancellationToken.None) ?? throw new InvalidOperationException();
        var listing = Assert.IsType<TaskListResult>((await AskAsync(session, new ListTasksRequest())).Result);

        Assert.Equal(2, listing.Total);
        Assert.True(listing.Tasks.Single(t => t.TaskId == runId.Value).Readable);
        Assert.False(listing.Tasks.Single(t => t.TaskId == broken).Readable);

        var read = await AskAsync(session, new ReadTaskRequest { TaskId = broken.ToString("D") });
        Assert.Equal(ControllerErrorCodes.StateUnreadable, read.Error!.Code);
    }

    private async Task<TaskId> RunToAcceptanceAsync()
    {
        _harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");

            return Task.FromResult(AgentTurnOutcome.Done);
        };

        Assert.Equal(17, await _harness.RunAsync("run", "task"));

        var runId = _harness.PrintedRunId();
        _harness.ResetOutput();

        return runId;
    }

    // ---- aw ctl, in this process ---------------------------------------------------------------------------------------

    private async Task<(int Exit, string Out)> CtlAsync(params string[] args)
    {
        _harness.ControllerLauncher = _launcher;
        _harness.ResetOutput();

        var exit = await _harness.RunAsync(["ctl", .. args]);

        return (exit, _harness.Out.ToString());
    }

    [Fact]
    public async Task Ctl_health_starts_the_controller_and_reports_it()
    {
        var (exit, output) = await CtlAsync("health");

        Assert.Equal(0, exit);
        Assert.Equal(1, _launcher.Launches);

        var root = JsonDocument.Parse(output).RootElement;
        Assert.Equal("health", root.GetProperty("kind").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("controller").GetProperty("maximumProtocolVersion").GetInt32());
        Assert.Equal(string.Empty, _harness.Error.ToString());
    }

    [Fact]
    public async Task Ctl_task_prints_exactly_what_status_json_prints()
    {
        var runId = await RunToAcceptanceAsync();

        await _harness.RunAsync("status", runId.ToString(), "--json");
        var viaStatus = _harness.Out.ToString();

        var (exit, viaController) = await CtlAsync("task", runId.ToString());

        Assert.Equal(0, exit);
        Assert.Equal(viaStatus, viaController);
    }

    [Fact]
    public async Task Ctl_tasks_lists_the_runs_with_their_state_and_stage()
    {
        var runId = await RunToAcceptanceAsync();

        var (exit, output) = await CtlAsync("tasks", "--limit", "5");

        Assert.Equal(0, exit);

        var root = JsonDocument.Parse(output).RootElement;
        var task = Assert.Single(root.GetProperty("tasks").EnumerateArray());
        Assert.Equal(runId.Value.ToString("D"), task.GetProperty("taskId").GetString());
        Assert.Equal("awaiting_human", task.GetProperty("state").GetString());
        Assert.Equal("accept", task.GetProperty("stage").GetString());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Ctl_reports_an_unknown_task_exactly_as_status_json_does()
    {
        var unknown = Guid.NewGuid().ToString("D");

        await _harness.RunAsync("status", unknown, "--json");
        var viaStatus = _harness.Out.ToString();

        var (exit, viaController) = await CtlAsync("task", unknown);

        Assert.Equal(3, exit);
        Assert.Equal(viaStatus, viaController);
    }

    [Fact]
    public async Task Ctl_rejects_a_task_id_that_is_not_a_task_id_without_asking_the_catalog()
    {
        var (exit, output) = await CtlAsync("task", "not-a-guid");

        Assert.Equal(2, exit);
        Assert.Equal("invalid_task_id", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ctl_with_no_start_reports_that_no_controller_runs_and_starts_none()
    {
        var (exit, output) = await CtlAsync("health", "--no-start");

        Assert.Equal(31, exit);
        Assert.Equal("controller_unavailable", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Ctl_stop_stops_a_running_controller_and_does_nothing_when_none_runs()
    {
        var (none, noneOutput) = await CtlAsync("stop");

        Assert.Equal(0, none);
        Assert.False(JsonDocument.Parse(noneOutput).RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(0, _launcher.Launches);   // 'stop' never starts a controller in order to stop it

        await CtlAsync("health");
        var (stopped, stoppedOutput) = await CtlAsync("stop");

        Assert.Equal(0, stopped);
        Assert.True(JsonDocument.Parse(stoppedOutput).RootElement.GetProperty("accepted").GetBoolean());
        Assert.All(await WaitForExitsAsync(), exit => Assert.Equal(ControllerExit.Stopped, exit));
    }

    private async Task<IReadOnlyList<ControllerExit>> WaitForExitsAsync()
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            var exits = await _launcher.ExitsOfFinishedAsync();

            if (exits.Count == _launcher.Launches)
                return exits;

            await Task.Delay(25);
        }

        throw new TimeoutException("The controller did not stop.");
    }

    [Theory]
    [InlineData("ctl")]
    [InlineData("ctl", "nonsense")]
    [InlineData("ctl", "health", "extra")]
    [InlineData("ctl", "task")]
    [InlineData("ctl", "tasks", "--limit", "0")]
    [InlineData("ctl", "tasks", "--limit", "201")]
    [InlineData("ctl", "health", "--limit", "5")]
    [InlineData("ctl", "serve", "--idle-timeout", "soon")]
    public async Task Malformed_ctl_commands_are_usage_errors_that_touch_nothing(params string[] args)
    {
        _harness.ControllerLauncher = _launcher;

        Assert.Equal(2, await _harness.RunAsync(args));
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task The_other_commands_behave_the_same_while_a_controller_runs()
    {
        var runId = await RunToAcceptanceAsync();

        await _harness.RunAsync("status", runId.ToString());
        var statusBefore = _harness.Out.ToString();
        _harness.ResetOutput();
        await _harness.RunAsync("runs");
        var runsBefore = _harness.Out.ToString();

        await CtlAsync("health");

        _harness.ResetOutput();
        await _harness.RunAsync("status", runId.ToString());
        var statusAfter = _harness.Out.ToString();
        _harness.ResetOutput();
        await _harness.RunAsync("runs");

        Assert.Equal(statusBefore, statusAfter);
        Assert.Equal(runsBefore, _harness.Out.ToString());
    }
}
