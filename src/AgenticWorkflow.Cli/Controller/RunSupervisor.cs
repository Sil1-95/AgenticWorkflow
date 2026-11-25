using System.Text;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// The controller's ownership of agent work. A <see cref="PreparedStretch"/> handed to <see cref="Adopt"/> is a stretch whose decision is already durably recorded and whose
/// run's live lock is held; from then on the supervisor runs it in the background, holds the lock until it ends, and counts it as active (a controller with active work never
/// idles out). Nothing in here decides anything about a run: the stretch is the existing <see cref="RunExecutor"/> over the existing runner, so what the work does, and what
/// it records, is exactly what it was when a CLI process ran it.
/// <para>
/// While a stretch runs it leaves a <see cref="SupervisionRecord"/> naming the isolated agent host it started, written before the host starts. An orderly end removes it. If this
/// process dies instead, the OS releases the run's lock, the run is durably RUNNING with nobody working on it, and the record is what lets the next controller end the host the
/// dead one left behind and resume without a second agent (see <see cref="RunReconciler"/>).
/// </para>
/// <para>
/// Nothing here knows which agent it supervises: the runtime is whatever the <see cref="IAgentRuntimeFactory"/> creates.
/// </para>
/// </summary>
internal sealed class RunSupervisor(CliServices services, RunStore store, Guid controllerInstance, Action<string> log) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TaskId, Entry> _active = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _abandoned;

    /// <summary>Raised, with the run, when a stretch ended without a result (an unexpected failure) so the run can be put into a defined state. Set once by the owner.</summary>
    public Func<TaskId, Task>? Stranded { get; set; }

    /// <summary>How many stretches are supervised now, from the moment they are adopted until their lock is released.</summary>
    public int ActiveRuns
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }

    public bool Owns(TaskId runId)
    {
        lock (_gate)
            return _active.ContainsKey(runId);
    }

    /// <summary>
    /// Waits, at most <paramref name="grace"/>, for a stretch that has already recorded where it stopped to finish winding down (its report is written, its agent host stopped, its lock
    /// released). A run is durably AWAITING_HUMAN or BLOCKED a moment before its owner lets go of it, and a person who reacts to that state at once must not be refused for it.
    /// True when the run is not supervised (any longer).
    /// </summary>
    public async Task<bool> WaitForReleaseAsync(TaskId runId, TimeSpan grace, CancellationToken cancellationToken)
    {
        Entry? entry;

        lock (_gate)
            _active.TryGetValue(runId, out entry);

        if (entry is null)
            return true;

        try
        {
            await entry.Released.Task.WaitAsync(grace, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Takes over a prepared stretch, which it will run in the background and whose lock it releases when the stretch ends (or when adopting fails). <paramref name="attempts"/> is how
    /// many times the stretch has now been started by successive controllers (1 for the first).
    /// </summary>
    public void Adopt(PreparedStretch stretch, int attempts)
    {
        ArgumentNullException.ThrowIfNull(stretch);

        var entry = new Entry(stretch);

        try
        {
            lock (_gate)
            {
                if (_stopping.IsCancellationRequested)
                    throw new InvalidOperationException("The supervisor is stopping.");

                if (!_active.TryAdd(stretch.RunId, entry))
                    throw new InvalidOperationException("The run is already supervised.");
            }

            store.WriteSupervision(stretch.RunId, new SupervisionRecord(
                SupervisionRecord.CurrentVersion,
                stretch.RunId.ToString(),
                controllerInstance.ToString(),
                Environment.ProcessId,
                services.Clock.UtcNow,
                stretch.Execution.Stretch.ToString(),
                [],
                attempts));
        }
        catch
        {
            lock (_gate)
                _active.Remove(stretch.RunId);

            stretch.Dispose();

            throw;
        }

        // Started only after the registration above, so the run is counted as supervised from the moment this call returns.
        entry.Task = Task.Run(() => RunAsync(entry));
    }

    /// <summary>Asks every stretch to stop (its agent host is stopped as it ends) and waits for them, at most <paramref name="grace"/>. What they leave is durable and is resumed by the next controller.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        // A process that "died" (see Abandon) stops nothing on its way out: it is gone.
        if (_abandoned)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);

        Task[] running;

        lock (_gate)
            running = [.. _active.Values.Select(entry => entry.Task).OfType<Task>()];

        if (running.Length > 0)
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(grace)).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam: behave as if the controller process had died right now. The run locks are released (as the OS does for a dead process) and nothing is cleaned up: no supervision
    /// record is removed, no agent host is stopped, and what the stretches would still write is dropped.
    /// </summary>
    internal void Abandon()
    {
        List<Entry> entries;

        lock (_gate)
        {
            _abandoned = true;
            entries = [.. _active.Values];
            _active.Clear();
        }

        foreach (var entry in entries)
            entry.Stretch.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task RunAsync(Entry entry)
    {
        var stretch = entry.Stretch;
        var runId = stretch.RunId;
        var completed = false;

        try
        {
            var quiet = services.ForSupervision(
                new LogWriter(line => log($"run {runId}: {line}")),
                runtimeFactory: new RecordingRuntimeFactory(services.RuntimeFactory, (id, session) => RecordSession(id, session)));

            var exit = await new RunExecutor(quiet).ExecuteAsync(stretch.Execution, _stopping.Token).ConfigureAwait(false);

            completed = true;

            if (!_abandoned)
                log($"run {runId}: stretch ended (exit {exit})");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            completed = true;
        }
        catch (Exception e)
        {
            // Never the message: it can carry paths and agent output. The run is left as it is, and handed to the reconciler to be put into a defined state.
            if (!_abandoned)
                log($"run {runId}: stretch failed unexpectedly ({e.GetType().Name})");
        }
        finally
        {
            if (!_abandoned)
            {
                if (completed)
                    TryClearSupervision(runId);

                lock (_gate)
                    _active.Remove(runId);

                stretch.Dispose();
                entry.Released.TrySetResult();
            }
        }

        if (!completed && !_abandoned && !_stopping.IsCancellationRequested && Stranded is { } stranded)
        {
            try
            {
                await stranded(runId).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log($"run {runId}: could not be put into a defined state ({e.GetType().Name})");
            }
        }
    }

    private void RecordSession(TaskId runId, string session)
    {
        try
        {
            var (record, _) = store.ReadSupervision(runId);

            if (record is not null && !_abandoned)
                store.WriteSupervision(runId, record.WithSession(session));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without the record a dead controller's host could not be found again, so the stretch must not start.
            throw new ConfigurationException($"The supervision record of run {runId} could not be written ({e.Message}); the agent was not started.");
        }
    }

    private void TryClearSupervision(TaskId runId)
    {
        try
        {
            store.ClearSupervision(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"run {runId}: the supervision record could not be removed ({e.GetType().Name})");
        }
    }

    private sealed class Entry(PreparedStretch stretch)
    {
        public PreparedStretch Stretch { get; } = stretch;

        public Task? Task { get; set; }

        /// <summary>Set when the stretch is over and its lock is released.</summary>
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Writes the warnings of a supervised stretch to the controller's log, one bounded line at a time.</summary>
    private sealed class LogWriter(Action<string> write) : TextWriter
    {
        private const int MaxLine = 300;

        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();

                return;
            }

            if (value != '\r' && _line.Length < MaxLine)
                _line.Append(value);
        }

        public override void Flush()
        {
            if (_line.Length == 0)
                return;

            write(_line.ToString());
            _line.Clear();
        }
    }
}

/// <summary>Tells a supervisor which isolated agent host a run's stretch is about to start, before it starts. Everything else is the wrapped factory's.</summary>
internal sealed class RecordingRuntimeFactory(IAgentRuntimeFactory inner, Action<TaskId, string> record) : IAgentRuntimeFactory
{
    public IReadOnlyList<Prerequisite> CheckPrerequisites(ResolvedProject project) => inner.CheckPrerequisites(project);

    public IReadOnlyList<string> ValidateExecution(ProfileId profile, ExecutionConfiguration execution) => inner.ValidateExecution(profile, execution);

    public AgentRuntimeHost Create(ResolvedProject project, TaskId runId, RunBindings bindings)
    {
        var host = inner.Create(project, runId, bindings);

        // Nothing has been started yet: the name is durable before there is anything to lose.
        if (host.OwnedSession is { } session)
            record(runId, session);

        return host;
    }

    public Task<bool> StopOrphanedSessionAsync(string session, CancellationToken cancellationToken) => inner.StopOrphanedSessionAsync(session, cancellationToken);
}
