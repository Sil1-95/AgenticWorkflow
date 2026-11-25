using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw continue &lt;run-id&gt;</c>: the person's explicit operational retry of work they already asked for. When <c>aw request-changes</c> (or <c>aw answer</c>)
/// recorded the decision but its continuation could not run because the runtime failed, the run is BLOCKED (<see cref="RecoverableContinuation"/>); this command returns it to RUNNING and
/// executes the SAME pending work. It is not a decision: it records no gate decision, no semantic revision and no gate, and it takes no feedback, so nothing on
/// the command line can change what the work is. It is not a generic resume either: only that one state is accepted (and its twin, the same work still RUNNING because its owner died,
/// when nobody holds the run), and every other run is refused with nothing recorded.
/// <para>
/// Nothing is taken from process memory: the pending revision, the feedback, the block and the gate decision come from the replayed event stream; the project
/// from the run's metadata (and must be unchanged); the native conversation from <c>conversation.json</c> (resumed under the same rules as the original
/// continuation); the workspace baseline the scope is judged against from <c>continuation.json</c>. It re-applies the same refusals as
/// <see cref="RequestChangesCommand"/> before anything is recorded, so a retry can never be broader than the original request was.
/// </para>
/// </summary>
internal sealed class ContinueCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(ContinueRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var stretch = await PrepareAsync(request, services.Out, cancellationToken).ConfigureAwait(false);

            // ---- the same direct path, judged against the original baseline ---------------------------------------------------
            return await new RunExecutor(services).ExecuteAsync(stretch.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GateCommandErrors.Handles(e))
        {
            return GateCommandErrors.Report(services, "CONTINUE", e);
        }
    }

    /// <summary>
    /// Refuses what can be refused, returns a BLOCKED run to RUNNING, and returns the same pending work with the run's live lock held. A run that is already RUNNING with that work
    /// in progress is taken over the same way, without a state change, once its lock is held (the work is the same and nothing is recorded twice). Throws the refusals of
    /// <see cref="GateCommandErrors"/>; nothing was recorded when it does.
    /// </summary>
    public async Task<PreparedStretch> PrepareAsync(ContinueRequest request, TextWriter banner, CancellationToken cancellationToken)
    {
        var timing = new RunTiming();
        var runId = CommandLine.ParseRunId(request.RunId);
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);
        var recovery = new ContinuationRecoveryService(tasks);

        // ---- refuse what can be refused before anything is recorded ----------------------------------------------------------
        // Only the supported states: BLOCKED after a human-requested continuation failed, or that same work RUNNING with its owner gone. Everything else is refused.
        var snapshot = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        var interrupted = RecoverableContinuation.IsWorkInProgress(snapshot.Task);
        var pending = interrupted
            ? new PendingContinuation(PendingHumanInput.Find(snapshot.Task)!, snapshot.Task.StateReason)
            : await recovery.InspectAsync(runId, cancellationToken).ConfigureAwait(false);

        var project = ContinuationPreflight.ResolveProject(services, store, runId);
        var current = await ContinuationPreflight.CaptureWorkspaceAsync(services, project, cancellationToken).ConfigureAwait(false);
        ContinuationPreflight.RequireUsableConversation(services, store, runId, project, snapshot.Task);
        ContinuationPreflight.RequireLaunchable(services, snapshot.Task);

        // The baseline the whole request is judged against, recorded when it was made: not a fresh capture, which would absorb whatever the interrupted
        // attempt already changed (or anything changed since) and so hide it from the scope rules.
        var (baseline, baselineProblem) = new ContinuationBaselineStore(store, services.Clock).Find(runId, pending.BaselineKey);

        // A pending REVIEW has no stored baseline (nothing was changed when it became due, and a reviewer needs none to be judged): the workspace as it is now is the
        // reference, which is only used to list the changed files for the reviewer. A repair's baseline is stored with its findings and is required as for any continuation.
        if (baseline is null && pending.Kind is HumanInputKind.ReviewPending or HumanInputKind.ProvePending)
            baseline = current;

        if (baseline is null)
        {
            throw new ContinuationRefusedException(
                ContinuationRefusal.BaselineUnavailable,
                $"The workspace baseline of the requested changes cannot be used ({baselineProblem}), so the retry's scope cannot be judged and it was not started. "
                + $"Nothing was changed; 'aw abort {runId}' cancels the run.");
        }

        // One process continues a run at a time; a second continue (or a live run) fails here and records nothing.
        var liveLock = TryAcquireLiveLock(store, runId);

        try
        {
            // What an earlier process of this run's stretch left behind (an agent host nobody owns) is ended first, and the retry goes ahead only when it is confirmed gone:
            // two agents must never work on one run.
            if (!await OrphanedSessions.ReapAsync(services.RuntimeFactory, store, runId, cancellationToken).ConfigureAwait(false))
            {
                throw new ContinuationRefusedException(
                    ContinuationRefusal.SessionNotStopped,
                    "The agent session the interrupted attempt started could not be confirmed stopped, so a second agent was not started beside it. Nothing was changed; stop it, or 'aw abort' the run.");
            }

            if (interrupted)
            {
                // A RUNNING run is already where the retry would put it; only its owner is gone, and holding the lock is what makes this process the new one. Make sure it is
                // still that run now that the lock is held.
                var held = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);

                if (!RecoverableContinuation.IsWorkInProgress(held.Task) || PendingHumanInput.Find(held.Task)?.BaselineKey != pending.BaselineKey)
                    throw new ContinuationRefusedException(ContinuationRefusal.StateMoved, "The run changed while the continuation was being prepared; nothing was recorded.");
            }
            else
            {
                // ---- the operational retry: BLOCKED -> RUNNING, one reasoned state change; no decision, no semantic revision, no gate ------------
                await recovery.ResumeAsync(runId, cancellationToken).ConfigureAwait(false);
            }

            Describe(banner, runId, pending, baseline, current);

            return new PreparedStretch(
                new RunExecution(runId, project, project.ToDefinition(snapshot.Task), tasks, store, baseline, request.Presence, RunStretch.RecoveredContinuation, timing),
                liveLock);
        }
        catch
        {
            liveLock.Dispose();

            throw;
        }
    }

    private static void Describe(TextWriter banner, TaskId runId, PendingContinuation pending, ImplementationState baseline, ImplementationState current)
    {
        banner.WriteLine($"Run: {runId}");
        var clarification = pending.Kind == HumanInputKind.Clarification;
        var approval = pending.Kind == HumanInputKind.WhatApproved;
        var whatChanges = pending.Kind == HumanInputKind.WhatChangesRequested;
        var howApproval = pending.Kind == HumanInputKind.HowApproved;
        var howChanges = pending.Kind == HumanInputKind.HowChangesRequested;
        var reclassified = pending.Reclassification;
        var reviewPending = pending.Kind == HumanInputKind.ReviewPending;
        var reviewFindings = pending.Kind == HumanInputKind.ReviewFindings;
        var provePending = pending.Kind == HumanInputKind.ProvePending;
        var proofFindings = pending.Kind == HumanInputKind.ProofFindings;
        var origin = provePending
            ? "the work was verified (and reviewed, when required)"
            : proofFindings
            ? "the independent proof recorded its result"
            : reviewPending
            ? "the implementation was verified"
            : reviewFindings
            ? "the independent review recorded its findings"
            : reclassified is not null
            ? "the reclassification was recorded"
            : clarification
            ? "the agent asked"
            : approval
                ? "the WHAT was approved"
                : whatChanges
                    ? "changes to the WHAT were requested"
                    : howApproval
                        ? "the HOW was approved"
                        : howChanges ? "changes to the HOW were requested" : "the changes were requested";

        banner.WriteLine(provePending
            ? $"Continuing: the verified work is due its formal proof (semantic revision {pending.Revision.Ordinal}); a fresh prover is started and no decision, contract, report or revision is repeated."
            : proofFindings
            ? $"Continuing the repair of what the independent proof found (semantic revision {pending.Revision.Ordinal}; the proof stands and is not repeated, no new revision is recorded)."
            : reviewPending
            ? $"Continuing: the verified implementation is due its independent review (semantic revision {pending.Revision.Ordinal}); a fresh reviewer is started and no decision, report or revision is repeated."
            : reviewFindings
            ? $"Continuing the repair of the independent review's findings (semantic revision {pending.Revision.Ordinal}; the report stands and is not repeated, no new revision is recorded)."
            : reclassified is not null
            ? $"Continuing after the agent's report of new evidence invalidated the {reclassified.StageName} (semantic revision {pending.Revision.Ordinal}; the report stands and no new decision or revision is recorded)."
            : clarification
            ? $"Continuing after your answer (semantic revision {pending.Revision.Ordinal}; the decision stands and no new decision or revision is recorded)."
            : approval
                ? $"Continuing after your approval of the WHAT (semantic revision {pending.Revision.Ordinal}; the decision stands and no new decision or revision is recorded)."
                : whatChanges
                    ? $"Continuing the human-requested changes to the WHAT of semantic revision {pending.Revision.Ordinal} (the decision stands; no new decision or revision is recorded)."
                    : howApproval
                        ? $"Continuing after your approval of the HOW (semantic revision {pending.Revision.Ordinal}; the decision stands and no new decision or revision is recorded)."
                        : howChanges
                            ? $"Continuing the human-requested changes to the HOW of semantic revision {pending.Revision.Ordinal} (the decision stands; no new decision or revision is recorded)."
                            : $"Continuing the human-requested changes of semantic revision {pending.Revision.Ordinal} (the decision stands; no new decision or revision is recorded).");
        banner.WriteLine($"State: {Format.State(WorkflowState.Running)}");

        if (pending.Question is { } question)
            banner.WriteLine($"Question: {Format.Printable(question.Question)}");

        if (reclassified is not null)
            banner.WriteLine($"Evidence: {Format.Bounded(reclassified.Evidence, 300)}");
        else if (approval)
            banner.WriteLine($"Approved WHAT objective: {Format.Bounded(pending.What?.Objective, 200)}");
        else if (howApproval)
            banner.WriteLine($"Approved HOW strategy: {Format.Bounded(pending.How?.Strategy, 200)}");
        else if (proofFindings && pending.Proof is { } proven)
            banner.WriteLine($"Proof result: {ProofReport.VerdictName(proven.Verdict).ToUpperInvariant()} (round {proven.Round}): {Format.Bounded(proven.Report.Summary, 300)}");
        else if (reviewFindings && pending.Review is { } reviewed)
            banner.WriteLine($"Review findings: {reviewed.Report.Findings.Length} (round {reviewed.Round}): {Format.Bounded(reviewed.Report.Summary, 300)}");
        else if (!reviewPending && !provePending)
            banner.WriteLine($"{(clarification ? "Answer" : "Feedback")}: {Format.Printable(pending.Text)}");

        var changed = ChangedSince(baseline, current);
        banner.WriteLine(changed == 0
            ? $"Workspace: unchanged since {origin}."
            : $"Workspace: {changed} path(s) changed since {origin} (by the interrupted attempt or otherwise). They are not assumed correct or clean: the agent is told to inspect them and the controller verifies the result.");

        banner.WriteLine("Continuing the work: the agent's native conversation is resumed when it can be (the report below says what happened).");
        banner.WriteLine();
    }

    private static int ChangedSince(ImplementationState baseline, ImplementationState current)
        => baseline.PathDigests.Keys
            .Union(current.PathDigests.Keys, StringComparer.Ordinal)
            .Count(path => baseline.PathDigests.GetValueOrDefault(path) != current.PathDigests.GetValueOrDefault(path));

    private static IDisposable TryAcquireLiveLock(RunStore store, TaskId runId)
    {
        try
        {
            return store.AcquireLiveLock(runId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("This run is still live in another process; wait for it to finish, then continue it.");
        }
    }
}
