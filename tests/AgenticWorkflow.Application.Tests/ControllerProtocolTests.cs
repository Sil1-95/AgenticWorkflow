using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The controller's contract, handshake and request handling, exercised over in-memory duplex streams: no operating-system transport is involved, so these behaviours hold
/// for every transport by construction. Transport-specific behaviour (who may connect) is tested with the transports.
/// </summary>
public sealed class ControllerProtocolTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ---- fixtures -----------------------------------------------------------------------------------------------------

    private sealed class FakeCatalog : ITaskCatalog
    {
        public TaskReadModel? Model { get; set; }

        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public Task<TaskListing> ListAsync(int limit, CancellationToken cancellationToken)
        {
            Calls++;

            if (Fail)
                throw new InvalidOperationException("secret detail that must not reach a client");

            IReadOnlyList<TaskSummary> tasks = Model is null ? [] : [TaskSummary.From(Model, "C:\\work")];

            return Task.FromResult(new TaskListing(tasks.Count, tasks.Take(limit).ToList()));
        }

        public Task<TaskReadOutcome> ReadAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            Calls++;

            if (Fail)
                throw new InvalidOperationException("secret detail that must not reach a client");

            return Task.FromResult(Model is not null && Model.TaskId == taskId.Value
                ? TaskReadOutcome.Found(Model)
                : TaskReadOutcome.Failed(ControllerErrorCodes.NotFound, $"There is no task '{taskId}'."));
        }
    }

    private sealed class FakeLifecycle : IControllerLifecycle
    {
        public bool IsShuttingDown { get; set; }

        public int ShutdownRequests { get; private set; }

        public void RequestShutdown()
        {
            ShutdownRequests++;
            IsShuttingDown = true;
        }
    }

    private static readonly ControllerInfo Info = new()
    {
        InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ProcessId = 1234,
        StartedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Version = "test",
        Transport = "in_memory",
        MinimumProtocolVersion = ControllerProtocol.MinimumVersion,
        MaximumProtocolVersion = ControllerProtocol.Version,
        ReadModelSchemaVersion = TaskReadModel.CurrentSchemaVersion,
    };

    private static async Task<TaskReadModel> ModelAsync()
    {
        var harness = await DirectHarness.CreateAsync();

        return TaskReadModelProjector.Project(harness.Snapshot.Task);
    }

    /// <summary>A running server session on one end of an in-memory duplex stream; the test holds the other end.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();

        public FakeCatalog Catalog { get; } = new();

        public FakeLifecycle Lifecycle { get; } = new();

        public List<string> Log { get; } = [];

        public Stream Client { get; }

        public Task<SessionEnd> Ended { get; }

        public Connection(ControllerSessionOptions? options = null)
        {
            var (client, server) = DuplexStream.CreatePair();
            Client = client;

            var dispatcher = new ControllerDispatcher(Info, Catalog, Lifecycle, Log.Add);
            Ended = new ControllerServerSession(dispatcher, Info, Lifecycle, options, Log.Add).RunAsync(server, _cancel.Token);
        }

        public async Task<SessionEnd> EndAsync() => await Ended.WaitAsync(Patience);

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            await Client.DisposeAsync();
            await Ended.WaitAsync(Patience);
            _cancel.Dispose();
        }
    }

    private static async Task SendRawAsync(Stream stream, string json)
        => await ControllerWire.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json), ControllerProtocol.MaxRequestBytes, CancellationToken.None);

    private static async Task<JsonElement> ReadRawAsync(Stream stream)
    {
        var payload = await ControllerWire.ReadFrameAsync(stream, ControllerProtocol.MaxReplyBytes, new CancellationTokenSource(Patience).Token)
            ?? throw new InvalidOperationException("The server closed the connection.");

        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static async Task<ControllerClientSession> HandshakeAsync(Connection connection)
        => await ControllerClientSession.ConnectAsync(connection.Client, "test", new CancellationTokenSource(Patience).Token);

    // ---- version negotiation ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 5, 1)]
    [InlineData(0, 3, 1)]
    public void Overlapping_ranges_negotiate_the_newest_shared_version(int minimum, int maximum, int expected)
        => Assert.Equal(expected, ControllerProtocol.Negotiate(minimum, maximum));

    [Theory]
    [InlineData(2, 3)]
    [InlineData(-5, 0)]
    [InlineData(3, 2)]
    public void Disjoint_or_inverted_ranges_negotiate_nothing(int minimum, int maximum)
        => Assert.Null(ControllerProtocol.Negotiate(minimum, maximum));

    // ---- handshake -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_compatible_client_is_welcomed_with_the_controller_identity()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        Assert.Equal(ControllerProtocol.Version, client.ProtocolVersion);
        Assert.Equal(Info.InstanceId, client.Controller.InstanceId);
        Assert.Equal("in_memory", client.Controller.Transport);
    }

    [Fact]
    public async Task An_incompatible_client_is_refused_before_any_request_is_read()
    {
        await using var connection = new Connection();

        await SendRawAsync(connection.Client, """{"client":"future","minimumProtocolVersion":7,"maximumProtocolVersion":9}""");
        var welcome = await ReadRawAsync(connection.Client);

        Assert.False(welcome.GetProperty("ok").GetBoolean());
        Assert.Equal(ControllerErrorCodes.IncompatibleProtocol, welcome.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ControllerProtocol.Version, welcome.GetProperty("serverMaximumProtocolVersion").GetInt32());
        Assert.Equal(SessionEnd.HandshakeRefused, await connection.EndAsync());
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Fact]
    public async Task The_client_reports_a_refusal_as_a_handshake_failure_carrying_the_controllers_range()
    {
        var (client, server) = DuplexStream.CreatePair();

        var refusal = Task.Run(async () =>
        {
            await ControllerWire.ReadFrameAsync(server, ControllerProtocol.MaxRequestBytes, CancellationToken.None);
            await ControllerWire.WriteFrameAsync(
                server,
                ControllerWire.Encode(new ControllerWelcome
                {
                    Ok = false,
                    ProtocolVersion = null,
                    ServerMinimumProtocolVersion = 8,
                    ServerMaximumProtocolVersion = 9,
                    Controller = null,
                    Error = new ControllerError(ControllerErrorCodes.IncompatibleProtocol, "too old"),
                }),
                ControllerProtocol.MaxReplyBytes,
                CancellationToken.None);
        });

        var e = await Assert.ThrowsAsync<ControllerHandshakeRefusedException>(() => ControllerClientSession.ConnectAsync(client, "old", CancellationToken.None));
        await refusal;

        Assert.Equal(8, e.Welcome.ServerMinimumProtocolVersion);
        Assert.Equal(ControllerErrorCodes.IncompatibleProtocol, e.Welcome.Error!.Code);
    }

    [Fact]
    public async Task A_hello_that_is_not_a_hello_is_refused_without_serving_requests()
    {
        await using var connection = new Connection();

        await SendRawAsync(connection.Client, """{"operation":"health"}""");
        var welcome = await ReadRawAsync(connection.Client);

        Assert.False(welcome.GetProperty("ok").GetBoolean());
        Assert.Equal(SessionEnd.HandshakeRefused, await connection.EndAsync());
    }

    [Fact]
    public async Task A_client_that_never_says_hello_does_not_hold_the_connection()
    {
        await using var connection = new Connection(new ControllerSessionOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(100) });

        Assert.Equal(SessionEnd.TimedOut, await connection.EndAsync());
    }

    // ---- operations ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_reports_ready_and_the_controller_identity()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var reply = await client.SendAsync(new HealthRequest(), CancellationToken.None);

        Assert.True(reply.Ok);
        var health = Assert.IsType<HealthResult>(reply.Result);
        Assert.Equal(ControllerStatus.Ready, health.Status);
        Assert.Equal(Info.ProcessId, health.Controller.ProcessId);
        Assert.Equal(client.ProtocolVersion, reply.ProtocolVersion);
    }

    [Fact]
    public async Task A_known_task_is_read_as_the_read_model_and_listed_as_a_summary()
    {
        await using var connection = new Connection();
        connection.Catalog.Model = await ModelAsync();
        await using var client = await HandshakeAsync(connection);

        var read = await client.SendAsync(new ReadTaskRequest { TaskId = connection.Catalog.Model.TaskId.ToString("D") }, CancellationToken.None);
        var list = await client.SendAsync(new ListTasksRequest(), CancellationToken.None);

        var task = Assert.IsType<TaskResult>(read.Result);
        Assert.Equal(TaskReadModelJson.Serialize(connection.Catalog.Model), TaskReadModelJson.Serialize(task.Task));   // the same text 'aw status --json' prints

        var listing = Assert.IsType<TaskListResult>(list.Result);
        Assert.Equal(1, listing.Total);
        Assert.Equal(connection.Catalog.Model.TaskId, Assert.Single(listing.Tasks).TaskId);
    }

    [Fact]
    public async Task An_unknown_task_is_not_found_and_the_connection_stays_usable()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var missing = await client.SendAsync(new ReadTaskRequest { TaskId = Guid.NewGuid().ToString("D") }, CancellationToken.None);
        var health = await client.SendAsync(new HealthRequest(), CancellationToken.None);

        Assert.False(missing.Ok);
        Assert.Equal(ControllerErrorCodes.NotFound, missing.Error!.Code);
        Assert.True(health.Ok);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{0197d2e1-5c3a-7b8e-9f10-2a3b4c5d6e7f}")]
    [InlineData("../../etc/passwd")]
    public async Task A_task_id_that_is_not_a_task_id_is_rejected_before_the_catalog_is_asked(string taskId)
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var reply = await client.SendAsync(new ReadTaskRequest { TaskId = taskId }, CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Equal(ControllerErrorCodes.InvalidTaskId, reply.Error!.Code);
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ControllerProtocol.MaxListLimit + 1)]
    public async Task A_list_limit_outside_its_range_is_an_invalid_argument(int limit)
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var reply = await client.SendAsync(new ListTasksRequest { Limit = limit }, CancellationToken.None);

        Assert.Equal(ControllerErrorCodes.InvalidArgument, reply.Error!.Code);
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Fact]
    public async Task A_failure_inside_the_controller_is_reported_without_its_detail()
    {
        await using var connection = new Connection();
        connection.Catalog.Fail = true;
        await using var client = await HandshakeAsync(connection);

        var reply = await client.SendAsync(new ListTasksRequest(), CancellationToken.None);

        Assert.Equal(ControllerErrorCodes.InternalError, reply.Error!.Code);
        Assert.DoesNotContain("secret", reply.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Log, line => line.Contains("secret", StringComparison.Ordinal));   // not even the log carries the exception's message
        Assert.Contains(connection.Log, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    // ---- malformed and unsupported requests ---------------------------------------------------------------------------

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"noOperation":true}""")]
    [InlineData("""{"operation":7}""")]
    [InlineData("""{"operation":"read_task"}""")]                                   // a required argument is missing
    [InlineData("""{"operation":"health","extra":"a property this controller does not know"}""")]
    [InlineData("""{"operation":"list_tasks","limit":"many"}""")]
    public async Task A_message_that_is_not_a_valid_request_changes_nothing_and_leaves_the_connection_usable(string json)
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        await SendRawAsync(connection.Client, json);
        var reply = await ReadRawAsync(connection.Client);

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(ControllerErrorCodes.MalformedRequest, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, connection.Catalog.Calls);
        Assert.Equal(0, connection.Lifecycle.ShutdownRequests);
        Assert.True((await client.SendAsync(new HealthRequest(), CancellationToken.None)).Ok);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("approve_all")]
    [InlineData("execute")]
    [InlineData("exec")]
    [InlineData("run_command")]
    [InlineData("shell")]
    public async Task An_operation_the_controller_does_not_have_is_unsupported_and_nothing_happens(string operation)
    {
        await using var connection = new Connection();
        connection.Catalog.Model = await ModelAsync();
        await using var client = await HandshakeAsync(connection);

        await SendRawAsync(connection.Client, $$"""{"operation":"{{operation}}","taskId":"{{connection.Catalog.Model.TaskId:D}}","text":"yes, approve it"}""");
        var reply = await ReadRawAsync(connection.Client);

        Assert.Equal(ControllerErrorCodes.UnsupportedOperation, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Fact]
    public void The_operations_the_controller_has_are_exactly_the_documented_typed_set()
        => Assert.Equal(
            ["abort", "accept", "answer_clarification", "approve", "bind_role", "continue_task", "health", "list_tasks", "read_task", "request_changes", "shutdown", "start_task"],
            ControllerOperations.All.Order(StringComparer.Ordinal));

    // ---- framing violations and disconnects ---------------------------------------------------------------------------

    [Fact]
    public async Task A_frame_larger_than_the_limit_ends_the_connection_without_reading_it()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, ControllerProtocol.MaxRequestBytes + 1u);
        await connection.Client.WriteAsync(header);

        Assert.Equal(SessionEnd.ProtocolViolation, await connection.EndAsync());
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Fact]
    public async Task An_empty_frame_ends_the_connection()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        await connection.Client.WriteAsync(new byte[4]);

        Assert.Equal(SessionEnd.ProtocolViolation, await connection.EndAsync());
    }

    [Fact]
    public async Task A_client_that_vanishes_mid_request_leaves_nothing_behind()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100);
        await connection.Client.WriteAsync(header);
        await connection.Client.WriteAsync("{\"operation\":"u8.ToArray());
        await connection.Client.DisposeAsync();   // gone before the frame is complete

        Assert.Equal(SessionEnd.ProtocolViolation, await connection.EndAsync());
        Assert.Equal(0, connection.Catalog.Calls);
        Assert.Equal(0, connection.Lifecycle.ShutdownRequests);
    }

    [Fact]
    public async Task A_connection_that_breaks_abruptly_ends_quietly()
    {
        var (client, server) = DuplexStream.CreatePair();
        var lifecycle = new FakeLifecycle();
        var dispatcher = new ControllerDispatcher(Info, new FakeCatalog(), lifecycle);
        var ended = new ControllerServerSession(dispatcher, Info, lifecycle).RunAsync(server, CancellationToken.None);

        await using var session = await ControllerClientSession.ConnectAsync(client, "test", CancellationToken.None);
        ((DuplexStream)client).Abort();

        Assert.Equal(SessionEnd.Disconnected, await ended.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_client_that_closes_between_requests_completes_the_session()
    {
        await using var connection = new Connection();
        var client = await HandshakeAsync(connection);

        await client.DisposeAsync();

        Assert.Equal(SessionEnd.Completed, await connection.EndAsync());
    }

    [Fact]
    public async Task A_client_that_goes_silent_is_closed_after_the_idle_timeout()
    {
        await using var connection = new Connection(new ControllerSessionOptions { IdleTimeout = TimeSpan.FromMilliseconds(100) });
        await using var client = await HandshakeAsync(connection);

        Assert.Equal(SessionEnd.TimedOut, await connection.EndAsync());
    }

    // ---- shutdown -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shutdown_request_is_answered_first_and_then_stops_the_controller()
    {
        await using var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        var reply = await client.SendAsync(new ShutdownRequest(), CancellationToken.None);

        Assert.True(Assert.IsType<ShutdownResult>(reply.Result).Accepted);
        Assert.Equal(SessionEnd.Completed, await connection.EndAsync());
        Assert.Equal(1, connection.Lifecycle.ShutdownRequests);
    }

    [Fact]
    public async Task A_stopping_controller_takes_no_new_work_but_still_reports_health()
    {
        await using var connection = new Connection();
        connection.Catalog.Model = await ModelAsync();
        connection.Lifecycle.IsShuttingDown = true;
        await using var client = await HandshakeAsync(connection);

        var list = await client.SendAsync(new ListTasksRequest(), CancellationToken.None);
        var health = await client.SendAsync(new HealthRequest(), CancellationToken.None);

        Assert.Equal(ControllerErrorCodes.ShuttingDown, list.Error!.Code);
        Assert.Equal(ControllerStatus.ShuttingDown, Assert.IsType<HealthResult>(health.Result).Status);
        Assert.Equal(0, connection.Catalog.Calls);
    }

    [Fact]
    public async Task Cancelling_the_controller_ends_open_sessions()
    {
        var connection = new Connection();
        await using var client = await HandshakeAsync(connection);

        await connection.DisposeAsync();

        Assert.Equal(SessionEnd.Cancelled, await connection.Ended);
    }

    // ---- the wire ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requests_and_results_round_trip_with_their_discriminators()
    {
        var request = new ReadTaskRequest { TaskId = "0197d2e1-5c3a-7b8e-9f10-2a3b4c5d6e7f" };
        var text = Encoding.UTF8.GetString(ControllerWire.Encode<ControllerRequest>(request));

        Assert.Contains("\"operation\":\"read_task\"", text, StringComparison.Ordinal);
        Assert.Equal(request.TaskId, Assert.IsType<ReadTaskRequest>(ControllerWire.DecodeRequest(Encoding.UTF8.GetBytes(text)).Request).TaskId);

        var reply = ControllerReply.Success(new HealthResult { Status = ControllerStatus.Ready, Controller = Info });
        var decoded = ControllerWire.DecodeFromController<ControllerReply>(ControllerWire.Encode(reply));

        Assert.IsType<HealthResult>(decoded.Result);
        Assert.Contains("\"kind\":\"health\"", Encoding.UTF8.GetString(ControllerWire.Encode(reply)), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_client_ignores_what_a_newer_controller_adds_but_a_controller_refuses_what_a_newer_client_adds()
    {
        var newerReply = """{"protocolVersion":1,"ok":true,"result":{"kind":"shutdown","accepted":true,"somethingNew":1},"error":null,"alsoNew":2}"""u8.ToArray();
        Assert.IsType<ShutdownResult>(ControllerWire.DecodeFromController<ControllerReply>(newerReply).Result);

        var (request, error) = ControllerWire.DecodeRequest("""{"operation":"health","somethingNew":1}"""u8.ToArray());
        Assert.Null(request);
        Assert.Equal(ControllerErrorCodes.MalformedRequest, error!.Code);
    }
}

/// <summary>One end of an in-memory duplex stream. Closing an end gives the other a clean end of stream; <see cref="Abort"/> breaks it.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly ChannelReader<byte[]> _input;
    private readonly ChannelWriter<byte[]> _output;
    private byte[] _pending = [];
    private int _offset;

    private DuplexStream(ChannelReader<byte[]> input, ChannelWriter<byte[]> output)
    {
        _input = input;
        _output = output;
    }

    public static (Stream First, Stream Second) CreatePair()
    {
        var a = Channel.CreateUnbounded<byte[]>();
        var b = Channel.CreateUnbounded<byte[]>();

        return (new DuplexStream(a.Reader, b.Writer), new DuplexStream(b.Reader, a.Writer));
    }

    public void Abort() => _output.TryComplete(new IOException("The connection was reset."));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset >= _pending.Length)
        {
            if (!await _input.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;

            _pending = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;

        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _output.TryWrite(buffer.ToArray()) ? ValueTask.CompletedTask : throw new IOException("The connection is closed.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _output.TryComplete();

        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
