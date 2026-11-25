using System.Diagnostics;
using System.Net.Sockets;
using AgenticWorkflow.Application.Controller;

namespace AgenticWorkflow.Cli.Controller;

internal enum ControllerExit
{
    /// <summary>A client asked the controller to stop (<c>aw ctl stop</c>).</summary>
    Stopped,

    /// <summary>No client connected, and nothing was supervised, for the idle timeout.</summary>
    Idle,

    /// <summary>The process was asked to stop (Ctrl+C, SIGTERM).</summary>
    Cancelled,

    /// <summary>Another controller already serves this state root; nothing was started and nothing was changed.</summary>
    AlreadyRunning,
}

internal sealed record ControllerHostOptions
{
    public required string InstanceLockPath { get; init; }

    public required string Version { get; init; }

    /// <summary>The identity this controller reports in <c>health</c>. The same value is written into the supervision record of every run it works on, so a record can be tied to the controller that wrote it.</summary>
    public Guid InstanceId { get; init; } = Guid.NewGuid();

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long the controller stays up with no client at all. Zero or negative means until it is told to stop.</summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>How long a stopping controller lets in-flight requests finish before it cuts their connections.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a stopping controller lets the agent work it supervises wind down before it leaves. What is still unfinished stays durable and is resumed by the next controller.</summary>
    public TimeSpan WorkShutdownGrace { get; init; } = TimeSpan.FromSeconds(20);

    public ControllerSessionOptions Sessions { get; init; } = new();
}

/// <summary>
/// The long-lived controller process's body. It owns three things and nothing else: the single-instance lock, the local endpoint, and the loop that hands each accepted
/// connection to a <see cref="ControllerServerSession"/>. What a request means is the dispatcher's; how bytes travel is the transport's; where tasks come from is the
/// catalog's; what a workflow operation does, and who works on a run, is the <see cref="ISupervisedWorkflow"/>'s. Without a workflow the controller is read-only.
/// <para>
/// At start-up, before the first client is served, the workflow reconciles the runs an earlier controller left unfinished. The idle timeout counts only time with nothing to
/// do: an open connection, a supervised stretch of agent work, or a continuation still to start each keep the controller up, and the clock restarts when the last of them ends.
/// </para>
/// <para>
/// Order matters for stale endpoints: the instance lock is taken first, so if it is held another controller is alive and this one leaves without touching anything, and
/// if it is free any endpoint that exists is stale and the transport replaces it. On the way out the endpoint is removed before the lock is released.
/// </para>
/// </summary>
internal sealed class ControllerHost(IControllerTransport transport, ITaskCatalog catalog, ControllerHostOptions options, ControllerLog log, ISupervisedWorkflow? workflow = null, bool durable = true) : IControllerLifecycle
{
    private const int MaxConsecutiveAcceptFailures = 50;

    private readonly CancellationTokenSource _stop = new();
    private ControllerExit _exit = ControllerExit.Cancelled;

    public bool IsShuttingDown => _stop.IsCancellationRequested;

    /// <summary>Called by a request that asked the controller to stop, after its reply was sent.</summary>
    public void RequestShutdown() => Stop(ControllerExit.Stopped);

