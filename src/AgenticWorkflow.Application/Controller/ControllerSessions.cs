namespace AgenticWorkflow.Application.Controller;

/// <summary>How a connection ended, for the controller's own accounting and for tests.</summary>
public enum SessionEnd
{
    /// <summary>The client finished (closed the connection between requests, or asked the controller to stop).</summary>
    Completed,

    /// <summary>The client vanished: the stream broke or closed in the middle of an exchange. Nothing was left half done; every request is atomic.</summary>
    Disconnected,

    /// <summary>The hello was invalid or no protocol version is shared. No request was read.</summary>
    HandshakeRefused,

    /// <summary>The peer broke the framing rules (an oversized, empty or truncated frame). The stream cannot be resynchronized.</summary>
    ProtocolViolation,

    /// <summary>The peer did not send the hello, or the next request, in time.</summary>
    TimedOut,

    /// <summary>The controller is stopping.</summary>
    Cancelled,
}

public sealed record ControllerSessionOptions
{
    /// <summary>How long a client has to send its hello. A client that connects and says nothing is not allowed to hold a connection.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a connection may be silent between requests before the controller closes it.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The controller's side of one connection: the handshake, then request after request until the client leaves. It works on any duplex <see cref="Stream"/>, so the
/// same code serves every transport and a test needs none. Whatever the peer sends, it ends the connection without touching any task: the only effect of a request is
/// what <see cref="ControllerDispatcher"/> does for a well-formed, supported one.
/// </summary>
public sealed class ControllerServerSession(
    ControllerDispatcher dispatcher,
    ControllerInfo info,
    IControllerLifecycle lifecycle,
    ControllerSessionOptions? options = null,
    Action<string>? log = null)
{
    private readonly ControllerSessionOptions _options = options ?? new ControllerSessionOptions();

    public async Task<SessionEnd> RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var version = await HandshakeAsync(stream, cancellationToken).ConfigureAwait(false);

            if (version is null)
                return SessionEnd.HandshakeRefused;

            return await ServeAsync(stream, version.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ControllerFrameException e)
        {
            log?.Invoke($"connection closed: {e.Message}");

            return SessionEnd.ProtocolViolation;
        }
        catch (TimeoutException)
        {
            log?.Invoke("connection closed: the peer was silent too long");

            return SessionEnd.TimedOut;
        }
        catch (OperationCanceledException)
        {
            return SessionEnd.Cancelled;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return SessionEnd.Disconnected;
        }
    }

    /// <returns>The protocol version both sides speak, or null when the connection was refused (the refusal has been sent).</returns>
    private async Task<int?> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadWithinAsync(stream, _options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);

        if (payload is null)
            throw new IOException("The peer closed the connection before the hello.");

        var (hello, error) = ControllerWire.DecodeHello(payload);

        if (hello is null)
        {
            await WriteAsync(stream, Refusal(error!), cancellationToken).ConfigureAwait(false);

            return null;
        }

        if (ControllerProtocol.Negotiate(hello.MinimumProtocolVersion, hello.MaximumProtocolVersion) is not { } version)
        {
            log?.Invoke($"refused client '{hello.Client}': it speaks protocol {hello.MinimumProtocolVersion}..{hello.MaximumProtocolVersion}");

            await WriteAsync(
                stream,
                Refusal(new ControllerError(
                    ControllerErrorCodes.IncompatibleProtocol,
                    $"This controller speaks protocol {ControllerProtocol.MinimumVersion}..{ControllerProtocol.Version}; the client speaks {hello.MinimumProtocolVersion}..{hello.MaximumProtocolVersion}.")),
                cancellationToken).ConfigureAwait(false);

            return null;
        }

        await WriteAsync(
            stream,
            new ControllerWelcome
            {
                Ok = true,
                ProtocolVersion = version,
                ServerMinimumProtocolVersion = ControllerProtocol.MinimumVersion,
                ServerMaximumProtocolVersion = ControllerProtocol.Version,
                Controller = info,
                Error = null,
            },
            cancellationToken).ConfigureAwait(false);

