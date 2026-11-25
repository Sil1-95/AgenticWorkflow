using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Core.Gates;

/// <summary>
/// The kinds of workflow-owned human gate: the final ACCEPT, the CLARIFY question, the WHAT approval and the HOW approval. The enum is the single place a gate kind is
/// named; nothing else is generic about gates, and there is no gate graph.
/// </summary>
public enum GateType
{
    /// <summary>The human's decision on the coherent final engineering state: complete, or change it. Bound to a verified implementation.</summary>
    Accept,

    /// <summary>
    /// A question the working agent deliberately raised because a human-owned answer materially affects what the task means. Bound to the semantic revision it
    /// was asked under; it needs no implementation or verification, and it is answered with text (an answer, not an approval).
    /// </summary>
    Clarify,

    /// <summary>
    /// A bounded proposal of what the task is to achieve, which a human approves before anything is implemented. Bound to the semantic revision it was
    /// proposed under and to its <see cref="WhatProposal"/> only; it needs no implementation or verification, because nothing is implemented before it is approved.
    /// </summary>
    What,

    /// <summary>
    /// A bounded proposal of the implementation strategy the task is to use, which a human approves before anything is implemented. Bound, like WHAT, to the
    /// semantic revision it was proposed under and to its <see cref="HowProposal"/> only; it needs no implementation or verification.
    /// </summary>
    How,
}

/// <summary>What a person can do with an open gate. Always an explicit, structured action, never text interpreted as one.</summary>
public enum GateAction
{
    /// <summary>The reviewed state is accepted; the workflow completes.</summary>
    Accept,

    /// <summary>The reviewed state is not accepted; the workflow returns to work under a new semantic revision.</summary>
    RequestChanges,

    /// <summary>The gate is closed without acceptance and the workflow is cancelled.</summary>
    Cancel,

    /// <summary>A CLARIFY question is answered; the workflow returns to work under a new semantic revision that carries the answer.</summary>
    Answer,

    /// <summary>A WHAT or HOW proposal is approved; the workflow returns to work under a new semantic revision that records what was approved. Nothing is implemented or verified by it.</summary>
    Approve,
}

/// <summary>Where a gate is in its life. Derived from the history; a gate is decided at most once.</summary>
public enum GateStatus
{
    Open,
    Accepted,
    ChangesRequested,
    Cancelled,
    Answered,
    Approved,

    /// <summary>
    /// A gate that was approved and is no longer current: new evidence invalidated it, or the WHAT it was derived from was invalidated or superseded. It is never
    /// stored: it is derived from the history, which still shows the approval.
    /// </summary>
    Invalidated,
}

/// <summary>
/// An open request for a human decision. An ACCEPT gate is bound to the exact state the human is asked to review: the semantic revision, the implementation
/// revision and the passing verification run of that implementation. A CLARIFY gate is bound only to the semantic revision it was asked under and carries
/// its <see cref="ClarificationRequest"/>; it has no implementation or verification, because nothing was verified when the agent asked. A WHAT gate is bound
/// only to the semantic revision and carries its <see cref="WhatProposal"/>, for the same reason; a HOW gate likewise carries its <see cref="HowProposal"/>. The binding never
/// changes; a gate whose binding is no longer the task's current state is <em>stale</em> (see <c>WorkflowTask.IsCurrent</c>).
/// </summary>
public sealed record Gate
{
    public Gate(
        GateId id,
        TaskId taskId,
        GateType type,
        SemanticRevisionId semanticRevisionId,
        ImplementationRevisionId? implementationRevisionId,
        VerificationRunId? verificationRunId,
        DateTimeOffset openedAt,
        ClarificationRequest? clarification = null,
        WhatProposal? what = null,
        HowProposal? how = null)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown gate type.");

        switch (type)
        {
            case GateType.Accept:
                if (implementationRevisionId is null || verificationRunId is null)
                    throw new InvalidGateException("An ACCEPT gate binds an implementation revision and a verification run.");

                if (clarification is not null || what is not null || how is not null)
                    throw new InvalidGateException("An ACCEPT gate carries no clarification question, no WHAT proposal and no HOW proposal.");

                break;

            case GateType.Clarify:
                if (clarification is null)
                    throw new InvalidGateException("A CLARIFY gate carries its question.");

                if (what is not null || how is not null)
                    throw new InvalidGateException("A CLARIFY gate carries no WHAT or HOW proposal.");

                RequireNoImplementation("A CLARIFY gate", "nothing was verified when the agent asked", implementationRevisionId, verificationRunId);
                break;

            case GateType.What:
                if (what is null)
                    throw new InvalidGateException("A WHAT gate carries its proposal.");

                if (clarification is not null || how is not null)
                    throw new InvalidGateException("A WHAT gate carries no clarification question and no HOW proposal.");

                RequireNoImplementation("A WHAT gate", "nothing is implemented before it is approved", implementationRevisionId, verificationRunId);
                break;

            default:
                if (how is null)
                    throw new InvalidGateException("A HOW gate carries its proposal.");

                if (clarification is not null || what is not null)
                    throw new InvalidGateException("A HOW gate carries no clarification question and no WHAT proposal.");

                RequireNoImplementation("A HOW gate", "nothing is implemented before it is approved", implementationRevisionId, verificationRunId);
                break;
        }

