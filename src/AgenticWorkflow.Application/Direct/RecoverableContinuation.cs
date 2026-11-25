using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;

namespace AgenticWorkflow.Application.Direct;

/// <summary>Why <c>aw continue</c> was refused. Nothing was written in any of these cases.</summary>
public enum ContinuationRefusal
{
    /// <summary>The run is not BLOCKED (it is RUNNING, AWAITING_HUMAN, or already terminal), so there is no blocked continuation to retry.</summary>
    NotBlocked,

    /// <summary>The run is BLOCKED, but not because a human-requested continuation failed (for example a native interaction nobody could answer).</summary>
    NotRecoverable,

    /// <summary>The recoverable work cannot be retried safely from what was recorded (its workspace baseline is missing or does not belong to this decision).</summary>
    BaselineUnavailable,

    /// <summary>The run's history moved while the continuation was being recorded.</summary>
    StateMoved,

    /// <summary>The agent host of the interrupted attempt could not be confirmed stopped, so another agent must not be started on the run beside it.</summary>
    SessionNotStopped,
}

/// <summary>A continuation was refused; <see cref="Reason"/> says why in a form a caller can act on.</summary>
public sealed class ContinuationRefusedException(ContinuationRefusal reason, string message) : Exception(message)
{
    public ContinuationRefusal Reason { get; } = reason;
}

/// <summary>
/// The pending work of a recoverable continuation, read from the authoritative history alone: the human input that asked for it (a request for changes or an
/// answer to a CLARIFY question), the semantic revision that input created, and why execution is blocked. Nothing here is a second copy of the decision.
/// </summary>
public sealed record PendingContinuation(PendingHumanInput Input, string BlockedReason)
{
    public HumanInputKind Kind => Input.Kind;

    /// <summary>The human decision that carries the input; null for a reclassification, which the agent raised itself.</summary>
    public GateDecision? Decision => Input.Decision;

    /// <summary>The id the workspace baseline of this continuation is kept under (the decision's gate, or the reclassification's own id).</summary>
    public GateId BaselineKey => Input.BaselineKey;

    /// <summary>The reclassification whose revised proposal is pending; null for human input.</summary>
    public Reclassification? Reclassification => Input.Reclassification;

    public SemanticRevision Revision => Input.Revision;

    /// <summary>The question being answered; null otherwise.</summary>
    public ClarificationRequest? Question => Input.Question;

    /// <summary>The WHAT proposal that was approved or sent back; null for any other input.</summary>
    public WhatProposal? What => Input.What;

    /// <summary>The HOW proposal that was approved or sent back; null for any other input.</summary>
    public HowProposal? How => Input.How;

    /// <summary>The independent review whose findings are being repaired; null for any other input.</summary>
    public Review? Review => Input.Review;

    /// <summary>The not-passed proof whose findings are the pending repair, or null.</summary>
    public Proof? Proof => Input.Proof;

    /// <summary>The person's own words: the feedback or the answer.</summary>
    public string Text => Input.Text;
}

/// <summary>
/// The one definition of "a human-requested continuation that is blocked and can be retried" (extended with the answer to a CLARIFY question as a
/// second producer of the same kind of pending work), shared by whoever records the block, whoever recognizes it and whoever retries it.
/// <para>
/// A human decision (REQUEST CHANGES, or the ANSWER to a question) is a semantic event and is recorded exactly once, together with its semantic revision. Executing the work it asks for is
/// operational: when that cannot proceed because the runtime failed, the workflow is BLOCKED (progress cannot currently continue), not RUNNING (nobody is
/// working). The block is recorded with a reason that starts with <see cref="BlockedReasonPrefix"/>, in the same event stream as everything else, so a fresh
/// process can recognize it without any other store. Recovering it re-enters RUNNING without a new decision, a new semantic revision or a new gate.
/// </para>
/// </summary>
public static class RecoverableContinuation
{
    /// <summary>
    /// What the reason of the recoverable BLOCKED transition starts with. It is the marker that separates this block from every other BLOCKED reason
    /// (for example a native interaction nobody could answer, which <c>aw continue</c> must refuse). It is part of the persisted history: do not change it.
    /// </summary>
    public const string BlockedReasonPrefix = "Agent continuation blocked by a runtime failure";

    private const int CauseLimit = 240;

    /// <summary>The reason recorded with the RUNNING to BLOCKED transition. <paramref name="phase"/> and <paramref name="cause"/> are bounded, single-line text.</summary>
    public static string BlockedReason(string phase, string cause, bool sessionConfirmedStopped)
        => $"{BlockedReasonPrefix} (while {BoundedText.Line(phase, 60)}): {BoundedText.Line(cause, CauseLimit)} "
           + "The semantic change remains current and the work may be continued with 'aw continue'; the agent may already have changed the workspace."
           + (sessionConfirmedStopped ? string.Empty : " The agent session could not be confirmed stopped.");

