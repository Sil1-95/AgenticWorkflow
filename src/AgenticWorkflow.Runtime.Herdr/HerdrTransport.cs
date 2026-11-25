using System.Diagnostics;
using System.Text;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>One JSON-RPC message per line, in both directions. The seam that lets the runtime be tested without a core.</summary>
internal interface IHerdrLineTransport : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The next line, or null when the peer closed the connection.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

/// <summary>Opens a new connection to the isolated core. Each call is one independent connection.</summary>
internal interface IHerdrTransportFactory
{
    Task<IHerdrLineTransport> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The official bridge: a long-lived <c>hc connect</c> child process whose stdin/stdout carry the JSON-RPC lines.
/// Using it means this code never depends on the core's named-pipe naming scheme (which was only inferred from observation), and
/// the isolated session is selected the same way as for every other <c>hc</c> call.
/// </summary>
internal sealed class HcConnectTransport : IHerdrLineTransport
{
    private readonly Process _process;
    private readonly StringBuilder _stderrTail = new();
    private readonly Task _stderrPump;

    private HcConnectTransport(Process process)
    {
        _process = process;
        _stderrPump = PumpStderrAsync();
    }

    public static IHerdrTransportFactory Factory(HcClientOptions options) => new ConnectFactory(options);

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new HerdrProtocolException($"The 'hc connect' bridge closed its input. {StderrSummary()}", e);
        }
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // already closed
        }

        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // exited meanwhile
            }
        }

        _process.Dispose();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 2000)
                        _stderrTail.Remove(0, _stderrTail.Length - 2000);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the process is gone
        }
    }

    private string StderrSummary()
    {
        lock (_stderrTail)
            return _stderrTail.Length == 0 ? string.Empty : $"stderr: {_stderrTail.ToString().Trim()}";
    }

    private sealed class ConnectFactory(HcClientOptions options) : IHerdrTransportFactory
    {
        public Task<IHerdrLineTransport> OpenAsync(CancellationToken cancellationToken)
        {
            if (options.Session is null)
                throw new InvalidOperationException("The runtime only connects to a named, isolated session, never the default core.");

            var info = new ProcessStartInfo(options.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var argument in new[] { "connect", "--session", options.Session, "--json" })
                info.ArgumentList.Add(argument);

            foreach (var key in info.Environment.Keys.ToList())
            {
                if (options.ScrubbedEnvironmentPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    info.Environment.Remove(key);
            }

            info.Environment["HC_SESSION"] = options.Session;

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new HerdrProtocolException("'hc connect' did not start.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new HcNotFoundException(options.Executable, e);
            }

            return Task.FromResult<IHerdrLineTransport>(new HcConnectTransport(process));
        }
    }
}
