using System.Diagnostics;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Direct;

// The conditional formal PROVE. The implementer writes a Proof Contract (a typed signal); once the work verified (and, when required, was reviewed and passed), a
// fresh, read-only prover conversation judges the contract claim by claim and submits one structured proof through a typed capability. The controller records it bound to
// exactly the contract, semantic revision, implementation revision, verification run and review it judged; Core refuses anything stale. The proof is judgment about claims
// the ordinary checks do not establish alone; it never replaces the controller's verification and never completes the task by itself.
public sealed partial class DirectPathRunner
{
    private const int ProverTurns = 2;

    /// <summary>
    /// What the required stages that follow a passing controller verification (contract, REVIEW, PROVE) leave the run with: nothing outstanding (<see cref="Passed"/>), a stop,
    /// or work the implementer must do (<see cref="RepairPrompt"/>, sent to its own conversation) with the status the run ends in if it has no turn left for it.
    /// </summary>
    private sealed record StageResult(DirectRunResult? Stop, string? RepairPrompt, DirectRunStatus UnresolvedStatus, string UnresolvedWhat)
    {
        public static StageResult Passed { get; } = new(null, null, DirectRunStatus.Completed, string.Empty);

        public static StageResult Stopped(DirectRunResult stop) => new(stop, null, DirectRunStatus.Completed, string.Empty);

        public static StageResult Repair(string prompt, DirectRunStatus unresolvedStatus, string what) => new(null, prompt, unresolvedStatus, what);
    }

    /// <summary>
    /// The required stages after a passing verification, in order: the implementer's Proof Contract (when PROVE is required), the independent REVIEW (when required), the
    /// independent PROVE (when required). Each leaves the work either satisfied, stopped or for the implementer to repair; a repair is verified again and every later stage is
    /// judged afresh by a NEW independent conversation. <paramref name="session"/> is the implementer's live session, or null when a recovered stage runs without one.
    /// </summary>
    private async Task<StageResult> RunRequiredStagesAsync(RunState run, DirectTaskDefinition definition, AgentSessionId? session, ImplementationState baseline, CancellationToken ct)
    {
        var task = run.Snapshot!.Task;

        // The contract comes first so that a review is never spent on work that cannot then be proved.
        if (task.IsProofContractMissing)
        {
            if (session is null || run.ContractReminded)
            {
                return StageResult.Stopped(Build(
                    run,
                    DirectRunStatus.ProofContractMissing,
                    $"proof_contract_missing: formal PROVE is required and the implementer submitted no valid Proof Contract{(run.ContractProblem is null ? string.Empty : $" ({run.ContractProblem})")} after the one reminder; nothing was proved. The work is preserved",
                    run.Snapshot));
            }

            run.ContractReminded = true;
            return StageResult.Repair(ProofPrompts.ContractMissing(task, definition, run.ContractProblem), DirectRunStatus.ProofContractMissing, "no Proof Contract was submitted");
        }

        if (task.IsReviewDue)
        {
            var implementerIdentity = await ImplementerIdentityAsync(run, session, ct).ConfigureAwait(false);
            var reviewed = await RunIndependentReviewAsync(run, definition, implementerIdentity, baseline, ct).ConfigureAwait(false);

            if (reviewed.Stop is { } reviewStop)
                return StageResult.Stopped(reviewStop);

            if (!run.Snapshot!.Task.HasCurrentReviewPass)
                return StageResult.Repair(ReviewPrompts.Findings(run.Snapshot.Task.Reviews[^1]), DirectRunStatus.ReviewUnresolved, "the independent review still requires changes");
        }

        if (run.Snapshot!.Task.IsProveDue)
        {
            var implementerIdentity = await ImplementerIdentityAsync(run, session, ct).ConfigureAwait(false);
            var proved = await RunIndependentProofAsync(run, definition, implementerIdentity, baseline, ct).ConfigureAwait(false);

            if (proved.Stop is { } proofStop)
                return StageResult.Stopped(proofStop);

            if (!run.Snapshot!.Task.HasCurrentProofPass)
                return StageResult.Repair(ProofPrompts.Findings(run.Snapshot.Task.CurrentProof!), DirectRunStatus.ProofUnresolved, "the formal proof did not pass");
        }

        return StageResult.Passed;
    }

