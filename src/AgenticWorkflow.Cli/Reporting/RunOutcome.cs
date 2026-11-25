using AgenticWorkflow.Application.Direct;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// Why a run ended, in the categories a person acts on differently. Derived from the direct path's structured <see cref="DirectRunStatus"/>
/// and, for the one status that shares a cause, its stable reason text. Presentation vocabulary: it is never persisted as workflow state.
/// </summary>
internal enum RunOutcome
{
    /// <summary>The workflow is COMPLETED: either the controller completed a run that requires no acceptance, or the human accepted the verified result with <c>aw accept</c>.</summary>
    Completed,

    /// <summary>The controller verified the final working tree and opened the ACCEPT gate: AWAITING_HUMAN, durably, until the person decides with <c>aw accept</c> or <c>aw request-changes</c>.</summary>
    AwaitingAcceptance,

    /// <summary>The agent deliberately asked a question and the controller opened the workflow-owned CLARIFY gate: AWAITING_HUMAN, durably, until the person answers with <c>aw answer</c>.</summary>
    AwaitingClarification,

    /// <summary>The agent proposed the WHAT and the controller opened the workflow-owned WHAT gate: AWAITING_HUMAN, durably, until the person decides with <c>aw approve</c> or <c>aw request-changes</c>. Nothing was implemented or verified.</summary>
    AwaitingWhat,

    /// <summary>The WHAT stage was broken (the agent changed the workspace before approval, or never proposed a required WHAT). The workflow is BLOCKED; nothing was recorded as implemented.</summary>
    WhatStageViolation,

    /// <summary>The agent proposed the HOW and the controller opened the workflow-owned HOW gate: AWAITING_HUMAN, durably, until the person decides with <c>aw approve</c> or <c>aw request-changes</c>. Nothing was implemented or verified.</summary>
    AwaitingHow,

    /// <summary>The HOW stage was broken (the agent changed the workspace before approval, or never proposed a required HOW). The workflow is BLOCKED; nothing was recorded as implemented.</summary>
    HowStageViolation,

    /// <summary>The agent never became ready (a trust, login or update dialog, or a crash before the first turn). Nothing was answered.</summary>
    StartupBlock,

    /// <summary>The agent finished but the controller's checks still fail after the permitted turns.</summary>
    VerificationUnresolved,

    /// <summary>The workflow moved under the run; nothing was applied.</summary>
    StaleWorkflowState,

    /// <summary>The runtime, the agent process or a workflow write failed (including a write whose outcome is unknown).</summary>
    RuntimeError,

    /// <summary>The person interrupted the run (Ctrl+C).</summary>
    Cancelled,

    /// <summary>The agent needed a person in its own interface (a permission, a question, a login) and nobody was attending the run. The session ended and the workflow is BLOCKED.</summary>
    NativeInteractionUnavailable,

    /// <summary>A person cancelled a BLOCKED (or stranded AWAITING_HUMAN) run with <c>aw abort</c>. The workflow is cancelled.</summary>
    Aborted,

    /// <summary>An independent REVIEW is required but the reviewer role has no explicit profile binding, so none could start. The requirement stays recorded.</summary>
    ReviewerUnbound,

    /// <summary>The independent reviewer changed the workspace it was only to read. The run is BLOCKED and the workspace is preserved untouched.</summary>
    ReviewerModifiedWorkspace,

    /// <summary>The independent review still required changes after the last permitted agent turn.</summary>
    ReviewUnresolved,

    /// <summary>Formal PROVE is required but the prover role has no explicit profile binding, so none could start. The requirement stays recorded.</summary>
    ProverUnbound,

    /// <summary>The independent prover changed the workspace it was only to read. The run is BLOCKED and the workspace is preserved untouched.</summary>
    ProverModifiedWorkspace,

    /// <summary>The proof still failed or was inconclusive after the last permitted agent turn.</summary>
    ProofUnresolved,

    /// <summary>PROVE is required but the implementer never submitted a Proof Contract for the current semantics, even after the one reminder. The verified work is preserved.</summary>
    ProofContractMissing,
}

