using System.Text;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// What the independent reviewer is told, and what the implementer is told when the review requires changes. The reviewer's message is the whole of its context:
/// a deterministic packet rendered from the authoritative task state alone. It never contains a transcript, the implementer's reasoning or an earlier reviewer's
/// conversation; on a re-review it carries the earlier review's structured findings, which are part of the task state, and nothing else of that review.
/// </summary>
internal static class ReviewPrompts
{
    private const int MaxChangedPathsListed = 40;

    /// <summary>The first and only message of a fresh reviewer session.</summary>
    public static string Reviewer(WorkflowTask task, DirectTaskDefinition definition, IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(changedPaths);

        var implementation = task.CurrentImplementationRevision!;
        var verification = task.CurrentVerification!;
        var round = task.Reviews.Length + 1;
        var text = new StringBuilder();

        text.AppendLine($"You are the independent REVIEWER of a finished piece of work (review round {round}). You did not write it and you have no memory of how it was written. Judge it from the state below and from the repository itself.");
        text.AppendLine();
        text.AppendLine("== The task ==");
        text.AppendLine(task.Intent.Trim());
        text.AppendLine();
        text.AppendLine($"Semantic revision {task.CurrentSemanticRevision.Ordinal} ({task.CurrentSemanticRevision.Kind}): {task.CurrentSemanticRevision.Explanation.Trim()}");

        AppendApprovedStage(text, "WHAT", task.LatestWhatGate, task, g => g.What is { } what ? WhatText.Render(what) : null);
        AppendApprovedStage(text, "HOW", task.LatestHowGate, task, g => g.How is { } how ? HowText.Render(how) : null);

        if (!task.Reclassifications.IsEmpty)
        {
            text.AppendLine();
            text.AppendLine("== Reclassifications (the implementer reported evidence that changed an approved stage; the approved stages above already reflect them) ==");
            foreach (var reclassification in task.Reclassifications)
            {
                text.AppendLine($"- {reclassification.StageName} reconsidered. Evidence: {reclassification.Evidence.Trim()} Reason: {reclassification.Reason.Trim()}");
            }
        }

        text.AppendLine();
        text.AppendLine("== The implementation under review ==");
        text.AppendLine($"Implementation revision: {implementation.Id} (workspace fingerprint {implementation.Fingerprint}).");
        text.AppendLine($"Workspace: {definition.WorkspacePath} (a Git repository). It is read-only for you: you cannot and must not change it.");
        AppendChangedPaths(text, changedPaths);

        text.AppendLine();
        text.AppendLine($"== Verification the controller already holds for exactly this implementation (run {verification.Id}, {verification.Outcome.ToString().ToLowerInvariant()}) ==");
        foreach (var check in verification.Checks)
        {
            var source = check.Source == VerificationEvidenceSource.AgentObserved ? "agent-observed, not re-executed" : "executed by the controller";
            text.AppendLine($"- {check.CheckId}: {check.Outcome.ToString().ToLowerInvariant()} ({source}). {check.Summary.Trim()}{(string.IsNullOrWhiteSpace(check.EvidenceReason) ? string.Empty : $" [{check.EvidenceReason}]")}");
        }

        text.AppendLine("These results are current and authoritative. Do NOT run the configured checks again: they already ran against exactly this state. Run a command yourself only for a specific, concrete concern the results above do not already answer, and then keep it narrow.");

        if (!task.Reviews.IsEmpty)
        {
            var previous = task.Reviews[^1];

            text.AppendLine();
            text.AppendLine($"== The previous review (round {previous.Round}, {VerdictName(previous.Verdict)}) and what was done since ==");
            text.AppendLine($"Summary: {previous.Report.Summary.Trim()}");
            RenderFindings(text, previous.Report.Findings);
            text.AppendLine("The implementer has worked on these findings since. Check what is actually in the repository now; do not assume they were addressed, and do not assume they were not.");
        }

        text.AppendLine();
        text.AppendLine("== How to review ==");
        text.AppendLine("This is a judgment of the work, not another verification. Decide whether the implementation correctly and completely does what the task and the approved stages above require, and whether it introduces a real defect.");
        text.AppendLine("Block only on a concrete, evidenced problem: wrong or missing behaviour against the requirement, a real bug, a regression, an unsafe or incorrect edge case that matters here, or a gap the verification above does not cover and that you can show.");
        text.AppendLine("Do NOT block on style, naming, formatting, harmless refactors, speculation, or because you would have designed it differently. Do not reopen the approved WHAT or HOW: judge the work against them. If the evidence shows an approved stage is itself wrong, or something only the human can decide is unsettled, say so as a finding and the implementer will route it; do not decide it yourself.");
        text.AppendLine("Read the code and, where useful, run read-only git commands or a narrow targeted check. You have no capability to edit, commit or change anything, and the controller verifies that the workspace is exactly as you found it.");
        text.AppendLine();
        text.AppendLine($"== How to answer ==");
        text.AppendLine($"When you have decided, call the `{AgentReviewSubmissionTool}` tool exactly once. That is the only way your review is recorded; text you write in the conversation is not read.");
        text.AppendLine("- verdict: PASS (the work is acceptable; findings must be empty) or CHANGES_REQUIRED (at least one finding).");
        text.AppendLine($"- summary: your overall judgment in a few sentences (at most {ReviewReport.MaxSummaryLength} characters).");
        text.AppendLine($"- findings: up to {ReviewReport.MaxFindings}, each with title, evidence (what you saw, where), impact (why it matters), recommendation (what to change) and an optional location (file:line). Keep them specific and few; do not pad.");
        text.AppendLine($"- notes: optional non-blocking remarks (at most {ReviewReport.MaxNotesLength} characters). They never prevent completion.");
        text.Append("Submit once. Do nothing after submitting.");

        return text.ToString();
    }

