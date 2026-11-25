using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>
/// Minimal direct JSON-RPC 2.0 connection to the herdr-core named pipe (one JSON message per line).
/// A proof of the native transport only: sequential request/response plus a read loop for
/// <c>event</c> notifications. Not a general OpenRPC client.
/// </summary>
internal sealed class HerdrPipeConnection : IAsyncDisposable
{
    private const int ProtocolGeneration = 1;

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _nextId;

    private HerdrPipeConnection(NamedPipeClientStream pipe)
    {
        _pipe = pipe;

        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(pipe, utf8);
        _writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>The pipe name herdr-core 0.1.19 listens on for a session (null = the default session).</summary>
    public static string PipeName(string? session) =>
        $"herdr-core-{Environment.UserName}-{session ?? "default"}.sock";

    /// <summary>Connects and performs the <c>hello</c> handshake (result kept in <see cref="Hello"/>).</summary>
    public static async Task<HerdrPipeConnection> ConnectAsync(
        string? session,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        var pipeName = PipeName(session);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            using var timeout = new CancellationTokenSource(connectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            throw new HerdrProtocolException($"No herdr-core is listening on pipe '{pipeName}' (waited {connectTimeout}).");
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        // The reader/writer may only be created once the pipe is connected.
        var connection = new HerdrPipeConnection(pipe);

        try
        {
            connection.Hello = await connection.CallAsync("hello", new { generation = ProtocolGeneration }, cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Result of the <c>hello</c> handshake (core identity, supported methods and features).</summary>
    public JsonElement Hello { get; private set; }

    /// <summary>Sends one request and returns its <c>result</c>, skipping interleaved notifications.</summary>
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var id = ++_nextId;
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });

        await _writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);

            if (!message.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                continue;

            if (message.TryGetProperty("error", out var error))
            {
                var data = error.TryGetProperty("data", out var errorData) ? errorData : default;

                throw new HerdrCommandException(
                    error.GetProperty("code").GetInt32(),
                    data.ValueKind == JsonValueKind.Object ? data.GetStringOrNull("kind") : null,
                    error.GetStringOrNull("message") ?? "(no message)",
                    data.ValueKind == JsonValueKind.Object ? data.GetStringOrNull("hint") : null);
            }

            return message.GetProperty("result").Clone();
        }
    }

    /// <summary>Reads the next JSON-RPC message (response or notification) from the core.</summary>
    public async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new HerdrProtocolException("The Herdr pipe was closed by the core.");

        try
        {
            using var document = JsonDocument.Parse(line);

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new HerdrProtocolException($"Malformed JSON-RPC message from Herdr: '{line}'.", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _reader.Dispose();

        return _pipe.DisposeAsync();
    }
}
