using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Acceptance;

/// <summary>Why a person's decision on a gate was refused. Nothing was written in any of these cases.</summary>
public enum GateRefusal
{
    /// <summary>The run was created without a final acceptance (<c>acceptance: none</c>): it has no gate to decide, and none is ever created for it afterwards.</summary>
    AcceptanceNotRequired,

    /// <summary>The run has no gate awaiting a decision (it never reached one, or it was already decided and the run moved on).</summary>
    NoPendingGate,

    /// <summary>The named gate is not a gate of this run (another run's gate, or an invented id).</summary>
    UnknownGate,

    /// <summary>The named gate was decided before; a gate is decided once.</summary>
    AlreadyDecided,

    /// <summary>The gate no longer describes the task: its semantic revision, implementation revision or verification is not the current one.</summary>
    NotCurrent,

    /// <summary>The working tree is no longer the exact state that was verified and shown to the person.</summary>
    WorkspaceChanged,

    /// <summary>The working tree could not be inspected, so it cannot be confirmed to be the reviewed state.</summary>
    WorkspaceUnavailable,

    /// <summary>The gate is of another kind than the command decides (an answer to an ACCEPT gate, an acceptance of a CLARIFY question, an approval of an ACCEPT gate...).</summary>
    WrongGateKind,

    /// <summary>The run's history moved while the decision was being made.</summary>
    StateMoved,

    /// <summary>
    /// The run's recorded agent conversation is corrupt or belongs to another run, profile or runtime. Continuing would mean either resuming something that
    /// cannot be trusted or silently starting fresh and losing the context, so the request is refused before anything is recorded.
    /// </summary>
    ConversationUnusable,
}

/// <summary>A decision on a gate was refused; <see cref="Reason"/> says why in a form a caller can act on.</summary>
public sealed class GateRefusedException(GateRefusal reason, string message) : Exception(message)
{
    public GateRefusal Reason { get; } = reason;
}

/// <summary>A decision that was recorded: the gate it closed and the task as it now stands.</summary>
public sealed record GateOutcome(Gate Gate, TaskChange Change);

/// <summary>
/// The person's side of the workflow-owned ACCEPT gate, for runs that require acceptance (a run created with no acceptance has no gate and every decision is refused). It works from nothing but the durable event stream (and, for ACCEPT, a read of the
/// working tree), so it is usable from any later process: the gate does not need the agent session that produced the work.
/// <para>
/// Every decision is an explicit structured action on a named (or the only open) gate. No text is interpreted as a decision. ACCEPT is
/// refused unless the gate is the open one, still current (semantic revision, implementation revision and verification), and the working tree is
/// still exactly the implementation state the gate binds; the controller then records the acceptance and completion in one atomic append, with no
/// agent or model involved. REQUEST CHANGES and cancel only close the gate and are allowed for any open gate, stale or not.
/// </para>
/// </summary>
public sealed class AcceptGateService
{
    private readonly WorkflowTaskService _tasks;
    private readonly IImplementationStateProvider _workspace;

    public AcceptGateService(WorkflowTaskService tasks, IImplementationStateProvider workspace)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(workspace);

