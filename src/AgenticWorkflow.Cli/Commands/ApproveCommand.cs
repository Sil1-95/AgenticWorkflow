using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw approve &lt;run-id&gt; --gate &lt;gate-id&gt;</c>: the person's explicit approval of the WHAT or HOW proposal the agent made; which one is decided by the
/// named gate, never by text. The proposal is a durable,
/// workflow-owned gate, so this works from a fresh process, long after the run's process and its agent session ended. The gate id is required: an approval is
/// always about a named proposal, never "whatever is open". Nothing in the command line but the command itself is read as approval.
/// <para>
/// The approval is recorded with a new <c>human_what_approval</c> semantic revision in one atomic append, the run returns to RUNNING, and the work continues in
/// the agent's own native conversation when it can be resumed (sent the approved WHAT); only when it cannot (an explicit, reported fallback) does a fresh session
/// start, which is sent the task and the ordered history of everything people added. Approving records no implementation, verifies nothing and completes nothing.
/// </para>
/// <para>
/// Everything that can be refused is refused before the approval is recorded (the gate is unknown, already decided, stale, not a WHAT proposal or not the open
/// one; usage; configuration changed since the run; prerequisites; a conversation record that cannot be trusted; a missing workspace baseline; a run live in
/// another process), so a refusal never leaves the run RUNNING with nobody working on it, and a repeated approval can never add a second decision or revision.
/// If the work then cannot run because the runtime failed, the run is BLOCKED and <c>aw continue</c> retries the same work.
/// </para>
/// </summary>
internal sealed class ApproveCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(ApproveRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var stretch = await PrepareAsync(request, services.Out, cancellationToken).ConfigureAwait(false);

            // ---- the work continues through the same direct path, judged against the baseline from before the proposal -------------
            return await new RunExecutor(services).ExecuteAsync(stretch.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GateCommandErrors.Handles(e))
        {
            return GateCommandErrors.Report(services, "APPROVE", e);
        }
    }

    /// <summary>
    /// Refuses what can be refused, records the decision, and returns the work it asks for with the run's live lock held. Throws the refusals of <see cref="GateCommandErrors"/>;
    /// nothing was recorded when it does.
    /// </summary>
    public async Task<PreparedStretch> PrepareAsync(ApproveRequest request, TextWriter banner, CancellationToken cancellationToken)
    {
        var timing = new RunTiming();
        var runId = CommandLine.ParseRunId(request.RunId);
        var gateId = CommandLine.ParseGateId(request.Gate)!.Value;
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);
        var snapshot = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        // The stage being approved is the named gate's own; an unknown gate falls through to the WHAT check, which refuses it with the shared message.
        var how = snapshot.Task.Gates.FirstOrDefault(g => g.Id == gateId)?.Type == GateType.How;
        var stage = how ? "HOW" : "WHAT";

        // ---- refuse what can be refused before anything is recorded ----------------------------------------------------------
        // Is this the run's open, current proposal? (Reads only; the same check is made again atomically when the approval is recorded.)
        if (how)
            await new HowService(tasks).RequireApprovableAsync(runId, gateId, cancellationToken).ConfigureAwait(false);
        else
            await new WhatService(tasks).RequireApprovableAsync(runId, gateId, cancellationToken).ConfigureAwait(false);

        var project = ContinuationPreflight.ResolveProject(services, store, runId);
        ContinuationPreflight.RequireUsableConversation(services, store, runId, project, snapshot.Task);
        ContinuationPreflight.RequireLaunchable(services, snapshot.Task);

        // The workspace baseline recorded when the agent proposed (before the gate was opened): the work that follows is judged against the state before the
        // agent worked, and a fresh capture would absorb whatever changed since.
        var (baseline, baselineProblem) = new ContinuationBaselineStore(store, services.Clock).Find(runId, gateId);

        if (baseline is null)
        {
            throw new ContinuationRefusedException(
                ContinuationRefusal.BaselineUnavailable,
                $"The workspace baseline of {stage} proposal {gateId} cannot be used ({baselineProblem}), so the work after the approval could not be judged against the state before the agent proposed. "
                + $"Nothing was recorded; 'aw abort {runId}' cancels the run.");
        }

        // The workspace must be usable (a workspace that cannot be fingerprinted cannot be worked in).
        await ContinuationPreflight.CaptureWorkspaceAsync(services, project, cancellationToken).ConfigureAwait(false);

        // One process continues a run at a time; a second approval (or a live run) fails here and records nothing.
        var liveLock = TryAcquireLiveLock(store, runId);

        try
        {
            // ---- the approval: gate closed as approved, RUNNING, new semantic revision (one atomic append) ------------------------
            var outcome = how
                ? await new HowService(tasks).ApproveAsync(runId, gateId, cancellationToken).ConfigureAwait(false)
                : await new WhatService(tasks).ApproveAsync(runId, gateId, cancellationToken).ConfigureAwait(false);
            var contentHash = how ? outcome.Gate.How!.ContentHash : outcome.Gate.What!.ContentHash;

            banner.WriteLine($"Run: {runId}");
            banner.WriteLine($"{stage} proposal {outcome.Gate.Id} approved (content {Format.ShortFingerprint(contentHash)}). Nothing is implemented, verified or complete yet.");
            banner.WriteLine($"State: {Format.State(outcome.Change.Task.WorkflowState)}");
            banner.WriteLine($"Semantic revision: {outcome.Change.Task.CurrentSemanticRevision.Ordinal} ({outcome.Change.Task.CurrentSemanticRevision.Kind})");
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

    private static IDisposable TryAcquireLiveLock(RunStore store, TaskId runId)
    {
        try
        {
            return store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("This run is still live in another process; wait for it to finish, then approve.");
        }
    }
}
