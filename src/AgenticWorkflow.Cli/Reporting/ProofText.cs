using System.Text;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// How formal PROVE reads in <c>aw status</c> and in the run report, from the authoritative history alone. It shows the requirement, the Proof Contract with every claim
/// and its <c>requires:</c> lines, the prover's explicit profile, the current state against the contract, semantics, implementation, verification and review, every proof
/// round in order with its per-claim verdicts and evidence references, the prover's cost, and what happens next. It never shows a transcript: a proof holds none, only the
/// structured report and its provenance.
/// </summary>
internal static class ProofText
{
    private const int ShortSessionLength = 8;
    private const int SummaryLimit = 400;
    private const int ClaimLimit = 300;

    /// <summary>True when there is anything to say about PROVE for this task.</summary>
    public static bool Applies(WorkflowTask task) => task.IsProveRequired || !task.ProofContracts.IsEmpty || !task.Proofs.IsEmpty;

    /// <summary>The one-line state: <c>PASS (current)</c>, <c>PASS (STALE)</c>, <c>FAIL</c>, <c>INCONCLUSIVE</c>, <c>none yet</c>.</summary>
    public static string State(WorkflowTask task) => task.ProofStatus switch
    {
        ProofStatus.PassedCurrent => "PASS (current)",
        ProofStatus.PassedStale => "PASS (STALE: the task changed since; historical, it cannot complete the run)",
        ProofStatus.Failed => "FAIL (the implementer has not answered the proof yet)",
        ProofStatus.Inconclusive => "INCONCLUSIVE (the implementer has not answered the proof yet)",
        ProofStatus.NotPassedStale => "not passed (STALE: the task changed since; a fresh proof is due once the work is verified)",
        _ => NoneYet(task),
    };

    private static string NoneYet(WorkflowTask task)
    {
        if (task.IsProofContractMissing)
            return "none yet (BLOCKED on the Proof Contract: the implementer has not submitted one for the current semantics)";

        return task.IsProveDue ? "none yet (due now: the work is verified)" : "none yet (due once the work is verified)";
    }

    /// <summary>The header line of <c>aw status</c> that describes the requirement.</summary>
    public static void AppendRequirement(StringBuilder text, WorkflowTask task)
    {
        if (task.ProveRequired)
            text.AppendLine("PROVE: REQUIRED (--require prove; fixed when the run was created)");
        else if (task.ProveEscalated)
            text.AppendLine($"PROVE: REQUIRED (requested by the agent with a Proof Contract: {Format.Bounded(task.ProofContracts[0].Rationale, 200)}; additive, it cannot be lowered)");
    }

    /// <summary>The full proof section: prover, contract, state, rounds in order, next step.</summary>
    public static void Append(StringBuilder text, WorkflowTask task)
    {
        if (!Applies(task))
            return;

        text.AppendLine(task.RoleBindings.TryGetValue(WellKnownRoles.Prover, out var binding)
            ? $"Prover: profile '{binding.Profile.Id}' (explicitly bound; a fresh conversation for every proof)"
            : "Prover: NOT BOUND (no proverProfile; neither the implementer's nor the reviewer's profile is ever used instead)");

        AppendContract(text, task);
        text.AppendLine($"Proof: {State(task)}");

        foreach (var proof in task.Proofs)
            AppendRound(text, task, proof);

        var next = NextStep(task);

        if (next is not null)
            text.AppendLine($"Proof next step: {next}");
    }

    /// <summary>The current Proof Contract with every claim and its requirements, then the number of earlier (stale) contracts.</summary>
    public static void AppendContract(StringBuilder text, WorkflowTask task)
    {
        if (task.CurrentProofContract is not { } contract)
        {
            text.AppendLine(task.ProofContracts.IsEmpty
                ? "Proof Contract: none submitted"
                : $"Proof Contract: none for the current semantic revision ({task.ProofContracts.Length} earlier one(s) are stale and prove nothing now)");
            return;
        }

        text.AppendLine($"Proof Contract: {contract.Claims.Length} claim(s), hash {Short(contract.ContentHash, 12)}, bound to semantic revision {task.CurrentSemanticRevision.Ordinal} (immutable for it)");
        text.AppendLine($"  rationale: {Format.Bounded(contract.Rationale, SummaryLimit)}");

        foreach (var claim in contract.Claims)
        {
            text.AppendLine($"  claim {claim.Id}: {Format.Bounded(claim.Statement, ClaimLimit)}");
            text.AppendLine($"    criterion: {Format.Bounded(claim.AcceptanceCriterion, ClaimLimit)}");

            foreach (var requirement in claim.Evidence)
                text.AppendLine($"    requires: {Format.Bounded(requirement.ToString(), ClaimLimit)}");
        }

        var earlier = task.ProofContracts.Length - 1;

        if (earlier > 0)
            text.AppendLine($"  ({earlier} earlier contract(s) for superseded semantics are kept as history)");
    }