        _tasks = tasks;
        _workspace = workspace;
    }

    /// <param name="gateId">The gate being accepted; null means "the open gate".</param>
    /// <param name="workspacePath">Where the reviewed working tree is; it must still be exactly the verified state.</param>
    public async Task<GateOutcome> AcceptAsync(TaskId taskId, GateId? gateId, string workspacePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var gate = Resolve(snapshot.Task, gateId, GateType.Accept);

        var eligibility = DirectPathCompletion.Evaluate(snapshot.Task);
        if (!snapshot.Task.IsCurrent(gate) || !eligibility.IsEligible)
        {
            throw new GateRefusedException(
                GateRefusal.NotCurrent,
                $"Gate {gate.Id} no longer describes the run ({(eligibility.IsEligible ? "its implementation or verification is not the current one" : string.Join(", ", eligibility.Reasons))}); it cannot be accepted.");
        }

        await RequireUnchangedWorkspaceAsync(snapshot.Task, gate, workspacePath, cancellationToken).ConfigureAwait(false);

        var change = await DecideAsync(() => _tasks.AcceptGateAsync(snapshot, gate.Id, cancellationToken)).ConfigureAwait(false);
        return new GateOutcome(gate, change);
    }

    /// <summary>
    /// Closes the gate as not approved and returns the run to RUNNING under a new semantic revision. <paramref name="feedback"/> is required and bounded. It applies
    /// to an ACCEPT gate (the result is changed), to a WHAT gate and to a HOW gate (the proposal is revised, nothing is implemented); never to a CLARIFY question.
    /// </summary>
    public async Task<GateOutcome> RequestChangesAsync(TaskId taskId, GateId? gateId, string feedback, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback);

        if (feedback.Length > GateDecision.MaxFeedbackLength)
            throw new ArgumentException($"Feedback must not exceed {GateDecision.MaxFeedbackLength} characters (was {feedback.Length}).", nameof(feedback));

        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var gate = Resolve(snapshot.Task, gateId, GateType.Accept, alsoGate: GateType.What, andAlsoGate: GateType.How);

        var change = await DecideAsync(() => _tasks.RequestChangesAsync(snapshot, gate.Id, feedback.Trim(), cancellationToken)).ConfigureAwait(false);
        return new GateOutcome(gate, change);
    }

    /// <summary>Closes the gate without accepting it and cancels the run.</summary>
    public async Task<GateOutcome> CancelAsync(TaskId taskId, GateId? gateId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var gate = Resolve(snapshot.Task, gateId, kind: null);

        var change = await DecideAsync(() => _tasks.CancelGateAsync(snapshot, gate.Id, cancellationToken)).ConfigureAwait(false);
        return new GateOutcome(gate, change);
    }

    /// <summary>
    /// The gate a decision is about: the named one if it belongs to the run and is open, otherwise the run's only open gate. <paramref name="kind"/> is the
    /// gate kind the decision applies to (null for cancel, which closes any gate). Shared by <see cref="ClarificationService"/>.
    /// </summary>
    internal static Gate Resolve(WorkflowTask task, GateId? requested, GateType? kind, GateType? alsoGate = null, GateType? andAlsoGate = null)
    {
        var gate = requested is { } id ? task.Gates.FirstOrDefault(g => g.Id == id) : task.PendingGate;

        if (gate is null)
        {
            // A run created without acceptance has no ACCEPT gate to decide, and none is ever created for it afterwards (a CLARIFY question can still be open).
            if (kind is not (GateType.Clarify or GateType.What or GateType.How) && task.Acceptance == AcceptanceRequirement.NotRequired)
            {
                throw new GateRefusedException(
                    GateRefusal.AcceptanceNotRequired,
                    $"Run {task.Id} does not require acceptance (it is {task.WorkflowState}), so it has no ACCEPT gate to decide. Nothing was recorded. "
                    + "Start runs that need your review with 'aw run --acceptance human'.");
            }

            throw requested is { } missing
                ? new GateRefusedException(GateRefusal.UnknownGate, $"Gate {missing} is not a gate of run {task.Id}.")
                : new GateRefusedException(GateRefusal.NoPendingGate, $"Run {task.Id} has no gate awaiting a decision (it is {task.WorkflowState}).");
        }

        if (task.DecisionFor(gate.Id) is { } decision)
            throw new GateRefusedException(GateRefusal.AlreadyDecided, $"Gate {gate.Id} was already decided ({Describe(decision.Action)}); a gate is decided once.");

        if (kind is { } expected && gate.Type != expected && gate.Type != alsoGate && gate.Type != andAlsoGate)
        {
            throw new GateRefusedException(
                GateRefusal.WrongGateKind,
                gate.Type switch
                {
                    GateType.Clarify => $"Gate {gate.Id} is a CLARIFY question, not {Article(expected)} {Name(expected)}; it is answered with 'aw answer {task.Id} --question {gate.Id} --text \"...\"'. Nothing was recorded.",
                    GateType.What => $"Gate {gate.Id} is a WHAT proposal, not {Article(expected)} {Name(expected)}; it is decided with 'aw approve {task.Id} --gate {gate.Id}' or 'aw request-changes {task.Id} --gate {gate.Id} --feedback \"...\"'. Nothing was recorded.",
                    GateType.How => $"Gate {gate.Id} is a HOW proposal, not {Article(expected)} {Name(expected)}; it is decided with 'aw approve {task.Id} --gate {gate.Id}' or 'aw request-changes {task.Id} --gate {gate.Id} --feedback \"...\"'. Nothing was recorded.",
                    _ => $"Gate {gate.Id} is an ACCEPT gate, not {Article(expected)} {Name(expected)}; it is decided with 'aw accept' or 'aw request-changes'. Nothing was recorded.",
                });
        }

        return gate;
    }

    private static string Name(GateType kind) => kind switch
    {
        GateType.Clarify => "clarification question",
        GateType.What => "WHAT proposal",
        GateType.How => "HOW proposal",
        _ => "ACCEPT gate",
    };

    private static string Article(GateType kind) => kind == GateType.Accept ? "an" : "a";

    private async Task RequireUnchangedWorkspaceAsync(WorkflowTask task, Gate gate, string workspacePath, CancellationToken cancellationToken)
    {
        var reviewed = task.ImplementationRevisions.First(r => r.Id == gate.ImplementationRevisionId);

        ImplementationState current;
        try
        {
            current = await _workspace.CaptureAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new GateRefusedException(
                GateRefusal.WorkspaceUnavailable,
                $"The working tree '{workspacePath}' could not be inspected ({e.Message}), so it cannot be confirmed to be the state that was verified.");
        }

        if (current.Fingerprint != reviewed.Fingerprint)
        {
            throw new GateRefusedException(
                GateRefusal.WorkspaceChanged,
                $"The working tree changed after it was verified, so gate {gate.Id} does not describe it. Nothing was accepted; request changes to have it verified again.");
        }
    }

    internal static async Task<TaskChange> DecideAsync(Func<Task<TaskChange>> decision)
    {
        try
        {
            return await decision().ConfigureAwait(false);
        }
        catch (StaleWorkflowStateException e)
        {
            throw new GateRefusedException(GateRefusal.StateMoved, $"The run changed while the decision was being made; nothing was recorded. {e.Message}");
        }
    }

    internal static string Describe(GateAction action) => action switch
    {
        GateAction.Accept => "accepted",
        GateAction.RequestChanges => "changes requested",
        GateAction.Answer => "answered",
        GateAction.Approve => "approved",
        _ => "cancelled",
    };
}
