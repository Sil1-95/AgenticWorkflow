using System.Text;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw status &lt;run-id&gt;</c>: loads the run's durable event stream and replays it through the same Core/Application code the run used. Every
/// workflow fact it prints comes from that replay. Metadata supplies the workspace path only, and the recorded process outcome is shown as an
/// annotation; neither can change the state shown. Works in a fresh process, long after the run's process ended.
/// </summary>
internal sealed class StatusCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(StatusRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.RunId, "D", out var guid) || guid == Guid.Empty)
        {
            var message = $"'{request.RunId}' is not a run id (expected a GUID such as 0197d2e1-5c3a-7b8e-9f10-2a3b4c5d6e7f).";

            if (!request.Json)
                throw new UsageException(message);

            services.Out.Write(TaskReadModelJson.Serialize(new TaskReadFailure(TaskReadFailureCodes.InvalidTaskId, message)));

            return ExitCodes.UsageOrConfiguration;
        }

        var runId = new TaskId(guid);
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        try
        {
            var task = await store.ReplayAsync(runId, cancellationToken).ConfigureAwait(false);

            services.Out.Write(request.Json
                ? await RenderJsonAsync(store, runId, task, cancellationToken).ConfigureAwait(false)
                : await RenderAsync(store, runId, task, cancellationToken).ConfigureAwait(false));

            return ExitCodes.Success;
        }
        catch (RunStateException e)
        {
            if (request.Json)
            {
                services.Out.Write(TaskReadModelJson.Serialize(new TaskReadFailure(
                    e.Problem == RunStateProblem.NotFound ? TaskReadFailureCodes.NotFound : TaskReadFailureCodes.StateUnreadable,
                    e.Message)));
            }
            else
            {
                services.Error.WriteLine($"{(e.Problem == RunStateProblem.NotFound ? "RUN NOT FOUND" : "RUN STATE UNREADABLE")}: {e.Message}");

                if (e.Problem == RunStateProblem.Unreadable)
                    services.Error.WriteLine("Nothing was repaired or guessed; the run's files were left untouched.");
            }

            return e.Problem == RunStateProblem.NotFound ? ExitCodes.RunNotFound : ExitCodes.RunStateUnreadable;
        }
    }

    /// <summary>The structured report: the replayed task projected to the read model, plus the run's operational facts. Nothing but the one JSON document is written.</summary>
    private async Task<string> RenderJsonAsync(RunStore store, TaskId runId, WorkflowTask task, CancellationToken cancellationToken)
    {
        return TaskReadModelJson.Serialize(await new RunReadModelReader(services, store).ProjectAsync(runId, task, cancellationToken).ConfigureAwait(false));
    }

    private async Task<string> RenderAsync(RunStore store, TaskId runId, WorkflowTask task, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var (metadata, metadataProblem) = store.ReadMetadata(runId);
        var (recorded, outcomeProblem) = store.ReadOutcome(runId);

        text.AppendLine($"Run: {runId}");
        text.AppendLine($"Workspace: {metadata?.Workspace ?? $"unknown ({metadataProblem})"}");
        text.AppendLine($"State: {Format.State(task.WorkflowState)}");
        text.AppendLine($"Acceptance: {AcceptancePolicy.Describe(task.Acceptance)} (fixed when the run was created)");

        if (task.WhatRequired)
            text.AppendLine("Required: WHAT approval (--require what; fixed when the run was created)");

        if (task.HowRequired)
            text.AppendLine("Required: HOW approval (--require how; fixed when the run was created)");

        if (!task.HowRequired && task.HowEscalated)
            text.AppendLine("Required: HOW approval (added by the agent's reclassification; additive, it cannot be lowered)");

        if (!task.WhatRequired && task.WhatEscalated)
            text.AppendLine("Required: WHAT approval (added by the agent's reclassification; additive, it cannot be lowered)");

        ReviewText.AppendRequirement(text, task);
        ProofText.AppendRequirement(text, task);

        if (task.LatestWhatGate is { } latestWhat)
            text.AppendLine($"WHAT: {RunReportRenderer.StageState(task, latestWhat)}");

        if (task.LatestHowGate is { } latestHow)
            text.AppendLine($"HOW: {RunReportRenderer.StageState(task, latestHow)}");

        var semantic = task.CurrentSemanticRevision;
        text.AppendLine($"Semantic revision: {semantic.Ordinal} ({semantic.Kind})");

        text.AppendLine(task.Reclassifications.IsEmpty
            ? task.CurrentImplementationRevision is { } implementation
                ? $"Implementation: {Format.ShortFingerprint(implementation.Fingerprint.Value)}{(implementation.SemanticRevisionId == task.CurrentSemanticRevisionId ? string.Empty : " (targets an earlier semantic revision)")}"
                : "Implementation: none recorded"
            : $"Implementation: {RunReportRenderer.ImplementationState(task)}");

        text.AppendLine($"Verification rounds: {task.Verifications.Length}");

        if (task.CurrentVerification is { } verification)
        {
            text.AppendLine(task.IsVerificationStale
                ? $"Verification: STALE {verification.Outcome.ToString().ToUpperInvariant()} (for an implementation that is no longer current; it cannot complete the run)"
                : $"Verification: {verification.Outcome.ToString().ToUpperInvariant()}");
            RunReportRenderer.AppendChecks(text, verification);
        }
        else
        {
            text.AppendLine("Verification: none recorded for the current implementation");
        }

        ReviewText.Append(text, task);
        ProofText.Append(text, task);

        if (task.WorkflowState != WorkflowState.Completed)
        {
            var eligibility = DirectPathCompletion.Evaluate(task);
            text.AppendLine($"Completion: not reached ({(eligibility.IsEligible ? EligibleMeaning(task.Acceptance) : string.Join(", ", eligibility.Reasons))})");
        }

        text.AppendLine($"Reason: {task.StateReason}");

        if (!task.Reclassifications.IsEmpty)
        {
            text.AppendLine();
            RunReportRenderer.AppendReclassification(text, task, indent: string.Empty);
        }

        await AppendGateAsync(text, runId, task, metadata, cancellationToken).ConfigureAwait(false);

        var recoverable = RecoverableContinuation.Find(task);

        if (recoverable is not null)
            AppendRecoverableContinuation(text, store, runId, recoverable, recorded);
        else
            AppendExecutionState(text, store, runId, task);

        AppendRecordedOutcome(text, task, recorded, outcomeProblem);

        return text.ToString();
    }

    private static string EligibleMeaning(AcceptanceRequirement acceptance) => acceptance == AcceptanceRequirement.HumanRequired
        ? "awaiting the human's ACCEPT"
        : "verified, but the controller has not recorded the completion (the run's process ended first)";

    /// <summary>
    /// The workflow-owned ACCEPT gate, entirely from the replayed stream: the gate awaiting a decision (with what it binds, whether it is still current, and the
    /// actions that decide it) and the history of gates already decided. Whether the working tree still matches the verified state is the one fact read from
    /// outside the stream, and it is only informational here: <c>aw accept</c> checks it again at the moment of the decision.
    /// </summary>
    private async Task AppendGateAsync(StringBuilder text, TaskId runId, WorkflowTask task, RunMetadata? metadata, CancellationToken cancellationToken)
    {
        if (task.PendingGate is { Type: GateType.Clarify } question)
            AppendQuestion(text, runId, task, question);
        else if (task.PendingGate is { Type: GateType.What } proposal)
            AppendWhatProposal(text, runId, task, proposal);
        else if (task.PendingGate is { Type: GateType.How } how)
            AppendHowProposal(text, runId, task, how);
        else if (task.PendingGate is { } pending)
        {
            var semantic = task.SemanticRevisions.First(r => r.Id == pending.SemanticRevisionId);
            var implementation = task.ImplementationRevisions.First(r => r.Id == pending.ImplementationRevisionId);
            var verification = task.Verifications.First(v => v.Id == pending.VerificationRunId);
            var current = task.IsCurrent(pending);

            text.AppendLine();
            text.AppendLine($"Pending gate: {pending.Type.ToString().ToUpperInvariant()} (owned by AgenticWorkflow; durable, needs no agent session)");
            text.AppendLine($"  gate: {pending.Id}");
            text.AppendLine($"  semantic revision: {semantic.Ordinal} ({semantic.Kind})");
            text.AppendLine($"  implementation revision: {Format.ShortFingerprint(implementation.Fingerprint.Value)}");
            text.AppendLine($"  verification: {verification.Outcome.ToString().ToUpperInvariant()} ({verification.Id})");
            text.AppendLine($"  binding: {(current ? "current" : "STALE; it no longer describes the run and cannot be accepted")}");
            text.AppendLine($"  working tree: {await DescribeWorkspaceAsync(metadata, implementation, cancellationToken).ConfigureAwait(false)}");
            text.AppendLine();
            text.AppendLine("Actions:");

            if (current)
                text.AppendLine($"  aw accept {runId} --gate {pending.Id}");

            text.AppendLine($"  aw request-changes {runId} --gate {pending.Id} --feedback \"<what to change>\"");
            text.AppendLine($"  aw abort {runId}");
        }

        var closed = task.Gates.Where(g => g.Id != task.PendingGate?.Id).ToList();

        if (closed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Gate history:");

            foreach (var gate in closed)
            {
                var decision = task.DecisionFor(gate.Id)!;

                if (gate.Clarification is { } asked)
                {
                    text.AppendLine($"  {gate.Type.ToString().ToUpperInvariant()} {gate.Id}: {Describe(decision.Action)} (asked under semantic revision {task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId).Ordinal})");
                    text.AppendLine($"    question: {Format.Printable(asked.Question)}");

                    if (decision.Answer is { } answer)
                        text.AppendLine($"    answer: {Format.Printable(answer)}");

                    continue;
                }

                if (gate.What is { } proposed)
                {
                    text.AppendLine($"  {gate.Type.ToString().ToUpperInvariant()} {gate.Id}: {Describe(decision.Action)}{Invalidated(task, gate)} (proposed under semantic revision {task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId).Ordinal}, content {Format.ShortFingerprint(proposed.ContentHash)})");
                    text.AppendLine($"    objective: {Format.Bounded(proposed.Objective, 200)}");

                    if (decision.Feedback is { } changes)
                        text.AppendLine($"    feedback: {Format.Printable(changes)}");

                    continue;
                }

                if (gate.How is { } strategy)
                {
                    text.AppendLine($"  {gate.Type.ToString().ToUpperInvariant()} {gate.Id}: {Describe(decision.Action)}{Invalidated(task, gate)} (proposed under semantic revision {task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId).Ordinal}, content {Format.ShortFingerprint(strategy.ContentHash)})");
                    text.AppendLine($"    strategy: {Format.Bounded(strategy.Strategy, 200)}");

                    if (decision.Feedback is { } changes)
                        text.AppendLine($"    feedback: {Format.Printable(changes)}");

                    continue;
                }

                var implementation = task.ImplementationRevisions.First(r => r.Id == gate.ImplementationRevisionId);

                text.AppendLine($"  {gate.Type.ToString().ToUpperInvariant()} {gate.Id}: {Describe(decision.Action)} (implementation {Format.ShortFingerprint(implementation.Fingerprint.Value)})");

                if (decision.Feedback is { } feedback)
                    text.AppendLine($"    feedback: {Format.Printable(feedback)}");
            }
        }
        else if (task.WorkflowState == WorkflowState.Completed && task.Gates.IsEmpty && task.Acceptance == AcceptanceRequirement.HumanRequired)
        {
            text.AppendLine();
            text.AppendLine("Acceptance: none recorded (this run completed before the ACCEPT gate existed).");
        }
    }

    /// <summary>" -> INVALIDATED" for an approval a reclassification (or a later approved WHAT) made no longer current: the decision itself is never rewritten.</summary>
    private static string Invalidated(WorkflowTask task, Gate gate) => task.StatusOf(gate) == GateStatus.Invalidated ? " -> INVALIDATED" : string.Empty;

    private static string Describe(GateAction action) => action switch
    {
        GateAction.Accept => "ACCEPTED",
        GateAction.RequestChanges => "CHANGES REQUESTED",
        GateAction.Answer => "ANSWERED",
        GateAction.Approve => "APPROVED",
        _ => "CANCELLED",
    };

    /// <summary>
    /// The open WHAT proposal, entirely from the replayed stream: every section, why approval is warranted, what it binds, whether it is still current and the
    /// three actions. It needs no agent session and no <c>hc attach</c>: the decision is a command.
    /// </summary>
    private static void AppendWhatProposal(StringBuilder text, TaskId runId, WorkflowTask task, Gate proposal)
    {
        text.AppendLine();
        text.AppendLine("Pending gate: WHAT (a requirements proposal the agent made; owned by AgenticWorkflow; durable, needs no agent session)");
        RunReportRenderer.AppendWhat(text, task, proposal, indent: "  ");
        text.AppendLine($"  binding: {(task.IsCurrent(proposal) ? "current" : "STALE; it was proposed under an earlier semantic revision and cannot be approved")}");
        text.AppendLine("  nothing was implemented, verified or completed; no implementation is bound to this proposal");
        text.AppendLine();
        text.AppendLine("Actions:");

        if (task.IsCurrent(proposal))
            RunReportRenderer.AppendWhatActions(text, runId, proposal, indent: "  ");
        else
            text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>
    /// The open HOW proposal, entirely from the replayed stream: every section, why approval is warranted, what it binds, whether it is still current and the
    /// actions. It needs no agent session and no <c>hc attach</c>: the decision is a command.
    /// </summary>
    private static void AppendHowProposal(StringBuilder text, TaskId runId, WorkflowTask task, Gate proposal)
    {
        text.AppendLine();
        text.AppendLine("Pending gate: HOW (an implementation strategy the agent proposed; owned by AgenticWorkflow; durable, needs no agent session)");
        RunReportRenderer.AppendHow(text, task, proposal, indent: "  ");
        text.AppendLine($"  binding: {(task.IsCurrent(proposal) ? "current" : "STALE; it was proposed under an earlier semantic revision and cannot be approved")}");
        text.AppendLine("  nothing was implemented, verified or completed; no implementation is bound to this proposal");
        text.AppendLine();
        text.AppendLine("Actions:");

        if (task.IsCurrent(proposal))
            RunReportRenderer.AppendWhatActions(text, runId, proposal, indent: "  ");
        else
            text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>
    /// The open CLARIFY question, entirely from the replayed stream: what is asked, why it matters, the semantic revision it was asked under, whether it is
    /// still current, and the two actions. It needs no agent session and no <c>hc attach</c>: the answer is given with a command.
    /// </summary>
    private static void AppendQuestion(StringBuilder text, TaskId runId, WorkflowTask task, Gate question)
    {
        var asked = question.Clarification!;
        var semantic = task.SemanticRevisions.First(r => r.Id == question.SemanticRevisionId);

        text.AppendLine();
        text.AppendLine("Pending gate: CLARIFY (a question the agent raised; owned by AgenticWorkflow; durable, needs no agent session)");
        text.AppendLine($"  question id: {question.Id}");
        text.AppendLine($"  question: {Format.Printable(asked.Question)}");
        text.AppendLine($"  why it matters: {Format.Printable(asked.Reason)}");
        text.AppendLine($"  asked under semantic revision: {semantic.Ordinal} ({semantic.Kind})");
        text.AppendLine($"  asked in agent turn: {asked.Turn}");
        text.AppendLine($"  binding: {(task.IsCurrent(question) ? "current" : "STALE; it was asked under an earlier semantic revision and cannot be answered")}");
        text.AppendLine("  nothing was verified and nothing is complete; no implementation is bound to this question");
        text.AppendLine();
        text.AppendLine("Actions:");

        if (task.IsCurrent(question))
            text.AppendLine($"  aw answer {runId} --question {question.Id} --text \"<your answer>\"");

        text.AppendLine($"  aw abort {runId}");
    }

    private async Task<string> DescribeWorkspaceAsync(RunMetadata? metadata, ImplementationRevision reviewed, CancellationToken cancellationToken)
    {
        if (metadata is null)
            return "not checked (the run's workspace is unknown)";

        try
        {
            var now = await services.Workspace.CaptureAsync(metadata.Workspace, cancellationToken).ConfigureAwait(false);

            return now.Fingerprint == reviewed.Fingerprint
                ? "unchanged since verification"
                : "CHANGED since verification (accepting would be refused; request changes to have it verified again)";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"not checked ({e.Message})";
        }
    }

    /// <summary>
    /// A run BLOCKED because the continuation of a human's requested changes failed (<see cref="RecoverableContinuation"/>): what work is pending (from the
    /// replayed stream), what is known about how it can be continued (the run's own files; the conversation id is never printed) and the two actions.
    /// </summary>
    private void AppendRecoverableContinuation(StringBuilder text, RunStore store, TaskId runId, PendingContinuation pending, RunOutcomeRecord? recorded)
    {
        text.AppendLine();
        text.AppendLine("Pending work:");

        if (pending.Reclassification is { } reclassified)
        {
            text.AppendLine($"  The agent's reclassification of the {reclassified.StageName} (semantic revision {pending.Revision.Ordinal}); the report stands and is not repeated; the agent still owes the revised proposal");
            text.AppendLine($"  Evidence: {Format.Bounded(reclassified.Evidence, 300)}");
        }
        else if (pending.Kind == HumanInputKind.WhatApproved)
        {
            text.AppendLine($"  Your approval of the WHAT proposal (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Objective: {Format.Bounded(pending.What?.Objective, 200)}");
        }
        else if (pending.Kind == HumanInputKind.ReviewPending)
        {
            text.AppendLine($"  The independent review of the verified implementation (semantic revision {pending.Revision.Ordinal}); a FRESH reviewer reviews it. The implementer is not involved");
        }
        else if (pending.Kind == HumanInputKind.ReviewFindings && pending.Review is { } findings)
        {
            text.AppendLine($"  The repair of the independent review's findings (review round {findings.Round}, semantic revision {pending.Revision.Ordinal}); the ORIGINAL implementer conversation is resumed with the findings only; the report stands and is not repeated");
            text.AppendLine($"  Summary: {Format.Bounded(findings.Report.Summary, 300)}");
        }
        else if (pending.Kind == HumanInputKind.ProvePending)
        {
            text.AppendLine($"  The formal proof of the verified work (semantic revision {pending.Revision.Ordinal}); a FRESH prover proves the Proof Contract. The implementer is not involved");
        }
        else if (pending.Kind == HumanInputKind.ProofFindings && pending.Proof is { } proven)
        {
            text.AppendLine($"  The repair of what the independent proof found (proof round {proven.Round}, semantic revision {pending.Revision.Ordinal}); the ORIGINAL implementer conversation is resumed with a bounded delta only; the proof stands and is not repeated");
            text.AppendLine($"  Summary: {Format.Bounded(proven.Report.Summary, 300)}");
        }
        else if (pending.Kind == HumanInputKind.HowApproved)
        {
            text.AppendLine($"  Your approval of the HOW proposal (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Strategy: {Format.Bounded(pending.How?.Strategy, 200)}");
        }
        else if (pending.Kind == HumanInputKind.HowChangesRequested)
        {
            text.AppendLine($"  Your requested changes to the HOW proposal (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Feedback: {Format.Printable(pending.Text)}");
        }
        else if (pending.Kind == HumanInputKind.WhatChangesRequested)
        {
            text.AppendLine($"  Your requested changes to the WHAT proposal (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Feedback: {Format.Printable(pending.Text)}");
        }
        else if (pending.Question is { } question)
        {
            text.AppendLine($"  Your answer to the agent's question (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Question: {Format.Printable(question.Question)}");
            text.AppendLine($"  Answer: {Format.Printable(pending.Text)}");
        }
        else
        {
            text.AppendLine($"  Human-requested changes (semantic revision {pending.Revision.Ordinal}); the decision stands and is not repeated");
            text.AppendLine($"  Feedback: {Format.Printable(pending.Text)}");
        }
        text.AppendLine();
        text.AppendLine("Continuation:");
        text.AppendLine("  blocked by a runtime failure; nothing is working on it. The agent may already have changed the workspace.");
        text.AppendLine($"  {DescribeConversation(store, runId)}");
        text.AppendLine($"  {DescribeBaseline(store, runId, pending)}");

        if (recorded?.Continuation is { } last)
            text.AppendLine($"  last continuation: {last}");

        text.AppendLine();
        text.AppendLine("Action:");
        text.AppendLine($"  aw continue {runId}");
        text.AppendLine($"  aw abort {runId}");
    }

    private string DescribeConversation(RunStore store, TaskId runId)
    {
        try
        {
            return new RunConversationStore(store, services.Clock).Find(runId) is null
                ? "native conversation: none recorded (a fresh session would be started, with the task and everything people added to it so far)"
                : "native conversation recorded";
        }
        catch (InvalidAgentConversationException e)
        {
            return $"native conversation record cannot be used ({e.Message}); 'aw continue' would be refused";
        }
    }

    private string DescribeBaseline(RunStore store, TaskId runId, PendingContinuation pending)
    {
        var (baseline, problem) = new ContinuationBaselineStore(store, services.Clock).Find(runId, pending.BaselineKey);

        return baseline is not null
            ? "scope baseline recorded (the retry is judged against the workspace as it was before the work you decided on began)"
            : $"scope baseline unavailable ({problem}); 'aw continue' would be refused";
    }

    /// <summary>
    /// What the run's agent session is doing, concretely, per state: a BLOCKED run says why nothing is working and what (if anything) can be done; an
    /// AWAITING_HUMAN run without a workflow gate was waiting for a person in the agent's own interface. "Live" is the one fact that does not come from the
    /// stream: whether a process still holds the run, where that person can still answer.
    /// </summary>
    private static void AppendExecutionState(StringBuilder text, RunStore store, TaskId runId, WorkflowTask task)
    {
        if (task.WorkflowState == WorkflowState.Blocked)
        {
            if (WhatStageBlock.IsBlockedBy(task))
            {
                RunReportRenderer.AppendWhatStageBlock(text, runId);
            }
            else if (HowStageBlock.IsBlockedBy(task))
            {
                RunReportRenderer.AppendHowStageBlock(text, runId);
            }
            else if (RuntimeFailureBlock.IsBlockedBy(task))
            {
                RunReportRenderer.AppendRuntimeFailureBlock(text, runId);
            }
            else if (UnresolvedVerificationBlock.IsBlockedBy(task))
            {
                RunReportRenderer.AppendUnresolvedVerificationBlock(text, runId);
            }
            else if (NativeInteractionBlock.IsBlockedBy(task))
            {
                text.AppendLine();
                text.AppendLine("Agent session: ended; the native interaction it was waiting for can no longer be answered.");
                text.AppendLine($"Action: 'aw abort {runId}' cancels this blocked run, or run the task again (--interactive, and answer the agent's prompt in its Herdr session).");
            }
            else
            {
                text.AppendLine();
                text.AppendLine("No in-place recovery command is currently available for this blocked run.");
                text.AppendLine($"Action: 'aw abort {runId}' cancels it.");
            }

            return;
        }

        // RUNNING is the event stream's word. That no process holds the run is an operational observation, shown as one: a run recorded before runtime
        // failures were (a process that ended without recording anything) still reads RUNNING, and nothing here rewrites or infers a state from it.
        if (task.WorkflowState == WorkflowState.Running)
        {
            if (!store.IsLive(runId))
            {
                text.AppendLine();
                text.AppendLine("Agent session: no aw process currently holds this run, so nothing is working on it; its process ended without recording what happened (a run from before runtime failures were recorded can read RUNNING this way).");
                text.AppendLine("No in-place recovery command is available for a RUNNING run: inspect the working tree with git, or start a new run.");
            }

            return;
        }

        // An AWAITING_HUMAN at the workflow's own gate (ACCEPT, CLARIFY or WHAT) is not a native interaction: it needs no agent session and is shown above.
        if (task.WorkflowState != WorkflowState.AwaitingHuman || task.PendingGate is not null)
            return;

        text.AppendLine();
        text.AppendLine("Owner: the agent's native interface (this wait needs the live agent session).");

        if (store.IsLive(runId))
        {
            // The attach pointer is a display annotation of the live process; a run without a usable one keeps the older wording.
            if (!NativeWaitStatus.TryAppend(text, store, runId))
                text.AppendLine("Agent session: live in another process, waiting for a person in its own interface (see that run's output for where to answer).");

            return;
        }

        // Only a run whose process died mid-wait is left here (a clean stop records BLOCKED); the stream could not say the session ended.
        text.AppendLine("Agent session: not live (the run's process ended without recording what happened); whatever the agent was waiting for can no longer be answered.");
        text.AppendLine($"Action: 'aw abort {runId}' cancels the run, or run the task again.");
    }

    /// <summary>The process-level result is shown for context only; where it contradicts the replayed state, the replay is what the output already says.</summary>
    private static void AppendRecordedOutcome(StringBuilder text, WorkflowTask task, RunOutcomeRecord? recorded, string? problem)
    {
        if (recorded is null)
        {
            text.AppendLine(problem is not null && !problem.Contains("missing", StringComparison.Ordinal)
                ? $"Last run result: unavailable ({problem})"
                : "Last run result: not recorded (the run may still be in progress, or its process ended abnormally)");
            return;
        }

        var agrees = (recorded.Outcome == nameof(RunOutcome.Completed)) == (task.WorkflowState == WorkflowState.Completed);

        text.AppendLine($"Last run result: {recorded.Outcome} (exit {recorded.ExitCode}) - {recorded.Reason}");

        if (!agrees)
            text.AppendLine("Warning: the recorded run result disagrees with the event stream; the event stream is authoritative.");
    }
}
