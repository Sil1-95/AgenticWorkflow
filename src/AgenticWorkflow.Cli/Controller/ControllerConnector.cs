using AgenticWorkflow.Application.Controller;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>A controller that was started on behalf of a client: the client watches it only to stop waiting when it died.</summary>
internal interface IStartedController : IDisposable
{
    bool HasExited { get; }

    int? ExitCode { get; }
}

/// <summary>Starts a controller process for a state root. The only implementation that matters in production is <see cref="ProcessControllerLauncher"/>; tests substitute their own.</summary>
internal interface IControllerLauncher
{
    IStartedController Launch(string stateRoot);
}

internal sealed record ControllerConnectorOptions
{
    /// <summary>
    /// How long one probe waits for the endpoint to accept a connection. Short on purpose: a probe that finds nothing is the normal first step of starting a controller,
    /// and the controller always keeps a connection waiting, so a healthy one answers at once.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a controller that accepted the connection has to answer the hello.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a client waits for a controller it started (or is waiting for another client to start) to become ready.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long to keep looking for another controller after one that this client started has left because it lost the instance lock.</summary>
    public TimeSpan RelaunchAfter { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>The client could not obtain a healthy, compatible controller. <see cref="Code"/> is a <see cref="ControllerErrorCodes"/> value.</summary>
internal sealed class ControllerUnavailableException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The client side of the automatic lifecycle: attach to the controller if a healthy one answers, otherwise start one and wait, boundedly, until it answers a handshake.
/// <para>
/// Exactly one controller results from any number of simultaneous clients. Every client that finds none competes for the start-up gate, a lock only one can hold; the
/// holder launches the controller and the others keep trying to attach until it is ready. The gate is a courtesy that avoids a stampede of processes; the guarantee
/// itself is the controller's own single-instance lock, which makes every controller but one leave at once, so a client that starts without the gate (or after a gate
/// holder died) still cannot create a second controller.
/// </para>
/// <para>
/// A controller that is present but wrong is never replaced by this class: an incompatible protocol, an endpoint that is not the current user's, or a holder that does not
/// answer is reported, because starting another could not succeed (the first still owns the lock) and killing it is not a client's decision.
/// </para>
/// </summary>
internal sealed class ControllerConnector(
    IControllerTransport transport,
    IControllerLauncher launcher,
    ControllerPaths paths,
    string stateRoot,
    ControllerConnectorOptions? options = null,
    string clientName = "aw-ctl")
{
    private const int MaxLaunches = 3;

    private readonly ControllerConnectorOptions _options = options ?? new ControllerConnectorOptions();

    /// <summary>A session with the controller. When none runs and <paramref name="startIfAbsent"/> is false, null is returned and nothing is started.</summary>
    public async Task<ControllerClientSession?> ConnectAsync(bool startIfAbsent, CancellationToken cancellationToken)
    {
        if (await TryAttachAsync(cancellationToken).ConfigureAwait(false) is { } existing)
            return existing;

        if (!startIfAbsent)
            return null;

        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ControllerClientSession> StartAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.StartTimeout;
        ControllerFileLock? gate = null;

        try
        {
            while ((gate = ControllerFileLock.TryAcquire(paths.SpawnGate)) is null)
            {
                // Another client is starting the controller; keep looking for it until it answers.
                if (await TryAttachAsync(cancellationToken).ConfigureAwait(false) is { } started)
                    return started;

                if (DateTime.UtcNow > deadline)
                    throw Unavailable("No controller became ready (another client was starting one).");

                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            // A controller may have become ready between the first attach and taking the gate.
            if (await TryAttachAsync(cancellationToken).ConfigureAwait(false) is { } raced)
                return raced;

            var launches = 0;
            IStartedController? process = null;
            DateTime? deferredSince = null;

            try
            {
                while (true)
                {
                    if (process is null)
                    {
                        process = launcher.Launch(stateRoot);
                        launches++;
                    }

                    if (await TryAttachAsync(cancellationToken).ConfigureAwait(false) is { } ready)
                        return ready;

                    if (process.HasExited)
                    {
                        if (process.ExitCode is not 0)
                            throw Unavailable($"The controller exited while starting (exit {process.ExitCode}); see '{paths.Log}'.");

                        // A controller that exits successfully lost the instance lock: another controller is starting, or is stopping and has not yet let go. Give that one a
                        // moment to answer; if none does, start again (a few times at most).
                        deferredSince ??= DateTime.UtcNow;

                        if (DateTime.UtcNow - deferredSince > _options.RelaunchAfter)
                        {
                            if (launches >= MaxLaunches)
                                throw Unavailable($"No controller became ready; see '{paths.Log}'.");

                            process.Dispose();
                            process = null;
                            deferredSince = null;
                        }
                    }

                    if (DateTime.UtcNow > deadline)
                        throw Unavailable($"The controller did not become ready within {_options.StartTimeout.TotalSeconds:0} seconds; see '{paths.Log}'.");

                    await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                process?.Dispose();
            }
        }
        finally
        {
            gate?.Dispose();
        }
    }

    /// <summary>A handshaken session, or null when nothing listens. Anything listening that cannot be used throws (see the class remarks).</summary>
    private async Task<ControllerClientSession?> TryAttachAsync(CancellationToken cancellationToken)
    {
        Stream stream;

        try
        {
            stream = await transport.ConnectAsync(_options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ControllerNotReachableException)
        {
            return null;
        }
        catch (ControllerPeerRejectedException e)
        {
            throw Unavailable($"A controller endpoint exists but does not belong to this user: {e.Message}");
        }

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_options.HandshakeTimeout);

        try
        {
            return await ControllerClientSession.ConnectAsync(stream, clientName, handshake.Token).ConfigureAwait(false);
        }
        catch (ControllerHandshakeRefusedException e)
        {
            throw new ControllerUnavailableException(
                e.Welcome.Error?.Code ?? ControllerErrorCodes.IncompatibleProtocol,
                $"{e.Message} (this client speaks protocol {ControllerProtocol.MinimumVersion}..{ControllerProtocol.Version}; the controller speaks {e.Welcome.ServerMinimumProtocolVersion}..{e.Welcome.ServerMaximumProtocolVersion}). Stop the running controller ('aw ctl stop') and retry.",
                e);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ControllerUnavailableException(ControllerErrorCodes.ControllerUnresponsive, "A controller holds the endpoint but did not answer the handshake.");
        }
        catch (IOException)
        {
            // The connection closed or was reset before the welcome: most likely a controller that is leaving (it stopped listening a moment after this client connected).
            // The caller treats it as "none there" and starts one if it may; whatever is really there will not answer a handshake either, and the attempts are bounded.
            return null;
        }
        catch (Exception e) when (e is ControllerFrameException or System.Text.Json.JsonException)
        {
            throw new ControllerUnavailableException(ControllerErrorCodes.ControllerUnresponsive, "Something holds the controller endpoint but does not speak the controller protocol.", e);
        }
    }

    private static ControllerUnavailableException Unavailable(string message) => new(ControllerErrorCodes.ControllerUnavailable, message);
}