    /// <summary>The pending work of a task that is BLOCKED because a human-requested continuation failed, or null when the task is in no such state.</summary>
    public static PendingContinuation? Find(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.WorkflowState != WorkflowState.Blocked || !task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal))
            return null;

        return PendingHumanInput.Find(task) is { } input ? new PendingContinuation(input, task.StateReason) : null;
    }

    /// <summary>
    /// True when the task is RUNNING with human input (requested changes, or an answer) still being worked on: the latest gate decision carries it, no gate has
    /// been opened since, and the task still is under the semantic revision that decision created. This is the state a failed continuation would strand.
    /// </summary>
    public static bool IsWorkInProgress(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Running && PendingHumanInput.Find(task) is not null;
    }

    /// <summary>Why <paramref name="task"/> cannot be recovered, in words for a person, or null when it can.</summary>
    public static ContinuationRefusedException? Refusal(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (Find(task) is not null)
            return null;

        var state = task.WorkflowState.ToString().ToUpperInvariant();

        if (task.WorkflowState != WorkflowState.Blocked)
        {
            var advice = task.WorkflowState switch
            {
                WorkflowState.Completed or WorkflowState.Cancelled or WorkflowState.Failed => "It has ended; there is nothing to continue.",
                WorkflowState.AwaitingHuman => "It is waiting for a person (see 'aw status'); continuing is not how that is answered.",
                _ => "It is not blocked, so there is no blocked continuation to retry.",
            };

            return new ContinuationRefusedException(ContinuationRefusal.NotBlocked, $"Run {task.Id} is {state}. {advice}");
        }

        return new ContinuationRefusedException(
            ContinuationRefusal.NotRecoverable,
            $"Run {task.Id} is BLOCKED, but not by a failed continuation of human input (requested changes or an answer), which is the only thing 'aw continue' recovers. Reason: {task.StateReason}");
    }
}

/// <summary>
/// The operational recovery of a blocked continuation. It writes exactly one thing: the BLOCKED to RUNNING transition that says "the controller is working on
/// this again". It never records a gate decision, a semantic revision or a gate; the human already decided and that decision stays as it was.
/// </summary>
public sealed class ContinuationRecoveryService
{
    private readonly WorkflowTaskService _tasks;

    public ContinuationRecoveryService(WorkflowTaskService tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _tasks = tasks;
    }

    private static string DescribeKind(HumanInputKind kind) => kind switch
    {
        HumanInputKind.Clarification => "human clarification",
        HumanInputKind.WhatApproved => "human WHAT approval",
        HumanInputKind.WhatChangesRequested => "human-requested changes to the WHAT",
        HumanInputKind.HowApproved => "human HOW approval",
        HumanInputKind.HowChangesRequested => "human-requested changes to the HOW",
        HumanInputKind.Reclassification => "agent reclassification",
        HumanInputKind.ReviewPending => "independent review",
        HumanInputKind.ReviewFindings => "repair of the independent review's findings",
        HumanInputKind.ProvePending => "formal proof",
        HumanInputKind.ProofFindings => "repair of the formal proof's findings",
        _ => "human-requested changes",
    };

    /// <summary>The pending work of <paramref name="taskId"/>; <see cref="ContinuationRefusedException"/> when the run is not in the one supported state. Writes nothing.</summary>
    public async Task<PendingContinuation> InspectAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);

        return RecoverableContinuation.Find(snapshot.Task) ?? throw RecoverableContinuation.Refusal(snapshot.Task)!;
    }

    /// <summary>
    /// Re-enters RUNNING for the pending work, over the state it was read from. Refused (and nothing written) unless the run is exactly the supported
    /// blocked continuation at this moment.
    /// </summary>
    public async Task<TaskChange> ResumeAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        var pending = RecoverableContinuation.Find(snapshot.Task) ?? throw RecoverableContinuation.Refusal(snapshot.Task)!;

        try
        {
            return await _tasks.ChangeWorkflowStateAsync(
                snapshot,
                WorkflowState.Running,
                $"Continuing the {DescribeKind(pending.Kind)} of semantic revision {pending.Revision.Ordinal} with 'aw continue' after the runtime failure; no new decision, review or semantic revision was recorded.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (StaleWorkflowStateException e)
        {
            throw new ContinuationRefusedException(ContinuationRefusal.StateMoved, $"The run changed while the continuation was being recorded; nothing was recorded. {e.Message}");
        }
    }
}
