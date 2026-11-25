using System.Text;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>What a finished <c>aw run</c> knows, assembled from data the run already produced (no extra model call, no summary generation).</summary>
internal sealed record RunReport(
    RunOutcome Outcome,
    DirectRunResult Result,
    TaskId RunId,
    string Workspace,
    TimeSpan Elapsed,
    IReadOnlyList<string> ChangedPaths);

/// <summary>
/// The human-readable ending of a run. Every outcome has its own heading and says what it means and what to do next; an incomplete run never
/// reads like a failure of the controller, and nothing but <see cref="RunOutcome.Completed"/> ever says COMPLETED.
/// </summary>
internal static class RunReportRenderer
{
    public static string Render(RunReport report)
    {
        var text = new StringBuilder();

        text.AppendLine(Heading(report.Outcome));
        text.AppendLine();

        AppendMeaning(text, report);

        if (report.Outcome is not (RunOutcome.Completed or RunOutcome.AwaitingAcceptance or RunOutcome.AwaitingClarification or RunOutcome.AwaitingWhat or RunOutcome.AwaitingHow))
            text.AppendLine($"Reason: {report.Result.Reason}");

        text.AppendLine($"Turns: {report.Result.TurnsUsed}");
        AppendContinuation(text, report.Result.Continuation);

        if (report.Outcome == RunOutcome.AwaitingClarification)
        {
            // Nothing was verified (the agent stopped to ask), so there is no verification or changed-paths summary to show: only the question and what to do.
            AppendQuestion(text, report);
            text.AppendLine();
            text.AppendLine($"Elapsed: {Format.Duration(report.Elapsed)}");
            text.AppendLine($"Run: {report.RunId}");

            return text.ToString();
        }

        if (report.Outcome == RunOutcome.AwaitingWhat)
        {
            // Nothing was implemented or verified (the agent stopped to propose the WHAT), so there is no verification or changed-paths summary: the proposal and what to do.
            if (report.Result.Task?.Task.PendingGate is { Type: GateType.What } proposed)
            {
                AppendReclassificationIfAny(text, report.Result.Task.Task);
                text.AppendLine();
                AppendWhat(text, report.Result.Task.Task, proposed, indent: "  ");
                text.AppendLine();
                text.AppendLine("Approve it, send it back, or stop:");
                AppendWhatActions(text, report.RunId, proposed, indent: "  ");
            }

            text.AppendLine();
            text.AppendLine($"Elapsed: {Format.Duration(report.Elapsed)}");
            text.AppendLine($"Run: {report.RunId}");

            return text.ToString();
        }

        if (report.Outcome == RunOutcome.AwaitingHow)
        {
            // Nothing was implemented or verified (the agent stopped to propose the HOW), so there is no verification or changed-paths summary: the proposal and what to do.
            if (report.Result.Task?.Task.PendingGate is { Type: GateType.How } proposed)
            {
                AppendReclassificationIfAny(text, report.Result.Task.Task);
                text.AppendLine();
                AppendHow(text, report.Result.Task.Task, proposed, indent: "  ");
                text.AppendLine();
                text.AppendLine("Approve it, send it back, or stop:");
                AppendWhatActions(text, report.RunId, proposed, indent: "  ");
            }

            text.AppendLine();
            text.AppendLine($"Elapsed: {Format.Duration(report.Elapsed)}");
            text.AppendLine($"Run: {report.RunId}");

            return text.ToString();
        }

        if (report.Result.Task is { } snapshot)
        {
            AppendVerification(text, snapshot.Task, report.Outcome);
            ReviewText.Append(text, snapshot.Task);
            ProofText.Append(text, snapshot.Task);
            text.AppendLine($"Acceptance: {AcceptancePolicy.Describe(snapshot.Task.Acceptance)}");
        }

        AppendChanged(text, report.ChangedPaths);

        foreach (var note in report.Result.Notes)
            text.AppendLine($"Note: {note}");

        if (report.Outcome == RunOutcome.AwaitingAcceptance && report.Result.Task?.Task.PendingGate is { } gate)
        {
            text.AppendLine();
            text.AppendLine("Review the result, then decide:");
            AppendGateActions(text, report.RunId, gate, indent: "  ");
        }

        if (report.Result.Task?.Task is { } blocked && RecoverableContinuation.Find(blocked) is not null)
        {
            AppendRecovery(text, report.RunId);
        }
        else if (report.Result.Task?.Task is { } violated && WhatStageBlock.IsBlockedBy(violated))
        {
            AppendWhatStageBlock(text, report.RunId);
        }
        else if (report.Result.Task?.Task is { } howViolated && HowStageBlock.IsBlockedBy(howViolated))
        {
            AppendHowStageBlock(text, report.RunId);
        }
        else if (report.Result.Task?.Task is { } failed && RuntimeFailureBlock.IsBlockedBy(failed))
        {
            AppendRuntimeFailureBlock(text, report.RunId);
        }
        else if (report.Result.Task?.Task is { } unresolved && UnresolvedVerificationBlock.IsBlockedBy(unresolved))
        {
            AppendUnresolvedVerificationBlock(text, report.RunId);
        }

        text.AppendLine();
        text.AppendLine($"Elapsed: {Format.Duration(report.Elapsed)}");
        text.AppendLine($"Run: {report.RunId}");

        return text.ToString();
    }