/// <summary>
/// The CLI's exit-code contract. <c>0</c> means exactly one thing: the run completed (or the query succeeded). Every incomplete or failed
/// outcome is nonzero, and each class a person handles differently has its own stable code. Raw exception or process codes never leak out.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>An unexpected failure inside aw itself.</summary>
    public const int InternalError = 1;

    /// <summary>Bad command line, invalid or missing project configuration, or a missing prerequisite. Nothing was launched.</summary>
    public const int UsageOrConfiguration = 2;

    /// <summary><c>aw status</c>: there is no such run.</summary>
    public const int RunNotFound = 3;

    /// <summary><c>aw status</c>: the run's durable state cannot be read or replayed.</summary>
    public const int RunStateUnreadable = 4;

    // 10 was "agent blocked". A blocked agent is a native human interaction now, reported as NativeInteractionUnavailable (15); the number is not reused.
    public const int StartupBlock = 11;
    public const int VerificationUnresolved = 12;
    public const int StaleWorkflowState = 13;
    public const int RuntimeError = 14;

    /// <summary>The agent needed a person in its own interface, nobody was attending the run and the session ended: the workflow is BLOCKED.</summary>
    public const int NativeInteractionUnavailable = 15;

    /// <summary>A person cancelled a BLOCKED (or stranded AWAITING_HUMAN) run with <c>aw abort</c>.</summary>
    public const int Aborted = 16;

    /// <summary>
    /// The work is verified and the workflow-owned ACCEPT gate is open: nothing is complete until the person decides. Deliberately not 0 (that means completed)
    /// and not 15 (a native agent interaction that needs the live session): this gate is durable and is answered later, from any process.
    /// </summary>
    public const int AwaitingAcceptance = 17;

    /// <summary><c>aw accept</c> / <c>aw request-changes</c> / <c>aw answer</c> / <c>aw continue</c>: the decision or continuation was refused (no open gate, unknown or already decided gate, stale gate, changed working tree; a run that is not a recoverable blocked continuation). Nothing was recorded.</summary>
    public const int GateDecisionRefused = 18;

    /// <summary>
    /// The agent asked a question and the workflow-owned CLARIFY gate is open: nothing is verified or complete until the person answers with <c>aw answer</c>.
    /// Deliberately its own code: not 17 (verified, awaiting ACCEPT: there is nothing to review yet), not 15 (a native interaction that needs the live session).
    /// </summary>
    public const int AwaitingClarification = 19;

    /// <summary>
    /// The agent proposed the WHAT and the workflow-owned WHAT gate is open: nothing is implemented, verified or complete until the person decides with
    /// <c>aw approve</c> or <c>aw request-changes</c>. Its own code: not 17 (there is no verified result to review), not 19 (a question, not a contract).
    /// </summary>
    public const int AwaitingWhat = 20;

    /// <summary>The WHAT stage was violated (work before approval, or a required WHAT never proposed): the run is BLOCKED and nothing was recorded as implemented.</summary>
    public const int WhatStageViolation = 21;

    /// <summary>
    /// The agent proposed the HOW (implementation strategy) and the workflow-owned HOW gate is open: nothing is implemented, verified or complete until the person
    /// decides with <c>aw approve</c> or <c>aw request-changes</c>. Its own code: not 20 (that is a WHAT, the requirements), not 17 (there is no verified result).
    /// </summary>
    public const int AwaitingHow = 22;

    /// <summary>The HOW stage was violated (work before approval, or a required HOW never proposed): the run is BLOCKED and nothing was recorded as implemented.</summary>
    public const int HowStageViolation = 23;

    /// <summary>An independent REVIEW is required and the reviewer role is not bound to a profile: nothing could review. Configure <c>reviewerProfile</c>, then <c>aw continue</c>.</summary>
    public const int ReviewerUnbound = 24;

    /// <summary>The independent reviewer changed the workspace: the run is BLOCKED and nothing was absorbed or reverted.</summary>
    public const int ReviewerModifiedWorkspace = 25;

    /// <summary>The independent review still required changes after the last permitted agent turn.</summary>
    public const int ReviewUnresolved = 26;

    /// <summary>Formal PROVE is required and the prover role is not bound to a profile: nothing could prove. The work is preserved; late binding is unsupported.</summary>
    public const int ProverUnbound = 27;

    /// <summary>The independent prover changed the workspace: the run is BLOCKED and nothing was absorbed or reverted.</summary>
    public const int ProverModifiedWorkspace = 28;

    /// <summary>The proof still failed or was inconclusive after the last permitted agent turn.</summary>
    public const int ProofUnresolved = 29;

    /// <summary>PROVE is required but no Proof Contract was submitted for the current semantics after the one reminder: the run is BLOCKED, the work preserved.</summary>
    public const int ProofContractMissing = 30;

    /// <summary>No healthy controller could be reached or started (<c>aw ctl</c>); nothing was changed.</summary>
    public const int ControllerUnavailable = 31;

    /// <summary>The controller and this client speak no protocol version in common (<c>aw ctl</c>); nothing was changed.</summary>
    public const int ControllerIncompatible = 32;

    /// <summary><c>aw doctor</c> found at least one failing check: something that prevents normal operation. Warnings alone exit 0.</summary>
    public const int DiagnosisFailed = 33;

    /// <summary>The conventional "terminated by Ctrl+C" code.</summary>
    public const int Cancelled = 130;

    public static int For(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Completed => Success,
        RunOutcome.AwaitingAcceptance => AwaitingAcceptance,
        RunOutcome.AwaitingClarification => AwaitingClarification,
        RunOutcome.AwaitingWhat => AwaitingWhat,
        RunOutcome.WhatStageViolation => WhatStageViolation,
        RunOutcome.AwaitingHow => AwaitingHow,
        RunOutcome.HowStageViolation => HowStageViolation,
        RunOutcome.StartupBlock => StartupBlock,
        RunOutcome.VerificationUnresolved => VerificationUnresolved,
        RunOutcome.StaleWorkflowState => StaleWorkflowState,
        RunOutcome.RuntimeError => RuntimeError,
        RunOutcome.Cancelled => Cancelled,
        RunOutcome.NativeInteractionUnavailable => NativeInteractionUnavailable,
        RunOutcome.Aborted => Aborted,
        RunOutcome.ReviewerUnbound => ReviewerUnbound,
        RunOutcome.ReviewerModifiedWorkspace => ReviewerModifiedWorkspace,
        RunOutcome.ReviewUnresolved => ReviewUnresolved,
        RunOutcome.ProverUnbound => ProverUnbound,
        RunOutcome.ProverModifiedWorkspace => ProverModifiedWorkspace,
        RunOutcome.ProofUnresolved => ProofUnresolved,
        RunOutcome.ProofContractMissing => ProofContractMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown run outcome."),
    };
}

