using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>What an earlier controller left behind for a run that its owner no longer works on: the isolated agent hosts it recorded before starting them.</summary>
internal static class OrphanedSessions
{
    /// <summary>
    /// Ends every isolated agent host the run's supervision record names and, when all are confirmed gone, removes the record. False when any could not be confirmed stopped, in which
    /// case the record stays (the next attempt tries again) and the run must not get a second agent. True, trivially, when there is no record.
    /// </summary>
    public static async Task<bool> ReapAsync(IAgentRuntimeFactory factory, RunStore store, TaskId runId, CancellationToken cancellationToken)
    {
        var (record, _) = store.ReadSupervision(runId);

        if (record is null)
        {
            // A damaged record cannot be read, but it still means a stretch did not end: nothing can be proven about what it left.
            return !File.Exists(Path.Combine(store.RunDirectory(runId), RunStore.SupervisionFileName));
        }

        var allGone = true;

        foreach (var session in record.Sessions)
        {
            try
            {
                allGone &= await factory.StopOrphanedSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                allGone = false;
            }
        }

        if (allGone)
            store.ClearSupervision(runId);

        return allGone;
    }
}

/// <summary>What <see cref="RunReconciler"/> did for one run.</summary>
internal enum ReconcileOutcome
{
    /// <summary>The run needed nothing, or is not this controller's to touch (somebody holds it, it is supervised, it has ended, it waits for a person).</summary>
    Untouched,

    /// <summary>An interrupted stretch was resumed under this controller.</summary>
    Resumed,

    /// <summary>The run was left BLOCKED in a state <c>continue_task</c> can retry (the interruption was recoverable, but resuming now was not safe or possible).</summary>
    BlockedRecoverable,

    /// <summary>The run was left BLOCKED because nothing recorded says it can be resumed safely: it needs a person (<c>abort</c>, or inspection).</summary>
    BlockedUnresumable,

    /// <summary>Only the leftovers of an orderly-looking end were cleaned up (an agent host recorded for a run that is no longer RUNNING).</summary>
    CleanedUp,
}

/// <summary>
/// Makes the durable state of runs honest after a controller died (or after a stretch failed in a way nothing could report). A run whose stretch was interrupted is durably RUNNING
/// with nobody working on it; this decides, from durable evidence only, which of these it is:
/// <list type="bullet">
/// <item><description><b>Resumable</b>: RUNNING with human input (a decision, an answer, a recorded reclassification) still being worked on, whose baseline, project and conversation record can be trusted, and whose
/// recorded agent host is confirmed gone. The stretch is resumed under the same rules as <c>aw continue</c>: no new decision, no new revision, no second agent.</description></item>
/// <item><description><b>Recoverable but not now</b>: the same work, but resuming is not safe or possible this moment (an agent host that cannot be confirmed stopped, a missing prerequisite, a changed configuration, a stretch that keeps
/// dying). The run is recorded BLOCKED in exactly the way a failed continuation is, so <c>continue_task</c> (or <c>aw continue</c>) can retry it later.</description></item>
/// <item><description><b>Ambiguous</b>: RUNNING with no human input pending (a first stretch), or with a baseline that cannot be used. Nothing durable says what the agent was asked to do or what it may have changed, so the run is
/// recorded BLOCKED with a reason that is not a recoverable one: it is for a person to abort or inspect. It is never restarted from a guess.</description></item>
/// </list>
/// Everything else (a run waiting for a person, ended, or held by another process) is left exactly as it is. Every write is one reasoned state change made over the state that was
/// read, so doing this twice, or racing another process, can never record anything twice.
/// </summary>
internal sealed class RunReconciler(CliServices services, RunStore store, RunSupervisor supervisor, Action<string> log)
{
    private const string UnresumableReason =
        "The controller that was working on this run ended, and nothing recorded says what the agent had been asked or what it changed, so the run was not resumed. "
        + "It is not continued from a guess: inspect the workspace, then 'abort' the run and start again.";