        Id = id;
        TaskId = taskId;
        Type = type;
        SemanticRevisionId = semanticRevisionId;
        ImplementationRevisionId = implementationRevisionId;
        VerificationRunId = verificationRunId;
        OpenedAt = openedAt;
        Clarification = clarification;
        What = what;
        How = how;
    }

    public GateId Id { get; }

    public TaskId TaskId { get; }

    public GateType Type { get; }

    public SemanticRevisionId SemanticRevisionId { get; }

    /// <summary>The implementation an ACCEPT gate asks the human to review; null for a gate that precedes implementation.</summary>
    public ImplementationRevisionId? ImplementationRevisionId { get; }

    /// <summary>The passing verification of that implementation; null for a gate that precedes implementation.</summary>
    public VerificationRunId? VerificationRunId { get; }

    public DateTimeOffset OpenedAt { get; }

    /// <summary>The question a CLARIFY gate asks; null for an ACCEPT gate.</summary>
    public ClarificationRequest? Clarification { get; }

    /// <summary>The proposal a WHAT gate asks the human to approve; null for any other gate. Immutable once the gate is open.</summary>
    public WhatProposal? What { get; }

    /// <summary>The proposal a HOW gate asks the human to approve; null for any other gate. Immutable once the gate is open.</summary>
    public HowProposal? How { get; }

    private static void RequireNoImplementation(string gate, string because, ImplementationRevisionId? implementationRevisionId, VerificationRunId? verificationRunId)
    {
        if (implementationRevisionId is not null || verificationRunId is not null)
            throw new InvalidGateException($"{gate} binds no implementation revision or verification run; {because}.");
    }
}

/// <summary>
/// The one decision that closes a gate. Feedback exists only for <see cref="GateAction.RequestChanges"/>, an answer only for <see cref="GateAction.Answer"/>
/// (an approval, like an acceptance, carries neither);
/// each is required there and bounded, so a person's prose can never grow workflow history without limit.
/// </summary>
public sealed record GateDecision
{
    public const int MaxFeedbackLength = 2000;
    public const int MaxAnswerLength = 2000;

    public GateDecision(GateId gateId, GateAction action, string? feedback, DateTimeOffset decidedAt, string? answer = null)
    {
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown gate action.");

        if (action == GateAction.RequestChanges)
        {
            Guard.NotBlank(feedback, nameof(feedback));

            if (feedback!.Length > MaxFeedbackLength)
                throw new ArgumentException($"Feedback must not exceed {MaxFeedbackLength} characters.", nameof(feedback));
        }
        else if (feedback is not null)
        {
            throw new InvalidGateException($"A '{action}' decision carries no feedback; only a request for changes does.");
        }

        if (action == GateAction.Answer)
        {
            Guard.NotBlank(answer, nameof(answer));

            if (answer!.Length > MaxAnswerLength)
                throw new ArgumentException($"An answer must not exceed {MaxAnswerLength} characters.", nameof(answer));
        }
        else if (answer is not null)
        {
            throw new InvalidGateException($"A '{action}' decision carries no answer; only an answer to a clarification does.");
        }

        GateId = gateId;
        Action = action;
        Feedback = feedback;
        Answer = answer;
        DecidedAt = decidedAt;
    }

    public GateId GateId { get; }

    public GateAction Action { get; }

    public string? Feedback { get; }

    /// <summary>The human's answer to a CLARIFY question, recorded exactly as given. Semantic input, not an approval.</summary>
    public string? Answer { get; }

    public DateTimeOffset DecidedAt { get; }

    public GateStatus ResultingStatus => Action switch
    {
        GateAction.Accept => GateStatus.Accepted,
        GateAction.RequestChanges => GateStatus.ChangesRequested,
        GateAction.Answer => GateStatus.Answered,
        GateAction.Approve => GateStatus.Approved,
        _ => GateStatus.Cancelled,
    };
}