    /// <summary>Sent to the same reviewer session when its turn ended without a submitted report.</summary>
    public static string NotSubmitted()
        => $"Your turn ended without a review being recorded. Reach your verdict and call the `{AgentReviewSubmissionTool}` tool exactly once with verdict, summary and findings. Nothing else records a review.";

    /// <summary>The delta sent to the ORIGINAL implementer conversation when the review required changes: only the authoritative findings, never the reviewer's conversation.</summary>
    public static string Findings(Review review, bool recovering = false)
    {
        ArgumentNullException.ThrowIfNull(review);

        var text = new StringBuilder();

        if (recovering)
        {
            text.AppendLine("A previous attempt at addressing these review findings was interrupted by a runtime failure and may already have changed the working tree. Inspect the current workspace first and preserve any correct work already completed.");
            text.AppendLine();
        }

        text.AppendLine("Independent REVIEW found:");
        text.AppendLine($"Summary: {review.Report.Summary.Trim()}");
        RenderFindings(text, review.Report.Findings);
        text.AppendLine();
        text.Append("Address these findings against the current authoritative semantics and repository. If a finding reveals that an approved WHAT/HOW is no longer valid, use the existing reclassification mechanism rather than silently changing semantics.");
        text.AppendLine();
        text.AppendLine();
        text.Append("If, after inspecting the repository, you conclude a finding is mistaken, leave the workspace unchanged for it and finish; a fresh independent review will decide. You cannot mark a finding resolved.");

        return text.ToString();
    }

    /// <summary>How the agent is told it may ask for an independent review. Narrow on purpose: it only ever adds rigor.</summary>
    public static string RequestSignal { get; }
        = $"{AgentReviewRequestSignal.ToolName}: only for a risk that makes an independent review of your verified work worthwhile (a subtle change to shared behaviour, a security-relevant edit, a change whose correctness your own checks cannot show). It does not stop your turn and cannot be withdrawn. Not for routine work.";

    private const string AgentReviewSubmissionTool = AgentReviewSubmission.ToolName;

    private static void AppendApprovedStage(StringBuilder text, string label, Gate? gate, WorkflowTask task, Func<Gate, string?> render)
    {
        if (gate is null || task.StatusOf(gate) != GateStatus.Approved || render(gate) is not { } body)
            return;

        text.AppendLine();
        text.AppendLine($"== Approved {label} (what the human signed off; judge the work against it) ==");
        text.AppendLine(body);
    }

    private static void AppendChangedPaths(StringBuilder text, IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            text.AppendLine("Changed files: none were detected against the state the run began with. Use `git status` and `git diff` to inspect the repository.");
            return;
        }

        text.AppendLine($"Files changed in this run ({changedPaths.Count}; `git diff` and `git status` show the content):");
        foreach (var path in changedPaths.Take(MaxChangedPathsListed))
            text.AppendLine($"- {path}");

        if (changedPaths.Count > MaxChangedPathsListed)
            text.AppendLine($"- ... and {changedPaths.Count - MaxChangedPathsListed} more");
    }

    private static void RenderFindings(StringBuilder text, IReadOnlyList<ReviewFinding> findings)
    {
        var number = 1;

        foreach (var finding in findings)
        {
            text.AppendLine($"{number++}. {finding.Title.Trim()}{(finding.Location is null ? string.Empty : $" ({finding.Location})")}");
            text.AppendLine($"   Evidence: {finding.Evidence.Trim()}");
            text.AppendLine($"   Impact: {finding.Impact.Trim()}");
            text.AppendLine($"   Recommendation: {finding.Recommendation.Trim()}");
        }
    }

    private static string VerdictName(ReviewVerdict verdict) => verdict == ReviewVerdict.Pass ? "PASS" : "CHANGES_REQUIRED";
}
