using System.Text.Json;
using System.Threading.Channels;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// A deterministic in-memory Herdr core speaking the JSON-RPC subset the runtime uses, over in-memory line transports.
/// It records every request so tests can prove what was (and was never) asked of the core.
/// </summary>
internal sealed class FakeHerdrCore : IHerdrTransportFactory, IHerdrServerControl
{
    private readonly object _gate = new();
    private readonly List<ServerSide> _connections = [];
    private int _nextTerminal = 1;
    private long _seq;

    public Dictionary<string, FakeTerminal> Terminals { get; } = [];

    /// <summary>A copy taken under the core's lock: safe to enumerate while the runtime is creating or removing terminals on another thread.</summary>
    public IReadOnlyList<FakeTerminal> TerminalSnapshot()
    {
        lock (_gate)
            return Terminals.Values.ToArray();
    }

    public List<(string Method, JsonElement Params, int Connection)> Requests { get; } = [];

    public int ServerStarts { get; private set; }

    public int ServerStops { get; private set; }

    public int ConnectionsOpened => _connections.Count;

    /// <summary>What the "agent" does when text arrives. Default: starts working. Tests script the rest.</summary>
    public Action<FakeTerminal, string>? OnSend { get; set; }

    /// <summary>Test code to run on a freshly launched terminal (for example to make it exit at startup, as a failed resume does). Runs before the status is reported.</summary>
    public Action<FakeTerminal>? OnLaunch { get; set; }

    /// <summary>Initial status a freshly launched terminal reports (null: reports nothing).</summary>
    public string? StatusAtLaunch { get; set; } = "idle";

    public string? StatusKindAtLaunch { get; set; }

    public int Generation { get; set; } = 1;

    /// <summary>When set, <c>terminal.run</c> fails with this Herdr error kind.</summary>
    public string? FailRunWith { get; set; }

    /// <summary>When true, status/turn events are not pushed (the stream is silent); <c>terminal.get</c> still tells the truth.</summary>
    public bool SuppressEvents { get; set; }

    public FakeHerdrCore()
    {
        // someone else's terminal that the runtime must never touch
        Terminals["t99"] = new FakeTerminal("t99", "users-own-shell", new Dictionary<string, string> { ["owner"] = "someone-else" }, ["pwsh"], "C:\\", this)
        {
            Status = "idle",
        };
    }

    public IEnumerable<string> Methods => Requests.Select(r => r.Method);

    public IEnumerable<JsonElement> ParamsOf(string method) => Requests.Where(r => r.Method == method).Select(r => r.Params);

