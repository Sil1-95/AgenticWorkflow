using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw abort &lt;run-id&gt;</c>: the person's explicit cancellation of a run that cannot continue: BLOCKED (the usual result of exit 15, or a continuation of
/// requested changes that failed and that <c>aw continue</c> could retry; once cancelled, that retry is refused), left
/// AWAITING_HUMAN by a process that died, or AWAITING_HUMAN at the ACCEPT gate (the person chooses to stop instead of accepting or requesting changes; the gate is
/// closed as not approved), or AWAITING_HUMAN at a CLARIFY question (the person chooses to stop instead of answering; the question is closed unanswered), or RUNNING with nobody working on it (its owner died: the lock proves it). The workflow becomes CANCELLED. It answers nothing the agent asked and allows or denies nothing.
/// </summary>
internal sealed class AbortCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(AbortRequest request, CancellationToken cancellationToken)
    {
        var runId = CommandLine.ParseRunId(request.RunId);

        try
        {
            var gate = await RecordAsync(runId, cancellationToken).ConfigureAwait(false);

            services.Out.WriteLine($"Run: {runId}");
            services.Out.WriteLine(gate is null
                ? "The run was ABORTED; the workflow is CANCELLED. Nothing the agent was waiting for was allowed or denied."
                : gate.Type switch
                {
                    AgenticWorkflow.Core.Gates.GateType.Clarify => $"The run was ABORTED; question {gate.Id} was closed unanswered and the workflow is CANCELLED.",
                    AgenticWorkflow.Core.Gates.GateType.What => $"The run was ABORTED; WHAT proposal {gate.Id} was closed without approval and the workflow is CANCELLED.",
                    AgenticWorkflow.Core.Gates.GateType.How => $"The run was ABORTED; HOW proposal {gate.Id} was closed without approval and the workflow is CANCELLED.",
                    _ => $"The run was ABORTED; gate {gate.Id} was closed without acceptance and the workflow is CANCELLED.",
                });

            return ExitCodes.Aborted;
        }
        catch (GateRefusedException e)
        {
            return GateCommandErrors.Report(services, "ABORT", e);
        }
        catch (RunStateException e)
        {
            services.Error.WriteLine($"{(e.Problem == RunStateProblem.NotFound ? "RUN NOT FOUND" : "RUN STATE UNREADABLE")}: {e.Message}");

            return e.Problem == RunStateProblem.NotFound ? ExitCodes.RunNotFound : ExitCodes.RunStateUnreadable;
        }
        catch (Exception e) when (e is EventStoreException or DomainException or Microsoft.Data.Sqlite.SqliteException)
        {
            services.Error.WriteLine($"RUN STATE UNREADABLE: {e.Message}");
            services.Error.WriteLine("Nothing was repaired or guessed; the run's files were left untouched.");

            return ExitCodes.RunStateUnreadable;
        }
    }

    /// <summary>
    /// Cancels the run and returns the gate that was closed with it (null when there was none). Holds the run's lock while it does, so a run somebody is working on is refused,
    /// and records the outcome. Throws <see cref="UsageException"/> for a run that is live or not in a state that can be aborted; nothing was recorded then.
    /// <paramref name="expectedGate"/>, when given, must be the gate the run is waiting at.
    /// </summary>
    public async Task<AgenticWorkflow.Core.Gates.Gate?> RecordAsync(TaskId runId, CancellationToken cancellationToken, AgenticWorkflow.Core.Identity.GateId? expectedGate = null)
    {
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);

        // The lock is the proof that nobody is working on the run: it is held by the process that is, and released by the OS when that process dies.
        using var liveLock = TryAcquireLiveLock(store, runId);

        var snapshot = await tasks.FindAsync(runId, cancellationToken).ConfigureAwait(false)
                       ?? throw new RunStateException(RunStateProblem.Unreadable, $"The run's event stream holds no history for '{runId}'.");

        if (snapshot.Task.WorkflowState is not (WorkflowState.Blocked or WorkflowState.AwaitingHuman or WorkflowState.Running))
            throw new UsageException($"Run {runId} is {Format.State(snapshot.Task.WorkflowState)}, not BLOCKED or AWAITING_HUMAN; there is nothing to abort.");

        if (expectedGate is { } expected && snapshot.Task.PendingGate?.Id != expected)
            throw new UsageException($"Run {runId} is not waiting at gate {expected}; nothing was aborted.");

        var gate = snapshot.Task.PendingGate;

        if (gate is not null)
        {
            // A workflow-owned gate is closed by its own decision (cancel), never by a plain state change; the gate stays in history as not approved.
            await new AcceptGateService(tasks, services.Workspace).CancelAsync(runId, gate.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var reason = snapshot.Task.WorkflowState == WorkflowState.Running
                ? "Cancelled by the person with 'aw abort' after the process that was working on the run ended."
                : "Cancelled by the person with 'aw abort' after the run's agent session ended.";

            await tasks.ChangeWorkflowStateAsync(snapshot, WorkflowState.Cancelled, reason, cancellationToken).ConfigureAwait(false);
        }

        TryRecordOutcome(store, runId);

        return gate;
    }

    private static IDisposable TryAcquireLiveLock(RunStore store, TaskId runId)
    {
        try
        {
            return store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("This run is still live in another process: answer the agent in its own session, or press Ctrl+C in that process, not 'aw abort'.");
        }
    }

    private void TryRecordOutcome(RunStore store, TaskId runId)
    {
        try
        {
            store.WriteOutcome(runId, new RunOutcomeRecord(
                RunOutcomeRecord.CurrentVersion,
                nameof(RunOutcome.Aborted),
                ExitCodes.Aborted,
                "aborted: aborted with 'aw abort'",
                services.Clock.UtcNow,
                0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            services.Error.WriteLine($"warning: the run outcome could not be recorded ({e.Message}).");
        }
    }
}
