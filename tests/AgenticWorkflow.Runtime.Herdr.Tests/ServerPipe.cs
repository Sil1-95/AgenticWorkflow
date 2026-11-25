using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// Runs the real MCP server loop (<see cref="ClaudeClarificationServer.RunAsync(TextReader, TextWriter, string?, CancellationToken)"/>) the way Claude does: one process for a whole
/// conversation, one JSON-RPC line at a time, reading the channel directory the controller wrote. Unlike <c>Handle</c>, the latch state lives across calls exactly as it does live.
/// </summary>
internal sealed class ServerPipe : IAsyncDisposable
{
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _replies = Channel.CreateUnbounded<string>();
    private readonly Task<int> _server;

    public ServerPipe(string channelDirectory)
        => _server = Task.Run(() => ClaudeClarificationServer.RunAsync(new RequestReader(_requests.Reader), new ReplyWriter(_replies.Writer), channelDirectory));

    /// <summary>Sends one JSON-RPC line and returns the server's reply to it.</summary>
    public async Task<JsonDocument> CallAsync(string request)
    {
        await _requests.Writer.WriteAsync(request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        return JsonDocument.Parse(await _replies.Reader.ReadAsync(timeout.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();

        Assert.Equal(0, await _server.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class RequestReader(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            => await lines.WaitToReadAsync(cancellationToken) && lines.TryRead(out var line) ? line : null;
    }

    private sealed class ReplyWriter(ChannelWriter<string> lines) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            lines.TryWrite(value ?? string.Empty);

            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;
    }
}