    /// <summary>
    /// Records the Proof Contract the agent submitted in <paramref name="turnNumber"/>, when it raised one. Additive and never terminal: it is recorded and the turn
    /// continues exactly as before. A contract that Core would refuse (malformed, out of bounds, a second one for a semantic revision that has one) records nothing, is noted and
    /// is the reason the one reminder gives; it never fails the turn. More than one contract in a turn is ambiguous and none is recorded.
    /// </summary>
    private async Task TryRecordProveRequestAsync(RunState run, DirectTaskDefinition definition, ProfileId profile, AgentSessionId session, AgentTurnNumber turnNumber, CancellationToken ct)
    {
        if (_clarifications is not { } support || !support.Signals.CanSignal(profile))
            return;

        var requests = await ReadSignalsAsync(run, "agent Proof Contracts", () => support.Signals.CollectProveRequestsAsync(session, turnNumber, ct)).ConfigureAwait(false);
        if (requests.Count == 0)
            return;

        var task = run.Snapshot!.Task;

        if (requests.Count > 1)
        {
            run.ContractProblem = $"{requests.Count} contracts were submitted in one turn and none was recorded";
            run.Notes.Add($"the agent submitted {requests.Count} Proof Contracts in one turn; none was recorded");
            return;
        }

        if (task.ProofContractRefusal() is { } refusal)
        {
            run.Notes.Add($"the agent submitted a Proof Contract; nothing was recorded: {refusal}");
            return;
        }

        var request = requests[0];
        ProofContract? contract;
        string? problem;

        try
        {
            var claims = request.Claims.Select(c => new ProofClaim(
                c.Id,
                c.Statement,
                c.Evidence.Select(e => ProofEvidenceRequirement.Parse(e.Kind, e.Subject)),
                c.AcceptanceCriterion)).ToList();

            problem = ProofContract.Problem(request.Rationale, claims) ?? UnsatisfiableRequirement(task, definition, claims);
            contract = null;

            if (problem is null)
            {
                run.Snapshot = await CommitAsync(run, () => _tasks.RecordProofContractAsync(run.Snapshot!, request.Rationale, claims, turnNumber.Value, session.Value, ct)).ConfigureAwait(false);
                contract = run.Snapshot.Task.CurrentProofContract;
            }
        }
        catch (Exception e) when (e is ArgumentException or DomainException)
        {
            problem = e.Message;
            contract = null;
        }

        if (problem is not null || contract is null)
        {
            var bounded = Bound(problem ?? "the contract was not recorded", 300);
            run.ContractProblem = bounded;
            run.Notes.Add($"the agent submitted a malformed Proof Contract; nothing was recorded: {bounded}");
            return;
        }

        run.ContractProblem = null;
        run.Notes.Add(task.ProveRequired
            ? $"the agent submitted its Proof Contract ({contract.Claims.Length} claim(s)); the verified work will be proved before the task completes"
            : $"the agent asked for formal PROVE with a Proof Contract of {contract.Claims.Length} claim(s); it was recorded and the verified work will be proved before the task completes");
    }

