using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>Tunables of a direct run that are not part of the task itself.</summary>
public sealed record DirectRunOptions
{
    /// <summary>How long one agent turn may take before it is reported as an error.</summary>
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Stop the session this run started when the run ends, whatever the outcome. Only that session is ever stopped.</summary>
    public bool StopSessionWhenDone { get; init; } = true;

    /// <summary>Time budget for stopping the session and for reloading task state after an unknown outcome.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when a person is available to act in the agent's own native interface (for example its permission prompt). The run then keeps waiting while
    /// the agent waits for that person, within <see cref="TurnTimeout"/>. False (the default) means nobody is there: the run stops with
    /// <see cref="DirectRunStatus.NativeInteractionUnavailable"/> instead of leaving a session nobody will answer. Either way the controller never answers the interaction.
    /// </summary>
    public bool WaitForHuman { get; init; }
}

/// <summary>
/// The first executable form of the Workflow V1.1 direct path: one root agent in one persistent session
/// implements, the controller independently verifies, failures go back to the SAME session, and only the
/// controller can record completion. It orchestrates ports; it knows no Herdr, SQLite, Git or process details.
/// <para>
/// Authority split: the agent may signal "done". It has no way to complete the workflow. After controller verification passes, what happens is the task's own
/// acceptance policy (<see cref="AcceptanceRequirement"/>, persisted when it was created): with none required the controller records the completion
/// (<see cref="WorkflowTaskService.CompleteVerifiedAsync"/>, <see cref="DirectRunStatus.Completed"/>); with the human required it opens the workflow-owned ACCEPT gate
/// (<see cref="WorkflowTaskService.OpenAcceptGateAsync"/>) and ends with <see cref="DirectRunStatus.AwaitingAcceptance"/>, the workflow durably AWAITING_HUMAN until only the
/// human's explicit ACCEPT (<see cref="WorkflowTaskService.AcceptGateAsync"/>, from any later process) completes it. That gate is the workflow's own: unlike a native
/// permission prompt it needs no live agent session, which this runner stops as usual.
/// </para>
/// <para>
/// Safety: the <see cref="TaskSnapshot"/> in hand is the basis of every mutation, so a task that moves while the agent
/// works yields <see cref="DirectRunStatus.StaleWorkflowState"/> and nothing is applied. A mutation whose outcome is
/// unknown (cancellation, storage failure) is never repeated: the run reloads and stops
/// (<see cref="DirectRunStatus.Indeterminate"/>). An agent or runtime failure is not workflow meaning (no verdict, no
/// Failed), but it does end the attempt: the task is then recorded BLOCKED (<see cref="RuntimeFailureBlock"/>, or <see cref="RecoverableContinuation"/> for a
/// continuation of human input), never left RUNNING with nothing working on it.
/// </para>
/// <para>
/// Native human interaction: when the agent waits for a person in its own interface (a tool permission, a question, a login) the
/// runner only OBSERVES it. It records AWAITING_HUMAN when the runtime reports the wait and RUNNING again when the runtime reports the agent
/// resumed. It never allows, denies or answers anything, and it never learns what the person chose. Hard limits are not part of that interaction:
/// they are the agent's own deny rules, applied before any prompt exists, and the controller's verification after the fact.
/// </para>
/// <para>
/// Ordering note: within a turn the order is scope observation -> fingerprint -> implementation revision ->
/// verification (scope result included as a check) -> completion. The implementation revision is recorded before
/// verifying so the evidence always binds to a revision that exists, and the configured checks still run when the
/// scope is violated so one repair message can carry all evidence. The workspace is fingerprinted again after the
/// checks: if it moved while they ran, the verification is recorded as failed (<see cref="WorkspaceStableCheckId"/>)
/// rather than letting evidence about one state stand for another.
/// </para>
/// <para>
/// Evidence reuse is an optional optimisation behind <see cref="IExecutionEvidenceSource"/>. For each configured check the
/// runner asks <see cref="ExecutionEvidenceEvaluator"/> whether the native runtime's own record of the agent running exactly that
/// check against exactly the state being verified is sufficient; if so the check is recorded as passed with
/// <see cref="VerificationEvidenceSource.AgentObserved"/> provenance, otherwise (and whenever anything is missing, malformed,
/// stale or ambiguous) the controller executes it as it always did. Reuse never changes who owns the verification run or the
/// completion rule.
/// </para>
/// </summary>
public sealed partial class DirectPathRunner
{
    private const int SummaryLimit = 300;
    private const int InteractionTextLimit = 300;

    public static VerificationCheckId ScopeCheckId { get; } = new("workspace_scope");

    /// <summary>The sensor that fails a verification whose checks ran while the workspace was changing (see <see cref="WorkspaceMovedDiagnostic"/>).</summary>
    public static VerificationCheckId WorkspaceStableCheckId { get; } = new("workspace_stable");

    private readonly WorkflowTaskService _tasks;
    private readonly IAgentRuntime _runtime;
    private readonly IImplementationStateProvider _workspace;
    private readonly IVerificationRunner _verifier;
    private readonly DirectRunOptions _options;
    private readonly IExecutionEvidenceSource? _evidence;
    private readonly AgentConversationSupport? _conversations;
    private readonly AgentClarificationSupport? _clarifications;
    private readonly INativeInteractionObserver? _interactions;

    /// <param name="evidence">Optional. Null (or a session without the capability) means the controller executes every check.</param>
    /// <param name="conversations">
    /// Optional native conversation continuity. Null means no conversation is recorded and every continuation is an explicit fresh session
    /// (<see cref="AgentContinuationReasons.ResumeUnsupported"/>).
    /// </param>
    /// <param name="clarifications">
    /// Optional agent-to-controller CLARIFY signal. Null means the agent is not told it may ask, and no question can be raised.
    /// </param>
    /// <param name="interactions">
    /// Optional visibility of native human interactions. Told when an attended wait opened (after AWAITING_HUMAN was durably recorded) and closed; it can
    /// only inform a person and never changes the run, whatever it does.
    /// </param>
    public DirectPathRunner(
        WorkflowTaskService tasks,
        IAgentRuntime runtime,
        IImplementationStateProvider workspace,
        IVerificationRunner verifier,
        DirectRunOptions? options = null,
        IExecutionEvidenceSource? evidence = null,
        AgentConversationSupport? conversations = null,
        AgentClarificationSupport? clarifications = null,
        INativeInteractionObserver? interactions = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(verifier);

        _tasks = tasks;
        _runtime = runtime;
        _workspace = workspace;
        _verifier = verifier;
        _options = options ?? new DirectRunOptions();
        _evidence = evidence;
        _conversations = conversations;
        _clarifications = clarifications;
        _interactions = interactions;
    }

