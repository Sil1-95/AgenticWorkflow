using System.Net.Sockets;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// The Linux and macOS transport: a Unix-domain socket inside a directory that only the current user can enter.
/// <para>
/// Access control is enforced explicitly, in three layers, and does not rely on any default: (1) the directory is created with mode 0700 and, every time, set to 0700 again
/// and read back (a <c>chmod</c> by a process that does not own the directory fails, so success also proves ownership); a symbolic link in its place is refused; (2) the
/// socket file is set to 0600; (3) the kernel's statement of who the peer is (see <see cref="IUnixPeerVerifier"/>) must be the current user on both sides of the
/// connection. If any layer cannot be established the transport does not start; it never falls back to a weaker endpoint.
/// </para>
/// <para>
/// The socket's path is short (the platform limits it to about 100 bytes), so the caller supplies a directory that is already short and a file name derived from the state
/// root. Single-instance ownership is the caller's lock, not the socket: a socket file found at start-up is, by that lock, left behind by a controller that died, and is removed.
/// </para>
/// </summary>
internal sealed class UnixSocketControllerTransport(string directory, string socketName, IUnixPeerVerifier? verifier = null) : IControllerTransport
{
    /// <summary>The longest socket path the supported platforms accept, with room for the terminator.</summary>
    private const int MaxPathBytes = 100;

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IUnixPeerVerifier _verifier = verifier ?? new UnixPeerCredentials();

    public string Kind => "unix_socket";

    private string SocketPath => Path.Combine(directory, socketName);

    public IControllerListener Listen(Action<string> log)
    {
        var path = SocketPath;

        if (System.Text.Encoding.UTF8.GetByteCount(path) > MaxPathBytes)
            throw new ControllerEndpointException($"The controller socket path is longer than the platform allows ({MaxPathBytes} bytes).");

        EnsurePrivateDirectory(directory);

        // The caller holds the single-instance lock, so a socket that exists belongs to a controller that died.
        TryDelete(path);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, PrivateFile);

            socket.Listen(16);

            return new Listener(socket, path, _verifier, log);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            socket.Dispose();
            TryDelete(path);

            throw new ControllerEndpointException("The controller socket cannot be created for the current user only.", e);
        }
    }

    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when ((e is SocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();

            throw new ControllerNotReachableException("No controller is listening.", e);
        }
        catch
        {
            socket.Dispose();

            throw;
        }

        // The server must be this user too: a socket that someone else managed to put at the name is never spoken to.
        if (_verifier.IsCurrentUser(socket) == false)
        {
            socket.Dispose();

            throw new ControllerPeerRejectedException("The controller socket is served by another user.");
        }

        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Creates the directory private (0700) or proves the existing one is, and refuses a link, a directory it does not own, or one that cannot be made private.</summary>
    internal static void EnsurePrivateDirectory(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);

                return;
            }

            Directory.CreateDirectory(path, PrivateDirectory);

            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new ControllerEndpointException("The controller's runtime directory is a symbolic link.");

            // chmod by a non-owner fails, so this both enforces and proves ownership.
            File.SetUnixFileMode(path, PrivateDirectory);

            if (File.GetUnixFileMode(path) != PrivateDirectory)
                throw new ControllerEndpointException("The controller's runtime directory could not be made private to the current user.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ControllerEndpointException("The controller's runtime directory is not usable by the current user only.", e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // If it cannot be removed, binding fails next and reports the endpoint as unusable.
        }
    }

    private sealed class Listener(Socket socket, string path, IUnixPeerVerifier verifier, Action<string> log) : IControllerListener
    {
        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);

                // Anything but a positive answer is a refusal: a platform that cannot say who the peer is does not get to connect.
                if (verifier.IsCurrentUser(client) == true)
                    return new NetworkStream(client, ownsSocket: true);

                log("rejected a connection whose peer is not the current user (or cannot be identified)");
                client.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            TryDelete(path);

            return ValueTask.CompletedTask;
        }
    }
}