    /// <summary>Reconciles every run in the state root. A run that cannot be read is skipped and logged; one failure never stops the rest.</summary>
    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (var runId in store.ListRunIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await ReconcileAsync(runId, cancellationToken).ConfigureAwait(false);

                if (outcome != ReconcileOutcome.Untouched)
                    log($"run {runId}: reconciled ({outcome})");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log($"run {runId}: could not be reconciled ({e.GetType().Name})");
            }
        }
    }

    public async Task<ReconcileOutcome> ReconcileAsync(TaskId runId, CancellationToken cancellationToken)
    {
        if (supervisor.Owns(runId) || store.IsLive(runId))
            return ReconcileOutcome.Untouched;

        WorkflowTaskService tasks;
        WorkflowTask task;

        try
        {
            tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);
            task = (await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false)).Task;
        }
        catch (Exception e) when (e is RunStateException or EventStoreException or DomainException or Microsoft.Data.Sqlite.SqliteException or TaskNotFoundException)
        {
            return ReconcileOutcome.Untouched;
        }

        var (record, _) = store.ReadSupervision(runId);
        var hasRecord = record is not null || File.Exists(Path.Combine(store.RunDirectory(runId), RunStore.SupervisionFileName));

        if (task.WorkflowState != WorkflowState.Running)
        {
            // A run waiting for a person in the agent's own interface (a native wait, not a workflow gate) is waiting in a session the dead controller held.
            var nativeWait = task.WorkflowState == WorkflowState.AwaitingHuman ? store.ReadNativeInteraction(runId).Record : null;

            // The run is not (or no longer) RUNNING, so nothing is resumed; only what the dead stretch left behind is ended.
            if (!hasRecord && nativeWait is null)
                return ReconcileOutcome.Untouched;

            var reaped = await OrphanedSessions.ReapAsync(services.RuntimeFactory, store, runId, cancellationToken).ConfigureAwait(false);

            if (!reaped)
                return ReconcileOutcome.Untouched;

            // The wait can no longer be answered, so the run says so (BLOCKED, abort), exactly as when its session ends in an orderly way, instead of asking for a person who has nowhere to go.
            if (nativeWait is not null
                && await BlockAsync(runId, tasks, NativeInteractionBlock.ControllerEndedReason(Bounded(nativeWait.Description)), ReconcileOutcome.BlockedUnresumable, WorkflowState.AwaitingHuman, cancellationToken).ConfigureAwait(false) == ReconcileOutcome.BlockedUnresumable)
            {
                store.ClearNativeInteraction(runId);

                return ReconcileOutcome.BlockedUnresumable;
            }

            return hasRecord ? ReconcileOutcome.CleanedUp : ReconcileOutcome.Untouched;
        }

        var sessionsGone = await OrphanedSessions.ReapAsync(services.RuntimeFactory, store, runId, cancellationToken).ConfigureAwait(false);

        if (!RecoverableContinuation.IsWorkInProgress(task))
            return await BlockAsync(runId, tasks, UnresumableReason, ReconcileOutcome.BlockedUnresumable, cancellationToken).ConfigureAwait(false);

        var attempts = record?.Attempts ?? 0;

        if (!sessionsGone)
        {
            return await BlockAsync(
                runId,
                tasks,
                RecoverableContinuation.BlockedReason("controller interruption", "the controller that was working on this run ended and the agent session it started could not be confirmed stopped.", sessionConfirmedStopped: false),
                ReconcileOutcome.BlockedRecoverable,
                cancellationToken).ConfigureAwait(false);
        }

        if (attempts >= SupervisionRecord.MaxAutomaticAttempts)
        {
            return await BlockAsync(
                runId,
                tasks,
                RecoverableContinuation.BlockedReason("controller interruption", $"the controller ended {attempts} times while working on this stretch, so it is no longer resumed automatically.", sessionConfirmedStopped: true),
                ReconcileOutcome.BlockedRecoverable,
                cancellationToken).ConfigureAwait(false);
        }

        // The shared preparation, exactly as 'aw continue' applies it: the project, prerequisites, conversation record and baseline are all re-checked, and the run's lock is taken.
        PreparedStretch stretch;

        try
        {
            stretch = await new ContinueCommand(services.ForSupervision(TextWriter.Null)).PrepareAsync(new ContinueRequest(runId.ToString(), HumanPresence.Interactive), TextWriter.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (ContinuationRefusedException e) when (e.Reason == ContinuationRefusal.BaselineUnavailable)
        {
            return await BlockAsync(runId, tasks, UnresumableReason, ReconcileOutcome.BlockedUnresumable, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ContinuationRefusedException or GateRefusedException or ConfigurationException or UsageException)
        {
            // Whatever refused (a stale configuration, a missing prerequisite, a run somebody else just took) is the person's to fix or to wait out; a refusal because the run is
            // held now is not a reason to block it.
            if (e is UsageException)
                return ReconcileOutcome.Untouched;

            return await BlockAsync(
                runId,
                tasks,
                RecoverableContinuation.BlockedReason("controller interruption", $"it could not be resumed: {FirstLine(e.Message)}", sessionConfirmedStopped: true),
                ReconcileOutcome.BlockedRecoverable,
                cancellationToken).ConfigureAwait(false);
        }

        supervisor.Adopt(stretch, attempts + 1);

        return ReconcileOutcome.Resumed;
    }

    private Task<ReconcileOutcome> BlockAsync(TaskId runId, WorkflowTaskService tasks, string reason, ReconcileOutcome outcome, CancellationToken cancellationToken)
        => BlockAsync(runId, tasks, reason, outcome, WorkflowState.Running, cancellationToken);

    private async Task<ReconcileOutcome> BlockAsync(TaskId runId, WorkflowTaskService tasks, string reason, ReconcileOutcome outcome, WorkflowState expected, CancellationToken cancellationToken)
    {
        IDisposable liveLock;

        try
        {
            liveLock = store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ReconcileOutcome.Untouched;
        }

        using (liveLock)
        {
            var snapshot = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);

            if (snapshot.Task.WorkflowState != expected)
                return ReconcileOutcome.Untouched;

            try
            {
                await tasks.ChangeWorkflowStateAsync(snapshot, WorkflowState.Blocked, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (StaleWorkflowStateException)
            {
                return ReconcileOutcome.Untouched;
            }
        }

        return outcome;
    }

    private static string Bounded(string? text)
    {
        var line = FirstLine(text ?? string.Empty);

        return line.Length == 0 ? "(no detail given)" : line;
    }

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();

        return line.Length <= 200 ? line : line[..200];
    }
}