    public static string Heading(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Completed => "COMPLETED",
        RunOutcome.AwaitingAcceptance => "VERIFIED - AWAITING ACCEPT",
        RunOutcome.AwaitingClarification => "QUESTION - AWAITING YOUR ANSWER",
        RunOutcome.AwaitingWhat => "WHAT PROPOSED - AWAITING YOUR APPROVAL",
        RunOutcome.WhatStageViolation => "BLOCKED - WHAT STAGE VIOLATED",
        RunOutcome.AwaitingHow => "HOW PROPOSED - AWAITING YOUR APPROVAL",
        RunOutcome.HowStageViolation => "BLOCKED - HOW STAGE VIOLATED",
        RunOutcome.StartupBlock => "BLOCKED - AGENT STARTUP",
        RunOutcome.VerificationUnresolved => "INCOMPLETE - VERIFICATION STILL FAILING",
        RunOutcome.StaleWorkflowState => "STOPPED - STALE WORKFLOW STATE",
        RunOutcome.RuntimeError => "ERROR - RUNTIME FAILURE",
        RunOutcome.Cancelled => "CANCELLED",
        RunOutcome.NativeInteractionUnavailable => "BLOCKED - NATIVE INTERACTION UNAVAILABLE",
        RunOutcome.Aborted => "ABORTED",
        RunOutcome.ReviewerUnbound => "BLOCKED - REVIEWER NOT BOUND",
        RunOutcome.ReviewerModifiedWorkspace => "BLOCKED - REVIEWER MODIFIED THE WORKSPACE",
        RunOutcome.ReviewUnresolved => "INCOMPLETE - REVIEW STILL REQUIRES CHANGES",
        RunOutcome.ProverUnbound => "BLOCKED - PROVER NOT BOUND",
        RunOutcome.ProverModifiedWorkspace => "BLOCKED - PROVER MODIFIED THE WORKSPACE",
        RunOutcome.ProofUnresolved => "INCOMPLETE - PROOF STILL FAILING",
        RunOutcome.ProofContractMissing => "BLOCKED - PROOF CONTRACT MISSING",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown run outcome."),
    };