    /// <summary>Runs the task from its start. Ends with the ACCEPT gate open (<see cref="DirectRunStatus.AwaitingAcceptance"/>) or without one; never completed.</summary>
    public Task<DirectRunResult> RunAsync(
        TaskId taskId,
        DirectTaskDefinition definition,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(taskId, definition, Stretch.Initial, baseline: null, cancellationToken);

    /// <summary>
    /// Continues a task after human input: the human requested changes at the ACCEPT gate (a new <c>human_request_changes</c> semantic revision), or answered the
    /// agent's CLARIFY question (a new <c>human_clarification</c> one). Either way the task is RUNNING under that revision and the work it asks for is pending.
    /// <para>
    /// Resume first: a process is not a conversation. When the runtime can resume (<see cref="IAgentConversationRuntime.CanResume"/>) and a conversation was
    /// recorded for this run, a new session is attached to that same native conversation and sent only the feedback; the agent already knows the task, and
    /// nothing is replayed. A fresh session is used only as an explicit fallback (<see cref="AgentContinuationReasons"/>: resume unsupported, no conversation
    /// recorded, or the runtime says the conversation does not exist), which is sent the original objective, scope and checks and the whole ordered history of
    /// human input (<see cref="HumanInputHistory"/>); if that history does not fit its bound the fresh start is refused rather than truncated.
    /// Every other failure to resume (a corrupt or mismatched reference, a runtime that crashed or timed out) ends the run: it is never turned into silent
    /// context loss. This is not an independence boundary, so nothing here chooses a fresh session for its own sake.
    /// </para>
    /// <para>
    /// Resuming the conversation does not reuse the semantic revision: the closed gate and the new <c>human_request_changes</c> revision are the controller's,
    /// recorded before this runs. The loop that follows is the ordinary one: repair, implementation revision, verification, and a new ACCEPT gate.
    /// </para>
    /// <para>
    /// When the continuation cannot proceed because the runtime failed (see <see cref="IsRuntimeFailure"/>), the work is stranded: the human's decision stands,
    /// the semantic revision is current, and no process is working on it. The task is then recorded BLOCKED (<see cref="RecoverableContinuation"/>), never
    /// left RUNNING, and <see cref="RecoverHumanInputAsync"/> retries the same pending work.
    /// </para>
    /// </summary>
    public Task<DirectRunResult> ContinueAfterHumanInputAsync(
        TaskId taskId,
        DirectTaskDefinition definition,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(taskId, definition, Stretch.Continuation, baseline: null, cancellationToken);

    /// <summary>
    /// As <see cref="ContinueAfterHumanInputAsync(TaskId, DirectTaskDefinition, CancellationToken)"/>, with the workspace state the stretch's scope is
    /// judged against supplied by the caller (the one it recorded before the decision) instead of captured here.
    /// </summary>
    public Task<DirectRunResult> ContinueAfterHumanInputAsync(
        TaskId taskId,
        DirectTaskDefinition definition,
        ImplementationState baseline,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(taskId, definition, Stretch.Continuation, baseline ?? throw new ArgumentNullException(nameof(baseline)), cancellationToken);

    /// <summary>
    /// Retries the pending work of human input (requested changes, or an answer to a question) whose continuation was blocked by a runtime failure
    /// (<see cref="RecoverableContinuation"/>), after the task was returned to RUNNING (<see cref="ContinuationRecoveryService.ResumeAsync"/>). It is the same
    /// work, not a new request: the decision, the semantic revision and the human's words are exactly the ones already recorded, and nothing here records another.
    /// <para>
    /// The agent may already have received the instruction and may already have changed the workspace, and the runtime cannot say which (delivery is at least
    /// once), so nothing is assumed: the conversation is resumed (or, only under the explicit fallback conditions, a fresh session is started), the agent is sent
    /// the authoritative input again and told to inspect the workspace and keep correct work, and the controller verifies whatever state results.
    /// <paramref name="baseline"/> is the state the stretch's scope is judged against: the one recorded when the input was given (or the question raised), so a path the
    /// interrupted attempt already changed outside the scope is still a violation and is never absorbed into a new baseline.
    /// </para>
    /// </summary>
    public Task<DirectRunResult> RecoverHumanInputAsync(
        TaskId taskId,
        DirectTaskDefinition definition,
        ImplementationState baseline,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(taskId, definition, Stretch.Recovery, baseline ?? throw new ArgumentNullException(nameof(baseline)), cancellationToken);

    private async Task<DirectRunResult> RunCoreAsync(
        TaskId taskId,
        DirectTaskDefinition definition,
        Stretch stretch,
        ImplementationState? baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var run = new RunState(taskId, _interactions);
        DirectRunResult result;

        try
        {
            result = await ExecuteAsync(run, definition, stretch, baseline, cancellationToken).ConfigureAwait(false);
        }
        catch (RunStop stop)
        {
            result = Build(run, stop.Status, stop.Message, stop.Snapshot, stop.Eligibility);
        }
        catch (OperationCanceledException)
        {
            // Not inside a workflow mutation (those are translated to RunStop): nothing was left half-applied.
            result = Build(run, DirectRunStatus.Interrupted, "cancelled while no workflow mutation was in flight", run.Snapshot);
        }
        catch (AgentStartupBlockedException e)
        {
            result = Build(run, DirectRunStatus.StartupBlocked, $"agent_startup_blocked: {e.Message}", run.Snapshot);
        }
        catch (AgentRuntimeException e)
        {
            result = Build(run, DirectRunStatus.ExecutionFailure, $"agent_runtime_failure: {e.Message}", run.Snapshot);
        }
        catch (Exception e)
        {
            // A port failed in a way it did not model. The run still ends with a structured result so the session
            // is cleaned up and the caller sees the cause; workflow state is whatever was last confirmed.
            result = Build(run, DirectRunStatus.ExecutionFailure, $"execution_failure: {e.GetType().Name}: {e.Message}", run.Snapshot);
        }

        // The session is about to be stopped, so a wait that was recorded can no longer be answered. Unless the run is ending precisely
        // because of that wait, the workflow must not keep claiming it: it goes back to RUNNING, as for any other incomplete run.
        if (run.AwaitingHuman && result.Status != DirectRunStatus.NativeInteractionUnavailable)
            result = result with { Task = await TryLeaveAwaitingHumanAsync(run).ConfigureAwait(false) ?? result.Task };

        // Whatever ended the wait, nobody can answer it through this run any more.
        run.Interactions.Closed();

        var sessionGone = run.Session is null;

        if (run.Session is { } session && _options.StopSessionWhenDone)
        {
            var stopped = await StopSessionAsync(session, run).ConfigureAwait(false);
            sessionGone = stopped;

            // The native interaction was answerable only while the session lived. Once it is confirmed gone nothing can continue this run, so the
            // workflow says so: BLOCKED, not a wait nobody can answer. (A session kept alive, or one that could not be stopped, is not claimed gone:
            // the wait stays AWAITING_HUMAN.)
            if (stopped && run.AwaitingHuman && result.Status == DirectRunStatus.NativeInteractionUnavailable)
                result = result with { Task = await TryBlockAfterSessionEndedAsync(run, session).ConfigureAwait(false) ?? result.Task };
        }

        // An attempt the runtime could not carry out must not leave the workflow RUNNING: nothing is working on it. A continuation of human input is BLOCKED
        // as recoverable work (the human's decision stands and 'aw continue' retries it); a first attempt has no such pending work, so it is BLOCKED by the
        // runtime failure itself, with no recovery claimed.
        if (IsRuntimeFailure(result.Status))
        {
            result = result with
            {
                // A reclassification recorded by this very run is pending work exactly as human input would be: the attempt is recoverable ('aw continue').
                Task = (stretch == Stretch.Initial && !run.ReclassificationRecorded && !run.ReviewPhase && !run.ProvePhase
                    ? await TryBlockFailedInitialAttemptAsync(run, result, sessionGone).ConfigureAwait(false)
                    : await TryBlockStrandedContinuationAsync(run, result, sessionGone).ConfigureAwait(false)) ?? result.Task,
            };
        }
        else if (result.Status is DirectRunStatus.WhatStageViolation or DirectRunStatus.HowStageViolation)
        {
            // A pre-implementation stage (WHAT, HOW) was broken: nothing is working on the run and nothing can be retried in place, so it is BLOCKED with its own
            // reason, never left RUNNING.
            result = result with { Task = await TryBlockStageViolationAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }
        else if (result.Status == DirectRunStatus.ReviewerModifiedWorkspace)
        {
            result = result with { Task = await TryBlockReviewerWorkspaceAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }
        else if (result.Status == DirectRunStatus.ReviewUnresolved)
        {
            result = result with { Task = await TryBlockUnresolvedReviewAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }
        else if (result.Status == DirectRunStatus.ProverModifiedWorkspace)
        {
            result = result with { Task = await TryBlockProverWorkspaceAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }
        else if (result.Status is DirectRunStatus.ProofUnresolved or DirectRunStatus.ProofContractMissing)
        {
            result = result with { Task = await TryBlockUnresolvedProofAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }
        else if (result.Status == DirectRunStatus.VerificationUnresolved)
        {
            // A verdict, not a failure, but the same truth: the permitted turns are used up and nothing is working on the run, so it cannot progress.
            result = result with { Task = await TryBlockUnresolvedVerificationAsync(run, result).ConfigureAwait(false) ?? result.Task };
        }

        return result with { Notes = run.Notes.ToArray() };
    }

    private async Task<DirectRunResult> ExecuteAsync(RunState run, DirectTaskDefinition definition, Stretch stretch, ImplementationState? stretchBaseline, CancellationToken ct)
    {
        // ---- 1. load and validate; the runtime is not touched until every check passes -------------------------
        TaskSnapshot snapshot;
        try
        {
            snapshot = await _tasks.LoadAsync(run.TaskId, ct).ConfigureAwait(false);
        }
        catch (TaskNotFoundException)
        {
            return Build(run, DirectRunStatus.Rejected, "task_not_found: the task has no history", null);
        }

        run.Snapshot = snapshot;

        if (Validate(snapshot.Task, definition) is { } rejection)
            return Build(run, DirectRunStatus.Rejected, rejection, snapshot);

        PendingHumanInput? humanInput = null;
        if (stretch != Stretch.Initial)
        {
            humanInput = HumanInputOf(snapshot.Task, recovering: stretch == Stretch.Recovery);

            if (humanInput is null)
                return Build(run, DirectRunStatus.Rejected, "no_human_input: the task has no unaddressed human request for changes or answer to a question", snapshot);
        }

        var profile = snapshot.Task.GetBinding(definition.RootRole).Profile.Id;

        // ---- 2. baseline of the workspace, then one persistent session ---------------------------------------
        var baseline = stretchBaseline ?? await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);

        var request = new AgentSessionRequest(profile, definition.WorkspacePath, $"task {run.TaskId}")
        {
            ObservableChecks = _evidence is null ? [] : definition.Checks,
        };

        var mayAskForClarification = _clarifications is { } support && support.Signals.CanSignal(profile);
        var mayProposeWhat = mayAskForClarification;
        var mayProposeHow = mayAskForClarification;

        // A stage that must be approved cannot be approved if the agent has no way to propose it. Refused before anything starts, never silently skipped.
        if (!snapshot.Task.WhatSatisfied && !mayProposeWhat)
        {
            return Build(
                run,
                DirectRunStatus.Rejected,
                "what_unavailable: the task needs a human-approved WHAT before implementation, but this runtime cannot receive the agent's WHAT proposal",
                snapshot);
        }

        if (!snapshot.Task.HowSatisfied && !mayProposeHow)
        {
            return Build(
                run,
                DirectRunStatus.Rejected,
                "how_unavailable: the task needs a human-approved HOW before implementation, but this runtime cannot receive the agent's HOW proposal",
                snapshot);
        }

        // a recovered review is due. The implementer is not involved: a fresh reviewer reads the verified implementation. If it requires changes, the work that follows
        // is the implementer repairing them, which is the ordinary resumed continuation below.
        // the same for a recovered proof (the prover died, or the contract was durable but the proof never began), and for a review recovered on a task that also requires one.
        if (humanInput is { Kind: HumanInputKind.ReviewPending or HumanInputKind.ProvePending })
        {
            var recovered = await RunRequiredStagesAsync(run, definition, null, baseline, ct).ConfigureAwait(false);

            if (recovered.Stop is { } recoveredStop)
                return recoveredStop;

            snapshot = run.Snapshot!;

            if (recovered.RepairPrompt is null)
                return await FinishVerifiedAsync(run, snapshot, ct).ConfigureAwait(false);

            humanInput = PendingHumanInput.Find(snapshot.Task);

            if (humanInput is null)
                return Build(run, DirectRunStatus.Rejected, "no_human_input: the recovered stage left no pending work to continue", snapshot);
        }

        AgentSessionId session;
        string prompt;

        if (humanInput is null)
        {
            session = await StartNewConversationAsync(run, request, ct).ConfigureAwait(false);
            prompt = DirectPathPrompts.Initial(snapshot.Task, definition, mayAskForClarification, mayProposeWhat, mayProposeHow);
        }
        else
        {
            var recovering = stretch == Stretch.Recovery;

            // What a fresh session would be told, decided before one is started: if it cannot be told everything, it is not started.
            var history = HumanInputHistory.Of(snapshot.Task);
            var freshHistoryFits = HumanInputHistory.TryRender(history, out var historyText) && history.Count > 0;

            run.Phase = Phases.Resuming;
            var findings = humanInput.Kind is HumanInputKind.ReviewFindings or HumanInputKind.ProofFindings;
            var (continued, resumed) = await ContinueConversationAsync(
                run,
                request,
                freshStartIsPossible: freshHistoryFits && !findings,
                findings ? "implementer_conversation_unavailable: the independent review or proof did not pass, which only the original implementer conversation is resumed to repair; it cannot be resumed, and a fresh agent is not substituted. 'aw abort' cancels the run." : null,
                ct).ConfigureAwait(false);
            session = continued;
            prompt = resumed
                ? ResumedPrompt(snapshot.Task, humanInput, recovering)
                : DirectPathPrompts.FreshContinuation(snapshot.Task, definition, historyText, recovering, mayAskForClarification, mayProposeWhat, mayProposeHow);
        }

        var evaluator = new ExecutionEvidenceEvaluator(definition.Checks);

        // ---- 3. the loop ----------------------------------------------------------------------------------------
        for (var turn = 1; turn <= definition.MaxAgentTurns; turn++)
        {
            await BeginEvidenceWindowAsync(session, run, ct).ConfigureAwait(false);

            // What is known if this fails: before the send returns, the turn may or may not have reached the agent; after it, the turn was submitted.
            run.Phase = Phases.Sending;
            var turnNumber = await _runtime.SendTurnAsync(session, prompt, ct).ConfigureAwait(false);

            run.Phase = Phases.Waiting;
            var outcome = await _runtime.WaitForTurnAsync(session, turnNumber, _options.TurnTimeout, ct).ConfigureAwait(false);

            // A wait for a person is observed, never answered: the SAME session continues the SAME turn once the person has acted natively.
            var followed = await FollowNativeInteractionsAsync(run, turnNumber, session, outcome, ct).ConfigureAwait(false);

            if (followed.Stop is { } stop)
                return stop;

            outcome = followed.Outcome;

            // a request for an independent review is additive and never ends the turn; it is recorded and the turn is processed as usual.
            if (outcome.Status is AgentTurnStatus.Done or AgentTurnStatus.Error)
            {
                await TryRecordReviewRequestAsync(run, profile, session, turnNumber, ct).ConfigureAwait(false);

                // likewise for the Proof Contract: additive, recorded, and the turn is processed as usual.
                await TryRecordProveRequestAsync(run, definition, profile, session, turnNumber, ct).ConfigureAwait(false);
            }

            snapshot = run.Snapshot!;

            // New evidence the agent deliberately reported reroutes the task and the SAME conversation continues, with only the delta, to make the revised proposal.
            // It is handled before any other signal: more than one signal in a turn fails safely, and nothing is observed, implemented or verified for this turn.
            if (outcome.Status is AgentTurnStatus.Done or AgentTurnStatus.Error
                && await TryReclassifyAsync(run, profile, session, turnNumber, turn, baseline, definition, ct).ConfigureAwait(false) is { } reclassified)
            {
                if (reclassified.Stop is { } reclassifyStop)
                    return reclassifyStop;

                if (turn == definition.MaxAgentTurns)
                {
                    return Build(
                        run,
                        DirectRunStatus.ExecutionFailure,
                        $"reclassification_unresolved: the reclassification was recorded but the agent had no turn left to make the revised proposal in {definition.MaxAgentTurns} turn(s)",
                        run.Snapshot);
                }

                prompt = reclassified.Prompt!;
                snapshot = run.Snapshot!;
                continue;
            }

            // A question, a WHAT or a HOW the agent deliberately signalled ends the stretch here, before anything is observed, implemented or verified: nothing is
            // claimed about unfinished work. Only one such signal may end a turn.
            if (outcome.Status is AgentTurnStatus.Done or AgentTurnStatus.Error
                && await TryOpenSignalledGateAsync(run, profile, session, turnNumber, turn, baseline, definition, ct).ConfigureAwait(false) is { } signalled)
                return signalled;

            if (outcome.Status != AgentTurnStatus.Done)
            {
                // Runtime status only: no verification is fabricated and the workflow state is left alone.
                run.Turns.Add(new DirectTurnRecord(turn, session, outcome.Status, null, null, []));
                return Build(run, ToStatus(outcome.Status), $"agent_{outcome.Status.ToString().ToLowerInvariant()}: {outcome.Reason ?? "no reason given"}", snapshot);
            }

            // Stop before implementation: while a pre-implementation stage is unsatisfied no implementation may exist. A turn that ended without a proposal
            // either changed the workspace (a violation, refused) or changed nothing (reminded once per remaining turn). Nothing is recorded as implemented either way.
            // The stages are ordered: the WHAT first, and only then the HOW.
            if (!snapshot.Task.MayImplement)
            {
                var stage = snapshot.Task.WhatSatisfied ? ProposalStage.How : ProposalStage.What;

                run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, null, null, []));

                var changedBefore = StageViolationPaths(snapshot.Task, baseline, await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false));
                if (changedBefore.Count > 0)
                    return Build(run, stage.Violation, WorkBeforeApprovalReason(stage, changedBefore), snapshot);

                if (turn == definition.MaxAgentTurns)
                    return Build(run, stage.Violation, $"{stage.Name}_not_proposed: the task needs a human-approved {stage.Label} and the agent did not propose one in {definition.MaxAgentTurns} turn(s)", snapshot);

                prompt = stage.IsHow ? DirectPathPrompts.HowReminder() : DirectPathPrompts.WhatReminder();
                continue;
            }

            run.Phase = Phases.Observing;

            // what the runtime says the agent executed this turn (optional; failure to obtain it only means "no reuse")
            var observed = await CollectEvidenceAsync(session, run, ct).ConfigureAwait(false);

            // scope observation + exact fingerprint
            var current = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
            var violations = ObserveScope(definition.Scope, baseline, current);

            // implementation revision (bound to the current semantic revision by Core)
            if (NeedsNewImplementationRevision(snapshot.Task, current))
                snapshot = await CommitAsync(run, () => _tasks.RecordImplementationRevisionAsync(snapshot, current.Fingerprint, ct)).ConfigureAwait(false);

            run.Snapshot = snapshot;
            var implementationId = snapshot.Task.CurrentImplementationRevisionId;

            // controller verification: the scope sensor first, then every configured check
            var diagnostics = new List<CheckDiagnostic>();
            if (violations.Count > 0)
                diagnostics.Add(ScopeDiagnostic(violations));

            var usedActivities = new HashSet<string>(StringComparer.Ordinal);
            var decisions = new List<EvidenceDecision>();

            foreach (var check in definition.Checks)
            {
                var decision = evaluator.Evaluate(definition.WorkspacePath, check, observed, current.Fingerprint, usedActivities);
                decisions.Add(decision);

                var diagnostic = decision.Reusable
                    ? AgentObservedDiagnostic(check, decision.Observation!)
                    : await RunCheckAsync(definition.WorkspacePath, check, ct).ConfigureAwait(false);

                // The reason is recorded with the result so that it can be read long after the run; it never changes the outcome.
                diagnostics.Add(diagnostic with { EvidenceReason = Bound(decision.Reason, VerificationCheckResult.MaxEvidenceReasonLength) });
            }

            run.EvidenceDecisions = decisions;

            // A verification may only describe the state the revision was recorded for. If the workspace moved while the
            // checks ran, the evidence is about some other state, so it is recorded as failed and the new state is
            // observed (and recorded as its own revision) on the next turn.
            var afterChecks = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);
            if (afterChecks.Fingerprint != current.Fingerprint)
                diagnostics.Add(WorkspaceMovedDiagnostic());

            run.LastVerification = diagnostics;

            var results = diagnostics.Select(d => new VerificationCheckResult(d.CheckId, d.Outcome, d.Summary, d.ExitCode, d.Observation, d.EvidenceReason)).ToList();
            snapshot = await CommitAsync(run, () => _tasks.RecordLightweightVerificationAsync(snapshot, results, ct)).ConfigureAwait(false);
            run.Snapshot = snapshot;

            var verification = snapshot.Task.CurrentVerification!.Outcome;
            run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, implementationId, verification, violations));

            if (verification == VerificationOutcome.Passed)
            {
                // A task that requires an independent review and/or formal proof is judged by fresh independent conversations before it may complete or ask for ACCEPT.
                // A review or proof that does not pass sends its findings, as a delta, to this same implementer conversation; the repair is verified again and NEW fresh
                // judges follow. A required Proof Contract that does not exist yet is asked for once.
                var stages = await RunRequiredStagesAsync(run, definition, session, baseline, ct).ConfigureAwait(false);

                if (stages.Stop is { } stageStop)
                    return stageStop;

                snapshot = run.Snapshot!;

                if (stages.RepairPrompt is { } repair)
                {
                    if (turn == definition.MaxAgentTurns)
                    {
                        return Build(
                            run,
                            stages.UnresolvedStatus,
                            $"turn_limit_reached: {stages.UnresolvedWhat} and the implementer has no turn left in {definition.MaxAgentTurns} turn(s)",
                            snapshot);
                    }

                    prompt = repair;
                    continue;
                }

                return await FinishVerifiedAsync(run, snapshot, ct).ConfigureAwait(false);
            }

            if (turn == definition.MaxAgentTurns)
                break;

            // Same session, concise evidence, no other agent.
            prompt = DirectPathPrompts.Repair(violations, diagnostics);
        }

        return Build(
            run,
            DirectRunStatus.VerificationUnresolved,
            $"turn_limit_reached: verification still failing after {definition.MaxAgentTurns} turn(s)",
            snapshot);
    }

    // ---- agent conversation: new, resumed or (explicitly) fresh ---------------------------------------------------------------

    private async Task<AgentSessionId> StartNewConversationAsync(RunState run, AgentSessionRequest request, CancellationToken ct)
    {
        var session = await _runtime.StartSessionAsync(Attended(run, request), ct).ConfigureAwait(false);
        run.Session = session;
        run.Continuation = AgentContinuation.New;

        await RecordConversationAsync(run, session, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// The resume-first policy for a continuation. Returns the session and whether it is the earlier conversation resumed (true) or a fresh session (false).
    /// Only conditions that positively mean "there is no conversation to resume" fall back; everything else surfaces as the failure it is.
    /// </summary>
    private async Task<(AgentSessionId Session, bool Resumed)> ContinueConversationAsync(RunState run, AgentSessionRequest request, bool freshStartIsPossible, string? noFreshStartMessage, CancellationToken ct)
    {
        string fallbackReason;

        if (_conversations is not { } support || !support.Runtime.CanResume(request.Profile))
        {
            fallbackReason = AgentContinuationReasons.ResumeUnsupported;
        }
        else
        {
            var record = await FindRecordAsync(run, support, ct).ConfigureAwait(false);

            if (record is null)
            {
                fallbackReason = AgentContinuationReasons.NoConversationRecorded;
            }
            else
            {
                // Checked here as well as by the runtime: a reference is only ever resumed by the run and the profile it was recorded for.
                if (record.Task != run.TaskId)
                    throw Invalid(run, $"the recorded conversation belongs to run {record.Task}, not this run");

                if (record.Conversation.Profile != request.Profile)
                    throw Invalid(run, $"the recorded conversation belongs to profile '{record.Conversation.Profile}', not '{request.Profile}'");

                try
                {
                    var resumedSession = await support.Runtime.ResumeSessionAsync(Attended(run, request), record.Conversation, ct).ConfigureAwait(false);
                    run.Session = resumedSession;
                    run.Continuation = AgentContinuation.Resumed;

                    return (resumedSession, true);
                }
                catch (AgentConversationNotFoundException)
                {
                    fallbackReason = AgentContinuationReasons.ConversationNotFound;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not "not found": a crash, a timeout, a refused or blocked start. Context must not be lost behind the person's back.
                    run.Continuation = new AgentContinuation(AgentContinuationKind.ResumeFailed);
                    throw;
                }
            }
        }

        // A fresh session remembers nothing, so it must be told everything humans added. If that does not fit, it is refused, never silently truncated.
        if (!freshStartIsPossible)
        {
            throw new RunStop(
                DirectRunStatus.Rejected,
                noFreshStartMessage is null
                    ? $"fresh_context_too_large: the native conversation cannot be resumed ({fallbackReason}) and the human input given so far does not fit the {HumanInputHistory.MaxCharacters}-character bound for a fresh session, so none was started. 'aw abort' cancels the run."
                    : $"{noFreshStartMessage} (resume: {fallbackReason})",
                run.Snapshot);
        }

        var fresh = await _runtime.StartSessionAsync(Attended(run, request), ct).ConfigureAwait(false);
        run.Session = fresh;
        run.Continuation = new AgentContinuation(AgentContinuationKind.FreshSession, fallbackReason);
        run.Notes.Add($"fresh agent session: the native conversation was not resumed ({fallbackReason}); this session does not remember the earlier one");

        // The fresh session is now the run's conversation.
        await RecordConversationAsync(run, fresh, ct).ConfigureAwait(false);
        return (fresh, false);
    }

    private static async Task<AgentConversationRecord?> FindRecordAsync(RunState run, AgentConversationSupport support, CancellationToken ct)
    {
        try
        {
            return await support.Store.FindAsync(run.TaskId, ct).ConfigureAwait(false);
        }
        catch (InvalidAgentConversationException e)
        {
            throw Invalid(run, e.Message);
        }
    }

    private static RunStop Invalid(RunState run, string message)
    {
        run.Continuation = new AgentContinuation(AgentContinuationKind.ResumeFailed);
        return new RunStop(DirectRunStatus.Rejected, $"conversation_reference_invalid: {message}", run.Snapshot);
    }

    /// <summary>
    /// Keeps the run's resumable conversation durable beside the run. Never fails the run: if the reference cannot be kept, the only consequence is that a
    /// later continuation is an explicit fresh session.
    /// </summary>
    private async Task RecordConversationAsync(RunState run, AgentSessionId session, CancellationToken ct)
    {
        if (_conversations is not { } support || support.Runtime.ConversationOf(session) is not { } conversation)
            return;

        try
        {
            await support.Store.SaveAsync(new AgentConversationRecord(run.TaskId, conversation), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Notes.Add($"the agent conversation could not be recorded ({e.GetType().Name}: {e.Message}); a later request for changes will start a fresh session");
        }
    }

    // ---- native human interaction ----------------------------------------------------------------------------------------

    /// <summary>
    /// Follows the agent through every native interaction of the turn. When the runtime reports a wait, the workflow becomes AWAITING_HUMAN; if no
    /// person is available (<see cref="DirectRunOptions.WaitForHuman"/> is false) the run ends there, otherwise it waits on the same turn. When the
    /// runtime reports the agent resumed (or the turn ended some other way) the workflow is RUNNING again. Nothing here depends on what the person chose.
    /// </summary>
    private async Task<(AgentTurnOutcome Outcome, DirectRunResult? Stop)> FollowNativeInteractionsAsync(
        RunState run,
        AgentTurnNumber turnNumber,
        AgentSessionId session,
        AgentTurnOutcome outcome,
        CancellationToken ct)
    {
        while (outcome.Status is AgentTurnStatus.AwaitingHuman or AgentTurnStatus.Resumed)
        {
            if (outcome.Status == AgentTurnStatus.AwaitingHuman)
            {
                var interaction = outcome.Interaction ?? throw new AgentRuntimeException("the runtime reported awaiting_human without an interaction");
                var what = await RecordInteractionAsync(run, interaction, ct).ConfigureAwait(false);

                if (!_options.WaitForHuman)
                {
                    return (outcome, Build(
                        run,
                        DirectRunStatus.NativeInteractionUnavailable,
                        $"native_interaction_unavailable: the agent needed a person in its own interface ({what}); nobody was available, so the session was stopped",
                        run.Snapshot));
                }

                // Only now that AWAITING_HUMAN is durable, and only because a person is attending, is anyone told.
                run.Interactions.Opened(interaction);
            }
            else if (run.AwaitingHuman)
            {
                await ReturnToRunningAsync(run, "The agent resumed after a native human interaction.", ct).ConfigureAwait(false);
            }

            outcome = await _runtime.WaitForTurnAsync(session, turnNumber, _options.TurnTimeout, ct).ConfigureAwait(false);
        }

        // The turn ended without a separate "resumed" observation (the agent finished, or died, while the wait was being reported).
        if (run.AwaitingHuman)
            await ReturnToRunningAsync(run, $"The native human interaction ended ({outcome.Status.ToString().ToLowerInvariant()}).", ct).ConfigureAwait(false);

        return (outcome, null);
    }

    /// <summary>Records, durably, that the agent is waiting for a person in its own interface. Returns the short description kept for the run.</summary>
    private async Task<string> RecordInteractionAsync(RunState run, AgentInteraction interaction, CancellationToken ct)
    {
        var what = $"{interaction.Kind.ToString().ToLowerInvariant()}: {Bound(interaction.Description)}";

        run.Snapshot = await CommitAsync(
            run,
            () => _tasks.ChangeWorkflowStateAsync(run.Snapshot!, WorkflowState.AwaitingHuman, $"The agent is waiting for a person in its own interface ({what}).", ct)).ConfigureAwait(false);

        run.AwaitingHuman = true;
        run.InteractionSummary = what;

        return what;
    }

    /// <summary>
    /// When a person is attending, lets the runtime report an agent that needs them while it is still starting (trust this folder, log in): the wait is recorded exactly like
    /// a mid-turn one, the session is kept, and the person answers in the agent's own session. When nobody is attending the request is returned unchanged, so such a
    /// startup fails as before instead of leaving a session nobody will answer.
    /// </summary>
    private AgentSessionRequest Attended(RunState run, AgentSessionRequest request)
        => _options.WaitForHuman ? request with { StartupInteractions = new StartupInteractionRecorder(this, run) } : request;

    private sealed class StartupInteractionRecorder(DirectPathRunner runner, RunState run) : IAgentStartupInteractions
    {
        public async Task OpenedAsync(AgentInteraction interaction, CancellationToken cancellationToken)
        {
            await runner.RecordInteractionAsync(run, interaction, cancellationToken).ConfigureAwait(false);

            // Only now that AWAITING_HUMAN is durable, and only because a person is attending, is anyone told.
            run.Interactions.Opened(interaction);
        }

        public async Task ClosedAsync(CancellationToken cancellationToken)
        {
            if (run.AwaitingHuman)
                await runner.ReturnToRunningAsync(run, "The agent finished starting after a native human interaction.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReturnToRunningAsync(RunState run, string reason, CancellationToken ct)
    {
        run.Snapshot = await CommitAsync(run, () => _tasks.ChangeWorkflowStateAsync(run.Snapshot!, WorkflowState.Running, reason, ct)).ConfigureAwait(false);
        run.AwaitingHuman = false;
        run.Interactions.Closed();
    }

    /// <summary>
    /// After a run that ended for another reason while a wait was recorded (interruption, runtime failure), returns the workflow to RUNNING using the
    /// controller's own bounded budget, since the caller's token may be the reason we are here. Never throws; a failure is reported as a note.
    /// </summary>
    private async Task<TaskSnapshot?> TryLeaveAwaitingHumanAsync(RunState run)
    {
        using var budget = new CancellationTokenSource(_options.CleanupTimeout);

        try
        {
            var current = await _tasks.FindAsync(run.TaskId, budget.Token).ConfigureAwait(false);

            if (current is null || current.Task.WorkflowState != WorkflowState.AwaitingHuman)
                return current;

            return (await _tasks.ChangeWorkflowStateAsync(current, WorkflowState.Running, "The run ended while the agent was waiting for a person; its session was stopped.", budget.Token).ConfigureAwait(false)).Snapshot;
        }
        catch (Exception e)
        {
            run.Notes.Add($"the workflow could not be returned to RUNNING after the native interaction: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Records AWAITING_HUMAN to BLOCKED once the agent session that held the native interaction has been stopped, using the controller's own bounded
    /// budget. Only a workflow still AWAITING_HUMAN is moved. Never throws; a failure is reported as a note and the (stale) AWAITING_HUMAN remains
    /// the last confirmed state.
    /// </summary>
    private async Task<TaskSnapshot?> TryBlockAfterSessionEndedAsync(RunState run, AgentSessionId session)
    {
        using var budget = new CancellationTokenSource(_options.CleanupTimeout);

        try
        {
            var current = await _tasks.FindAsync(run.TaskId, budget.Token).ConfigureAwait(false);

            if (current is null || current.Task.WorkflowState != WorkflowState.AwaitingHuman)
                return current;

            var reason = NativeInteractionBlock.BlockedReason(run.InteractionSummary);
            var changed = await _tasks.ChangeWorkflowStateAsync(current, WorkflowState.Blocked, reason, budget.Token).ConfigureAwait(false);
            run.AwaitingHuman = false;

            return changed.Snapshot;
        }
        catch (Exception e)
        {
            run.Notes.Add($"the workflow could not be moved to BLOCKED after session {session} ended: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // ---- a continuation the runtime could not carry out -------------------------------------------------------------------------

    /// <summary>
    /// The run ended because something outside the workflow's meaning failed (the runtime, the agent process, a port, an interruption) rather than because the
    /// work finished or the controller reached a verdict. (A native interaction nobody could answer is handled by its own BLOCKED path; a verification that keeps
    /// failing is a verdict, not a failure, and has its own BLOCKED path; a stale or unknown-outcome mutation means the history is not ours to change.)
    /// </summary>
    private static bool IsRuntimeFailure(DirectRunStatus status)
        => status is DirectRunStatus.ExecutionFailure
            or DirectRunStatus.StartupBlocked
            or DirectRunStatus.AgentError
            or DirectRunStatus.AgentCancelled
            or DirectRunStatus.Interrupted
            or DirectRunStatus.Rejected
            or DirectRunStatus.ReviewerUnbound
            or DirectRunStatus.ProverUnbound;

    /// <summary>
    /// Records RUNNING to BLOCKED for a continuation of the human's requested changes that could not be carried out, using the controller's own bounded budget
    /// (the caller's token may be the reason we are here). The reload comes first: only a task that is still RUNNING with that request pending is moved, so a task
    /// that moved on (or that something else already ended) is left exactly as it is. No decision, semantic revision or gate is recorded: the human's decision
    /// stands and the same work can be retried. Never throws; a failure is reported as a note and the last confirmed state remains.
    /// </summary>
    private Task<TaskSnapshot?> TryBlockStrandedContinuationAsync(RunState run, DirectRunResult result, bool sessionGone)
        => TryBlockRunningAsync(
            run,
            RecoverableContinuation.IsWorkInProgress,
            RecoverableContinuation.BlockedReason(run.Phase, result.Reason, sessionGone),
            "after the continuation failed");

    /// <summary>
    /// Records RUNNING to BLOCKED for a first attempt that the runtime could not carry out: no live session works on the run, no gate is pending and nothing
    /// was verified or completed, so the workflow must not keep claiming RUNNING. The same guards apply as for a stranded continuation (own budget, reload first,
    /// only a task still RUNNING is moved, so nothing is recorded twice and a task that moved on is left alone), but the reason is not the recoverable one:
    /// there is no recorded human input to retry, so no recovery command is promised.
    /// </summary>
    private Task<TaskSnapshot?> TryBlockFailedInitialAttemptAsync(RunState run, DirectRunResult result, bool sessionGone)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            RuntimeFailureBlock.BlockedReason(run.Phase, result.Reason, sessionGone),
            "after the agent attempt failed");

    /// <summary>
    /// Records RUNNING to BLOCKED for a run whose verification is still failing after the last permitted turn, with the same guards as the runtime-failure block
    /// (own budget, reload first, only a task still RUNNING is moved). The reason is neither the runtime-failure nor the recoverable one: nothing is recorded that
    /// could be retried, so no recovery command is promised.
    /// </summary>
    private Task<TaskSnapshot?> TryBlockUnresolvedVerificationAsync(RunState run, DirectRunResult result)
        => TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            UnresolvedVerificationBlock.BlockedReason(result.Reason),
            "after verification stayed unresolved");

    private async Task<TaskSnapshot?> TryBlockRunningAsync(RunState run, Func<WorkflowTask, bool> mayBlock, string reason, string when)
    {
        using var budget = new CancellationTokenSource(_options.CleanupTimeout);

        try
        {
            var current = await _tasks.FindAsync(run.TaskId, budget.Token).ConfigureAwait(false);

            if (current is null || !mayBlock(current.Task))
                return current;

            return (await _tasks.ChangeWorkflowStateAsync(current, WorkflowState.Blocked, reason, budget.Token).ConfigureAwait(false)).Snapshot;
        }
        catch (Exception e)
        {
            run.Notes.Add($"the workflow could not be moved to BLOCKED {when}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The runtime's own text, on one line, bounded and free of control characters, so it can sit in a workflow reason and be shown on a terminal.
    /// It derives from the agent's tool input, so it is never trusted as anything but display text.
    /// </summary>
    private static string Bound(string text, int limit = InteractionTextLimit)
    {
        var printable = new string(text.Select(c => char.IsControl(c) && !char.IsWhiteSpace(c) ? '?' : c).ToArray());
        var singleLine = string.Join(' ', printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (singleLine.Length == 0)
            singleLine = "(none given)";

        return singleLine.Length <= limit ? singleLine : singleLine[..(limit - 3)] + "...";
    }

    // ---- validation ------------------------------------------------------------------------------------------------

    private static string? Validate(WorkflowTask task, DirectTaskDefinition definition)
    {
        if (task.IsTerminal || task.WorkflowState != WorkflowState.Running)
            return $"task_not_running: the task is {task.WorkflowState}";

        if (task.CompletionBoundary != CompletionBoundary.WorkingTree)
            return $"unsupported_completion_boundary: the direct path implements only '{CompletionBoundary.WorkingTree}', the task requires '{task.CompletionBoundary}'";

        if (!task.RoleBindings.TryGetValue(definition.RootRole, out var binding))
            return $"role_unbound: role '{definition.RootRole}' has no explicit profile binding";

        if (binding.Profile.Id != definition.ExecutionProfile)
            return $"profile_mismatch: role '{definition.RootRole}' is bound to '{binding.Profile.Id}', not '{definition.ExecutionProfile}'";

        // Core refuses such a binding at creation; checked again because this is the gate before launching anything.
        if (!binding.Profile.Capabilities.IsSupersetOf(binding.Role.RequiredCapabilities))
            return $"profile_lacks_capabilities: profile '{binding.Profile.Id}' does not provide what role '{binding.Role.Id}' requires";

        return null;
    }

    /// <summary>
    /// The human input (a request for changes, or an answer to a question) that is still being worked on, read from the authoritative state (never from the
    /// caller): <see cref="PendingHumanInput.Find"/>. Normally nothing may have been implemented under its semantic revision yet (the work has just begun); a
    /// recovered continuation may already have an implementation under it (the failure came after a first attempt, during a repair), and it is still the same input.
    /// </summary>
    private static PendingHumanInput? HumanInputOf(WorkflowTask task, bool recovering)
    {
        if (PendingHumanInput.Find(task) is not { } input)
            return null;

        // The work a review leaves pending is only ever picked up by a recovery ('aw continue'): within a run the loop carries it.
        if (input.Kind is HumanInputKind.ReviewPending or HumanInputKind.ReviewFindings or HumanInputKind.ProvePending or HumanInputKind.ProofFindings)
            return recovering ? input : null;

        var answered = task.CurrentImplementationRevision?.SemanticRevisionId == task.CurrentSemanticRevisionId;
        return answered && !recovering ? null : input;
    }

    private static string ResumedPrompt(WorkflowTask task, PendingHumanInput input, bool recovering)
        => input.Kind switch
        {
            HumanInputKind.Clarification => DirectPathPrompts.ResumedClarification(input.Question!, input.Text, recovering),
            HumanInputKind.Reclassification => DirectPathPrompts.ReclassificationRecorded(input.Reclassification!, recovering),
            HumanInputKind.WhatApproved => DirectPathPrompts.ResumedWhatApproved(input.What!, recovering, howStillRequired: !task.HowSatisfied, adaptProvisionalWork: task.ActiveReclassification is not null),
            HumanInputKind.WhatChangesRequested => DirectPathPrompts.ResumedWhatChanges(input.Text, recovering),
            HumanInputKind.HowApproved => DirectPathPrompts.ResumedHowApproved(input.How!, recovering, adaptProvisionalWork: task.ActiveReclassification is not null),
            HumanInputKind.HowChangesRequested => DirectPathPrompts.ResumedHowChanges(input.Text, recovering),
            HumanInputKind.ReviewFindings => ReviewPrompts.Findings(input.Review!, recovering),
            HumanInputKind.ProofFindings => ProofPrompts.Findings(input.Proof!, recovering),
            _ => DirectPathPrompts.ResumedRequestedChanges(input.Text, recovering),
        };

    // ---- the agent's workflow signals ----------------------------------------------------------------------------------------------

    /// <summary>
    /// If the agent deliberately signalled the controller during <paramref name="turnNumber"/>, records the matching gate durably and ends the stretch. The three
    /// signals (a CLARIFY question, a WHAT proposal, a HOW proposal) are all read first: only one terminal signal may end a turn, so more than one, of the same kind
    /// or of different kinds, fails safely and deterministically as an execution failure with nothing recorded, never by choosing a winner. Returns null when
    /// nothing was signalled (or the HOW was proposed before the WHAT it depends on was approved, which is not recorded and is handled as a turn without a proposal).
    /// </summary>
    private async Task<DirectRunResult?> TryOpenSignalledGateAsync(
        RunState run,
        ProfileId profile,
        AgentSessionId session,
        AgentTurnNumber turnNumber,
        int turn,
        ImplementationState baseline,
        DirectTaskDefinition definition,
        CancellationToken ct)
    {
        if (_clarifications is not { } support || !support.Signals.CanSignal(profile))
            return null;

        var questions = await ReadSignalsAsync(run, "agent signals", () => support.Signals.CollectClarificationsAsync(session, turnNumber, ct)).ConfigureAwait(false);
        var whats = await ReadSignalsAsync(run, "agent WHAT proposals", () => support.Signals.CollectWhatProposalsAsync(session, turnNumber, ct)).ConfigureAwait(false);
        var hows = await ReadSignalsAsync(run, "agent HOW proposals", () => support.Signals.CollectHowProposalsAsync(session, turnNumber, ct)).ConfigureAwait(false);

        var kinds = new[] { questions.Count > 0, whats.Count > 0, hows.Count > 0 }.Count(raised => raised);
        if (kinds == 0)
            return null;

        run.Phase = Phases.Recording;

        if (kinds > 1)
        {
            return Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"signal_conflict: the agent raised more than one workflow signal in one turn ({questions.Count} question(s), {whats.Count} WHAT proposal(s), {hows.Count} HOW proposal(s)); none was recorded",
                run.Snapshot);
        }

        if (questions.Count > 0)
            return await OpenClarificationAsync(run, support, session, turnNumber, turn, baseline, questions, ct).ConfigureAwait(false);

        if (whats.Count > 0)
        {
            return await OpenProposalAsync(
                run, support, session, turnNumber, turn, baseline, definition, ProposalStage.What, whats.Count,
                () => new WhatProposal(whats[0].Objective, whats[0].RequiredBehavior, whats[0].Scope, whats[0].Constraints, whats[0].NonGoals, whats[0].AcceptanceConditions, whats[0].Rationale, turnNumber.Value),
                (basis, gateId, proposal) => _tasks.OpenWhatGateAsync(basis, gateId, proposal, ct),
                ct).ConfigureAwait(false);
        }

        // A HOW is proposed over an intent that is settled. One raised while the WHAT is still to be approved is not recorded (Core would refuse the gate): the
        // turn counts as one without a proposal, so the workspace is checked and the agent is reminded of the stage that is actually due.
        if (!run.Snapshot!.Task.WhatSatisfied)
        {
            run.Notes.Add("the agent proposed the HOW before the WHAT was approved; the proposal was not recorded");
            return null;
        }

        return await OpenProposalAsync(
            run, support, session, turnNumber, turn, baseline, definition, ProposalStage.How, hows.Count,
            () => new HowProposal(hows[0].Strategy, hows[0].Components, hows[0].DataAndControlFlow, hows[0].Compatibility, hows[0].RisksAndTradeoffs, hows[0].VerificationApproach, hows[0].Rationale, turnNumber.Value),
            (basis, gateId, proposal) => _tasks.OpenHowGateAsync(basis, gateId, proposal, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>The result of an accepted or refused reclassification report: either the run stops (nothing recorded), or the loop continues with <see cref="Prompt"/>.</summary>
    private sealed record ReclassificationStep(DirectRunResult? Stop, string? Prompt);

    /// <summary>
    /// If the agent reported new evidence during <paramref name="turnNumber"/>, records it durably and returns the delta to continue the same conversation with.
    /// The report is read first and is the only signal that may end the turn: another signal beside it, more than one report, a stage Core says cannot be reconsidered
    /// (already being worked, a task that is not running) or invalid text all fail safely with nothing recorded. The workspace baseline is kept first (keyed by the
    /// reclassification) so the work can be recovered; the exact workspace state at acceptance becomes the baseline of the provisional work, and Core records one atomic
    /// event. Nothing is reverted and nothing is absorbed into an implementation revision. Null when no report was raised.
    /// </summary>
    private async Task<ReclassificationStep?> TryReclassifyAsync(
        RunState run,
        ProfileId profile,
        AgentSessionId session,
        AgentTurnNumber turnNumber,
        int turn,
        ImplementationState baseline,
        DirectTaskDefinition definition,
        CancellationToken ct)
    {
        if (_clarifications is not { } support || !support.Signals.CanSignal(profile))
            return null;

        var reports = await ReadSignalsAsync(run, "agent reclassifications", () => support.Signals.CollectReclassificationsAsync(session, turnNumber, ct)).ConfigureAwait(false);
        if (reports.Count == 0)
            return null;

        run.Phase = Phases.Recording;

        var others = (await ReadSignalsAsync(run, "agent signals", () => support.Signals.CollectClarificationsAsync(session, turnNumber, ct)).ConfigureAwait(false)).Count
                     + (await ReadSignalsAsync(run, "agent WHAT proposals", () => support.Signals.CollectWhatProposalsAsync(session, turnNumber, ct)).ConfigureAwait(false)).Count
                     + (await ReadSignalsAsync(run, "agent HOW proposals", () => support.Signals.CollectHowProposalsAsync(session, turnNumber, ct)).ConfigureAwait(false)).Count;

        if (reports.Count > 1 || others > 0)
        {
            return Stopped(run, $"reclassification_conflict: the agent raised {reports.Count} reclassification(s) and {others} other workflow signal(s) in one turn; none was recorded");
        }

        var report = reports[0];
        var task = run.Snapshot!.Task;

        if (task.ReclassificationRefusal(report.Stage) is { } refusal)
            return Stopped(run, $"reclassification_refused: {refusal}; nothing was recorded");

        var current = await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false);

        // The report's text is validated by Core's own rules before anything is kept or recorded (a probe with placeholder identities), so an invalid one is a clean refusal.
        try
        {
            _ = new Reclassification(
                _tasks.NewReclassificationId(),
                report.Stage,
                report.Evidence,
                report.Reason,
                turnNumber.Value,
                session.Value,
                task.CurrentSemanticRevisionId,
                new SemanticRevisionId(Guid.NewGuid()),
                current.Fingerprint,
                provisionalWork: false,
                []);
        }
        catch (ArgumentException e)
        {
            return Stopped(run, $"reclassification_invalid: {e.Message} Nothing was recorded.");
        }

        // Provisional work: the workspace differs from the last recorded implementation, or, when none was ever recorded, from the state the stretch began with.
        var reference = task.CurrentImplementationRevision?.Fingerprint ?? baseline.Fingerprint;
        var provisional = current.Fingerprint != reference;

        var id = _tasks.NewReclassificationId();

        try
        {
            await support.Baselines.SaveAsync(run.TaskId, new GateId(id.Value), baseline, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Stopped(run, $"reclassification_baseline_unavailable: the workspace baseline could not be kept ({e.GetType().Name}: {e.Message}), so the reclassification was not recorded");
        }

        try
        {
            run.Snapshot = await CommitAsync(
                run,
                () => _tasks.ReclassifyAsync(run.Snapshot!, id, report.Stage, report.Evidence, report.Reason, turnNumber.Value, session.Value, current.Fingerprint, provisional, ct)).ConfigureAwait(false);
        }
        catch (RunStop stop) when (stop.Status == DirectRunStatus.Rejected)
        {
            return Stopped(run, $"reclassification_invalid: {stop.Message} Nothing was recorded.");
        }

        run.ReclassificationRecorded = true;
        run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, null, null, []));

        return new ReclassificationStep(null, DirectPathPrompts.ReclassificationRecorded(run.Snapshot.Task.Reclassifications[^1]));
    }

    private static ReclassificationStep Stopped(RunState run, string message)
        => new(Build(run, DirectRunStatus.ExecutionFailure, message, run.Snapshot), null);

    private static async Task<IReadOnlyList<T>> ReadSignalsAsync<T>(RunState run, string what, Func<Task<IReadOnlyList<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Notes.Add($"{what} could not be read: {e.GetType().Name}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Records the agent's question durably and ends the stretch: the workspace baseline is saved first (keyed by the gate it is about to belong to), then the
    /// CLARIFY gate is opened under the current semantic revision and the workflow becomes AWAITING_HUMAN. Nothing is claimed about the unfinished work: no
    /// implementation revision, no verification, no ACCEPT gate. A malformed, conflicting or unrecordable question ends the run as an execution failure with
    /// nothing recorded; the agent's own conversation is untouched either way.
    /// </summary>
    private async Task<DirectRunResult> OpenClarificationAsync(
        RunState run,
        AgentClarificationSupport support,
        AgentSessionId session,
        AgentTurnNumber turnNumber,
        int turn,
        ImplementationState baseline,
        IReadOnlyList<AgentClarificationSignal> signals,
        CancellationToken ct)
    {
        if (signals.Count > 1)
        {
            return Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"clarification_conflict: the agent raised {signals.Count} questions in one turn; none was recorded",
                run.Snapshot);
        }

        ClarificationRequest question;
        try
        {
            question = new ClarificationRequest(signals[0].Question, signals[0].Reason, turnNumber.Value);
        }
        catch (ArgumentException e)
        {
            return Build(run, DirectRunStatus.ExecutionFailure, $"clarification_invalid: {e.Message} Nothing was recorded.", run.Snapshot);
        }

        // The baseline first: the question must be resumable with the scope judged against the workspace as it was before the agent asked.
        var gateId = _tasks.NewGateId();
        try
        {
            await support.Baselines.SaveAsync(run.TaskId, gateId, baseline, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"clarification_baseline_unavailable: the workspace baseline of the question could not be kept ({e.GetType().Name}: {e.Message}), so the question was not recorded",
                run.Snapshot);
        }

        run.Snapshot = await CommitAsync(run, () => _tasks.OpenClarifyGateAsync(run.Snapshot!, gateId, question, ct)).ConfigureAwait(false);
        run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, null, null, []));

        return Build(run, DirectRunStatus.AwaitingClarification, $"awaiting_clarification: the agent asked a question (gate {gateId}); awaiting the human's answer", run.Snapshot);
    }

    /// <summary>
    /// Validates the agent's WHAT or HOW proposal, enforces the stop-before-implementation invariant, records it durably and ends the stretch: the workspace must
    /// still be exactly the state the stretch began with (reading and diagnostics change nothing; any edit does, and is refused as the stage's violation), the baseline
    /// is saved first (keyed by the gate it is about to belong to), then the gate is opened under the current semantic revision and the workflow becomes
    /// AWAITING_HUMAN. Nothing is claimed about the unfinished work: no implementation revision, no verification, no ACCEPT gate. A malformed, conflicting or
    /// unrecordable proposal ends the run as an execution failure with nothing recorded; the agent's own conversation is untouched either way.
    /// </summary>
    private async Task<DirectRunResult> OpenProposalAsync<TProposal>(
        RunState run,
        AgentClarificationSupport support,
        AgentSessionId session,
        AgentTurnNumber turnNumber,
        int turn,
        ImplementationState baseline,
        DirectTaskDefinition definition,
        ProposalStage stage,
        int proposed,
        Func<TProposal> build,
        Func<TaskSnapshot, GateId, TProposal, Task<TaskChange>> open,
        CancellationToken ct)
    {
        if (proposed > 1)
        {
            return Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"{stage.Name}_conflict: the agent proposed {proposed} {stage.Label}s in one turn; none was recorded",
                run.Snapshot);
        }

        TProposal proposal;
        try
        {
            proposal = build();
        }
        catch (ArgumentException e)
        {
            return Build(run, DirectRunStatus.ExecutionFailure, $"{stage.Name}_invalid: {e.Message} Nothing was recorded.", run.Snapshot);
        }

        // Stop before implementation: a proposal describes work not yet done. If the agent already changed the workspace, it is refused, not recorded.
        var changed = StageViolationPaths(run.Snapshot!.Task, baseline, await _workspace.CaptureAsync(definition.WorkspacePath, ct).ConfigureAwait(false));
        if (changed.Count > 0)
        {
            run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, null, null, []));

            return Build(run, stage.Violation, WorkBeforeApprovalReason(stage, changed), run.Snapshot);
        }

        var gateId = _tasks.NewGateId();
        try
        {
            await support.Baselines.SaveAsync(run.TaskId, gateId, baseline, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Build(
                run,
                DirectRunStatus.ExecutionFailure,
                $"{stage.Name}_baseline_unavailable: the workspace baseline of the proposal could not be kept ({e.GetType().Name}: {e.Message}), so the proposal was not recorded",
                run.Snapshot);
        }

        run.Snapshot = await CommitAsync(run, () => open(run.Snapshot!, gateId, proposal)).ConfigureAwait(false);
        run.Turns.Add(new DirectTurnRecord(turn, session, AgentTurnStatus.Done, null, null, []));

        return Build(run, stage.Awaiting, $"awaiting_{stage.Name}: the agent proposed the {stage.Label} (gate {gateId}); awaiting the human's decision", run.Snapshot);
    }

    /// <summary>The paths whose content (or presence) differs between two workspace states, plus the repository HEAD when it moved. Empty means the workspace is unchanged.</summary>
    private static IReadOnlyList<string> DifferingPaths(ImplementationState before, ImplementationState after)
    {
        var differing = before.PathDigests.Keys
            .Union(after.PathDigests.Keys, StringComparer.Ordinal)
            .Where(path => !string.Equals(before.PathDigests.GetValueOrDefault(path), after.PathDigests.GetValueOrDefault(path), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (!string.Equals(before.BaseIdentity, after.BaseIdentity, StringComparison.Ordinal))
            differing.Insert(0, "(repository history)");

        return differing;
    }

    /// <summary>
    /// The paths that changed while a pre-implementation stage is unsatisfied. Normally that is the difference from the state the stretch began with. After a
    /// reclassification the workspace may legitimately hold provisional work from before it, so the reference is the state recorded when the reclassification was
    /// accepted: the provisional work is preserved exactly, and any further change before the rerouted stage is approved is a violation.
    /// </summary>
    private static IReadOnlyList<string> StageViolationPaths(WorkflowTask task, ImplementationState baseline, ImplementationState current)
    {
        if (task.ActiveReclassification is { } active)
            return active.Workspace == current.Fingerprint ? [] : ["(the workspace changed after the reclassification was accepted)"];

        return DifferingPaths(baseline, current);
    }

    private static string WorkBeforeApprovalReason(ProposalStage stage, IReadOnlyList<string> changed)
        => $"work_before_{stage.Name}_approval: the agent changed the workspace before the {stage.Label} was approved ({string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? $", and {changed.Count - 5} more" : string.Empty)})";

    /// <summary>
    /// Records RUNNING to BLOCKED for a broken WHAT or HOW stage, with the same guards as the other blocks (own budget, reload first, only a task still RUNNING is
    /// moved). Nothing recoverable is recorded, so no recovery command is promised.
    /// </summary>
    private Task<TaskSnapshot?> TryBlockStageViolationAsync(RunState run, DirectRunResult result)
    {
        var how = result.Status == DirectRunStatus.HowStageViolation;

        return TryBlockRunningAsync(
            run,
            task => task.WorkflowState == WorkflowState.Running,
            how ? HowStageBlock.BlockedReason(result.Reason) : WhatStageBlock.BlockedReason(result.Reason),
            how ? "after the HOW stage was violated" : "after the WHAT stage was violated");
    }

    // ---- workspace observation -------------------------------------------------------------------------------------

    private static bool NeedsNewImplementationRevision(WorkflowTask task, ImplementationState current)
    {
        var existing = task.CurrentImplementationRevision;

        // A revision that targets an older semantic revision cannot serve the current one even if the files are
        // unchanged, so the state observed after this turn is recorded against the current semantic revision.
        return existing is null
               || existing.Fingerprint != current.Fingerprint
               || existing.SemanticRevisionId != task.CurrentSemanticRevisionId;
    }

    private static IReadOnlyList<ScopeViolation> ObserveScope(WorkspaceScope scope, ImplementationState baseline, ImplementationState current)
    {
        var violations = new List<ScopeViolation>();

        if (!string.Equals(baseline.BaseIdentity, current.BaseIdentity, StringComparison.Ordinal))
            violations.Add(new ScopeViolation("(repository history)", "the repository HEAD changed; commits and branch moves are not permitted"));

        var changed = baseline.PathDigests.Keys
            .Union(current.PathDigests.Keys, StringComparer.Ordinal)
            .Where(path =>
            {
                var hadBefore = baseline.PathDigests.TryGetValue(path, out var before);
                var hasNow = current.PathDigests.TryGetValue(path, out var now);
                return hadBefore != hasNow || !string.Equals(before, now, StringComparison.Ordinal);
            });

        violations.AddRange(scope.Evaluate(changed));
        return violations;
    }

    private static CheckDiagnostic ScopeDiagnostic(IReadOnlyList<ScopeViolation> violations)
    {
        var listed = string.Join("; ", violations.Select(v => $"{v.Path} ({v.Reason})"));
        return new CheckDiagnostic(
            ScopeCheckId,
            VerificationOutcome.Failed,
            Truncate($"{violations.Count} out-of-scope change(s): {listed}"),
            string.Empty);
    }

    private static CheckDiagnostic WorkspaceMovedDiagnostic()
        => new(
            WorkspaceStableCheckId,
            VerificationOutcome.Failed,
            "the workspace changed while the checks were running, so this verification does not describe the recorded revision; the current state will be re-observed and verified",
            string.Empty,
            null);

    // ---- controller verification -----------------------------------------------------------------------------------

    private async Task<CheckDiagnostic> RunCheckAsync(string workspace, VerificationCheckSpec check, CancellationToken ct)
    {
        var execution = await _verifier.RunAsync(workspace, check, ct).ConfigureAwait(false);

        var tail = DirectPathPrompts.OutputTail(execution.StdOutTail, execution.StdErrTail);

        if (execution.StartError is not null)
            return new CheckDiagnostic(check.Id, VerificationOutcome.Failed, Truncate($"could not start: {execution.StartError}"), tail, null);

        if (execution.TimedOut)
            return new CheckDiagnostic(check.Id, VerificationOutcome.Failed, Truncate($"timed out after {check.Timeout}"), tail, execution.ExitCode);

        if (execution.ExitCode != check.ExpectedExitCode)
            return new CheckDiagnostic(
                check.Id,
                VerificationOutcome.Failed,
                Truncate($"exit {execution.ExitCode?.ToString() ?? "none"} (expected {check.ExpectedExitCode})"),
                tail,
                execution.ExitCode);

        return new CheckDiagnostic(check.Id, VerificationOutcome.Passed, Truncate($"exit {execution.ExitCode} (expected {check.ExpectedExitCode})"), tail, execution.ExitCode);
    }

    private static CheckDiagnostic AgentObservedDiagnostic(VerificationCheckSpec check, AgentObservation observation)
        => new(
            check.Id,
            VerificationOutcome.Passed,
            Truncate($"agent-observed: {observation.Runtime} ran `{observation.CommandIdentity}` (tool use {observation.ToolUseId}) with exit 0 against this exact implementation state; not re-executed"),
            string.Empty,
            0,
            observation);

    // ---- optional execution evidence ---------------------------------------------------------------------------------

    private async Task BeginEvidenceWindowAsync(AgentSessionId session, RunState run, CancellationToken ct)
    {
        if (_evidence is null)
            return;

        try
        {
            await _evidence.BeginTurnAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Optimisation telemetry must never fail the task: without a window there is simply nothing to reuse.
            run.Notes.Add($"evidence window could not be opened: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task<ObservedTurnEvidence?> CollectEvidenceAsync(AgentSessionId session, RunState run, CancellationToken ct)
    {
        if (_evidence is null)
            return null;

        try
        {
            return await _evidence.CollectTurnAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Notes.Add($"evidence could not be collected: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // ---- mutations: stale and unknown-outcome handling ---------------------------------------------------------------

    /// <summary>
    /// Runs one workflow mutation and translates its failure modes. A stale basis or a Core refusal means nothing
    /// was written; anything else (cancellation, storage failure) leaves the outcome unknown, so the run reloads the
    /// authoritative state and stops. A mutation is never retried here.
    /// </summary>
    private async Task<TaskSnapshot> CommitAsync(RunState run, Func<Task<TaskChange>> mutation)
    {
        try
        {
            return (await mutation().ConfigureAwait(false)).Snapshot;
        }
        catch (StaleWorkflowStateException e)
        {
            var reloaded = await TryReloadAsync(run).ConfigureAwait(false);
            throw new RunStop(DirectRunStatus.StaleWorkflowState, $"stale_workflow_state: {e.Message}", reloaded);
        }
        catch (CompletionNotEligibleException)
        {
            // A refusal, not an unknown outcome: nothing was written. The caller reports it.
            throw;
        }
        catch (DomainException e)
        {
            throw new RunStop(DirectRunStatus.Rejected, $"refused_by_core: {e.Message}", run.Snapshot);
        }
        catch (TaskNotFoundException e)
        {
            throw new RunStop(DirectRunStatus.Rejected, $"task_not_found: {e.Message}", null);
        }
        catch (Exception e)
        {
            // OperationCanceledException or an unexpected storage failure: the append may or may not have committed.
            var reloaded = await TryReloadAsync(run).ConfigureAwait(false);
            throw new RunStop(
                DirectRunStatus.Indeterminate,
                $"indeterminate_outcome: a workflow mutation ended with {e.GetType().Name}; it was not repeated"
                + (reloaded is null ? " and the task could not be reloaded" : $"; the task was reloaded at version {reloaded.Version}"),
                reloaded);
        }
    }

    private async Task<TaskSnapshot?> TryReloadAsync(RunState run)
    {
        // The caller's token may be the reason we are here, so reload under our own bounded budget.
        using var budget = new CancellationTokenSource(_options.CleanupTimeout);

        try
        {
            return await _tasks.FindAsync(run.TaskId, budget.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> StopSessionAsync(AgentSessionId session, RunState run)
    {
        using var budget = new CancellationTokenSource(_options.CleanupTimeout);

        try
        {
            await _runtime.StopSessionAsync(session, budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            run.Notes.Add($"stopping session {session} failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static DirectRunStatus ToStatus(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Error => DirectRunStatus.AgentError,
        AgentTurnStatus.Cancelled => DirectRunStatus.AgentCancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only an error or a cancellation is a stop status of a turn."),
    };

    private static DirectRunResult Build(
        RunState run,
        DirectRunStatus status,
        string reason,
        TaskSnapshot? snapshot,
        DirectCompletionEligibility? eligibility = null)
        => new()
        {
            Status = status,
            Reason = reason,
            Task = snapshot,
            Session = run.Session,
            Continuation = run.Continuation,
            Turns = run.Turns.ToArray(),
            LastVerification = run.LastVerification,
            EvidenceDecisions = run.EvidenceDecisions,
            Eligibility = eligibility ?? (snapshot is null ? null : DirectPathCompletion.Evaluate(snapshot.Task)),
            Notes = run.Notes.ToArray(),
        };

    private static string Truncate(string text)
        => text.Length <= SummaryLimit ? text : text[..(SummaryLimit - 3)] + "...";

    /// <summary>The two proposal stages that precede implementation: how each is named in reasons and which run statuses it ends with.</summary>
    private sealed record ProposalStage(string Name, string Label, DirectRunStatus Awaiting, DirectRunStatus Violation)
    {
        public static ProposalStage What { get; } = new("what", "WHAT", DirectRunStatus.AwaitingWhat, DirectRunStatus.WhatStageViolation);

        public static ProposalStage How { get; } = new("how", "HOW", DirectRunStatus.AwaitingHow, DirectRunStatus.HowStageViolation);

        public bool IsHow => Name == "how";
    }

    /// <summary>Which stretch of the run this is: the first, the one after human input (requested changes or an answer), or a retry of that one after a runtime failure.</summary>
    private enum Stretch
    {
        Initial,
        Continuation,
        Recovery,
    }

    /// <summary>
    /// Where a failure happened, in words (recorded in the BLOCKED reason and shown by <c>aw status</c>). It is a diagnosis of the last attempt, never a
    /// proof about the conversation: an earlier attempt may already have delivered the instruction.
    /// </summary>
    private static class Phases
    {
        public const string Resuming = "starting or resuming the agent conversation";
        public const string Sending = "sending the turn to the agent";
        public const string Waiting = "waiting for the agent's turn";
        public const string Observing = "observing and verifying the result";
        public const string Recording = "recording the agent's question";
        public const string Reviewing = "starting the independent reviewer";
        public const string WaitingForReviewer = "waiting for the independent reviewer";
        public const string RecordingReview = "recording the independent review";
        public const string Proving = "starting the independent prover";
        public const string WaitingForProver = "waiting for the independent prover";
        public const string RecordingProof = "recording the independent proof";
    }

    private sealed class RunState(TaskId taskId, INativeInteractionObserver? observer)
    {
        public TaskId TaskId { get; } = taskId;

        public NativeInteractionAnnouncer Interactions { get; } = new(observer);

        public string Phase { get; set; } = Phases.Resuming;

        public TaskSnapshot? Snapshot { get; set; }

        public AgentSessionId? Session { get; set; }

        public AgentContinuation? Continuation { get; set; }

        /// <summary>True from the moment AWAITING_HUMAN was recorded until the workflow returned to RUNNING.</summary>
        public bool AwaitingHuman { get; set; }

        /// <summary>True once this run recorded an agent reclassification: the work that follows is a recoverable continuation, not a first attempt.</summary>
        public bool ReclassificationRecorded { get; set; }

        /// <summary>True once the run entered the independent review phase: whatever fails from here on is recoverable work (a fresh review, or the repair of its findings), not a first attempt.</summary>
        public bool ReviewPhase { get; set; }

        /// <summary>True once the run entered the independent proof phase: whatever fails from here on is recoverable work (a fresh proof, or the repair of its findings), not a first attempt.</summary>
        public bool ProvePhase { get; set; }

        /// <summary>True once the implementer was reminded, in this run, that PROVE is required and it has no Proof Contract: the one reminder is spent.</summary>
        public bool ContractReminded { get; set; }

        /// <summary>Why the last Proof Contract the agent submitted was not recorded (bounded display text), or null; told to the implementer in the one reminder.</summary>
        public string? ContractProblem { get; set; }

        /// <summary>The reviewer or prover session this run already stopped itself (after an unanswerable native interaction), so it is not stopped twice.</summary>
        public AgentSessionId? StageSessionStopped { get; set; }

        /// <summary>The bounded description of the last native interaction observed, for the BLOCKED reason.</summary>
        public string InteractionSummary { get; set; } = "unspecified";

        public List<DirectTurnRecord> Turns { get; } = [];

        public IReadOnlyList<CheckDiagnostic> LastVerification { get; set; } = [];

        public IReadOnlyList<EvidenceDecision> EvidenceDecisions { get; set; } = [];

        public List<string> Notes { get; } = [];
    }

    /// <summary>Internal control flow: ends the run with a specific result from deep inside the loop.</summary>
    private sealed class RunStop(DirectRunStatus status, string message, TaskSnapshot? snapshot, DirectCompletionEligibility? eligibility = null)
        : Exception(message)
    {
        public DirectRunStatus Status { get; } = status;

        public TaskSnapshot? Snapshot { get; } = snapshot;

        public DirectCompletionEligibility? Eligibility { get; } = eligibility;
    }
}