    /// <summary>
    /// The first evidence requirement of <paramref name="claims"/> that this task can never satisfy, or null: <c>review:current-pass</c> where no independent review is
    /// required (no review will ever exist to cite) and <c>verification:&lt;check&gt;</c> for a check the project did not configure. Such a contract would make the proof
    /// necessarily INCONCLUSIVE, so it is refused at the boundary and the agent is told exactly what to change.
    /// </summary>
    private static string? UnsatisfiableRequirement(WorkflowTask task, DirectTaskDefinition definition, IEnumerable<ProofClaim> claims)
    {
        foreach (var claim in claims)
        {
            foreach (var requirement in claim.Evidence)
            {
                if (requirement.Kind == ProofEvidenceKind.Review && !task.IsReviewRequired)
                    return $"claim {claim.Id} requires review:current-pass, but no independent review is required for this task, so none will ever exist; use verification:<check> or observation:<description>";

                if (requirement.Kind == ProofEvidenceKind.Verification && definition.Checks.All(c => c.Id.Value != requirement.Subject))
                    return $"claim {claim.Id} requires verification:{requirement.Subject}, which is not a configured check (configured: {string.Join(", ", definition.Checks.Select(c => c.Id.Value))})";
            }
        }

        return null;
    }
    /// <summary>
    /// Runs one independent proof of the current verified (and reviewed, when required) implementation, in a brand-new prover conversation that is never the implementer's,
    /// a reviewer's or an earlier prover's. Ends either with the proof recorded (see <see cref="WorkflowTask.CurrentProof"/>) or with a stop. A prover that cannot be started or
    /// never reports leaves the proof due: the run is stranded as recoverable and a retry starts a fresh prover over the same deterministic packet.
    /// </summary>
    private async Task<ReviewStep> RunIndependentProofAsync(RunState run, DirectTaskDefinition definition, string implementerIdentity, ImplementationState runBaseline, CancellationToken ct)
    {
        run.ProvePhase = true;
        run.Phase = Phases.Proving;

        var resolved = await ResolveProverAsync(run, definition).ConfigureAwait(false);
        if (resolved.Stop is { } unresolved)
            return new ReviewStep(unresolved);

        var proverProfile = resolved.Profile!;

        if (_clarifications is not { } support || !support.Signals.CanSignal(proverProfile))
        {
            return new ReviewStep(Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"proof_unavailable: the runtime cannot receive a proof from prover profile '{proverProfile}'; no proof was started",
                run.Snapshot));
        }

        var task = run.Snapshot!.Task;
        var before = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
        var changed = DifferingPaths(runBaseline, before);
        var prompt = ProofPrompts.Prover(task, definition, changed);

        var request = new AgentSessionRequest(proverProfile, definition.WorkspacePath, $"prove {run.TaskId} round {task.Proofs.Length + 1}")
        {
            Purpose = AgentSessionPurpose.Prove,
            ObservableChecks = _evidence is null ? [] : definition.Checks,
        };

        var startup = Stopwatch.StartNew();
        var prover = await _runtime.StartSessionAsync(Attended(run, request), ct).ConfigureAwait(false);

