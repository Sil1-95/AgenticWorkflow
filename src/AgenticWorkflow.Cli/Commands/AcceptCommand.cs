using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw accept &lt;run-id&gt; [--gate &lt;gate-id&gt;]</c>: the person's explicit ACCEPT of the run's workflow-owned gate. It works from a fresh process, long after the
/// run's process and its agent session ended: it needs only the durable event stream and the working tree. It makes no model call and starts no agent.
/// The decision is the command itself; no text is read as approval. Completion is recorded by the controller (Core) in the same atomic append as the
/// acceptance, and only if the gate is the open one, still current, and the working tree is still exactly the verified state.
/// </summary>
internal sealed class AcceptCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(AcceptRequest request, CancellationToken cancellationToken)
    {
        var runId = CommandLine.ParseRunId(request.RunId);
        var gateId = CommandLine.ParseGateId(request.Gate);

        try
        {
            var outcome = await RecordAsync(runId, gateId, cancellationToken).ConfigureAwait(false);

            var task = outcome.Change.Task;
            var implementation = task.ImplementationRevisions.First(r => r.Id == outcome.Gate.ImplementationRevisionId);

            services.Out.WriteLine("COMPLETED");
            services.Out.WriteLine();
            services.Out.WriteLine("You accepted the verified result. No agent or model was involved.");
            services.Out.WriteLine($"Run: {runId}");
            services.Out.WriteLine($"State: {Format.State(task.WorkflowState)}");
            services.Out.WriteLine($"Accepted gate: {outcome.Gate.Id}");
            services.Out.WriteLine($"Implementation: {Format.ShortFingerprint(implementation.Fingerprint.Value)}");
            services.Out.WriteLine($"Verification: {task.Verifications.First(v => v.Id == outcome.Gate.VerificationRunId).Outcome.ToString().ToUpperInvariant()}");

            return ExitCodes.Success;
        }
        catch (Exception e) when (GateCommandErrors.Handles(e))
        {
            return GateCommandErrors.Report(services, "ACCEPT", e);
        }
    }

    /// <summary>
    /// Accepts the run's gate and records the outcome. Throws the refusals of <see cref="GateCommandErrors"/>; nothing was recorded when it does. Starts no agent and makes no
    /// model call, so it needs no run lock: the acceptance is one atomic append that is refused unless the gate is still the open, current one.
    /// </summary>
    public async Task<GateOutcome> RecordAsync(AgenticWorkflow.Core.Identity.TaskId runId, AgenticWorkflow.Core.Identity.GateId? gateId, CancellationToken cancellationToken)
    {
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));
        var tasks = await store.OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);

        // The workspace is a descriptive fact from the run's metadata. Without it the working tree cannot be confirmed, so nothing can be accepted.
        var (metadata, metadataProblem) = store.ReadMetadata(runId);
        if (metadata is null)
        {
            throw new GateRefusedException(
                GateRefusal.WorkspaceUnavailable,
                $"The run's workspace is unknown ({metadataProblem}), so the working tree cannot be confirmed to be the verified state.");
        }

        var outcome = await new AcceptGateService(tasks, services.Workspace)
            .AcceptAsync(runId, gateId, metadata.Workspace, cancellationToken)
            .ConfigureAwait(false);

        TryRecordOutcome(store, runId);

        return outcome;
    }

    private void TryRecordOutcome(RunStore store, AgenticWorkflow.Core.Identity.TaskId runId)
    {
        try
        {
            store.WriteOutcome(runId, new RunOutcomeRecord(
                RunOutcomeRecord.CurrentVersion,
                nameof(RunOutcome.Completed),
                ExitCodes.Success,
                "accepted: the human accepted the verified result with 'aw accept'",
                services.Clock.UtcNow,
                0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            services.Error.WriteLine($"warning: the run outcome could not be recorded ({e.Message}).");
        }
    }
}
