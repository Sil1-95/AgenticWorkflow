using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw answer &lt;run-id&gt; --question &lt;question-id&gt; --text "..."</c>: the person's explicit answer to a question the agent raised. The question is a
/// durable, workflow-owned CLARIFY gate, so this works from a fresh process, long after the run's process and its agent session ended. Free text is valid here
/// because it is an answer to a <em>named</em> question; it is never read as an approval, and answering accepts nothing.
/// <para>
/// The answer is recorded with a new <c>human_clarification</c> semantic revision in one atomic append, the run returns to RUNNING, and the work continues in the
/// agent's own native conversation when it can be resumed (sent only the question and the answer); only when it cannot (an explicit, reported fallback) does a
/// fresh session start, which is sent the task and the ordered history of everything people added. No transcript is replayed.
/// </para>
/// <para>
/// Everything that can be refused is refused before the answer is recorded (the question is unknown, already answered, stale or not the open one; usage;
/// configuration changed since the run; prerequisites; a conversation record that cannot be trusted; a missing workspace baseline; a run live in another
/// process), so a refusal never leaves the run RUNNING with nobody working on it, and a repeated answer can never add a second decision or revision. If the
/// work then cannot run because the runtime failed, the run is BLOCKED and <c>aw continue</c> retries the same work.
/// </para>
/// </summary>
internal sealed class AnswerCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(AnswerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var stretch = await PrepareAsync(request, services.Out, cancellationToken).ConfigureAwait(false);

            // ---- the work continues through the same direct path, judged against the baseline from before the question -----------
            return await new RunExecutor(services).ExecuteAsync(stretch.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GateCommandErrors.Handles(e))
        {
            return GateCommandErrors.Report(services, "ANSWER", e);
        }
    }

    /// <summary>
    /// Refuses what can be refused, records the decision, and returns the work it asks for with the run's live lock held. Throws the refusals of <see cref="GateCommandErrors"/>;
    /// nothing was recorded when it does.
    /// </summary>
    public async Task<PreparedStretch> PrepareAsync(AnswerRequest request, TextWriter banner, CancellationToken cancellationToken)
    {
        var timing = new RunTiming();
        var runId = CommandLine.ParseRunId(request.RunId);
        var questionId = CommandLine.ParseGateId(request.Question)!.Value;
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);
        var clarifications = new ClarificationService(tasks);

        // ---- refuse what can be refused before anything is recorded ----------------------------------------------------------
        // Is this the run's open, current question? (Reads only; the same check is made again atomically when the answer is recorded.)
        await clarifications.RequireAnswerableAsync(runId, questionId, cancellationToken).ConfigureAwait(false);

        var snapshot = await tasks.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
        var project = ContinuationPreflight.ResolveProject(services, store, runId);
        ContinuationPreflight.RequireUsableConversation(services, store, runId, project, snapshot.Task);
        ContinuationPreflight.RequireLaunchable(services, snapshot.Task);

        // The workspace baseline recorded when the agent asked (before the question was opened). Without it the scope of the work that follows cannot be
        // judged against the state before the agent worked, and a fresh capture would absorb whatever it had already changed.
        var (baseline, baselineProblem) = new ContinuationBaselineStore(store, services.Clock).Find(runId, questionId);

        if (baseline is null)
        {
            throw new ContinuationRefusedException(
                ContinuationRefusal.BaselineUnavailable,
                $"The workspace baseline of question {questionId} cannot be used ({baselineProblem}), so the work after the answer could not be judged against the state before the agent asked. "
                + $"Nothing was recorded; 'aw abort {runId}' cancels the run.");
        }

        // The workspace must be usable (a workspace that cannot be fingerprinted cannot be worked in).
        await ContinuationPreflight.CaptureWorkspaceAsync(services, project, cancellationToken).ConfigureAwait(false);

        // One process continues a run at a time; a second answer (or a live run) fails here and records nothing.
        var liveLock = TryAcquireLiveLock(store, runId);

        try
        {
            // ---- the answer: question closed as answered, RUNNING, new semantic revision (one atomic append) ---------------------
            var outcome = await clarifications.AnswerAsync(runId, questionId, request.Text, cancellationToken).ConfigureAwait(false);

            banner.WriteLine($"Run: {runId}");
            banner.WriteLine($"Question {outcome.Gate.Id} answered (not an approval: nothing is accepted).");
            banner.WriteLine($"State: {Format.State(outcome.Change.Task.WorkflowState)}");
            banner.WriteLine($"Semantic revision: {outcome.Change.Task.CurrentSemanticRevision.Ordinal} ({outcome.Change.Task.CurrentSemanticRevision.Kind})");
            banner.WriteLine($"Answer: {Format.Printable(request.Text)}");
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
            throw new UsageException("This run is still live in another process; wait for it to finish, then answer.");
        }
    }
}
