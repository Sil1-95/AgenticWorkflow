using System.Text;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// What the independent prover is told, what the implementer is told when a proof did not pass or no contract exists, and how the implementer is told it may write a
/// Proof Contract. The prover's message is the whole of its context: a deterministic packet rendered from the authoritative task state alone. It never contains a
/// transcript, the implementer's reasoning, a reviewer's conversation or an earlier prover's conversation; on a re-proof it carries the earlier proof's structured
/// per-claim results, which are part of the task state, and nothing else of that proof.
/// </summary>
internal static class ProofPrompts
{
    private const int MaxChangedPathsListed = 40;

    private const string ProofSubmissionTool = AgentProofSubmission.ToolName;

    /// <summary>The first and only message of a fresh prover session.</summary>
    public static string Prover(WorkflowTask task, DirectTaskDefinition definition, IReadOnlyList<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(changedPaths);

        var contract = task.CurrentProofContract!;
        var implementation = task.CurrentImplementationRevision!;
        var verification = task.CurrentVerification!;
        var round = task.Proofs.Length + 1;
        var text = new StringBuilder();

        text.AppendLine($"You are the independent PROVER of a finished piece of work (proof round {round}). You did not write it and you have no memory of how it was written. Your job is to establish, claim by claim, whether the Proof Contract below actually holds for the repository as it is now.");
        text.AppendLine();
        text.AppendLine("== The task ==");
        text.AppendLine(task.Intent.Trim());
        text.AppendLine();
        text.AppendLine($"Semantic revision {task.CurrentSemanticRevision.Ordinal} ({task.CurrentSemanticRevision.Kind}): {task.CurrentSemanticRevision.Explanation.Trim()}");

        AppendApprovedStage(text, "WHAT", task.LatestWhatGate, task, g => g.What is { } what ? WhatText.Render(what) : null);
        AppendApprovedStage(text, "HOW", task.LatestHowGate, task, g => g.How is { } how ? HowText.Render(how) : null);

        text.AppendLine();
        text.AppendLine("== The implementation under proof ==");
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

        text.AppendLine("These results are current and authoritative: cite them (as `verification:<check>`) instead of reproducing them. Do NOT run the configured checks again; they already ran against exactly this state. Run a command yourself only to observe something the results above do not already show.");

        if (task.IsReviewRequired && task.CurrentReview is { Verdict: ReviewVerdict.Pass } review)
        {
            text.AppendLine();
            text.AppendLine($"== The independent review of exactly this implementation (review {review.Id}, PASS) ==");
            text.AppendLine($"Summary: {review.Report.Summary.Trim()}");
            if (!string.IsNullOrWhiteSpace(review.Report.Notes))
                text.AppendLine($"Notes: {review.Report.Notes.Trim()}");

            text.AppendLine($"A claim that requires `review:current-pass` is satisfied by citing `review:{review.Id}`. The review is judgment about the code, not proof of a claim by itself.");
        }

        text.AppendLine();
        text.AppendLine($"== The Proof Contract (written by the implementer for semantic revision {task.CurrentSemanticRevision.Ordinal}; it is fixed and you may not change or add claims) ==");
        text.AppendLine($"Rationale: {contract.Rationale.Trim()}");
        foreach (var claim in contract.Claims)
        {
            text.AppendLine();
            text.AppendLine($"Claim {claim.Id}: {claim.Statement.Trim()}");
            text.AppendLine($"  Acceptance criterion: {claim.AcceptanceCriterion.Trim()}");
            foreach (var requirement in claim.Evidence)
                text.AppendLine($"  requires: {requirement}");
        }

        if (!task.Proofs.IsEmpty)
        {
            var previous = task.Proofs[^1];

            text.AppendLine();
            text.AppendLine($"== The previous proof (round {previous.Round}, {ProofReport.VerdictName(previous.Verdict).ToUpperInvariant()}) and what was done since ==");
            text.AppendLine($"Summary: {previous.Report.Summary.Trim()}");
            RenderClaimResults(text, previous.Report.Claims);
            text.AppendLine("The implementer has worked on these since. Establish each claim again from what is in the repository now; do not assume the earlier verdicts still hold, and do not assume they do not.");
        }

        text.AppendLine();
        text.AppendLine("== How to prove ==");
        text.AppendLine("For each claim decide whether its acceptance criterion is actually established by evidence you can cite. Use the controller's verification above where it covers the claim, read the code, and make a targeted observation (a narrow command) only where a claim needs one. Keep every command narrow and purposeful: you are being measured on cost, and rerunning broad test suites is waste.");
        text.AppendLine("- pass: the acceptance criterion is demonstrably met AND every evidence requirement of the claim is satisfied by evidence you cite.");
        text.AppendLine("- fail: you found concrete evidence that the claim does not hold.");
        text.AppendLine("- inconclusive: the claim cannot be established either way (for example a required observation is not possible, or a required review is not current). Never pass a claim you did not establish.");
        text.AppendLine("A `requires: observation:` line asks for a targeted observation by you: run the narrow command that shows it and cite it. Your shell commands are numbered in the order you run them, starting at 1; cite one as `command:<n>` (for example `command:2`). You may only cite commands you actually ran.");
        text.AppendLine("Do NOT block on style, naming, formatting or preference, and do not reopen the approved WHAT or HOW. You have no capability to edit, commit or change anything, and the controller verifies that the workspace is exactly as you found it.");
        text.AppendLine();
        text.AppendLine("== How to answer ==");
        text.AppendLine($"When you have decided, call the `{ProofSubmissionTool}` tool exactly once. That is the only way your proof is recorded; text you write in the conversation is not read.");
        text.AppendLine($"- summary: your overall judgment in a few sentences (at most {ProofReport.MaxSummaryLength} characters).");
        text.AppendLine($"- claims: one entry for EVERY claim above (no more, no fewer), each with claim_id, verdict (pass, fail or inconclusive), evidence (typed references: `verification:<check>`, `review:<id>` or `command:<n>`; at most {ProofClaimResult.MaxEvidenceRefs}) and a short summary (at most {ProofClaimResult.MaxSummaryLength} characters) of why.");
        text.AppendLine("The overall verdict is derived from your claim verdicts; you do not submit one.");
        text.Append("Submit once. Do nothing after submitting.");

        return text.ToString();
    }

