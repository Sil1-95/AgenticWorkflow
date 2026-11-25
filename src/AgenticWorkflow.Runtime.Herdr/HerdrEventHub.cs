using System.Collections.Concurrent;
using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>What the event stream has said about one terminal so far. Immutable.</summary>
internal sealed record TerminalObservation(
    long Version,
    bool Finished,
    string? FinishedReason,
    int? ExitCode,
    string? LastStatus,
    IReadOnlyList<(long Version, string State)> StatusHistory)
{
    /// <summary>True if the program reported <paramref name="state"/> in an event after <paramref name="version"/>.</summary>
    public bool SawStatusSince(long version, string state)
        => StatusHistory.Any(entry => entry.Version > version && entry.State == state);
}

/// <summary>
/// Follows the structured event stream of one terminal. Events only wake waiters and record facts (status words
/// seen, finished); decisions are taken against the authoritative <c>terminal.get</c> state so a missed or
/// reordered event can delay a decision but never change it.
/// </summary>
internal sealed class TerminalWatch
{
    private readonly object _gate = new();
    private readonly List<(long Version, string State)> _statuses = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _version;
    private bool _finished;
    private string? _finishedReason;
    private int? _exitCode;
    private string? _lastStatus;

    public TerminalObservation Observe()
    {
        lock (_gate)
            return new TerminalObservation(_version, _finished, _finishedReason, _exitCode, _lastStatus, _statuses.ToArray());
    }

    public void Apply(string type, JsonElement data)
    {
        lock (_gate)
        {
            switch (type)
            {
                case "status":
                    if (data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("status", out var status)
                        && status.ValueKind == JsonValueKind.Object
                        && status.GetStringOrNull("state") is { } state)
                    {
                        _lastStatus = state;
                        _statuses.Add((_version + 1, state));
                    }

                    break;

                case "finished":
                    _finished = true;
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        _finishedReason = data.GetStringOrNull("reason");
                        _exitCode = data.GetIntOrNull("exit_code");
                    }

                    break;
            }

            _version++;
            Signal();
        }
    }

    /// <summary>A snapshot arrived after the subscriber fell behind: facts may have been lost, so wake everyone to re-read.</summary>
    public void Resync()
    {
        lock (_gate)
        {
            _version++;
            Signal();
        }
    }

    /// <summary>Returns when something happened after <paramref name="sinceVersion"/>, or after <paramref name="maxWait"/>.</summary>
    public async Task WaitForChangeAsync(long sinceVersion, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        Task pending;
        lock (_gate)
        {
            if (_version > sinceVersion)
                return;

            pending = _changed.Task;
        }

        try
        {
            await pending.WaitAsync(maxWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // a bounded wait is a normal check-in
        }
    }

    private void Signal()
    {
        var current = _changed;
        _changed = NewSignal();
        current.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Routes <c>event</c> notifications from the dedicated event connection to per-terminal watches.</summary>
internal sealed class HerdrEventHub
{
    private readonly ConcurrentDictionary<string, TerminalWatch> _watches = new(StringComparer.Ordinal);

    /// <summary>Set when the event connection is lost: every waiter must stop trusting the stream.</summary>
    public Exception? Lost { get; private set; }

    public TerminalWatch Watch(string address) => _watches.GetOrAdd(address, _ => new TerminalWatch());

    public void Forget(string address) => _watches.TryRemove(address, out _);

    public void Handle(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "event" when parameters.ValueKind == JsonValueKind.Object:
                if (parameters.GetStringOrNull("terminal") is { } terminal
                    && parameters.GetStringOrNull("type") is { } type)
                {
                    var data = parameters.TryGetProperty("data", out var d) ? d : default;
                    Watch(terminal).Apply(type, data);
                }

                break;

            case "missed":
                foreach (var watch in _watches.Values)
                    watch.Resync();

                break;
        }
    }

    public void MarkLost(Exception reason)
    {
        Lost = reason;

        foreach (var watch in _watches.Values)
            watch.Resync();
    }
}
