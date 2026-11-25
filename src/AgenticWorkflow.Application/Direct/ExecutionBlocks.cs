using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// The BLOCKED recorded when an agent attempt ends because the runtime failed (the agent process died, a turn timed out, the session could not start) and
/// nothing is working on the run any more: no semantic human gate is pending and nothing was verified or completed, so the workflow cannot progress and must not
/// claim RUNNING. It is the operational counterpart of <see cref="RecoverableContinuation"/> and deliberately distinct from it: that block carries the decision of a
/// person and is retried with <c>aw continue</c>; this one has no recorded pending work to retry, so no in-place recovery command applies to it.
/// <para>
/// The reason starts with <see cref="BlockedReasonPrefix"/>, in the same event stream as everything else, so a fresh process recognizes it without any other
/// store. It is part of the persisted history: do not change it, and never let it start with <see cref="RecoverableContinuation.BlockedReasonPrefix"/>.
/// </para>
/// </summary>
public static class RuntimeFailureBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition for a failed first attempt starts with.</summary>
    public const string BlockedReasonPrefix = "Agent execution blocked by runtime failure";

    private const int CauseLimit = 240;

    /// <summary>The reason recorded with the transition. <paramref name="phase"/> and <paramref name="cause"/> are bounded, single-line text.</summary>
    public static string BlockedReason(string phase, string cause, bool sessionConfirmedStopped)
        => $"{BlockedReasonPrefix} (while {BoundedText.Line(phase, 60)}): {BoundedText.Line(cause, CauseLimit)} "
           + "Nothing is working on this run and no in-place recovery command is available; the agent may already have changed the workspace."
           + (sessionConfirmedStopped ? string.Empty : " The agent session could not be confirmed stopped.");

    /// <summary>True when <paramref name="task"/> is BLOCKED because its agent attempt ended on a runtime failure.</summary>
    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the controller's verification is still failing after the permitted turns and nothing is working on the run any more. It is a
/// verdict about the work, not a runtime failure, and it is not recoverable work either: no human decision is pending and no command retries or re-verifies it
/// in place, so, like <see cref="RuntimeFailureBlock"/>, it is not a recoverable continuation and <c>aw continue</c> refuses it.
/// </summary>
public static class UnresolvedVerificationBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "Verification unresolved";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "Nothing is working on this run and no in-place recovery command is available; the working tree keeps the agent's last attempt.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the WHAT stage was broken: the task needs a human-approved WHAT before any implementation, and the agent changed the workspace
/// without one or finished without proposing one. The controller refuses to record an implementation, so nothing downstream (verification, an ACCEPT gate,
/// completion) exists. Not recoverable work: no human decision is pending and <c>aw continue</c> refuses it; the person inspects the working tree, aborts, or starts again.
/// </summary>
public static class WhatStageBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "WHAT stage violated";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "No implementation was recorded and nothing was verified. Nothing is working on this run and no in-place recovery command is available; the working tree keeps what the agent did.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the HOW stage was broken: the task needs a human-approved HOW before any implementation, and the agent changed the workspace
/// without one or finished without proposing one. Not recoverable work, exactly as for <see cref="WhatStageBlock"/>: no human decision is pending and
/// <c>aw continue</c> refuses it.
/// </summary>
public static class HowStageBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "HOW stage violated";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "No implementation was recorded and nothing was verified. Nothing is working on this run and no in-place recovery command is available; the working tree keeps what the agent did.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the independent reviewer changed the workspace it was only to read. Nothing is absorbed, recorded as an implementation revision or
/// reverted, and no report is recorded: the working tree keeps exactly what the reviewer left, for a person to inspect. Not recoverable work: <c>aw continue</c> refuses it.
/// </summary>
public static class ReviewerWorkspaceBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "reviewer_modified_workspace";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "The independent reviewer must not change the workspace. No review was recorded, no implementation revision was created and nothing was reverted; the working tree keeps what the reviewer left. No in-place recovery command is available.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the independent review still required changes after the last permitted agent turn. Like <see cref="UnresolvedVerificationBlock"/> it is a
/// verdict, not a failure, and not recoverable work.
/// </summary>
public static class ReviewUnresolvedBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "Review unresolved";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "Nothing is working on this run and no in-place recovery command is available; the working tree keeps the agent's last attempt.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the independent prover changed the workspace it was only to read. Nothing is absorbed, recorded as an implementation revision or
/// reverted, and no proof is recorded: the working tree keeps exactly what the prover left, for a person to inspect. Not recoverable work: <c>aw continue</c> refuses it.
/// </summary>
public static class ProverWorkspaceBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "prover_modified_workspace";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "The independent prover must not change the workspace. No proof was recorded, no implementation revision was created and nothing was reverted; the working tree keeps what the prover left. No in-place recovery command is available.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when the formal proof was not established after the last permitted agent turn, or when PROVE is required and the implementer still produced no
/// Proof Contract after its one reminder. Like <see cref="ReviewUnresolvedBlock"/> it is a verdict, not a failure, and not recoverable work.
/// </summary>
public static class ProofUnresolvedBlock
{
    /// <summary>What the reason of the RUNNING to BLOCKED transition starts with. Part of the persisted history: do not change it.</summary>
    public const string BlockedReasonPrefix = "Proof unresolved";

    private const int CauseLimit = 240;

    public static string BlockedReason(string cause)
        => $"{BlockedReasonPrefix}: {BoundedText.Line(cause, CauseLimit)} "
           + "Nothing is working on this run and no in-place recovery command is available; the working tree keeps the agent's last attempt.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// The BLOCKED recorded when an agent needed a person in its own interface and its session ended before anyone could answer. Its reason text is part of the
/// persisted history; the prefix lets a later process tell this block from the other BLOCKED reasons.
/// </summary>
public static class NativeInteractionBlock
{
    public const string BlockedReasonPrefix = "Native agent interaction was required";

    public static string BlockedReason(string interaction)
        => $"{BlockedReasonPrefix} ({interaction}), but no interactive supervision surface was available and the native session has ended.";

    /// <summary>The same block, recorded by the next controller when the one that held the wait ended and took the agent's session with it.</summary>
    public static string ControllerEndedReason(string interaction)
        => $"{BlockedReasonPrefix} ({interaction}), but the controller that held the native session ended and the session is gone.";

    public static bool IsBlockedBy(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.WorkflowState == WorkflowState.Blocked && task.StateReason.StartsWith(BlockedReasonPrefix, StringComparison.Ordinal);
    }
}

/// <summary>Runtime text on one line, bounded and free of control characters, so it can sit in a workflow reason and be shown on a terminal.</summary>
internal static class BoundedText
{
    public static string Line(string text, int limit)
    {
        var printable = new string(text.Select(c => char.IsControl(c) && !char.IsWhiteSpace(c) ? '?' : c).ToArray());
        var singleLine = string.Join(' ', printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (singleLine.Length == 0)
            singleLine = "(no detail given)";

        return singleLine.Length <= limit ? singleLine : singleLine[..(limit - 3)] + "...";
    }
}
