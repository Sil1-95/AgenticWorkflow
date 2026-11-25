using System.Diagnostics;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// Which stretch of a run is being driven: the first, the one after human input (the human requested changes, or answered the agent's question), or a retry of
/// that one after a runtime failure (<c>aw continue</c>).
/// </summary>
internal enum RunStretch
{
    Initial,
    AfterHumanInput,
    RecoveredContinuation,
}

/// <summary>
/// What one agent-driven stretch of a run needs: a durable run, its project and definition, which stretch it is, and the workspace state its scope is judged
/// against (<see cref="Baseline"/>: the state before the agent worked; for a retry, the one recorded when the changes were requested).
/// </summary>
internal sealed record RunExecution(
    TaskId RunId,
    ResolvedProject Project,
    DirectTaskDefinition Definition,
    WorkflowTaskService Tasks,
    RunStore Store,
    ImplementationState Baseline,
    HumanPresence Presence,
    RunStretch Stretch,
    RunTiming Timing);

/// <summary>
/// A stretch of work that is ready to run: everything that could be refused was refused, any human decision it follows is durably recorded, and the run's live lock is held.
/// Disposing it releases the lock, so whoever holds one owns the run and nobody else can drive it. The CLI runs it in its own process; the controller adopts it and
/// runs it in the background.
/// </summary>
internal sealed class PreparedStretch(RunExecution execution, IDisposable liveLock) : IDisposable
{
    public RunExecution Execution { get; } = execution;

    public TaskId RunId => Execution.RunId;

    public void Dispose() => liveLock.Dispose();
}

/// <summary>
/// Drives one stretch of agent work over the existing <see cref="DirectPathRunner"/> and reports how it ended. Shared by <c>aw run</c> (the first stretch),
/// <c>aw request-changes</c> and <c>aw answer</c> (the stretch after a human asked for changes, or answered the agent's question) and <c>aw continue</c> (a retry of that stretch after a runtime failure): composition
/// and reporting only; there is no loop, verification or completion rule here.
/// </summary>
internal sealed class RunExecutor(CliServices services)
{
    public async Task<int> ExecuteAsync(RunExecution execution, CancellationToken cancellationToken)
    {
        var progress = new RunProgress(services.Out);
        var waitForHuman = PersonIsAttending(execution.Presence);
        var clock = Stopwatch.StartNew();
        DirectRunResult result;

        // The agent is started from what the run recorded when its roles were bound, not from the project's configuration as it is now.
        var bindings = RunBindings.From((await execution.Tasks.LoadAsync(execution.RunId, cancellationToken).ConfigureAwait(false)).Task);

        execution.Timing.SetupDone();

        // Disposed after the runtime, whatever happens: it removes the native-wait annotation and stops any desktop notification still in flight.
        await using (var interactions = new CliNativeInteractionObserver(execution.RunId, progress, execution.Store, services.Clock, services.Notifier))
        await using (var host = services.RuntimeFactory.Create(execution.Project, execution.RunId, bindings))
        {
            var runner = new DirectPathRunner(
                execution.Tasks,
                new ProgressAgentRuntime(host.Runtime, progress, waitForHuman),
                services.Workspace,
                new ProgressVerificationRunner(services.Verifier, progress),
                new DirectRunOptions { WaitForHuman = waitForHuman },
                host.Evidence,
                host.Conversations is { } conversations
                    ? new AgentConversationSupport(new ProgressConversationRuntime(conversations, progress), new RunConversationStore(execution.Store, services.Clock))
                    : null,
                host.Signals is { } signals ? new AgentClarificationSupport(signals, new ContinuationBaselineSink(new ContinuationBaselineStore(execution.Store, services.Clock))) : null,
                interactions);

            result = execution.Stretch switch
            {
                RunStretch.Initial => await runner.RunAsync(execution.RunId, execution.Definition, cancellationToken).ConfigureAwait(false),
                RunStretch.AfterHumanInput => await runner.ContinueAfterHumanInputAsync(execution.RunId, execution.Definition, execution.Baseline, cancellationToken).ConfigureAwait(false),
                _ => await runner.RecoverHumanInputAsync(execution.RunId, execution.Definition, execution.Baseline, cancellationToken).ConfigureAwait(false),
            };
        }

        clock.Stop();
        execution.Timing.RunDone();

        var interrupted = cancellationToken.IsCancellationRequested;
        var outcome = RunOutcomeClassifier.Classify(result, interrupted);
        var exitCode = ExitCodes.For(outcome);

        var changed = await ChangedPathsAsync(execution.Project.Workspace, execution.Baseline).ConfigureAwait(false);

        services.Out.WriteLine();
        services.Out.Write(RunReportRenderer.Render(new RunReport(outcome, result, execution.RunId, execution.Project.Workspace, clock.Elapsed, changed)));

        TryRecordOutcome(execution.Store, execution.RunId, outcome, exitCode, result, clock.Elapsed);
        execution.Timing.ReportDone();

        if (services.GetEnvironmentVariable(CliEnvironment.TimingVariable) == "1")
            services.Error.WriteLine(execution.Timing.Describe());

        return exitCode;
    }

