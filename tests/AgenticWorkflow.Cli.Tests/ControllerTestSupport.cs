using System.Diagnostics;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.State;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>A test that only makes sense where the operating system has Unix file modes and peer credentials; elsewhere it is reported as skipped, not passed.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Needs Unix file modes and peer credentials (verified on Linux/macOS only).";
    }
}

/// <summary>A test of the Windows named-pipe transport.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Needs Windows named pipes and security descriptors.";
    }
}

/// <summary>
/// Starts "controllers" as tasks in this process, the way <see cref="ProcessControllerLauncher"/> starts them as processes, so the client's whole start-up logic runs against
/// the real transport and the real host without a process per test. It counts launches; every host it started is stopped on disposal.
/// </summary>
internal sealed class InProcessControllerLauncher(CliHarness harness, TimeSpan? idleTimeout = null) : IControllerLauncher, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<(Task<ControllerExit> Run, CancellationTokenSource Cancel)> _hosts = [];

    public int Launches { get; private set; }

    public IStartedController Launch(string stateRoot)
    {
        var services = harness.Services();
        var paths = new ControllerPaths(stateRoot);
        var host = new ControllerHost(
            ControllerEndpoints.For(stateRoot, services.GetEnvironmentVariable),
            new RunTaskCatalog(services, new RunStore(stateRoot)),
            new ControllerHostOptions { InstanceLockPath = paths.InstanceLock, Version = "test", IdleTimeout = idleTimeout ?? TimeSpan.Zero },
            new ControllerLog(paths.Log));

        var cancel = new CancellationTokenSource();
        var run = Task.Run(() => host.RunAsync(null, cancel.Token));

        lock (_gate)
        {
            Launches++;
            _hosts.Add((run, cancel));
        }

        return new Started(run);
    }

    /// <summary>How each launched controller ended so far; a controller that lost the instance lock reports <see cref="ControllerExit.AlreadyRunning"/>.</summary>
    public async Task<IReadOnlyList<ControllerExit>> ExitsOfFinishedAsync()
    {
        List<Task<ControllerExit>> finished;

        lock (_gate)
            finished = _hosts.Select(h => h.Run).Where(r => r.IsCompleted).ToList();

        return await Task.WhenAll(finished);
    }

    public async ValueTask DisposeAsync()
    {
        List<(Task<ControllerExit> Run, CancellationTokenSource Cancel)> hosts;

        lock (_gate)
            hosts = [.. _hosts];

        foreach (var (_, cancel) in hosts)
            await cancel.CancelAsync();

        await Task.WhenAll(hosts.Select(h => h.Run)).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class Started(Task<ControllerExit> run) : IStartedController
    {
        public bool HasExited => run.IsCompleted;

        public int? ExitCode => run.IsCompleted ? (run.IsFaulted ? 1 : 0) : null;

        public void Dispose()
        {
        }
    }
}

internal static class ControllerTestSupport
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A short private directory (a socket path is limited to about 100 bytes, so a test's usual deep temp path will not do).</summary>
    public static string ShortDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "awc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);

        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A controller that is still shutting down may hold a file; the temp directory is disposable.
        }
    }

    public static async Task<ControllerClientSession> ConnectAsync(IControllerTransport transport, string client = "test")
    {
        using var timeout = new CancellationTokenSource(Patience);
        var stream = await transport.ConnectAsync(TimeSpan.FromSeconds(5), timeout.Token);

        return await ControllerClientSession.ConnectAsync(stream, client, timeout.Token);
    }

    /// <summary>A running host over the transport, stopped (and its endpoint removed) on disposal.</summary>
    public sealed class RunningHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly TaskCompletionSource<ControllerInfo> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunningHost(IControllerTransport transport, string lockPath, ITaskCatalog? catalog = null, TimeSpan? idle = null, ControllerSessionOptions? sessions = null)
        {
            Host = new ControllerHost(
                transport,
                catalog ?? new EmptyCatalog(),
                new ControllerHostOptions { InstanceLockPath = lockPath, Version = "test", IdleTimeout = idle ?? TimeSpan.Zero, Sessions = sessions ?? new ControllerSessionOptions() },
                new ControllerLog(Path.Combine(Path.GetDirectoryName(lockPath)!, "test.log")));

            Exit = Task.Run(() => Host.RunAsync(info => _ready.TrySetResult(info), _cancel.Token));
            Exit.ContinueWith(t => _ready.TrySetException(t.Exception?.InnerException ?? new InvalidOperationException("The host ended before it was ready.")), TaskContinuationOptions.OnlyOnFaulted);
            Exit.ContinueWith(_ => _ready.TrySetCanceled(), TaskContinuationOptions.NotOnFaulted);
        }

        public ControllerHost Host { get; }

        public Task<ControllerExit> Exit { get; }

        public Task<ControllerInfo> Ready => _ready.Task.WaitAsync(Patience);

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();

            try
            {
                await Exit.WaitAsync(Patience);
            }
            catch (Exception e) when (e is ControllerEndpointException or OperationCanceledException)
            {
                // a host that never started has nothing to stop
            }

            _cancel.Dispose();
        }
    }

    public sealed class EmptyCatalog : ITaskCatalog
    {
        public Task<TaskListing> ListAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(new TaskListing(0, []));

        public Task<TaskReadOutcome> ReadAsync(AgenticWorkflow.Core.Identity.TaskId taskId, CancellationToken cancellationToken)
            => Task.FromResult(TaskReadOutcome.Failed(ControllerErrorCodes.NotFound, "none"));
    }
}