    // ---- IHerdrServerControl ------------------------------------------------------------------------------------------

    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ServerStarts++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ServerStops++;
        return Task.CompletedTask;
    }

    // ---- IHerdrTransportFactory ---------------------------------------------------------------------------------------

    public Task<IHerdrLineTransport> OpenAsync(CancellationToken cancellationToken)
    {
        var toServer = Channel.CreateUnbounded<string>();
        var toClient = Channel.CreateUnbounded<string>();
        var server = new ServerSide(this, toServer, toClient) { Index = _connections.Count };

        lock (_gate)
            _connections.Add(server);

        server.Start();
        return Task.FromResult<IHerdrLineTransport>(new ClientSide(toServer, toClient));
    }

    /// <summary>Simulates the core dropping every connection.</summary>
    public void DropConnections()
    {
        lock (_gate)
        {
            foreach (var connection in _connections)
                connection.Close();
        }
    }

    /// <summary>Drops only connections that subscribed to events (the dedicated event connection).</summary>
    public void DropEventConnections()
    {
        lock (_gate)
        {
            foreach (var connection in _connections.Where(c => c.Subscribed))
                connection.Close();
        }
    }

    // ---- request handling ---------------------------------------------------------------------------------------------

    private object? Handle(ServerSide connection, string method, JsonElement parameters)
    {
        lock (_gate)
            Requests.Add((method, parameters.Clone(), connection.Index));

        switch (method)
        {
            case "hello":
                return new { generation = Generation, version = "0.1.19-fake", core = new { }, framed = false, methods = Array.Empty<string>(), features = Array.Empty<string>() };

            case "events.subscribe":
                connection.Subscribed = true;
                connection.LabelFilter = parameters.TryGetProperty("labels", out var labels)
                    ? labels.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
                    : [];
                return new { seq = _seq, terminals = Array.Empty<object>() };

            case "terminal.run":
                return Run(parameters);

            case "terminal.get":
                return Terminal(parameters).ToJson();

            case "terminal.send":
                return Send(parameters);

            case "terminal.read":
                return new { text = Terminal(parameters).Output, complete = true, how = "screen" };

            case "terminal.kill":
            {
                var terminal = Terminal(parameters);
                terminal.End("killed", exitCode: null);
                return terminal.ToJson();
            }

            case "terminal.rm":
            {
                var terminal = Terminal(parameters);
                if (terminal.Live)
                    throw new FakeRpcError("live", "terminal is still running");

                lock (_gate)
                    Terminals.Remove(terminal.Address);

                return new { removed = true };
            }

            default:
                throw new FakeRpcError("unknown_method", $"the fake core does not implement {method}");
        }
    }

    private object Run(JsonElement parameters)
    {
        if (FailRunWith is { } kind)
            throw new FakeRpcError(kind, "scripted run failure");

        var argv = parameters.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToArray();
        var labels = parameters.TryGetProperty("labels", out var l)
            ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
            : [];

        var address = $"t{_nextTerminal++}";
        var terminal = new FakeTerminal(
            address,
            parameters.TryGetProperty("name", out var n) ? n.GetString() : null,
            labels,
            argv,
            parameters.TryGetProperty("cwd", out var c) ? c.GetString()! : "?",
            this);

        lock (_gate)
            Terminals[address] = terminal;

        OnLaunch?.Invoke(terminal);

        if (StatusAtLaunch is not null && terminal.Live)
            terminal.Report(StatusAtLaunch, StatusKindAtLaunch, null);

        return terminal.ToJson();
    }

    private object Send(JsonElement parameters)
    {
        var terminal = Terminal(parameters);

        if (!terminal.Live)
            throw new FakeRpcError("finished", "terminal has finished");

        if (terminal.Status == "blocked" && !(parameters.TryGetProperty("force", out var force) && force.GetBoolean()))
            throw new FakeRpcError("blocked", "the program is blocked");

        var text = parameters.GetProperty("text").GetString()!;
        var turn = terminal.Turn;
        terminal.Sent.Add(text);

        if (OnSend is { } script)
            script(terminal, text);
        else
            terminal.Report("working", null, null);

        return new { turn, status = terminal.Status, started = true };
    }

    private FakeTerminal Terminal(JsonElement parameters)
    {
        var key = parameters.GetProperty("terminal").GetString()!;

        lock (_gate)
        {
            return Terminals.Values.FirstOrDefault(t => t.Address == key || t.Name == key)
                ?? throw new FakeRpcError("not_found", $"no terminal '{key}'");
        }
    }

    // ---- event fan-out ------------------------------------------------------------------------------------------------

    internal void Publish(FakeTerminal terminal, string type, object data)
    {
        if (SuppressEvents)
            return;

        var message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "event",
            @params = new { seq = Interlocked.Increment(ref _seq), terminal = terminal.Address, type, data },
        });

        List<ServerSide> targets;
        lock (_gate)
            targets = _connections.Where(c => c.Subscribed && c.LabelFilter!.All(f => terminal.Labels.TryGetValue(f.Key, out var v) && v == f.Value)).ToList();

        foreach (var target in targets)
            target.Push(message);
    }

    private sealed class ServerSide(FakeHerdrCore core, Channel<string> inbound, Channel<string> outbound)
    {
        public int Index { get; init; }

        public bool Subscribed { get; set; }

        public Dictionary<string, string>? LabelFilter { get; set; }

        public void Start() => _ = Task.Run(LoopAsync);

        public void Push(string line) => outbound.Writer.TryWrite(line);

        public void Close()
        {
            outbound.Writer.TryComplete();
            inbound.Writer.TryComplete();
        }

        private async Task LoopAsync()
        {
            try
            {
                await foreach (var line in inbound.Reader.ReadAllAsync())
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = root.GetProperty("id").GetInt64();
                    var method = root.GetProperty("method").GetString()!;
                    var parameters = root.TryGetProperty("params", out var p) ? p : default;

                    string reply;
                    try
                    {
                        var result = core.Handle(this, method, parameters);
                        reply = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
                    }
                    catch (FakeRpcError e)
                    {
                        reply = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32000, message = e.Message, data = new { kind = e.Kind } } });
                    }

                    outbound.Writer.TryWrite(reply);
                }
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private sealed class ClientSide(Channel<string> toServer, Channel<string> toClient) : IHerdrLineTransport
    {
        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            if (!toServer.Writer.TryWrite(line))
                throw new IOException("connection closed");

            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await toClient.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            toServer.Writer.TryComplete();
            toClient.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeRpcError(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>One terminal inside the fake core, with the same facts Herdr reports.</summary>
internal sealed class FakeTerminal(
    string address,
    string? name,
    Dictionary<string, string> labels,
    string[] argv,
    string cwd,
    FakeHerdrCore core)
{
    public string Address { get; } = address;

    public string? Name { get; } = name;

    public Dictionary<string, string> Labels { get; } = labels;

    public string[] Argv { get; } = argv;

    public string Cwd { get; } = cwd;

    public bool Live { get; private set; } = true;

    public string? Status { get; set; }

    public string? StatusKind { get; private set; }

    public string? StatusMessage { get; private set; }

    public long Turn { get; private set; }

    public int? ExitCode { get; private set; }

    public string? Reason { get; private set; }

    public List<string> Sent { get; } = [];

    /// <summary>What <c>terminal.read</c> returns.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>The program reports a status word. Leaving <c>working</c> advances the turn, as Herdr does.</summary>
    public void Report(string state, string? kind, string? message)
    {
        var leavingWorking = Status == "working" && state != "working";

        Status = state;
        StatusKind = kind;
        StatusMessage = message;

        core.Publish(this, "status", new { records = 1, reported = true, status = new { app = "claude-code", state, kind, msg = message } });

        if (leavingWorking)
        {
            Turn++;
            core.Publish(this, "turn", new { turn = Turn });
        }
    }

    public void End(string reason, int? exitCode)
    {
        if (!Live)
            return;

        Live = false;
        Reason = reason;
        ExitCode = exitCode;
        core.Publish(this, "finished", new { reason, exit_code = exitCode });
    }

    public object ToJson() => new
    {
        address = Address,
        name = Name,
        state = Live ? "live" : "finished",
        exit_code = ExitCode,
        reason = Reason,
        turn = Turn,
        labels = Labels,
        status = Status is null ? null : new { app = "claude-code", state = Status, kind = StatusKind, msg = StatusMessage },
    };
}
