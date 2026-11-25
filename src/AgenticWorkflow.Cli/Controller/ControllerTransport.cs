namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// One kind of local, per-user carrier for the controller protocol: the only place that knows whether the bytes travel over a named pipe or a Unix-domain socket.
/// Everything above it (the protocol, the controller's semantics, the host, the client) sees a duplex <see cref="Stream"/> and nothing else. There is no network
/// transport: a transport that can be reached from another machine does not belong behind this interface.
/// </summary>
internal interface IControllerTransport
{
    /// <summary>The kind of carrier, for the controller's health report and log (<c>named_pipe</c>, <c>unix_socket</c>). Never an address.</summary>
    string Kind { get; }

    /// <summary>
    /// Creates the endpoint and starts listening. The caller must hold the controller's single-instance lock: it is what makes an endpoint left behind by a crashed controller
    /// provably stale. Throws <see cref="ControllerEndpointException"/> when the endpoint cannot be made private to the current user; it never falls back to a weaker one.
    /// </summary>
    IControllerListener Listen(Action<string> log);

    /// <summary>
    /// Connects to the endpoint and verifies that what answers belongs to the current user. Throws <see cref="ControllerNotReachableException"/> when nothing is listening
    /// and <see cref="ControllerPeerRejectedException"/> when something is listening that is not the current user's.
    /// </summary>
    Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IControllerListener : IAsyncDisposable
{
    /// <summary>The next connection, already verified to come from the current user. A peer that fails the check is closed and never returned.</summary>
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>The endpoint cannot be created, or cannot be made private to the current user. The controller does not start.</summary>
internal sealed class ControllerEndpointException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Nothing is listening at the endpoint.</summary>
internal sealed class ControllerNotReachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Something is listening at the endpoint but it is not the current user's: it is never spoken to.</summary>
internal sealed class ControllerPeerRejectedException(string message) : Exception(message);