internal static class RunOutcomeClassifier
{
    /// <param name="interruptRequested">True when the person interrupted the run. A run already completed, or whose gate is already open, keeps that outcome.</param>
    public static RunOutcome Classify(DirectRunResult result, bool interruptRequested)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Both are already durable: an interrupt that arrives after the controller recorded the completion, or opened the gate, does not undo it.
        if (result.Status == DirectRunStatus.Completed)
            return RunOutcome.Completed;

        if (result.Status == DirectRunStatus.AwaitingAcceptance)
            return RunOutcome.AwaitingAcceptance;

        if (result.Status == DirectRunStatus.AwaitingClarification)
            return RunOutcome.AwaitingClarification;

        if (result.Status == DirectRunStatus.AwaitingWhat)
            return RunOutcome.AwaitingWhat;

        if (result.Status == DirectRunStatus.AwaitingHow)
            return RunOutcome.AwaitingHow;

        if (interruptRequested || result.Status == DirectRunStatus.Interrupted)
            return RunOutcome.Cancelled;

        return result.Status switch
        {
            DirectRunStatus.WhatStageViolation => RunOutcome.WhatStageViolation,
            DirectRunStatus.HowStageViolation => RunOutcome.HowStageViolation,
            DirectRunStatus.VerificationUnresolved => RunOutcome.VerificationUnresolved,
            DirectRunStatus.StaleWorkflowState => RunOutcome.StaleWorkflowState,
            DirectRunStatus.StartupBlocked => RunOutcome.StartupBlock,
            DirectRunStatus.NativeInteractionUnavailable => RunOutcome.NativeInteractionUnavailable,
            DirectRunStatus.ReviewerUnbound => RunOutcome.ReviewerUnbound,
            DirectRunStatus.ReviewerModifiedWorkspace => RunOutcome.ReviewerModifiedWorkspace,
            DirectRunStatus.ReviewUnresolved => RunOutcome.ReviewUnresolved,
            DirectRunStatus.ProverUnbound => RunOutcome.ProverUnbound,
            DirectRunStatus.ProverModifiedWorkspace => RunOutcome.ProverModifiedWorkspace,
            DirectRunStatus.ProofUnresolved => RunOutcome.ProofUnresolved,
            DirectRunStatus.ProofContractMissing => RunOutcome.ProofContractMissing,
            _ => RunOutcome.RuntimeError,
        };
    }
}