    /// <summary>The rounds in the order they were recorded; the final run report keeps them all.</summary>
    public static void AppendRound(StringBuilder text, WorkflowTask task, Proof proof)
    {
        var currency = task.IsProofCurrent(proof) ? "current" : "historical";
        var verdict = ProofReport.VerdictName(proof.Verdict).ToUpperInvariant();
        var cost = $"{Format.Duration(TimeSpan.FromMilliseconds(proof.ProverMilliseconds))}, {proof.ProverCommandCount} prover command(s)";
        var rerun = proof.ConfiguredCheckRerun
            ? $"a configured check was re-run (duplicated verification {Format.Duration(TimeSpan.FromMilliseconds(proof.DuplicatedVerificationMilliseconds))})"
            : "no configured check re-run";
        var review = proof.ReviewId is null ? "no review evidence" : proof.ReviewEvidenceReused ? "review evidence reused" : "review passed, its evidence not cited";

        text.AppendLine($"  Proof round {proof.Round}: {verdict} ({currency})");
        text.AppendLine($"    prover conversation {Short(proof.ProverSession)}, independent of the implementer's {Short(proof.ImplementerSession)}; report {Short(proof.ReportHash, 12)}; contract {Short(proof.ContractHash, 12)}");
        text.AppendLine($"    cost: {cost}; {rerun}; {review}");
        text.AppendLine($"    summary: {Format.Bounded(proof.Report.Summary, SummaryLimit)}");

        foreach (var result in proof.Report.Claims)
        {
            var refs = result.Evidence.IsEmpty ? "no evidence cited" : string.Join(", ", result.Evidence.Select(e => e.ToString()));
            text.AppendLine($"    claim {result.ClaimId}: {ProofReport.VerdictName(result.Verdict).ToUpperInvariant()} ({Format.Bounded(refs, ClaimLimit)})");
            text.AppendLine($"      {Format.Bounded(result.Summary, ClaimLimit)}");
        }

        if (proof.ProverCommands.Length > 0)
        {
            var number = 1;

            foreach (var command in proof.ProverCommands)
                text.AppendLine($"    command {number++}: {Format.Bounded(command, ClaimLimit)}");
        }
    }

    /// <summary>What the workflow does next about PROVE, or null when nothing about it is pending.</summary>
    public static string? NextStep(WorkflowTask task)
    {
        if (!task.IsProveRequired || task.WorkflowState is WorkflowState.Completed or WorkflowState.Cancelled)
            return null;

        if (task.IsProofContractMissing && task.ProofStatus is ProofStatus.None or ProofStatus.PassedStale or ProofStatus.NotPassedStale)
            return "the implementer submits a Proof Contract for the current semantics (one reminder, then the run blocks), then the work is verified" + (task.IsReviewRequired ? ", reviewed" : string.Empty) + " and a fresh prover proves it";

        return task.ProofStatus switch
        {
            ProofStatus.PassedCurrent => task.Acceptance == AcceptanceRequirement.HumanRequired
                ? "the proof passed; the ACCEPT gate is next (PROVE is never ACCEPT: you still decide)"
                : "the proof passed; the controller completes the run",
            ProofStatus.Failed or ProofStatus.Inconclusive => "the ORIGINAL implementer repairs what the proof found (resumed with a bounded delta only), the repair is verified again" + (task.IsReviewRequired ? ", reviewed" : string.Empty) + ", then a NEW fresh prover proves it",
            ProofStatus.PassedStale or ProofStatus.NotPassedStale => "re-verify the current implementation" + (task.IsReviewRequired ? ", re-review it" : string.Empty) + ", then a NEW fresh prover proves it",
            _ => task.IsProveDue
                ? "a fresh independent prover proves the contract over the verified work"
                : "implement and verify first" + (task.IsReviewRequired ? ", then review" : string.Empty) + ", then a fresh independent prover proves the contract",
        };
    }

    private static string Short(string value, int length = ShortSessionLength)
        => value.Length <= length ? value : value[..length];
}