    private static void AppendMeaning(StringBuilder text, RunReport report)
    {
        switch (report.Outcome)
        {
            case RunOutcome.Completed:
                text.AppendLine(report.Result.Task?.Task.Acceptance == AcceptanceRequirement.NotRequired
                    ? "The controller verified the final working tree and this run does not require acceptance, so the controller completed it. No gate was opened and no human action is pending."
                    : "The controller verified the final working tree and you accepted it; the task is complete.");
                text.AppendLine();
                break;

            case RunOutcome.AwaitingAcceptance:
                text.AppendLine("The controller verified the final working tree. Nothing is complete yet: the result needs your decision.");
                text.AppendLine("The decision is durable. It does not need the agent session (which has ended) and can be made from any later aw process.");
                text.AppendLine();
                break;

            case RunOutcome.AwaitingClarification:
                text.AppendLine("The agent stopped to ask a question that only you can answer. Nothing was verified and nothing is complete.");
                text.AppendLine("The question is durable. It does not need the agent session (which has ended; its conversation is kept) and can be answered from any later aw process.");
                text.AppendLine();
                break;

            case RunOutcome.AwaitingWhat:
                text.AppendLine("The agent proposed the WHAT of the task, the requirements it will implement. Nothing was implemented, verified or completed: the proposal needs your decision first.");
                text.AppendLine("The proposal is durable. It does not need the agent session (which has ended; its conversation is kept) and can be decided from any later aw process.");
                text.AppendLine();
                break;

            case RunOutcome.AwaitingHow:
                text.AppendLine("The agent proposed the HOW of the task, the implementation strategy it intends to follow. Nothing was implemented, verified or completed: the proposal needs your decision first.");
                text.AppendLine("The proposal is durable. It does not need the agent session (which has ended; its conversation is kept) and can be decided from any later aw process.");
                text.AppendLine();
                break;

            case RunOutcome.HowStageViolation:
                text.AppendLine("The HOW stage of this run was broken, so the run stopped. No implementation was recorded and nothing was verified.");
                text.AppendLine();
                break;

            case RunOutcome.WhatStageViolation:
                text.AppendLine("The WHAT stage of this run was broken, so the run stopped. No implementation was recorded and nothing was verified.");
                text.AppendLine();
                break;

            case RunOutcome.StartupBlock:
                text.AppendLine("The agent did not become ready, so no task was sent. Nothing was answered on its behalf.");
                text.AppendLine($"Most often the repository is not trusted by the agent yet: open '{report.Workspace}' once in the agent (Claude Code or Pi), trust it, and run again.");
                text.AppendLine("aw never accepts trust, logs in or answers dialogs for you. A login or update prompt shows the same way.");
                text.AppendLine();
                break;

            case RunOutcome.VerificationUnresolved:
                text.AppendLine("The agent finished, but the controller's checks still fail after the permitted turns. The workflow remains incomplete.");
                text.AppendLine("The working tree keeps the agent's last attempt: inspect it with git, fix it by hand, or start a new, narrower run.");
                text.AppendLine();
                break;

            case RunOutcome.ProverUnbound:
                text.AppendLine("The work is verified and formal PROVE is required, but the prover role has no explicit profile, so no prover could start. Neither the implementer's nor the reviewer's profile is used instead.");
                text.AppendLine("No profile was chosen for it. Bind the role explicitly with 'aw bind <run-id> --role prover --profile <profile>' (the project's own profiles are listed by 'aw show'), then 'aw continue <run-id>'; or abort the run. The verified work is kept in the workspace.");
                text.AppendLine();
                break;

            case RunOutcome.ProverModifiedWorkspace:
                text.AppendLine("The independent prover changed the workspace it was only to read. Its proof was not recorded, nothing was absorbed as an implementation and nothing was reverted:");
                text.AppendLine("the working tree is exactly as the prover left it. Inspect it with git, then restore or accept it by hand; no in-place recovery command is available.");
                text.AppendLine();
                break;

            case RunOutcome.ProofUnresolved:
                text.AppendLine("The proof still fails or is inconclusive and the implementer has no turn left to answer it. The workflow remains incomplete.");
                text.AppendLine("The working tree keeps the agent's last attempt: inspect the proof below, fix it by hand, or start a new, narrower run.");
                text.AppendLine();
                break;

            case RunOutcome.ProofContractMissing:
                text.AppendLine("Formal PROVE is required but the implementer did not submit a Proof Contract for the current semantics, even after one reminder. Nothing could be proven; the verified work is preserved in the workspace.");
                text.AppendLine("No in-place recovery command is available for this block: start a new run (the verified work stays in the workspace) or abort this one.");
                text.AppendLine();
                break;

            case RunOutcome.StaleWorkflowState:
                text.AppendLine("This run's history was changed by something else while the agent worked, so nothing further was applied.");
                text.AppendLine("Inspect it with 'aw status <run-id>'; the working tree keeps whatever the agent changed.");
                text.AppendLine();
                break;

            case RunOutcome.RuntimeError:
                text.AppendLine(report.Result.Status == DirectRunStatus.Indeterminate
                    ? "A workflow write ended with an unknown outcome and was not repeated. Check 'aw status <run-id>' before acting."
                    : "The agent runtime or the agent process failed. The workflow remains incomplete.");
                text.AppendLine();
                break;

            case RunOutcome.Cancelled:
                text.AppendLine("The run was interrupted. The agent session this run started was stopped; the workflow remains incomplete.");
                text.AppendLine("The working tree may hold partial edits: review them with git status.");
                text.AppendLine();
                break;

            case RunOutcome.NativeInteractionUnavailable:
                text.AppendLine("The agent needed a person in its own interface (a permission prompt, a question or a login), and nobody was attending this run.");
                text.AppendLine("aw did not allow, deny or answer anything: that choice belongs to the agent's own interface. The agent session has ended, so the interaction can no longer be answered.");
                text.AppendLine("The workflow is BLOCKED. See it with 'aw status <run-id>', cancel it with 'aw abort <run-id>', or run the task again and answer the agent's prompt in its Herdr session (use --interactive).");
                text.AppendLine();
                break;

            case RunOutcome.Aborted:
                text.AppendLine("The run was aborted. The workflow is cancelled.");
                text.AppendLine();
                break;

            case RunOutcome.ReviewerUnbound:
                text.AppendLine("The implementation is verified and an independent REVIEW is required, but the reviewer role has no explicit profile, so no review could start. The implementer's profile is never used instead.");
                text.AppendLine("No profile was chosen for it. Bind the role explicitly with 'aw bind <run-id> --role reviewer --profile <profile>' (the project's own profiles are listed by 'aw show'), then 'aw continue <run-id>'; or abort the run. The verified work is kept in the workspace.");
                text.AppendLine();
                break;

            case RunOutcome.ReviewerModifiedWorkspace:
                text.AppendLine("The independent reviewer changed the workspace it was only to read. Its review was not recorded, nothing was absorbed as an implementation and nothing was reverted:");
                text.AppendLine("the working tree is exactly as the reviewer left it. Inspect it with git, then restore or accept it by hand; no in-place recovery command is available.");
                text.AppendLine();
                break;

            case RunOutcome.ReviewUnresolved:
                text.AppendLine("The independent review still requires changes and the implementer has no turn left to repair them. The workflow remains incomplete.");
                text.AppendLine("The working tree keeps the agent's last attempt: inspect the findings below, fix it by hand, or start a new, narrower run.");
                text.AppendLine();
                break;
        }
    }