    /// <summary>
    /// Whether a person is expected to act in the agent's own session (its native permission prompt, through Herdr) while this run waits for it. aw never
    /// asks the question itself; this only decides whether the run waits for that person or stops with exit 15. Told so (<c>--interactive</c>), or attached
    /// to a terminal, a person is assumed; <c>--non-interactive</c> says nobody is. Unrelated to the ACCEPT gate, which is answered later, from any process.
    /// </summary>
    private bool PersonIsAttending(HumanPresence presence)
        => presence == HumanPresence.Interactive || (presence == HumanPresence.Auto && services.IsTerminal);

    /// <summary>The paths this stretch changed: the difference between the working tree before and after. Best effort; it never fails the run.</summary>
    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string workspace, ImplementationState baseline)
    {
        try
        {
            var after = await services.Workspace.CaptureAsync(workspace, CancellationToken.None).ConfigureAwait(false);

            return
            [
                .. baseline.PathDigests.Keys
                    .Union(after.PathDigests.Keys, StringComparer.Ordinal)
                    .Where(path => baseline.PathDigests.GetValueOrDefault(path) != after.PathDigests.GetValueOrDefault(path))
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>The outcome file only annotates the run for <c>aw status</c>; failing to write it must not change what the run reports.</summary>
    private void TryRecordOutcome(RunStore store, TaskId runId, RunOutcome outcome, int exitCode, DirectRunResult result, TimeSpan elapsed)
    {
        try
        {
            store.WriteOutcome(
                runId,
                new RunOutcomeRecord(RunOutcomeRecord.CurrentVersion, outcome.ToString(), exitCode, result.Reason, services.Clock.UtcNow, Math.Round(elapsed.TotalSeconds, 1))
                {
                    Continuation = RunReportRenderer.DescribeContinuation(result.Continuation),
                });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            services.Error.WriteLine($"warning: the run outcome could not be recorded ({e.Message}).");
        }
    }
}

/// <summary>Where the process's own time went, for the optional timing diagnostic (<c>AGENTICWORKFLOW_TIMING=1</c>).</summary>
internal sealed class RunTiming
{
    private readonly DateTime _begunAt = DateTime.UtcNow;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _setup;
    private TimeSpan _run;
    private TimeSpan _report;

    public void SetupDone() => _setup = _clock.Elapsed;

    public void RunDone() => _run = _clock.Elapsed - _setup;

    public void ReportDone() => _report = _clock.Elapsed - _setup - _run;

    public string Describe()
    {
        using var self = Process.GetCurrentProcess();
        var startup = _begunAt - self.StartTime.ToUniversalTime();

        return $"timing: process start to command {Format.Duration(startup)}, setup {Format.Duration(_setup)}, run {Format.Duration(_run)}, reporting {Format.Duration(_report)}";
    }
}