        try
        {
            run.Notes.Add($"independent prover started in {startup.ElapsedMilliseconds} ms (round {task.Proofs.Length + 1}, profile '{proverProfile}')");

            // Never recorded as the run's conversation: a prover is a fresh conversation every time, and nothing ever resumes it.
            var proverIdentity = _conversations?.Runtime.ConversationOf(prover)?.Reference ?? prover.Value;

            await BeginEvidenceWindowAsync(prover, run, ct).ConfigureAwait(false);

            var active = Stopwatch.StartNew();
            ProofReport? report = null;
            IReadOnlyList<ObservedActivity> commandActivities = [];
            var trustworthy = false;
            string? refused = null;

            for (var attempt = 1; attempt <= ProverTurns; attempt++)
            {
                var submitted = await CollectProofSubmissionAsync(run, support, prover, prompt, ct).ConfigureAwait(false);
                if (submitted.Stop is { } stopped)
                    return new ReviewStep(stopped);

                if (submitted.Submission is null)
                {
                    if (attempt == ProverTurns)
                    {
                        // A proof that was submitted and refused is not the same failure as no proof at all: say which one it was.
                        var reason = refused is null
                            ? $"proof_not_submitted: the prover ended {ProverTurns} turn(s) without submitting a proof; nothing was recorded"
                            : $"proof_correction_not_submitted: the prover's proof was refused ({refused}) and it then submitted no corrected proof; nothing was recorded";

                        return new ReviewStep(Build(run, DirectRunStatus.ExecutionFailure, reason, run.Snapshot));
                    }

                    prompt = ProofPrompts.NotSubmitted();
                    continue;
                }

                try
                {
                    report = BuildProofReport(submitted.Submission);
                }
                catch (Exception e) when (e is ArgumentException or DomainException)
                {
                    return new ReviewStep(Build(run, DirectRunStatus.ExecutionFailure, $"proof_report_invalid: {e.Message} The proof was not recorded.", run.Snapshot));
                }

                var observed = await CollectEvidenceAsync(prover, run, ct).ConfigureAwait(false);
                trustworthy = observed is { IsTrustworthy: true };
                commandActivities = trustworthy
                    ? observed!.Activities.Where(a => a.Kind == ObservedActivityKind.Command && !string.IsNullOrWhiteSpace(a.Command)).ToList()
                    : [];

                // The report must resolve against the state it describes. One correction is allowed in the same prover session (a mistaken command number is the usual cause);
                // anything still wrong after that is refused and nothing is recorded.
                var recordedCount = Math.Min(commandActivities.Count, Proof.MaxCommands);
                if (run.Snapshot!.Task.ProofReportProblem(report, recordedCount) is not { } refusal)
                    break;

                if (attempt == ProverTurns)
                {
                    return new ReviewStep(Build(
                        run,
                        DirectRunStatus.ExecutionFailure,
                        $"proof_report_invalid: {Bound(refusal, 300)}. The proof was not recorded.",
                        run.Snapshot));
                }

                report = null;
                refused = Bound(refusal, 200);
                prompt = ProofPrompts.Refused(Bound(refusal, 300), recordedCount);
            }

            active.Stop();

            // Read-only is checked, never assumed: the workspace must be exactly what it was when the prover started.
            var after = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
            if (after.Fingerprint != before.Fingerprint)
            {
                var paths = DifferingPaths(before, after);

                return new ReviewStep(Build(
                    run,
                    DirectRunStatus.ProverModifiedWorkspace,
                    $"prover_modified_workspace: the prover changed the workspace ({(paths.Count == 0 ? "the workspace fingerprint moved" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $", and {paths.Count - 5} more" : string.Empty))}); its proof was not recorded and nothing was reverted",
                    run.Snapshot));
            }

            if (!trustworthy)
                run.Notes.Add("the prover's own commands could not be observed; none are recorded");

            var commands = commandActivities.Select(a => a.Command!).ToList();
            var rerun = commands.Any(c => definition.Checks.Any(check => CommandForm.Matches(check.Executable, check.Arguments, c)));
            var duplicatedMilliseconds = commandActivities
                .Where(a => definition.Checks.Any(check => CommandForm.Matches(check.Executable, check.Arguments, a.Command!)))
                .Sum(a => a.DurationMilliseconds ?? 0);
            var recorded = commands.Take(Proof.MaxCommands).Select(c => Bound(c, Proof.MaxCommandLength)).ToList();

            run.Phase = Phases.RecordingProof;
            run.Snapshot = await CommitAsync(
                run,
                () => _tasks.RecordProofAsync(run.Snapshot!, report!, proverProfile, proverIdentity, implementerIdentity, active.ElapsedMilliseconds, recorded, commands.Count, rerun, duplicatedMilliseconds, ct)).ConfigureAwait(false);

            var proof = run.Snapshot.Task.Proofs[^1];

            if (proof.Verdict != ProofVerdict.Pass)
            {
                // The state the repair stretch is judged against, kept before the implementer is told: the repair can then be recovered with the scope judged correctly.
                try
                {
                    await support.Baselines.SaveAsync(run.TaskId, new GateId(proof.Id.Value), after, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    run.Notes.Add($"the workspace baseline of the proof's findings could not be kept ({e.GetType().Name}: {e.Message}); a recovered repair would be judged against the workspace at recovery");
                }
            }

            return new ReviewStep(null);
        }
        finally
        {
            await StopStageSessionAsync(run, prover).ConfigureAwait(false);
        }
    }

    private sealed record ProofSubmissionResult(DirectRunResult? Stop, AgentProofSubmission? Submission);

