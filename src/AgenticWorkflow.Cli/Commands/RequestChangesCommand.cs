using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw request-changes &lt;run-id&gt; --feedback "..." [--gate &lt;gate-id&gt;]</c>: the person's explicit decision NOT to accept the verified result. The gate is closed
/// as not approved (it can never be accepted afterwards), the run returns to RUNNING under a new <c>human_request_changes</c> semantic revision, and the work
/// continues in the agent's own native conversation when it can be resumed (the agent process ended when the gate opened, the conversation did not), sent only
/// the feedback; only when it cannot (an explicit, reported fallback) does it start a fresh session that is sent the original task as well. No transcript is
/// ever replayed. The feedback is part of the command, never inferred from text anywhere else.
/// <para>
/// Everything that can be refused is refused before the decision is recorded (usage, configuration changed since the run, prerequisites, a run that is
/// live in another process), so a refusal never leaves the run RUNNING with nobody working on it. If the work then cannot run because the runtime failed, the
/// run is BLOCKED (not RUNNING) and <c>aw continue</c> retries the same work without a second decision.
/// </para>
/// </summary>
internal sealed class RequestChangesCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(RequestChangesRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var stretch = await PrepareAsync(request, services.Out, cancellationToken).ConfigureAwait(false);

            // ---- the work continues through the same direct path ---------------------------------------------------------------
            return await new RunExecutor(services).ExecuteAsync(stretch.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GateCommandErrors.Handles(e))
        {
            return GateCommandErrors.Report(services, "REQUEST CHANGES", e);
        }
    }

    /// <summary>
    /// Refuses what can be refused, records the decision, and returns the work it asks for with the run's live lock held. Throws the refusals of <see cref="GateCommandErrors"/>;
    /// nothing was recorded when it does.
    /// </summary>
    public async Task<PreparedStretch> PrepareAsync(RequestChangesRequest request, TextWriter banner, CancellationToken cancellationToken)
    {
        var timing = new RunTiming();
        var runId = CommandLine.ParseRunId(request.RunId);
        var gateId = CommandLine.ParseGateId(request.Gate);
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);
        var snapshot = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        // ---- refuse what can be refused before anything is recorded ----------------------------------------------------------
        var project = ContinuationPreflight.ResolveProject(services, store, runId);
        var baseline = await ContinuationPreflight.CaptureWorkspaceAsync(services, project, cancellationToken).ConfigureAwait(false);
        ContinuationPreflight.RequireUsableConversation(services, store, runId, project, snapshot.Task);
        ContinuationPreflight.RequireLaunchable(services, snapshot.Task);

        // One process continues a run at a time; a second request-changes (or a live run) fails here and records nothing.
        var liveLock = TryAcquireLiveLock(store, runId);

        try
        {
            // The workspace this request's work is judged against, kept before the decision so that a retry (aw continue) after a runtime failure judges the
            // whole request against it instead of a baseline that already contains the interrupted attempt's changes. If it cannot be kept, no decision is recorded.
            if (snapshot.Task.PendingGate is { } pending && (gateId is null || gateId == pending.Id))
                RecordBaseline(store, runId, pending.Id, baseline);

            // ---- the decision: gate closed as not approved, RUNNING, new semantic revision (one atomic append) -----------------
            var outcome = await new AcceptGateService(tasks, services.Workspace)
                .RequestChangesAsync(runId, gateId, request.Feedback, cancellationToken)
                .ConfigureAwait(false);

            banner.WriteLine($"Run: {runId}");
            banner.WriteLine($"Gate {outcome.Gate.Id} closed: changes requested (not approved; it can never be accepted).");
            banner.WriteLine($"State: {Format.State(outcome.Change.Task.WorkflowState)}");
            banner.WriteLine($"Feedback: {Format.Printable(request.Feedback)}");
            banner.WriteLine("Continuing the work: the agent's native conversation is resumed when it can be (the report below says what happened).");
            banner.WriteLine();

            return new PreparedStretch(
                new RunExecution(runId, project, project.ToDefinition(snapshot.Task), tasks, store, baseline, request.Presence, RunStretch.AfterHumanInput, timing),
                liveLock);
        }
        catch
        {
            liveLock.Dispose();

            throw;
        }
    }

    private void RecordBaseline(RunStore store, AgenticWorkflow.Core.Identity.TaskId runId, AgenticWorkflow.Core.Identity.GateId gate, ImplementationState baseline)
    {
        try
        {
            new ContinuationBaselineStore(store, services.Clock).Save(runId, gate, baseline);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"The workspace baseline of this request could not be recorded ({e.Message}); nothing was decided, so it can be retried.");
        }
    }

    private static IDisposable TryAcquireLiveLock(RunStore store, AgenticWorkflow.Core.Identity.TaskId runId)
    {
        try
        {
            return store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("This run is still live in another process; wait for it to finish, then request changes.");
        }
    }
}