    /// <summary>Sent to the same prover session when its turn ended without a submitted proof.</summary>
    public static string NotSubmitted()
        => $"Your turn ended without a proof being recorded. Reach a verdict for every claim and call the `{ProofSubmissionTool}` tool exactly once with summary and claims. Nothing else records a proof.";

    /// <summary>Sent to the same prover session when its proof could not be recorded because it did not resolve against the state it describes.</summary>
    public static string Refused(string problem, int observedCommands)
        => $"Your proof was not recorded: {problem}. Your session ran {observedCommands} command(s), numbered 1 to {observedCommands} in the order you ran them. Submit a corrected proof with the `{ProofSubmissionTool}` tool exactly once; cite only evidence that exists, and use inconclusive for a claim you cannot establish.";

    /// <summary>The delta sent to the ORIGINAL implementer conversation when the proof did not pass: only the authoritative per-claim results, never the prover's conversation.</summary>
    public static string Findings(Proof proof, bool recovering = false)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var text = new StringBuilder();

        if (recovering)
        {
            text.AppendLine("A previous attempt at addressing this formal proof was interrupted by a runtime failure and may already have changed the working tree. Inspect the current workspace first and preserve any correct work already completed.");
            text.AppendLine();
        }

        text.AppendLine($"Formal PROVE did not establish every claim of your Proof Contract ({ProofReport.VerdictName(proof.Verdict).ToUpperInvariant()}):");
        text.AppendLine($"Summary: {proof.Report.Summary.Trim()}");
        RenderClaimResults(text, proof.Report.Claims, onlyNotPassed: true);
        text.AppendLine();
        text.Append("Address what is not established against the current authoritative semantics and repository. The Proof Contract is fixed for this semantic revision and cannot be edited; if a claim is itself wrong because the approved WHAT/HOW is no longer valid, use the existing reclassification mechanism rather than silently changing semantics.");
        text.AppendLine();
        text.AppendLine();
        text.Append("If, after inspecting the repository, you conclude a verdict is mistaken, leave the workspace unchanged for it and finish; a fresh independent prover will decide. You cannot mark a claim proved.");