    private static void AppendQuestion(StringBuilder text, RunReport report)
    {
        if (report.Result.Task?.Task.PendingGate is not { Type: GateType.Clarify, Clarification: { } question } gate)
            return;

        text.AppendLine($"Question: {Format.Printable(question.Question)}");
        text.AppendLine($"Why it matters: {Format.Printable(question.Reason)}");
        text.AppendLine($"Asked under semantic revision: {report.Result.Task.Task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId).Ordinal}");
        text.AppendLine($"Question id: {gate.Id}");
        text.AppendLine();
        text.AppendLine("Answer it, or stop:");
        AppendClarifyActions(text, report.RunId, gate, indent: "  ");
    }

    /// <summary>
    /// The WHAT proposal as a person reviews it: every section it carries, why a decision on it is warranted, the semantic revision it binds and its content hash.
    /// Entirely from the replayed stream; shared with <c>aw status</c>.
    /// </summary>
    public static void AppendWhat(StringBuilder text, WorkflowTask task, Gate gate, string indent)
    {
        var proposal = gate.What!;
        var semantic = task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId);

        text.AppendLine($"{indent}Proposed WHAT (semantic revision {semantic.Ordinal}, agent turn {proposal.Turn}, content {Format.ShortFingerprint(proposal.ContentHash)}):");

