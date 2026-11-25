using System.IO.Pipes;
using System.Runtime.Versioning;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// The Windows transport: a named pipe that only the current user can open.
/// <para>
/// Access control comes from the operating system, not from the protocol: every instance is created with <see cref="PipeOptions.CurrentUserOnly"/>, which gives the pipe a
/// security descriptor that grants access to the current user alone (no other user, no group, not Everyone, not the network), and the client uses the same option, which
/// makes it refuse a pipe whose server is not the current user. The first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>, so a process that already owns
/// the name (a squatter, or a second controller) makes creation fail instead of being joined. The pipe is created in the local pipe namespace, which .NET additionally
/// marks to reject remote clients.
/// </para>
/// <para>
/// One instance is always waiting: the next is created before the connected one is handed out, so the name never has zero instances (which is the window in which another
/// process could take it).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedPipeControllerTransport(string pipeName) : IControllerTransport
{
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    public string Kind => "named_pipe";

    public IControllerListener Listen(Action<string> log)
    {
        try
        {
            return new Listener(pipeName, Create(pipeName, first: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ControllerEndpointException("The controller's named pipe already exists or cannot be created for the current user only.", e);
        }
    }

    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, Options);

        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

            return client;
        }
        catch (TimeoutException e)
        {
            await client.DisposeAsync().ConfigureAwait(false);

            throw new ControllerNotReachableException("No controller is listening.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            await client.DisposeAsync().ConfigureAwait(false);

            // CurrentUserOnly refuses a server that is not the current user (and access to a pipe the user may not open).
            throw new ControllerPeerRejectedException($"The controller endpoint is not owned by the current user ({e.GetType().Name}).");
        }
        catch (IOException e)
        {
            await client.DisposeAsync().ConfigureAwait(false);

            throw new ControllerNotReachableException("The controller endpoint could not be opened.", e);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    private static NamedPipeServerStream Create(string name, bool first)
        => new(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            first ? Options | PipeOptions.FirstPipeInstance : Options);

    private sealed class Listener(string name, NamedPipeServerStream waiting) : IControllerListener
    {
        private NamedPipeServerStream? _waiting = waiting;

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            var current = _waiting ?? throw new ObjectDisposedException(nameof(Listener));

            try
            {
                await current.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A client that connected and vanished leaves this instance unusable; the name keeps an instance regardless.
                _waiting = Create(name, first: false);
                await current.DisposeAsync().ConfigureAwait(false);

                throw;
            }

            // The name keeps an instance before the connected one leaves.
            _waiting = Create(name, first: false);

            return current;
        }

        public async ValueTask DisposeAsync()
        {
            var waiting = _waiting;
            _waiting = null;

            if (waiting is not null)
                await waiting.DisposeAsync().ConfigureAwait(false);
        }
    }
}
