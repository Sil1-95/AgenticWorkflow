using System.Diagnostics;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Application.Direct;

// The conditional independent REVIEW. Everything here happens after the controller's own verification passed and before the task may complete or open its ACCEPT
// gate. The review is judgment, not verification: a fresh reviewer conversation reads the verified implementation and submits one structured report through a typed
// capability. The controller records it bound to exactly the semantic revision, implementation revision and verification run it judged; Core refuses anything stale.
public sealed partial class DirectPathRunner
{
    private const int ReviewerTurns = 2;
    private const int MaxRecordedReviewerCommands = Review.MaxCommands;

    /// <summary>The result of running the independent review: the run stops (nothing, or nothing durable, recorded), or a review was recorded on <see cref="RunState.Snapshot"/>.</summary>
    private sealed record ReviewStep(DirectRunResult? Stop);

    /// <summary>
    /// Records the agent's request for an independent REVIEW when it raised one in <paramref name="turnNumber"/>. Additive and never terminal: it is recorded and the turn
    /// continues exactly as before (the agent may have legitimate implementation changes in the same turn). A request for a review that is already required, or whose text
    /// Core would refuse, records nothing and is noted; it never fails the turn. More than one request in a turn is one request.
    /// </summary>
    private async Task TryRecordReviewRequestAsync(RunState run, ProfileId profile, AgentSessionId session, AgentTurnNumber turnNumber, CancellationToken ct)
    {
        if (_clarifications is not { } support || !support.Signals.CanSignal(profile))
            return;

        var requests = await ReadSignalsAsync(run, "agent review requests", () => support.Signals.CollectReviewRequestsAsync(session, turnNumber, ct)).ConfigureAwait(false);
        if (requests.Count == 0)
            return;

        var task = run.Snapshot!.Task;

        if (task.ReviewRequestRefusal() is { } refusal)
        {
            run.Notes.Add($"the agent asked for an independent review; nothing was recorded: {refusal}");
            return;
        }

        if (ReviewRequest.TextProblem(requests[0].Reason) is { } problem)
        {
            run.Notes.Add($"the agent asked for an independent review with an unusable reason; nothing was recorded: {problem}");
            return;
        }

        run.Snapshot = await CommitAsync(run, () => _tasks.RequestReviewAsync(run.Snapshot!, requests[0].Reason, turnNumber.Value, session.Value, ct)).ConfigureAwait(false);

        run.Notes.Add(requests.Count > 1
            ? $"the agent asked for an independent review {requests.Count} times in one turn; one request was recorded"
            : "the agent asked for an independent review; it was recorded and the verified work will be reviewed before the task completes");
    }

    /// <summary>
    /// What follows a passing controller verification: the human's ACCEPT gate when acceptance requires it, otherwise the controller's own completion. The same for a task
    /// that never needed a review and for one whose review passed; Core refuses either unless every requirement, the review included, is satisfied.
    /// </summary>
    private async Task<DirectRunResult> FinishVerifiedAsync(RunState run, TaskSnapshot snapshot, CancellationToken ct)
    {
        var reviewed = (snapshot.Task.IsReviewRequired ? "; the independent review passed" : string.Empty) + (snapshot.Task.IsProveRequired ? "; the formal proof passed" : string.Empty);

        if (snapshot.Task.Acceptance == AcceptanceRequirement.HumanRequired)
        {
            // The controller, not the agent, opens the human's final ACCEPT over exactly this implementation and this verification. The workflow
            // becomes AWAITING_HUMAN and stays so durably: the session is stopped below and the gate is decided later, from any process.
            snapshot = await CommitAsync(run, () => _tasks.OpenAcceptGateAsync(snapshot, ct)).ConfigureAwait(false);
            run.Snapshot = snapshot;

            return Build(run, DirectRunStatus.AwaitingAcceptance, $"verified: current implementation passed controller verification{reviewed}; awaiting the human ACCEPT", snapshot);
        }

        // No acceptance required: the controller's own verification (and, when required, the independent review) is the confidence this task asked for, so the controller
        // records the completion. No gate, no human action. The agent's "done" played no part in it.
        try
        {
            snapshot = await CommitAsync(run, () => _tasks.CompleteVerifiedAsync(snapshot, ct)).ConfigureAwait(false);
        }
        catch (CompletionNotEligibleException e)
        {
            return Build(run, DirectRunStatus.Rejected, $"completion_refused: {string.Join(", ", e.Eligibility.Reasons)}", snapshot, e.Eligibility);
        }

        run.Snapshot = snapshot;
        return Build(run, DirectRunStatus.Completed, $"verified: current implementation passed controller verification{reviewed}; acceptance is not required", snapshot);
    }

