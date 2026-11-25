using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Cli.Controller;
using static AgenticWorkflow.Cli.Tests.ControllerTestSupport;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The operating-system transports: who can reach the controller and what is left behind. The Unix-domain-socket transport also runs on Windows (which has AF_UNIX), so its
/// protocol behaviour is tested everywhere; what depends on Unix file modes and kernel peer credentials is marked <see cref="UnixFactAttribute"/> and is reported as skipped,
/// never as passed, elsewhere.
/// </summary>
public sealed class ControllerTransportTests : IDisposable
{
    private readonly string _directory = ShortDirectory();

    public void Dispose() => Delete(_directory);

    private sealed class FakeVerifier(params bool?[] answers) : IUnixPeerVerifier
    {
        private readonly Queue<bool?> _answers = new(answers);
        private bool? _last;

        public bool? IsCurrentUser(Socket connected)
        {
            if (_answers.Count > 0)
                _last = _answers.Dequeue();

            return _last;
        }
    }

    private UnixSocketControllerTransport Unix(IUnixPeerVerifier? verifier = null, string? directory = null, string name = "t.sock")
        => new(directory ?? _directory, name, verifier ?? new FakeVerifier(true));

    private IControllerTransport Platform() => ControllerEndpoints.For(Path.Combine(_directory, "root"), _ => _directory);

    private static async Task<string> EchoOnceAsync(IControllerTransport transport)
    {
        await using var listener = transport.Listen(_ => { });
        var accepted = listener.AcceptAsync(CancellationToken.None);

        await using var client = await transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Patience);
        await using var server = await accepted.WaitAsync(Patience);