        return text.ToString();
    }

    /// <summary>The one reminder sent to the implementer when PROVE is required and no Proof Contract exists for the current semantics.</summary>
    public static string ContractMissing(WorkflowTask task, DirectTaskDefinition definition, string? problem = null)
        => $"Formal PROVE is required for this task and you have {(problem is null ? "not submitted a Proof Contract for the current semantics" : $"no usable Proof Contract for the current semantics (your last submission was not recorded: {problem})")}. Before the work can be proved you must call the `{AgentProveRequestSignal.ToolName}` tool exactly once with a short rationale and 1 to {ProofContract.MaxClaims} claims, each with an id, a statement of what must be true, an acceptance criterion and the evidence it needs. {EvidenceVocabulary(task, definition)} Do not change the code only to make a claim easier to prove.";

    /// <summary>How the implementer is told to write the Proof Contract on a run that forces PROVE.</summary>
    public static string RequiredGuidance(WorkflowTask task, DirectTaskDefinition definition)
        => $"Formal PROVE is required for this task. After your work verifies, an independent prover will judge a Proof Contract that YOU write. Before you finish, call the `{AgentProveRequestSignal.ToolName}` tool exactly once with a short rationale and 1 to {ProofContract.MaxClaims} claims: the consequential things that must be true for this work to be correct (not a restatement of your tests), each with an id (short, stable, e.g. c1), a statement, an acceptance criterion and the evidence it needs. {EvidenceVocabulary(task, definition)} The contract is immutable for its semantic revision, so write it once and honestly; it is not itself proof of anything.";

    /// <summary>How the agent is told it may ask for formal proof. Narrow on purpose: it only ever adds rigor.</summary>
    public static string RequestSignal(WorkflowTask task, DirectTaskDefinition definition)
        => $"{AgentProveRequestSignal.ToolName}: only for consequential claims that ordinary checks cannot establish alone (behaviour a test suite does not cover, a property needing a targeted observation). {EvidenceVocabulary(task, definition, brief: true)} It does not stop your turn and cannot be withdrawn. Not for routine work.";

    /// <summary>
    /// The closed evidence vocabulary, stated for THIS task: which controller checks exist to be cited and whether an independent review exists at all. A requirement the task can
    /// never satisfy is refused when the contract is recorded, so the agent is told up front rather than discovering it by being refused. <paramref name="brief"/> states the same
    /// facts in fewer words (the tool's own description carries the general vocabulary).
    /// </summary>
    private static string EvidenceVocabulary(WorkflowTask task, DirectTaskDefinition definition, bool brief = false)
    {
        var checks = string.Join(", ", definition.Checks.Select(c => $"`{c.Id}`"));
        var review = task.IsReviewRequired
            ? "`review:current-pass` (this task requires an independent review, so its current pass can be required)"
            : "Do NOT use `review:current-pass`: no independent review is required for this task, so none will exist and the claim could never be satisfied";

        return brief
            ? $"Evidence can cite only: `verification:<check>` (the configured checks are {checks}, and no others), `observation:<short description>`; {review}."
            : $"Evidence is a list drawn from a closed vocabulary: `verification:<check>` (a controller check that must have passed; the configured checks are {checks}, and no others), {review}, and `observation:<short description>` (a targeted observation the prover must make itself with a narrow command). Every claim needs at least one entry.";
    }

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

    private static void RenderClaimResults(StringBuilder text, IReadOnlyList<ProofClaimResult> results, bool onlyNotPassed = false)
    {
        foreach (var result in results)
        {
            if (onlyNotPassed && result.Verdict == ProofVerdict.Pass)
                continue;

            text.AppendLine($"- Claim {result.ClaimId}: {ProofReport.VerdictName(result.Verdict).ToUpperInvariant()}. {result.Summary.Trim()}");
        }
    }
}