    /// <summary>
    /// The identity the review records for the implementer's conversation (so Core can refuse a review that came from it): the native conversation reference when the
    /// runtime has one, otherwise the session id. When no implementer session is running (a recovered review) it is read from where the run's conversation was kept.
    /// </summary>
    private async Task<string> ImplementerIdentityAsync(RunState run, AgentSessionId? session, CancellationToken ct)
    {
        if (session is { } live)
            return _conversations?.Runtime.ConversationOf(live)?.Reference ?? live.Value;

        if (_conversations is { } support)
        {
            var record = await FindRecordAsync(run, support, ct).ConfigureAwait(false);
            if (record is not null)
                return record.Conversation.Reference;
        }

        return $"implementer-of-{run.TaskId}";
    }

    /// <summary>
    /// Runs one independent review of the current verified implementation, in a brand-new reviewer conversation that is never the implementer's and never an earlier
    /// reviewer's. Ends either with the review recorded (see <see cref="WorkflowTask.CurrentReview"/>) or with a stop. A reviewer that cannot be started or never reports
    /// leaves the review due: the run is stranded as recoverable and a retry starts a fresh reviewer over the same deterministic packet.
    /// </summary>
    private async Task<ReviewStep> RunIndependentReviewAsync(RunState run, DirectTaskDefinition definition, string implementerIdentity, ImplementationState runBaseline, CancellationToken ct)
    {
        run.ReviewPhase = true;
        run.Phase = Phases.Reviewing;

        var resolved = await ResolveReviewerAsync(run, definition).ConfigureAwait(false);
        if (resolved.Stop is { } unresolved)
            return new ReviewStep(unresolved);

        var reviewerProfile = resolved.Profile!;

        if (_clarifications is not { } support || !support.Signals.CanSignal(reviewerProfile))
        {
            return new ReviewStep(Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"review_unavailable: the runtime cannot receive a review report from reviewer profile '{reviewerProfile}'; no review was started",
                run.Snapshot));
        }

        var task = run.Snapshot!.Task;
        var before = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
        var changed = DifferingPaths(runBaseline, before);
        var prompt = ReviewPrompts.Reviewer(task, definition, changed);

        var request = new AgentSessionRequest(reviewerProfile, definition.WorkspacePath, $"review {run.TaskId} round {task.Reviews.Length + 1}")
        {
            Purpose = AgentSessionPurpose.Review,
            ObservableChecks = _evidence is null ? [] : definition.Checks,
        };

        var startup = Stopwatch.StartNew();
        var reviewer = await _runtime.StartSessionAsync(Attended(run, request), ct).ConfigureAwait(false);