        return await SendAsync(client, server, "ping");
    }

    /// <summary>Writes <paramref name="text"/> on one end and reads it on the other. The read is started first: a named pipe has no buffer, so a write completes only when the peer reads.</summary>
    private static async Task<string> SendAsync(Stream from, Stream to, string text)
    {
        var buffer = new byte[System.Text.Encoding.UTF8.GetByteCount(text)];
        var read = to.ReadExactlyAsync(buffer).AsTask();

        await from.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)).AsTask().WaitAsync(Patience);
        await read.WaitAsync(Patience);

        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    // ---- both transports ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_platform_transport_carries_bytes_between_a_client_and_the_controller()
        => Assert.Equal("ping", await EchoOnceAsync(Platform()));

    [Fact]
    public async Task The_unix_socket_transport_carries_bytes_between_a_client_and_the_controller()
        => Assert.Equal("ping", await EchoOnceAsync(Unix()));

    [Fact]
    public async Task Connecting_to_an_endpoint_nobody_listens_on_says_so()
    {
        await Assert.ThrowsAsync<ControllerNotReachableException>(() => Platform().ConnectAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
        await Assert.ThrowsAsync<ControllerNotReachableException>(() => Unix().ConnectAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_second_listener_on_the_same_pipe_name_is_refused_while_the_first_lives()
    {
        var transport = new NamedPipeControllerTransport($"aw-test-{Guid.NewGuid():N}");
        await using var first = transport.Listen(_ => { });

        Assert.Throws<ControllerEndpointException>(() => transport.Listen(_ => { }));
    }

    [Fact]
    public async Task A_client_that_connects_and_vanishes_does_not_stop_the_listener_from_serving_the_next()
    {
        var transport = Platform();
        await using var listener = transport.Listen(_ => { });

        await (await transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).DisposeAsync();

        try
        {
            // The vanished peer surfaces as a failed accept (the host logs it and retries) or as a connection that reads end-of-stream.
            await using var vanished = await listener.AcceptAsync(CancellationToken.None).WaitAsync(Patience);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
        }

        var accepted = listener.AcceptAsync(CancellationToken.None);
        await using var second = await transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await using var server = await accepted.WaitAsync(Patience);

        Assert.Equal("ok", await SendAsync(second, server, "ok"));
    }

    // ---- Windows: named pipe ------------------------------------------------------------------------------------------

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task The_named_pipe_grants_access_to_the_current_user_and_nobody_else()
    {
        var transport = Platform();
        await using var listener = transport.Listen(_ => { });

        var name = $"aw-controller-{ControllerEndpoints.EndpointName(Path.Combine(_directory, "root"))}";
        await using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        await probe.ConnectAsync(5000);

        var rules = probe.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;

        // Every allow-rule on the pipe names the current user (or the LocalSystem account every Windows process tree already trusts); there is no Everyone, no Users, no Network.
        var granted = rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => (SecurityIdentifier)r.IdentityReference).ToList();

        Assert.Contains(me, granted);
        Assert.DoesNotContain(granted, sid => sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.NetworkSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
    }

    // ---- Unix socket: the user check ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_peer_that_is_not_the_current_user_is_closed_and_never_returned()
    {
        var log = new List<string>();
        var server = Unix(new FakeVerifier(false, true));   // the first peer is someone else, the second is the current user
        var client = Unix(new FakeVerifier(true));
        await using var listener = server.Listen(log.Add);

        var accepted = listener.AcceptAsync(CancellationToken.None);

        await using var stranger = await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(300);

        Assert.False(accepted.IsCompleted);
        Assert.Contains(log, line => line.Contains("not the current user", StringComparison.Ordinal));

        await using var friend = await client.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await using var served = await accepted.WaitAsync(Patience);

        Assert.Equal("hi", await SendAsync(friend, served, "hi"));
    }

    [Fact]
    public async Task A_platform_that_cannot_identify_the_peer_does_not_get_to_connect()
    {
        var log = new List<string>();
        await using var listener = Unix(new FakeVerifier(new bool?[] { null })).Listen(log.Add);

        var accepted = listener.AcceptAsync(CancellationToken.None);
        await using var client = await Unix(new FakeVerifier(true)).ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await Task.Delay(300);

        Assert.False(accepted.IsCompleted);
        Assert.Contains(log, line => line.Contains("cannot be identified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_refuses_a_server_that_is_not_the_current_user()
    {
        await using var listener = Unix(new FakeVerifier(true)).Listen(_ => { });
        _ = listener.AcceptAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ControllerPeerRejectedException>(() => Unix(new FakeVerifier(false)).ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    // ---- Unix socket: the endpoint's files ----------------------------------------------------------------------------

    [Fact]
    public async Task A_socket_left_behind_by_a_dead_controller_is_replaced()
    {
        File.WriteAllText(Path.Combine(_directory, "t.sock"), "stale");   // what a crashed controller leaves: a file at the endpoint, with nothing listening

        Assert.Equal("ping", await EchoOnceAsync(Unix()));
    }

    [Fact]
    public async Task Stopping_the_listener_removes_its_socket()
    {
        var path = Path.Combine(_directory, "t.sock");
        var listener = Unix().Listen(_ => { });

        Assert.True(File.Exists(path));

        await listener.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_socket_path_longer_than_the_platform_allows_is_refused_instead_of_truncated()
    {
        var transport = Unix(directory: _directory, name: new string('x', 120) + ".sock");

        Assert.Throws<ControllerEndpointException>(() => transport.Listen(_ => { }));
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_directory_and_socket_are_private_to_the_user()
    {
        var directory = Path.Combine(_directory, "run");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);   // 0755: another user could enter it

        await using var listener = new UnixSocketControllerTransport(directory, "t.sock").Listen(_ => { });

        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(directory));                         // tightened to 0700
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "t.sock")));   // 0600
    }

    [UnixFact]
    public void A_runtime_directory_that_is_a_symbolic_link_is_refused()
    {
        var target = Path.Combine(_directory, "target");
        var link = Path.Combine(_directory, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        Assert.Throws<ControllerEndpointException>(() => new UnixSocketControllerTransport(link, "t.sock").Listen(_ => { }));
    }

    [UnixFact]
    public async Task The_real_kernel_check_recognises_the_current_user_as_the_peer()
    {
        var transport = new UnixSocketControllerTransport(_directory, "t.sock", new UnixPeerCredentials());
        await using var listener = transport.Listen(_ => { });

        var accepted = listener.AcceptAsync(CancellationToken.None);
        await using var client = await transport.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Patience);
        await using var server = await accepted.WaitAsync(Patience);

        Assert.NotNull(server);
    }
}