        return version;
    }

    private async Task<SessionEnd> ServeAsync(Stream stream, int version, CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await ReadWithinAsync(stream, _options.IdleTimeout, cancellationToken).ConfigureAwait(false);

            if (payload is null)
                return SessionEnd.Completed;

            var (request, error) = ControllerWire.DecodeRequest(payload);

            if (request is null)
            {
                // A message that is not a request changes nothing; the connection stays usable because the framing is intact.
                await ReplyAsync(stream, ControllerReply.Failure(error!.Code, error.Message, version), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var reply = await dispatcher.HandleAsync(request, cancellationToken).ConfigureAwait(false);

            await ReplyAsync(stream, reply with { ProtocolVersion = version }, cancellationToken).ConfigureAwait(false);

            // The reply is on the wire first: stopping cancels this connection's token.
            if (request is ShutdownRequest && reply.Result is ShutdownResult { Accepted: true })
            {
                lifecycle.RequestShutdown();

                return SessionEnd.Completed;
            }
        }
    }

    private static ControllerWelcome Refusal(ControllerError error) => new()
    {
        Ok = false,
        ProtocolVersion = null,
        ServerMinimumProtocolVersion = ControllerProtocol.MinimumVersion,
        ServerMaximumProtocolVersion = ControllerProtocol.Version,
        Controller = null,
        Error = error,
    };

    private static async Task ReplyAsync(Stream stream, ControllerReply reply, CancellationToken cancellationToken)
    {
        var payload = ControllerWire.Encode(reply);

        // A reply that cannot be framed is the controller's failure, not a reason to cut the connection.
        if (payload.Length > ControllerProtocol.MaxReplyBytes)
            payload = ControllerWire.Encode(ControllerReply.Failure(ControllerErrorCodes.InternalError, "The reply is larger than the protocol allows.", reply.ProtocolVersion));

        await ControllerWire.WriteFrameAsync(stream, payload, ControllerProtocol.MaxReplyBytes, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteAsync(Stream stream, ControllerWelcome welcome, CancellationToken cancellationToken)
        => ControllerWire.WriteFrameAsync(stream, ControllerWire.Encode(welcome), ControllerProtocol.MaxReplyBytes, cancellationToken);

    private static async Task<byte[]?> ReadWithinAsync(Stream stream, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        try
        {
            return await ControllerWire.ReadFrameAsync(stream, ControllerProtocol.MaxRequestBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
}

/// <summary>The controller refused the handshake; <see cref="Welcome"/> says why and which protocol versions it speaks.</summary>
public sealed class ControllerHandshakeRefusedException(ControllerWelcome welcome) : Exception(welcome.Error?.Message ?? "The controller refused the connection.")
{
    public ControllerWelcome Welcome { get; } = welcome;
}

/// <summary>The client's side of one connection: it performs the handshake, then sends typed requests and reads their replies.</summary>
public sealed class ControllerClientSession : IAsyncDisposable
{
    private readonly Stream _stream;

    private ControllerClientSession(Stream stream, ControllerWelcome welcome)
    {
        _stream = stream;
        Controller = welcome.Controller!;
        ProtocolVersion = welcome.ProtocolVersion!.Value;
    }

    public ControllerInfo Controller { get; }

    public int ProtocolVersion { get; }

    /// <summary>
    /// Sends the hello and reads the welcome. Throws <see cref="ControllerHandshakeRefusedException"/> when the controller refuses (an incompatible protocol, above all) and
    /// <see cref="ControllerFrameException"/> when what answers is not a controller, and <see cref="IOException"/> when the connection is closed or reset first. The stream is disposed on any failure.
    /// </summary>
    public static async Task<ControllerClientSession> ConnectAsync(Stream stream, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var hello = new ControllerHello { Client = clientName, MinimumProtocolVersion = ControllerProtocol.MinimumVersion, MaximumProtocolVersion = ControllerProtocol.Version };

            await ControllerWire.WriteFrameAsync(stream, ControllerWire.Encode(hello), ControllerProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);

            var payload = await ControllerWire.ReadFrameAsync(stream, ControllerProtocol.MaxReplyBytes, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The controller closed the connection during the handshake.");

            var welcome = ControllerWire.DecodeFromController<ControllerWelcome>(payload);

            if (!welcome.Ok || welcome.Controller is null || welcome.ProtocolVersion is null)
                throw new ControllerHandshakeRefusedException(welcome);

            return new ControllerClientSession(stream, welcome);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    public async Task<ControllerReply> SendAsync(ControllerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ControllerWire.WriteFrameAsync(_stream, ControllerWire.Encode(request), ControllerProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);

        var payload = await ControllerWire.ReadFrameAsync(_stream, ControllerProtocol.MaxReplyBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new ControllerFrameException("The controller closed the connection before it replied.");

        return ControllerWire.DecodeFromController<ControllerReply>(payload);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