        try
        {
            run.Notes.Add($"independent reviewer started in {startup.ElapsedMilliseconds} ms (round {task.Reviews.Length + 1}, profile '{reviewerProfile}')");

            // Never recorded as the run's conversation: a reviewer is a fresh conversation every time, and nothing ever resumes it.
            var reviewerIdentity = _conversations?.Runtime.ConversationOf(reviewer)?.Reference ?? reviewer.Value;

            await BeginEvidenceWindowAsync(reviewer, run, ct).ConfigureAwait(false);

            var active = Stopwatch.StartNew();
            var submission = await CollectSubmissionAsync(run, support, reviewer, prompt, ct).ConfigureAwait(false);
            if (submission.Stop is { } stopped)
                return new ReviewStep(stopped);

            active.Stop();

            // Read-only is checked, never assumed: the workspace must be exactly what it was when the reviewer started.
            var after = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
            if (after.Fingerprint != before.Fingerprint)
            {
                var paths = DifferingPaths(before, after);

                return new ReviewStep(Build(
                    run,
                    DirectRunStatus.ReviewerModifiedWorkspace,
                    $"reviewer_modified_workspace: the reviewer changed the workspace ({(paths.Count == 0 ? "the workspace fingerprint moved" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $", and {paths.Count - 5} more" : string.Empty))}); its report was not recorded and nothing was reverted",
                    run.Snapshot));
            }

            ReviewReport report;
            try
            {
                report = BuildReport(submission.Report!);
            }
            catch (Exception e) when (e is ArgumentException or DomainException)
            {
                return new ReviewStep(Build(run, DirectRunStatus.ExecutionFailure, $"review_report_invalid: {e.Message} The review was not recorded.", run.Snapshot));
            }

            var observed = await CollectEvidenceAsync(reviewer, run, ct).ConfigureAwait(false);
            var commands = observed is { IsTrustworthy: true }
                ? observed.Activities.Where(a => a.Kind == ObservedActivityKind.Command && !string.IsNullOrWhiteSpace(a.Command)).Select(a => a.Command!).ToList()
                : [];

            if (observed is not { IsTrustworthy: true })
                run.Notes.Add("the reviewer's own commands could not be observed; none are recorded");

            var rerun = commands.Any(c => definition.Checks.Any(check => CommandForm.Matches(check.Executable, check.Arguments, c)));
            var recorded = commands.Take(MaxRecordedReviewerCommands).Select(c => Bound(c, Review.MaxCommandLength)).ToList();

            run.Phase = Phases.RecordingReview;
            run.Snapshot = await CommitAsync(
                run,
                () => _tasks.RecordReviewAsync(run.Snapshot!, report, reviewerProfile, reviewerIdentity, implementerIdentity, active.ElapsedMilliseconds, recorded, commands.Count, rerun, ct)).ConfigureAwait(false);

            var review = run.Snapshot.Task.Reviews[^1];

            if (review.Verdict == ReviewVerdict.ChangesRequired)
            {
                // The state the repair stretch is judged against, kept before the implementer is told: the repair can then be recovered with the scope judged correctly.
                try
                {
                    await support.Baselines.SaveAsync(run.TaskId, new GateId(review.Id.Value), after, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    run.Notes.Add($"the workspace baseline of the review's findings could not be kept ({e.GetType().Name}: {e.Message}); a recovered repair would be judged against the workspace at recovery");
                }
            }

            return new ReviewStep(null);
        }
        finally
        {
            await StopStageSessionAsync(run, reviewer).ConfigureAwait(false);
        }
    }

    private sealed record Submission(DirectRunResult? Stop, AgentReviewSubmission? Report);

    /// <summary>
    /// Sends the reviewer its packet and waits for its one structured report. A turn that ends without one gets a single reminder in the same reviewer session; still none,
    /// more than one, or an error ends the run with nothing recorded. Nothing is read from the terminal or a transcript.
    /// </summary>
    private async Task<Submission> CollectSubmissionAsync(RunState run, AgentClarificationSupport support, AgentSessionId reviewer, string firstPrompt, CancellationToken ct)
    {
        var prompt = firstPrompt;

        for (var attempt = 1; attempt <= ReviewerTurns; attempt++)
        {
            run.Phase = Phases.Sending;
            var turnNumber = await _runtime.SendTurnAsync(reviewer, prompt, ct).ConfigureAwait(false);

            run.Phase = Phases.WaitingForReviewer;
            var outcome = await _runtime.WaitForTurnAsync(reviewer, turnNumber, _options.TurnTimeout, ct).ConfigureAwait(false);

            var followed = await FollowNativeInteractionsAsync(run, turnNumber, reviewer, outcome, ct).ConfigureAwait(false);
            if (followed.Stop is { } nativeStop)
            {
                // The wait could not be answered and nobody is there: the reviewer session is about to be stopped, so the wait is closed the same way an implementer's is.
                return new Submission(await EndStageWaitAsync(run, reviewer, nativeStop).ConfigureAwait(false), null);
            }

            outcome = followed.Outcome;

            if (outcome.Status != AgentTurnStatus.Done)
            {
                return new Submission(
                    Build(run, ToStatus(outcome.Status), $"reviewer_{outcome.Status.ToString().ToLowerInvariant()}: {outcome.Reason ?? "no reason given"}", run.Snapshot),
                    null);
            }

            var submissions = await ReadSignalsAsync(run, "review reports", () => support.Signals.CollectReviewSubmissionsAsync(reviewer, turnNumber, ct)).ConfigureAwait(false);

            if (submissions.Count > 1)
            {
                return new Submission(
                    Build(run, DirectRunStatus.ExecutionFailure, $"review_report_conflict: the reviewer submitted {submissions.Count} reports in one turn; none was recorded", run.Snapshot),
                    null);
            }

            if (submissions.Count == 1)
                return new Submission(null, submissions[0]);

            prompt = ReviewPrompts.NotSubmitted();
        }

        return new Submission(
            Build(run, DirectRunStatus.ExecutionFailure, $"review_not_submitted: the reviewer ended {ReviewerTurns} turn(s) without submitting a review; nothing was recorded", run.Snapshot),
            null);
    }