    /// <summary>Serves until stopped. Throws <see cref="ControllerEndpointException"/> when the endpoint cannot be made private to the current user.</summary>
    public async Task<ControllerExit> RunAsync(Action<ControllerInfo>? ready, CancellationToken cancellationToken)
    {
        using var instance = ControllerFileLock.TryAcquire(options.InstanceLockPath);

        if (instance is null)
            return ControllerExit.AlreadyRunning;

        var listener = transport.Listen(log.Write);

        using var external = cancellationToken.Register(() => Stop(ControllerExit.Cancelled));

        var info = new ControllerInfo
        {
            InstanceId = options.InstanceId,
            ProcessId = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow,
            Version = options.Version,
            Transport = transport.Kind,
            MinimumProtocolVersion = ControllerProtocol.MinimumVersion,
            MaximumProtocolVersion = ControllerProtocol.Version,
            ReadModelSchemaVersion = Application.ReadModel.TaskReadModel.CurrentSchemaVersion,
        };

        var dispatcher = new ControllerDispatcher(info, catalog, this, log.Write, workflow, durable);
        var session = new ControllerServerSession(dispatcher, info, this, options.Sessions, log.Write);
        var activity = new ActivityTracker();

        using var connections = new CancellationTokenSource();
        var running = new List<Task>();

        log.Write($"controller {info.InstanceId} ready ({transport.Kind}, protocol {info.MinimumProtocolVersion}..{info.MaximumProtocolVersion})");
        ready?.Invoke(info);

        var watcher = options.IdleTimeout > TimeSpan.Zero ? WatchIdleAsync(activity, _stop.Token) : Task.CompletedTask;

        try
        {
            // Clients that connect meanwhile wait in the listener's backlog: nobody is told anything about a run before it has been put into a defined state.
            if (workflow is not null && durable)
                await RecoverAsync(workflow).ConfigureAwait(false);

            var failures = 0;

            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var stream = await listener.AcceptAsync(_stop.Token).ConfigureAwait(false);

                    failures = 0;
                    running.RemoveAll(task => task.IsCompleted);
                    running.Add(ServeAsync(session, stream, activity, connections.Token));
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // One bad accept (a client that vanished while connecting) does not stop the controller; a listener that keeps failing does.
                    if (++failures >= MaxConsecutiveAcceptFailures)
                        throw;

                    log.Write($"accept failed: {e.GetType().Name}");
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // The endpoint goes first: from now on a client finds no controller (and starts a new one, which takes over once this one releases the lock) instead of
            // connecting to one that is leaving. Requests already in flight are allowed to finish.
            await listener.DisposeAsync().ConfigureAwait(false);
            await DrainAsync(running, connections, options.ShutdownGrace).ConfigureAwait(false);

            if (workflow is not null)
                await workflow.StopAsync(options.WorkShutdownGrace).ConfigureAwait(false);

            await watcher.ConfigureAwait(false);

            log.Write($"controller {info.InstanceId} stopped ({_exit})");
        }

        return _exit;
    }

    private async Task RecoverAsync(ISupervisedWorkflow supervised)
    {
        try
        {
            await supervised.RecoverAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // stopped while recovering: what is unfinished stays durable
        }
        catch (Exception e)
        {
            // A fault in recovery never keeps the controller from serving; the runs stay as they are, and each is reconciled again by the next controller or by 'continue'.
            log.Write($"recovery failed ({e.GetType().Name})");
        }
    }

    private void Stop(ControllerExit reason)
    {
        // The first reason wins: a client's stop request is not re-labelled by the Ctrl+C that follows it.
        if (_stop.IsCancellationRequested)
            return;

        _exit = reason;
        _stop.Cancel();
    }

    private static async Task ServeAsync(ControllerServerSession session, Stream stream, ActivityTracker activity, CancellationToken cancellationToken)
    {
        activity.Begin();

        try
        {
            await using (stream.ConfigureAwait(false))
                await session.RunAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            activity.End();
        }
    }

    private static async Task DrainAsync(List<Task> running, CancellationTokenSource connections, TimeSpan grace)
    {
        var pending = Task.WhenAll(running);

        if (await Task.WhenAny(pending, Task.Delay(grace)).ConfigureAwait(false) == pending)
            return;

        await connections.CancelAsync().ConfigureAwait(false);

        await Task.WhenAny(pending, Task.Delay(grace)).ConfigureAwait(false);
    }

    private async Task WatchIdleAsync(ActivityTracker activity, CancellationToken stopped)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(options.IdleTimeout.TotalMilliseconds / 4, 25, 1000));

        try
        {
            while (true)
            {
                await Task.Delay(interval, stopped).ConfigureAwait(false);

                // Supervised work is not idleness: while any stretch runs, the clock does not advance, and it restarts from the moment the last one ends.
                if (workflow is { ActiveRuns: > 0 })
                    activity.Touch();

                if (activity.IdleFor >= options.IdleTimeout)
                {
                    log.Write("no client for the idle timeout; stopping");
                    Stop(ControllerExit.Idle);

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping for another reason
        }
    }

    /// <summary>How many connections are open and for how long there have been none.</summary>
    private sealed class ActivityTracker
    {
        private int _open;
        private long _lastActive = Stopwatch.GetTimestamp();

        public TimeSpan IdleFor => Volatile.Read(ref _open) > 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActive));

        public void Touch() => Volatile.Write(ref _lastActive, Stopwatch.GetTimestamp());

        public void Begin() => Interlocked.Increment(ref _open);

        public void End()
        {
            Volatile.Write(ref _lastActive, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _open);
        }
    }
}