        foreach (var (name, body) in proposal.Sections())
        {
            text.AppendLine($"{indent}  {name}:");
            text.AppendLine(Format.Block(body, indent + "    "));
        }

        text.AppendLine($"{indent}Why approval is warranted: {Format.Bounded(proposal.Rationale, WhatProposal.MaxRationaleLength)}");
        text.AppendLine($"{indent}Proposal id: {gate.Id}");
    }

    /// <summary>
    /// The HOW proposal as a person reviews it: every section it carries, why a decision on it is warranted, the semantic revision it binds and its content
    /// hash. Entirely from the replayed stream; shared with <c>aw status</c>.
    /// </summary>
    public static void AppendHow(StringBuilder text, WorkflowTask task, Gate gate, string indent)
    {
        var proposal = gate.How!;
        var semantic = task.SemanticRevisions.First(r => r.Id == gate.SemanticRevisionId);

        text.AppendLine($"{indent}Proposed HOW (semantic revision {semantic.Ordinal}, agent turn {proposal.Turn}, content {Format.ShortFingerprint(proposal.ContentHash)}):");

        foreach (var (name, body) in proposal.Sections())
        {
            text.AppendLine($"{indent}  {name}:");
            text.AppendLine(Format.Block(body, indent + "    "));
        }

        text.AppendLine($"{indent}Why approval is warranted: {Format.Bounded(proposal.Rationale, HowProposal.MaxRationaleLength)}");
        text.AppendLine($"{indent}Proposal id: {gate.Id}");
    }

    private static void AppendReclassificationIfAny(StringBuilder text, WorkflowTask task)
    {
        if (task.ActiveReclassification is null)
            return;

        text.AppendLine();
        AppendReclassification(text, task, indent: "  ");
    }

    /// <summary>
    /// What the agent's latest reclassification did, entirely from the replayed stream: the evidence and reason, the stage the workflow was rerouted to, which approvals
    /// it invalidated (still visible in the history), whether the implementation and its verification are current or stale, and the state of the workspace
    /// (provisional work is preserved, never reverted or absorbed). Shared with <c>aw status</c>.
    /// </summary>
    public static void AppendReclassification(StringBuilder text, WorkflowTask task, string indent)
    {
        if (task.Reclassifications.IsEmpty)
            return;

        var latest = task.Reclassifications[^1];
        var revision = task.SemanticRevisions.First(r => r.Id == latest.SemanticRevisionId);
        var active = task.ActiveReclassification is not null;

        text.AppendLine($"{indent}Reclassification (semantic revision {revision.Ordinal}, {revision.Kind}, {(active ? "active" : "resolved")}; {task.Reclassifications.Length} in this run):");
        text.AppendLine($"{indent}  evidence reported by the agent: {Format.Bounded(latest.Evidence, Reclassification.MaxEvidenceLength)}");
        text.AppendLine($"{indent}  agent's reason: {Format.Bounded(latest.Reason, Reclassification.MaxReasonLength)}");
        text.AppendLine(latest.Stage == ReclassificationStage.How
            ? $"{indent}  rerouted to: HOW (the WHAT remains valid)"
            : $"{indent}  rerouted to: WHAT (the earliest invalid stage; the HOW is asked for again after it)");

        if (latest.InvalidatedGates.IsEmpty)
        {
            text.AppendLine($"{indent}  invalidated: nothing was approved to invalidate; {latest.StageName} approval is now required (additive, never lowered automatically)");
        }
        else
        {
            foreach (var gateId in latest.InvalidatedGates)
            {
                var gate = task.Gates.First(g => g.Id == gateId);
                text.AppendLine($"{indent}  invalidated: {gate.Type.ToString().ToUpperInvariant()} {gate.Id} (was approved; the approval stays in the history, no longer current)");
            }
        }

        if (task.LatestWhatGate is { } what)
            text.AppendLine($"{indent}  WHAT: {StageState(task, what)}");

        if (task.LatestHowGate is { } how)
            text.AppendLine($"{indent}  HOW: {StageState(task, how)}");

        text.AppendLine($"{indent}  implementation: {ImplementationState(task)}");
        text.AppendLine($"{indent}  verification: {VerificationState(task)}");

        if (active)
        {
            text.AppendLine(task.HasProvisionalWork
                ? $"{indent}  workspace: provisional work preserved (not reverted, not absorbed as an implementation); baseline {Format.ShortFingerprint(latest.Workspace.Value)}. No further edits are allowed until the {latest.StageName} is approved."
                : $"{indent}  workspace: unchanged since the report (baseline {Format.ShortFingerprint(latest.Workspace.Value)}); no edits are allowed until the {latest.StageName} is approved.");
        }
    }

    /// <summary>One gate's currentness in words: approved, invalidated (with why), or not approved yet.</summary>
    public static string StageState(WorkflowTask task, Gate gate)
    {
        if (task.StatusOf(gate) == GateStatus.Invalidated)
            return $"INVALIDATED (approved earlier, no longer current: {Format.Bounded(task.InvalidationOf(gate)!.Reason, 200)})";

        return task.StatusOf(gate) == GateStatus.Approved ? "approved (current)" : "not approved yet; nothing may be implemented until it is";
    }

    public static string ImplementationState(WorkflowTask task)
    {
        if (task.CurrentImplementationRevision is not { } implementation)
            return task.HasProvisionalWork ? "none recorded (the workspace holds provisional edits that are not an implementation)" : "none recorded";

        return task.IsImplementationCurrent
            ? $"current ({Format.ShortFingerprint(implementation.Fingerprint.Value)})"
            : $"STALE ({Format.ShortFingerprint(implementation.Fingerprint.Value)} targets semantic revision {task.SemanticRevisions.First(r => r.Id == implementation.SemanticRevisionId).Ordinal}; historical, kept in the history)";
    }

    public static string VerificationState(WorkflowTask task)
    {
        if (task.CurrentVerification is not { } verification)
            return "none recorded for the current implementation";

        return task.IsVerificationStale
            ? $"STALE ({verification.Outcome.ToString().ToUpperInvariant()} for an implementation that is no longer current; it cannot complete the run)"
            : $"current ({verification.Outcome.ToString().ToUpperInvariant()})";
    }

    /// <summary>What a person is told when the HOW stage was broken and the workflow is BLOCKED (<see cref="HowStageBlock"/>). Shared by <c>aw status</c>.</summary>
    public static void AppendHowStageBlock(StringBuilder text, TaskId runId)
    {
        text.AppendLine();
        text.AppendLine("The workflow is BLOCKED, not RUNNING: the HOW stage was violated and nothing is working on this run.");
        text.AppendLine("No in-place recovery command is available. The working tree keeps whatever the agent did: inspect it with git. Nothing was recorded as implemented.");
        text.AppendLine("Cancel it, then start a new run (add --require how to insist on the stage):");
        text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>The structured actions at an open WHAT or HOW proposal, each pinned to its gate id. Shared with <c>aw status</c>.</summary>
    public static void AppendWhatActions(StringBuilder text, TaskId runId, Gate gate, string indent)
    {
        text.AppendLine($"{indent}aw approve {runId} --gate {gate.Id}");
        text.AppendLine($"{indent}aw request-changes {runId} --gate {gate.Id} --feedback \"<what to change>\"");
        text.AppendLine($"{indent}aw abort {runId}");
    }

    /// <summary>What a person is told when the WHAT stage was broken and the workflow is BLOCKED (<see cref="WhatStageBlock"/>). Shared by <c>aw status</c>.</summary>
    public static void AppendWhatStageBlock(StringBuilder text, TaskId runId)
    {
        text.AppendLine();
        text.AppendLine("The workflow is BLOCKED, not RUNNING: the WHAT stage was violated and nothing is working on this run.");
        text.AppendLine("No in-place recovery command is available. The working tree keeps whatever the agent did: inspect it with git. Nothing was recorded as implemented.");
        text.AppendLine("Cancel it, then start a new run (add --require what to insist on the stage):");
        text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>The structured actions at an open CLARIFY question, each pinned to the question's id. Shared with <c>aw status</c>.</summary>
    public static void AppendClarifyActions(StringBuilder text, TaskId runId, Gate gate, string indent)
    {
        text.AppendLine($"{indent}aw answer {runId} --question {gate.Id} --text \"<your answer>\"");
        text.AppendLine($"{indent}aw abort {runId}");
    }

    /// <summary>
    /// What a person is told when an agent attempt ended on a runtime failure and the workflow is BLOCKED (<see cref="RuntimeFailureBlock"/>): nothing is working
    /// on it, and no command resumes it in place (<c>aw continue</c> retries only a continuation of human input, which this is not, so it is never mentioned here). Shared by <c>aw status</c>.
    /// </summary>
    public static void AppendRuntimeFailureBlock(StringBuilder text, TaskId runId)
    {
        text.AppendLine();
        text.AppendLine("The workflow is BLOCKED, not RUNNING: the agent attempt ended on a runtime failure and nothing is working on this run.");
        text.AppendLine("No in-place recovery command is currently available for this run. The agent may already have changed the working tree.");
        text.AppendLine("Cancel it, or start a new run:");
        text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>
    /// What a person is told when verification is still failing after the permitted turns and the workflow is BLOCKED (<see cref="UnresolvedVerificationBlock"/>).
    /// Shared by <c>aw status</c>.
    /// </summary>
    public static void AppendUnresolvedVerificationBlock(StringBuilder text, TaskId runId)
    {
        text.AppendLine();
        text.AppendLine("The workflow is BLOCKED, not RUNNING: verification is still failing and nothing is working on this run.");
        text.AppendLine("No in-place recovery command is currently available for this run. The working tree keeps the agent's last attempt: inspect it with git, fix it by hand, or start a new, narrower run.");
        text.AppendLine("Cancel it:");
        text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>
    /// What a person is told when the continuation of their requested changes could not run and the workflow is BLOCKED (<see cref="RecoverableContinuation"/>):
    /// the decision stands, only its execution was interrupted, and the same work is retried with <c>aw continue</c>. Shared by <c>aw status</c>.
    /// </summary>
    public static void AppendRecovery(StringBuilder text, TaskId runId)
    {
        text.AppendLine();
        text.AppendLine("The workflow is BLOCKED, not RUNNING: nothing is working on what was decided or reported (requested changes, your answer, your approval, or the agent's reclassification) right now. The decision or report stands and is unchanged;");
        text.AppendLine("only its execution was interrupted. The agent may already have changed the working tree.");
        text.AppendLine("Retry the same work (no new decision, no new semantic revision):");
        text.AppendLine($"  aw continue {runId}");
        text.AppendLine("or stop:");
        text.AppendLine($"  aw abort {runId}");
    }

    /// <summary>
    /// How the agent's conversation was obtained, when it is worth saying: a resumed native conversation, an explicit fresh-session fallback (with why) or a
    /// failed resume that was NOT replaced by a fresh session. A plain new conversation is the norm and says nothing. Shared with <c>aw status</c> wording.
    /// </summary>
    public static void AppendContinuation(StringBuilder text, AgentContinuation? continuation)
    {
        switch (continuation?.Kind)
        {
            case AgentContinuationKind.ResumedNative:
                text.AppendLine("Agent continuation: resumed native conversation");
                break;

            case AgentContinuationKind.FreshSession:
                text.AppendLine("Agent continuation: fresh session");
                text.AppendLine($"  Reason: {DescribeFallback(continuation.Reason)}");
                text.AppendLine("  The agent does not remember the earlier conversation; it was sent the task again with everything people added to it so far, in order.");
                break;

            case AgentContinuationKind.ResumeFailed:
                text.AppendLine("Agent continuation: resume failed");
                text.AppendLine("  The earlier conversation could not be resumed and no fresh session was substituted, so no context was lost.");
                break;
        }
    }

    /// <summary>The one-line provenance of a continuation for the run's recorded outcome (and <c>aw status</c>); null for a plain new conversation.</summary>
    public static string? DescribeContinuation(AgentContinuation? continuation) => continuation?.Kind switch
    {
        AgentContinuationKind.ResumedNative => "resumed native conversation",
        AgentContinuationKind.FreshSession => $"fresh session ({DescribeFallback(continuation.Reason)})",
        AgentContinuationKind.ResumeFailed => "resume failed",
        _ => null,
    };

    private static string DescribeFallback(string? reason) => reason switch
    {
        AgentContinuationReasons.ResumeUnsupported => "native resume unavailable (this runtime or profile cannot resume)",
        AgentContinuationReasons.NoConversationRecorded => "native conversation reference unavailable (none was recorded for this run)",
        AgentContinuationReasons.ConversationNotFound => "the native conversation no longer exists",
        _ => reason ?? "unspecified",
    };

    private static void AppendVerification(StringBuilder text, WorkflowTask task, RunOutcome outcome)
    {
        // Completed shows the verification the completion rests on; otherwise the latest one for the current implementation, if any.
        if (task.CurrentImplementationRevision is { } implementation)
            text.AppendLine($"Implementation: {Format.ShortFingerprint(implementation.Fingerprint.Value)}");

        if (task.CurrentVerification is not { } verification)
        {
            if (outcome != RunOutcome.Completed)
                text.AppendLine("Verification: none recorded");

            return;
        }

        text.AppendLine($"Verification: {verification.Outcome.ToString().ToUpperInvariant()}");
        AppendChecks(text, verification);
    }

    /// <summary>
    /// The structured actions a person can take at an open gate, each with the gate it is pinned to. The gate id makes the decision about the state that was
    /// reviewed: a command copied from earlier output cannot decide a gate opened later. Shared with <c>aw status</c>.
    /// </summary>
    public static void AppendGateActions(StringBuilder text, TaskId runId, Gate gate, string indent)
    {
        text.AppendLine($"{indent}aw accept {runId} --gate {gate.Id}");
        text.AppendLine($"{indent}aw request-changes {runId} --gate {gate.Id} --feedback \"<what to change>\"");
        text.AppendLine($"{indent}aw abort {runId}");
    }

    /// <summary>
    /// One line per check: result, id, who executed it and, when it was recorded, why (reused from the agent's own run, or executed by the controller).
    /// A reused check adds what was observed: which agent command, which tool use and how long it took. Never any command output.
    /// Runs recorded before reasons existed, and the controller-only sensors, simply have no reason. Shared with <c>aw status</c>.
    /// </summary>
    public static void AppendChecks(StringBuilder text, VerificationRun verification)
    {
        var width = verification.Checks.Max(c => c.CheckId.Value.Length);

        foreach (var check in verification.Checks)
        {
            var result = check.Outcome == VerificationOutcome.Passed ? "PASS" : "FAIL";
            var reason = check.EvidenceReason is { } why ? $"  ({Format.Printable(why)})" : string.Empty;
            var detail = check.Outcome == VerificationOutcome.Failed ? $"  {check.Summary}" : string.Empty;

            text.AppendLine($"  {result} {check.CheckId.Value.PadRight(width)}  {check.Source}{reason}{detail}");

            if (check.Observation is { } observation)
            {
                var duration = observation.DurationMilliseconds is { } milliseconds ? $", {Format.Duration(TimeSpan.FromMilliseconds(milliseconds))}" : string.Empty;

                text.AppendLine($"       observed: {Format.Printable(observation.Runtime)} ran `{Format.Printable(observation.CommandIdentity)}` (tool use {Format.Printable(observation.ToolUseId)}{duration}); not re-executed");
            }
        }
    }

    private static void AppendChanged(StringBuilder text, IReadOnlyList<string> changed)
    {
        if (changed.Count == 0)
        {
            text.AppendLine("Changed: nothing");
            return;
        }

        text.AppendLine("Changed:");

        foreach (var path in changed.Take(25))
            text.AppendLine($"  {path}");

        if (changed.Count > 25)
            text.AppendLine($"  ... and {changed.Count - 25} more");
    }
}