    private static ReviewReport BuildReport(AgentReviewSubmission submission)
    {
        var verdict = submission.Verdict?.Trim().ToUpperInvariant() switch
        {
            "PASS" => ReviewVerdict.Pass,
            "CHANGES_REQUIRED" => ReviewVerdict.ChangesRequired,
            _ => throw new ArgumentException($"The verdict must be PASS or CHANGES_REQUIRED (was '{Bound(submission.Verdict ?? string.Empty, 40)}')."),
        };

        return new ReviewReport(
            verdict,
            submission.Summary,
            submission.Findings.Select(f => new ReviewFinding(f.Title, f.Evidence, f.Impact, f.Recommendation, f.Location)),
            submission.Notes);
    }

    /// <summary>The reviewer role's profile: its explicit binding, or the profile the project explicitly configured (bound now). Never the implementer's, never a guess.</summary>
    private async Task<(ProfileId? Profile, DirectRunResult? Stop)> ResolveReviewerAsync(RunState run, DirectTaskDefinition definition)
    {
        var task = run.Snapshot!.Task;

        if (task.RoleBindings.TryGetValue(WellKnownRoles.Reviewer, out var binding))
        {
            if (definition.ReviewerProfile is { } configured && configured.Id != binding.Profile.Id)
            {
                return (null, Build(
                    run,
                    DirectRunStatus.Rejected,
                    $"reviewer_profile_mismatch: role '{WellKnownRoles.Reviewer}' is bound to '{binding.Profile.Id}', not the configured '{configured.Id}'",
                    run.Snapshot));
            }

            return (binding.Profile.Id, null);
        }

        if (definition.ReviewerProfile is not { } profile)
        {
            return (null, Build(
                run,
                DirectRunStatus.ReviewerUnbound,
                "reviewer_unbound: an independent review is required but no reviewer profile is configured for this project; the implementer is never used as the reviewer and no profile was chosen for it. Bind the reviewer role explicitly (bind_role / 'aw bind'), then continue; or abort this run",
                run.Snapshot));
        }

        run.Snapshot = await CommitAsync(run, () => _tasks.BindRoleAsync(run.Snapshot!, DirectPathRoles.Reviewer, profile)).ConfigureAwait(false);
        return (profile.Id, null);
    }

    /// <summary>
    /// A reviewer's native interaction that nobody could answer. The reviewer session is stopped here (the run only stops its own implementer session), and the wait that
    /// was recorded as AWAITING_HUMAN becomes BLOCKED once it is confirmed gone, exactly as for an implementer.
    /// </summary>
    private async Task<DirectRunResult> EndStageWaitAsync(RunState run, AgentSessionId reviewer, DirectRunResult stop)
    {
        if (stop.Status == DirectRunStatus.NativeInteractionUnavailable && await StopSessionAsync(reviewer, run).ConfigureAwait(false))
        {
            run.StageSessionStopped = reviewer;

            if (await TryBlockAfterSessionEndedAsync(run, reviewer).ConfigureAwait(false) is { } blocked)
                return stop with { Task = blocked };
        }

        return stop;
    }

    private async Task StopStageSessionAsync(RunState run, AgentSessionId reviewer)
    {
        if (run.StageSessionStopped == reviewer)
            return;

        await StopSessionAsync(reviewer, run).ConfigureAwait(false);
    }

    /// <summary>Records RUNNING to BLOCKED for a reviewer that changed the workspace, with the same guards as the other blocks. Not recoverable: the workspace is preserved for a person.</summary>
    private Task<TaskSnapshot?> TryBlockReviewerWorkspaceAsync(RunState run, DirectRunResult result)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            ReviewerWorkspaceBlock.BlockedReason(result.Reason),
            "after the reviewer changed the workspace");

    /// <summary>Records RUNNING to BLOCKED for a review that still required changes after the last permitted turn. A verdict, not recoverable work.</summary>
    private Task<TaskSnapshot?> TryBlockUnresolvedReviewAsync(RunState run, DirectRunResult result)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            ReviewUnresolvedBlock.BlockedReason(result.Reason),
            "after the review stayed unresolved");
}
