using System.Collections.Concurrent;
using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>The connection to the core ended (EOF, I/O failure) while requests or events were outstanding.</summary>
public sealed class HerdrConnectionLostException : HerdrException
{
    public HerdrConnectionLostException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// JSON-RPC 2.0 over a <see cref="IHerdrLineTransport"/> with one reader loop: responses complete the matching
/// request, notifications go to <see cref="NotificationReceived"/>. The first thing done on a new connection is the
/// <c>hello</c> handshake, which pins protocol generation 1 and fails fast against an incompatible core.
/// </summary>
internal sealed class HerdrRpcConnection : IAsyncDisposable
{
    public const int ProtocolGeneration = 1;

    private readonly IHerdrLineTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private long _nextId;
    private volatile Exception? _lost;

    private HerdrRpcConnection(IHerdrLineTransport transport) => _transport = transport;

    /// <summary>Called on the reader loop for every notification (<c>event</c>, <c>heartbeat</c>, <c>missed</c>). Must not block.</summary>
    public event Action<string, JsonElement>? NotificationReceived;

    /// <summary>Raised once when the connection ends without having been disposed.</summary>
    public event Action<Exception>? Lost;

    public bool IsLost => _lost is not null;

    public static async Task<HerdrRpcConnection> ConnectAsync(
        IHerdrLineTransport transport,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        var connection = new HerdrRpcConnection(transport);
        connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);

        try
        {
            var hello = await connection.CallAsync("hello", new { generation = ProtocolGeneration }, handshakeTimeout, cancellationToken).ConfigureAwait(false);

            if (!hello.TryGetProperty("generation", out var generation) || generation.GetInt32() != ProtocolGeneration)
                throw new HerdrProtocolException($"The core speaks protocol generation {hello.GetStringOrNull("generation") ?? "?"}, not {ProtocolGeneration}.");

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_lost is { } lost)
            throw new HerdrConnectionLostException("The connection to the core is closed.", lost);

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _transport.WriteLineAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new HerdrConnectionLostException($"'{method}' got no reply within {timeout}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // the transport is closed; the loop ends on its own
        }

        _stop.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await _transport.ReadLineAsync(_stop.Token).ConfigureAwait(false);

                if (line is null)
                {
                    Fail(new HerdrConnectionLostException("The core closed the connection."));
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Dispatch(line);
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or HerdrException)
        {
            Fail(new HerdrConnectionLostException("The connection to the core failed.", e));
        }
    }

    private void Dispatch(string line)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // A malformed line cannot be attributed to a request; ignore it rather than tear the connection down.
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
            return;

        if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
        {
            if (_pending.TryGetValue(id.GetInt64(), out var completion))
                Complete(completion, message);

            return;
        }

        if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var parameters = message.TryGetProperty("params", out var p) ? p : default;
            NotificationReceived?.Invoke(method.GetString()!, parameters);
        }
    }

    private static void Complete(TaskCompletionSource<JsonElement> completion, JsonElement message)
    {
        if (message.TryGetProperty("error", out var error))
        {
            var data = error.TryGetProperty("data", out var errorData) ? errorData : default;

            completion.TrySetException(new HerdrCommandException(
                error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0,
                data.ValueKind == JsonValueKind.Object ? data.GetStringOrNull("kind") : null,
                error.GetStringOrNull("message") ?? "(no message)",
                data.ValueKind == JsonValueKind.Object ? data.GetStringOrNull("hint") : null));
            return;
        }

        completion.TrySetResult(message.GetProperty("result"));
    }

    private void Fail(Exception reason)
    {
        if (_stop.IsCancellationRequested)
            return;

        _lost = reason;

        foreach (var pending in _pending.Values)
            pending.TrySetException(reason);

        Lost?.Invoke(reason);
    }
}