    /// <summary>
    /// Sends the prover one message and waits for its turn to end, then reads the proof it submitted in that turn, if any. A turn that errors, an unanswerable native
    /// interaction or more than one submission in a turn ends the run with nothing recorded. Nothing is read from the terminal or a transcript. No submission is returned as
    /// null: the caller decides whether one reminder remains.
    /// </summary>
    private async Task<ProofSubmissionResult> CollectProofSubmissionAsync(RunState run, AgentClarificationSupport support, AgentSessionId prover, string prompt, CancellationToken ct)
    {
        run.Phase = Phases.Sending;
        var turnNumber = await _runtime.SendTurnAsync(prover, prompt, ct).ConfigureAwait(false);

        run.Phase = Phases.WaitingForProver;
        var outcome = await _runtime.WaitForTurnAsync(prover, turnNumber, _options.TurnTimeout, ct).ConfigureAwait(false);

        var followed = await FollowNativeInteractionsAsync(run, turnNumber, prover, outcome, ct).ConfigureAwait(false);
        if (followed.Stop is { } nativeStop)
            return new ProofSubmissionResult(await EndStageWaitAsync(run, prover, nativeStop).ConfigureAwait(false), null);

        outcome = followed.Outcome;

        if (outcome.Status != AgentTurnStatus.Done)
        {
            return new ProofSubmissionResult(
                Build(run, ToStatus(outcome.Status), $"prover_{outcome.Status.ToString().ToLowerInvariant()}: {outcome.Reason ?? "no reason given"}", run.Snapshot),
                null);
        }

        var submissions = await ReadSignalsAsync(run, "proofs", () => support.Signals.CollectProofSubmissionsAsync(prover, turnNumber, ct)).ConfigureAwait(false);

        if (submissions.Count > 1)
        {
            return new ProofSubmissionResult(
                Build(run, DirectRunStatus.ExecutionFailure, $"proof_conflict: the prover submitted {submissions.Count} proofs in one turn; none was recorded", run.Snapshot),
                null);
        }

        return new ProofSubmissionResult(null, submissions.Count == 1 ? submissions[0] : null);
    }

    private static ProofReport BuildProofReport(AgentProofSubmission submission)
        => new(
            submission.Summary,
            submission.Claims.Select(c => new ProofClaimResult(
                c.ClaimId,
                ProofReport.ParseVerdict(c.Verdict),
                c.Evidence.Select(ProofEvidenceRef.Parse),
                c.Summary)));

    /// <summary>The prover role's profile: its explicit binding, or the profile the project explicitly configured (bound now). Never the implementer's or the reviewer's, never a guess.</summary>
    private async Task<(ProfileId? Profile, DirectRunResult? Stop)> ResolveProverAsync(RunState run, DirectTaskDefinition definition)
    {
        var task = run.Snapshot!.Task;

        if (task.RoleBindings.TryGetValue(WellKnownRoles.Prover, out var binding))
        {
            if (definition.ProverProfile is { } configured && configured.Id != binding.Profile.Id)
            {
                return (null, Build(
                    run,
                    DirectRunStatus.Rejected,
                    $"prover_profile_mismatch: role '{WellKnownRoles.Prover}' is bound to '{binding.Profile.Id}', not the configured '{configured.Id}'",
                    run.Snapshot));
            }

            return (binding.Profile.Id, null);
        }

        if (definition.ProverProfile is not { } profile)
        {
            return (null, Build(
                run,
                DirectRunStatus.ProverUnbound,
                "prover_unbound: formal PROVE is required but no prover profile is configured for this project; neither the implementer nor the reviewer is ever used as the prover. A started run's configuration cannot change, so binding a prover late is unsupported: start a new run with 'proverProfile' configured (and --require prove), or 'aw abort' this one",
                run.Snapshot));
        }

        run.Snapshot = await CommitAsync(run, () => _tasks.BindRoleAsync(run.Snapshot!, DirectPathRoles.Prover, profile)).ConfigureAwait(false);
        return (profile.Id, null);
    }

    /// <summary>Records RUNNING to BLOCKED for a prover that changed the workspace, with the same guards as the other blocks. Not recoverable: the workspace is preserved for a person.</summary>
    private Task<TaskSnapshot?> TryBlockProverWorkspaceAsync(RunState run, DirectRunResult result)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            ProverWorkspaceBlock.BlockedReason(result.Reason),
            "after the prover changed the workspace");

    /// <summary>Records RUNNING to BLOCKED for a proof that was not established after the last permitted turn, or for a Proof Contract never submitted. A verdict, not recoverable work.</summary>
    private Task<TaskSnapshot?> TryBlockUnresolvedProofAsync(RunState run, DirectRunResult result)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            ProofUnresolvedBlock.BlockedReason(result.Reason),
            "after the formal proof stayed unresolved");
}
