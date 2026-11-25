using System.Text;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// The two messages the direct path ever sends its root agent. Deliberately short: the agent needs the objective,
/// its limits and what will be checked, not the workflow specification, and it is never asked to produce
/// workflow state. Prompts are single-line-safe text (no leading slash, no markup the host would interpret).
/// </summary>
internal static class DirectPathPrompts
{
    private const int MaxOutputCharsPerStream = 1500;

    /// <param name="mayAskForClarification">True when the runtime gave this session the clarification capability, so the agent is told when it may use it.</param>
    /// <param name="mayProposeWhat">True when the runtime gave this session the WHAT capability, so the agent is told when it may (or, for a task that requires it, must) use it.</param>
    /// <param name="mayProposeHow">The same for the HOW (implementation strategy) capability.</param>
    public static string Initial(WorkflowTask task, DirectTaskDefinition definition, bool mayAskForClarification = false, bool mayProposeWhat = false, bool mayProposeHow = false)
    {
        var text = new StringBuilder();

        text.AppendLine($"Task: {definition.Objective}");
        text.AppendLine();

        return AppendTaskBody(text, task, definition, mayAskForClarification, mayProposeWhat, mayProposeHow);
    }

    /// <summary>
    /// How the agent is told about the WHAT stage. When the human required it, it is a hard rule: read what you need, propose, stop, change nothing before approval.
    /// When it is the agent's choice it is narrow on purpose: only for a task whose externally visible outcome is genuinely open (several plausible results a person
    /// would choose between), never for a task whose outcome is clear, so a simple task pays no planning cost. The tool name is part of the provider-neutral contract
    /// (<see cref="Execution.AgentWhatSignal.ToolName"/>); the proposal states intent, never implementation strategy.
    /// </summary>
    internal static string? WhatGuidance(WorkflowTask task, bool mayAskForClarification = true)
    {
        if (task.WhatApproved)
            return null;

        var rule = NotAQuestion("WHAT", mayAskForClarification);

        const string Content = "an objective, the required behavior, the scope, any constraints and non-goals, the acceptance conditions, and why a human decision is warranted. State intent and externally visible outcomes only: no implementation steps, files or design.";

        if (task.LatestWhatGate is { } latestWhat && task.StatusOf(latestWhat) == GateStatus.Invalidated)
        {
            return $"New evidence you reported invalidated the approved WHAT. Revise it and call the {Execution.AgentWhatSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file until a person approves a WHAT. {rule}";
        }

        if (task.LatestWhatGate is not null)
        {
            return $"Your WHAT proposal is not approved yet. Revise it and call the {Execution.AgentWhatSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file until a person approves a WHAT. {rule}";
        }

        if (task.WhatRequired || task.WhatEscalated)
        {
            return $"This task requires a human-approved WHAT before anything is implemented. First read the repository as much as you need (reading and running diagnostics is fine), then call the {Execution.AgentWhatSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file before a person approves the WHAT. {rule}";
        }

        return WhatSignal(rule);
    }

    /// <summary>
    /// True when the WHAT is the agent's own choice: nothing requires it and none is proposed or settled. It is then offered as one line of the compact workflow-signals
    /// section instead of a directive paragraph.
    /// </summary>
    internal static bool WhatIsAgentChoice(WorkflowTask task)
        => !task.WhatApproved && task.LatestWhatGate is null && !task.WhatRequired && !task.WhatEscalated;

    private static string WhatSignal(string rule)
        => $"{Execution.AgentWhatSignal.ToolName}: only if the task's externally visible outcome is genuinely open (several plausible results a person would want to choose between): before changing anything, state the intent and outcomes (no implementation), then stop and wait for approval. "
           + $"{rule} If the outcome is clear, do not use it.";

    /// <summary>
    /// How the agent is told about the HOW stage. When the human required it, it is a hard rule exactly as for the WHAT. When it is the agent's choice it is
    /// narrow on purpose: only for an implementation strategy with materially different alternatives and consequential technical tradeoffs (architecture or API
    /// boundaries, persistence or migration, concurrency, security, compatibility, performance, a substantial cross-component change), never merely because the task is
    /// hard, touches many files, needs reading, or has one obvious maintainable approach, so a simple task pays no planning cost. It is never offered before a WHAT
    /// that is still to be approved: the strategy is proposed over an intent that is settled. The tool name is part of the provider-neutral contract
    /// (<see cref="Execution.AgentHowSignal.ToolName"/>); the proposal states consequential strategy, never code, a file-by-file plan or a task checklist.
    /// </summary>
    internal static string? HowGuidance(WorkflowTask task, bool mayAskForClarification = true)
    {
        if (task.HowApproved)
            return null;

        var rule = NotAQuestion("HOW", mayAskForClarification);

        const string Content = "the strategy, the components and their responsibilities, the data or control flow and the compatibility or migration approach where they matter, the risks and tradeoffs including the main alternative you did not choose, how you will verify it where that is part of the design, and why a human decision is warranted. State consequential strategy only: no source code, no file-by-file plan, no task checklist.";

        if (!task.WhatSatisfied)
        {
            return task.HowRequired || task.HowEscalated
                ? $"This task also requires a human-approved HOW (implementation strategy). Do not propose it yet: it is proposed with the {Execution.AgentHowSignal.ToolName} tool only after the WHAT is approved."
                : null;
        }

        if (task.LatestHowGate is { } latestHow && task.StatusOf(latestHow) == GateStatus.Invalidated)
        {
            return $"New evidence you reported invalidated the approved HOW. Revise it and call the {Execution.AgentHowSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file until a person approves a HOW. {rule}";
        }

        if (task.LatestHowGate is not null)
        {
            return $"Your HOW proposal is not approved yet. Revise it and call the {Execution.AgentHowSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file until a person approves a HOW. {rule}";
        }

        if (task.HowRequired || task.HowEscalated)
        {
            return $"This task requires a human-approved HOW (implementation strategy) before anything is implemented. First read the repository as much as you need (reading and running diagnostics is fine), then call the {Execution.AgentHowSignal.ToolName} tool once with {Content} Then stop and wait. "
                   + $"Do not create, edit or delete any file before a person approves the HOW. {rule}";
        }

        return HowSignal(rule);
    }

    /// <summary>True when the HOW is the agent's own choice (see <see cref="WhatIsAgentChoice"/>): a WHAT is settled and nothing requires, proposes or settles a HOW.</summary>
    internal static bool HowIsAgentChoice(WorkflowTask task)
        => task.WhatSatisfied && !task.HowApproved && task.LatestHowGate is null && !task.HowRequired && !task.HowEscalated;

    private static string HowSignal(string rule)
        => $"{Execution.AgentHowSignal.ToolName}: only if the implementation strategy is consequential (materially different approaches with real technical tradeoffs, such as architecture or API boundaries, persistence, concurrency, security, compatibility, or a substantial cross-component change expensive to redo): before changing anything, state the strategy (no code or file-by-file plan), then stop and wait for approval. "
           + $"{rule} Do not use it merely because the task is difficult, touches many files, or needs you to read code, nor when one obvious maintainable approach exists.";

    /// <summary>
    /// The stage semantics, stated once for both proposals: a WHAT or HOW is a contract a person approves, never a place to ask. A decision only a person can make that the task
    /// leaves open is resolved first, with the clarification tool when this runtime offers it; asking it inside a proposal, or leaving it for the person's feedback on the proposal,
    /// is not allowed. No wording of a proposal is ever parsed for this: it is guidance to the agent, and the human keeps every decision.
    /// </summary>
    private static string NotAQuestion(string stage, bool mayAskForClarification)
        => $"A {stage} proposal is a contract a person approves, not a question: it must not leave open a decision that only a person can make."
           + (mayAskForClarification
               ? $" If one is still open, call the {Execution.AgentClarificationSignal.ToolName} tool first, and propose the {stage} once it is answered."
               : string.Empty);

    /// <summary>The turn sent when a task whose WHAT is approved still needs a HOW and the agent finished a turn without proposing one and without changing anything.</summary>
    public static string HowReminder()
        => $"You finished without proposing the HOW, and this task needs a person to approve the implementation strategy before anything is implemented. Call the {Execution.AgentHowSignal.ToolName} tool now, once, with the strategy, components and responsibilities, data or control flow, compatibility, risks and tradeoffs, verification approach and why a decision is warranted. "
           + "Do not create, edit or delete any file, then stop.";

    /// <summary>The turn sent when a task that needs a WHAT finished a turn without proposing one and without changing anything.</summary>
    public static string WhatReminder()
        => $"You finished without proposing the WHAT, and this task needs a person to approve it before anything is implemented. Call the {Execution.AgentWhatSignal.ToolName} tool now, once, with the objective, required behavior, scope, constraints, non-goals, acceptance conditions and why a decision is warranted. "
           + "Do not create, edit or delete any file, then stop.";

    /// <summary>
    /// How the agent is told it may ask for a human-owned answer. Narrow on purpose: it is for a missing or ambiguous requirement that the repository cannot settle and
    /// that materially changes the work, never for permissions, never for details the agent can reasonably decide, and it is used once and followed by stopping.
    /// The tool name is part of the provider-neutral contract (<see cref="Execution.AgentClarificationSignal.ToolName"/>); how it is delivered is the runtime's business.
    /// </summary>
    private static readonly string ClarificationSignal =
        $"{Execution.AgentClarificationSignal.ToolName}: a requirement this task depends on is missing or ambiguous, the repository cannot settle it, and the answer would materially change the work: ask once with the question and why it matters, then stop and wait for the answer. "
        + "A decision only a person can make (a product or requirement choice) that the task leaves open is asked with this tool BEFORE you propose any WHAT or HOW and before you implement; "
        + "a person's feedback on a proposal that already exists is not a substitute for asking it. "
        + "Not for permissions, for details you can reasonably decide yourself, or for what the repository already answers.";

    /// <summary>
    /// How the agent is told it may report new evidence. Narrow on purpose: only for something it learned while working (by reading, running or observing) that makes
    /// an assumption a settled or never-opened stage rests on no longer hold, never for an ordinary failure it can repair inside the strategy it already has, and used once
    /// and followed by stopping. It can only ask for more consideration, never less. The tool name is part of the provider-neutral contract
    /// (<see cref="Execution.AgentReclassificationSignal.ToolName"/>). It names no particular kind of evidence and no hidden fact.
    /// </summary>
    private static readonly string ReclassificationSignal =
        $"{Execution.AgentReclassificationSignal.ToolName}: new evidence that an assumption your approved WHAT or HOW (or your own plan) rests on is no longer true, so that following it would be wrong or expensive to redo: report it once with the earliest stage to reconsider (\"how\" if only the strategy is affected, \"what\" if the intent is), then stop and wait. Not for a failure you can repair within your approach.";

    /// <summary>
    /// The opening of the compact workflow-signals section. The signals are optional capabilities: work that is clear and fully specified uses none and simply proceeds to
    /// implementation and verification, which is stated first so that offering the signals never reads as a step the agent must take. The details of each signal's fields
    /// are in the tool's own description and are not repeated here.
    /// </summary>
    private const string SignalsIntro = "Workflow signals are optional. If the task is clear and fully specified, use none: just do the work, run the checks and finish. Use one only in these cases (once; the details of each are in its tool description):";

    /// <summary>
    /// Said once after the signals. The controller, not the agent's text, owns what happens after a signal that stops the agent: the wait, the person's decision and the
    /// resume. A decision reaches the agent only when the controller resumes the conversation, so nothing the agent writes approves or answers anything.
    /// </summary>
    private const string SignalsController = "After a signal that stops you the controller owns the wait and the resume: a person's decision reaches you only when the conversation is resumed, and nothing you write approves or answers anything.";

    /// <summary>
    /// The turn sent into the SAME conversation right after the controller recorded the agent's reclassification: only the delta. It says what was invalidated, that
    /// the workspace is preserved as provisional work and must not change, and exactly what to do next. No transcript is replayed.
    /// </summary>
    public static string ReclassificationRecorded(Reclassification reclassification, bool recovering = false)
    {
        var text = new StringBuilder();
        var what = reclassification.Stage == ReclassificationStage.What;

        text.AppendLine(recovering
            ? $"Your report of new evidence was recorded: the {reclassification.StageName} is invalidated. A previous attempt to continue was interrupted by a runtime failure."
            : $"Your report of new evidence was recorded: the {reclassification.StageName} is invalidated.");

        text.AppendLine(what
            ? "The approved WHAT, and the HOW that was derived from it, are no longer current, and neither are the implementation and verification that rested on them."
            : reclassification.InvalidatedGates.IsEmpty
                ? "A person must now approve a HOW before anything is implemented."
                : "The approved HOW is no longer current, and neither are the implementation and verification that rested on it. The WHAT still stands.");

        text.AppendLine("Whatever you already changed in the workspace is kept as provisional work: it is not approved and it must not change now.");
        text.AppendLine();
        text.Append(what
            ? $"Do not create, edit or delete any file. Call the {Execution.AgentWhatSignal.ToolName} tool once with a revised WHAT that takes the new evidence into account, then stop and wait for a person to approve it (the HOW is requested afterwards)."
            : $"Do not create, edit or delete any file. Call the {Execution.AgentHowSignal.ToolName} tool once with a revised HOW that takes the new evidence into account, then stop and wait for a person to approve it.");

        return text.ToString();
    }

    /// <summary>
    /// The paragraph appended to a resumed approval when the approved artifact replaces one that new evidence invalidated: the workspace still holds work done under the
    /// superseded assumptions, to be adapted, not discarded and not trusted.
    /// </summary>
    private static string AdaptProvisionalWork => "The workspace still holds work done under the assumptions that the new evidence invalidated. Adapt it to what was just approved: keep what still fits, change what does not, and do not start over unless nothing fits.";

    /// <summary>
    /// The first message of a FRESH session that continues a task after human input (requested changes or an answer): the original task, the whole ordered history
    /// of what humans have added since (<see cref="HumanInputHistory"/>, verbatim, labelled), the statement that the latest takes precedence, the same limits and
    /// checks, and where the work stands. Nothing is summarized and no earlier transcript is replayed.
    /// </summary>
    public static string FreshContinuation(WorkflowTask task, DirectTaskDefinition definition, string humanInputHistory, bool recovering, bool mayAskForClarification, bool mayProposeWhat = false, bool mayProposeHow = false)
    {
        var text = new StringBuilder();

        text.AppendLine($"Task: {definition.Objective}");
        text.AppendLine();
        text.AppendLine("This task is already under way. You are a new session and do not remember the earlier work, but the working tree holds it: continue from it.");
        text.AppendLine("People reviewing it, or answering your questions, have added the following since it began, oldest first. Each takes precedence over anything you would otherwise assume about the task, and a later item takes precedence over an earlier one:");
        text.AppendLine(humanInputHistory);

        if (recovering)
        {
            text.AppendLine();
            text.AppendLine(InterruptedAttempt);
        }

        text.AppendLine();

        return AppendTaskBody(text, task, definition, mayAskForClarification, mayProposeWhat, mayProposeHow);
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human approved the WHAT. The agent already knows the task and its own proposal; this carries the
    /// new fact and the approved artifact itself, so the approval cannot be applied to anything but what was approved.
    /// </summary>
    /// <param name="howStillRequired">
    /// True when the HOW stage is still unsatisfied: the agent is then told, in so many words, that the WHAT is approved but implementation is not yet permitted,
    /// so it cannot implement between the required stages.
    /// </param>
    public static string ResumedWhatApproved(WhatProposal approved, bool recovering = false, bool howStillRequired = false, bool adaptProvisionalWork = false)
    {
        var text = new StringBuilder();

        text.AppendLine(recovering
            ? "The WHAT proposal was approved. A previous attempt to continue was interrupted by a runtime failure."
            : "The WHAT proposal was approved.");
        text.AppendLine("Approved WHAT:");
        text.AppendLine("<<<");
        text.AppendLine(WhatText.Render(approved));
        text.AppendLine(">>>");
        text.AppendLine();

        if (howStillRequired)
        {
            text.Append($"The WHAT is approved. HOW approval is still required. Produce the HOW proposal by calling the {Execution.AgentHowSignal.ToolName} tool once, then stop; do not implement yet.");

            return text.ToString();
        }

        text.Append(recovering
            ? "Inspect the current workspace first and preserve any correct work already completed before the interruption. Continue from the current workspace."
            : "Continue from the current workspace.");

        if (adaptProvisionalWork)
            text.Append(' ').Append(AdaptProvisionalWork);

        return text.ToString();
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human approved the HOW: the approved strategy itself, so the approval cannot be applied to
    /// anything but what was approved, and the instruction to implement with it. Only the delta; the task is not sent again and no transcript is replayed.
    /// </summary>
    public static string ResumedHowApproved(HowProposal approved, bool recovering = false, bool adaptProvisionalWork = false)
    {
        var text = new StringBuilder();

        text.AppendLine(recovering
            ? "The HOW proposal was approved. A previous attempt to continue was interrupted by a runtime failure."
            : "The HOW proposal was approved.");
        text.AppendLine();
        text.AppendLine("Approved HOW:");
        text.AppendLine("<<<");
        text.AppendLine(HowText.Render(approved));
        text.AppendLine(">>>");
        text.AppendLine();
        text.Append(recovering
            ? "Inspect the current workspace first and preserve any correct work already completed before the interruption. Continue from the current workspace using this implementation strategy."
            : "Continue from the current workspace using this implementation strategy.");

        if (adaptProvisionalWork)
            text.Append(' ').Append(AdaptProvisionalWork);

        return text.ToString();
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human sent the HOW back: only the feedback, and the instruction to revise the strategy and not
    /// implement yet.
    /// </summary>
    public static string ResumedHowChanges(string feedback, bool recovering = false)
    {
        var text = new StringBuilder();

        text.AppendLine(recovering
            ? "The human requested changes to the HOW proposal. A previous attempt to continue was interrupted by a runtime failure."
            : "The human requested changes to the HOW proposal:");
        text.AppendLine();
        text.AppendLine("<<<");
        text.AppendLine(feedback.Trim());
        text.AppendLine(">>>");
        text.AppendLine();
        text.Append($"Revise the HOW strategy by calling the {Execution.AgentHowSignal.ToolName} tool again, then stop. Do not implement yet.");

        return text.ToString();
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human sent the WHAT back: only the feedback, and the instruction not to implement yet.
    /// </summary>
    public static string ResumedWhatChanges(string feedback, bool recovering = false)
    {
        var text = new StringBuilder();

        text.AppendLine(recovering
            ? "The human requested changes to the WHAT proposal. A previous attempt to continue was interrupted by a runtime failure."
            : "The human requested changes to the WHAT proposal:");
        text.AppendLine("<<<");
        text.AppendLine(feedback.Trim());
        text.AppendLine(">>>");
        text.AppendLine();
        text.Append($"Revise the WHAT contract by calling the {Execution.AgentWhatSignal.ToolName} tool again, then stop. Do not implement yet.");

        return text.ToString();
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human answered the agent's question. The agent already knows the task, its limits and its own work (and
    /// asked the question), so this carries only the new fact: the answer, with the question it answers so it cannot be applied to the wrong one.
    /// </summary>
    public static string ResumedClarification(ClarificationRequest question, string answer, bool recovering = false)
    {
        var text = new StringBuilder();

        text.AppendLine(recovering
            ? "Continue the task using the person's answer to your question. A previous attempt to continue was interrupted by a runtime failure."
            : "The person answered your question.");
        text.AppendLine($"Question: {question.Question}");
        text.AppendLine("Answer, verbatim:");
        text.AppendLine("<<<");
        text.AppendLine(answer.Trim());
        text.AppendLine(">>>");
        text.AppendLine();
        text.Append(recovering
            ? "Inspect the current workspace first and preserve any correct work already completed before the interruption. Continue the task with this answer."
            : "Continue the task from the current workspace, using this answer.");

        return text.ToString();
    }

    /// <summary>
    /// The turn sent into the RESUMED conversation after the human requested changes. The agent already knows the task, its limits and its own earlier work,
    /// so this carries only what is new: the feedback and where the work now stands. The original task is not sent again and no transcript is replayed.
    /// </summary>
    public static string ResumedRequestedChanges(string feedback, bool recovering = false)
    {
        var text = new StringBuilder();

        if (recovering)
        {
            // A retry after a runtime failure. This conversation may already hold the original instruction (the runtime cannot say), so the requirement is
            // restated in full, as the single authoritative statement of this semantic revision, together with how to treat what is already in the workspace.
            text.AppendLine("Continue addressing the current human-requested changes. A previous attempt was interrupted by a runtime failure.");
            text.AppendLine("The authoritative feedback for this semantic revision is:");
            text.AppendLine("<<<");
            text.AppendLine(feedback.Trim());
            text.AppendLine(">>>");
            text.AppendLine();
            text.Append("Inspect the current workspace first and preserve any correct work already completed before the runtime interruption.");

            return text.ToString();
        }

        text.AppendLine("The human requested changes to the previously verified result:");
        text.AppendLine("<<<");
        text.AppendLine(feedback.Trim());
        text.AppendLine(">>>");
        text.AppendLine();
        text.Append("Continue working from the current workspace.");

        return text.ToString();
    }

    private const string InterruptedAttempt =
        "A previous attempt at these changes was interrupted by a runtime failure and may already have changed the working tree. "
        + "Inspect the current workspace first and preserve any correct work already completed before the interruption.";

    private static string AppendTaskBody(StringBuilder text, WorkflowTask task, DirectTaskDefinition definition, bool mayAskForClarification, bool mayProposeWhat, bool mayProposeHow)
    {
        text.AppendLine($"Workspace: {definition.WorkspacePath} (a Git repository). Work only inside it.");

        if (definition.Scope.AllowedPaths.Count > 0)
            text.AppendLine($"You may change only: {string.Join(", ", definition.Scope.AllowedPaths)}.");

        if (definition.Scope.ProtectedPaths.Count > 0)
            text.AppendLine($"You must not change: {string.Join(", ", definition.Scope.ProtectedPaths)}.");

        text.AppendLine("Do not commit, push, create or switch branches, stash, reset, publish, release or deploy. Do not use the network.");

        if (task.Authority.ReservedForHuman.Count > 0)
            text.AppendLine($"Reserved for a human, never do these: {string.Join(", ", task.Authority.ReservedForHuman)}.");

        text.AppendLine();
        text.AppendLine("After you finish, the controller independently runs these checks and decides whether the task is complete:");
        foreach (var check in definition.Checks)
            text.AppendLine($"- {check.Id}: {check.Description}");

        if (!string.IsNullOrWhiteSpace(definition.OperatingNotes))
        {
            text.AppendLine();
            text.AppendLine(definition.OperatingNotes.Trim());
            text.AppendLine();
        }

        text.AppendLine("Run what you can yourself first. Your own report does not count; the controller's result does.");
        text.AppendLine();
        text.Append("Work autonomously until the task is done or you are genuinely blocked. ");
        text.Append("If finishing would require anything outside this scope, stop and say so instead of doing it.");

        // The optional workflow signals are one compact section (a line per signal the runtime offered and the task state allows); a directive that is specific to the task's
        // state (a stage the human required, a proposal being revised, a contract the task forces) keeps its own paragraph after it.
        var signals = new List<string>();
        var directives = new List<string>();

        if (mayAskForClarification)
        {
            signals.Add(ClarificationSignal);
            signals.Add(ReclassificationSignal);

            // asking for an independent review is only worth offering when the task does not already require one (a requested one cannot be withdrawn, and a required one is not the agent's to ask for).
            if (!task.IsReviewRequired)
                signals.Add(ReviewPrompts.RequestSignal);

            // a task that forces PROVE tells the agent to write the Proof Contract; otherwise formal proof is offered only while it is not yet required (a requested one cannot be withdrawn).
            if (task.IsProveRequired && task.IsProofContractMissing)
                directives.Add(ProofPrompts.RequiredGuidance(task, definition));
            else if (!task.IsProveRequired)
                signals.Add(ProofPrompts.RequestSignal(task, definition));
        }

        if (mayProposeWhat && WhatGuidance(task, mayAskForClarification) is { } whatGuidance)
            (WhatIsAgentChoice(task) ? signals : directives).Add(whatGuidance);

        if (mayProposeHow && HowGuidance(task, mayAskForClarification) is { } howGuidance)
            (HowIsAgentChoice(task) ? signals : directives).Add(howGuidance);

        if (signals.Count > 0)
        {
            text.AppendLine();
            text.AppendLine();
            text.AppendLine(SignalsIntro);

            foreach (var signal in signals)
                text.AppendLine($"- {signal}");

            text.AppendLine();
            text.Append(SignalsController);
        }

        foreach (var directive in directives)
        {
            text.AppendLine();
            text.AppendLine();
            text.Append(directive);
        }

        return text.ToString();
    }

    public static string Repair(
        IReadOnlyList<ScopeViolation> scopeViolations,
        IReadOnlyList<CheckDiagnostic> diagnostics)
    {
        var text = new StringBuilder();

        text.AppendLine("Controller verification failed. Repair the implementation within the existing task scope. Do not broaden scope.");

        if (scopeViolations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Out-of-scope changes (revert them, or stop if they are truly required):");
            foreach (var violation in scopeViolations)
                text.AppendLine($"- {violation.Path}: {violation.Reason}");
        }

        foreach (var diagnostic in diagnostics.Where(d => d.Outcome == VerificationOutcome.Failed))
        {
            text.AppendLine();
            text.AppendLine($"check: {diagnostic.CheckId}");
            text.AppendLine($"result: {diagnostic.Summary}");

            if (!string.IsNullOrWhiteSpace(diagnostic.OutputTail))
            {
                text.AppendLine("output:");
                text.AppendLine(diagnostic.OutputTail.TrimEnd());
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The last <see cref="MaxOutputCharsPerStream"/> characters of stdout and, if present, of stderr.</summary>
    public static string OutputTail(string stdOutTail, string stdErrTail)
    {
        var text = new StringBuilder();

        AppendTail(text, stdOutTail);
        if (!string.IsNullOrWhiteSpace(stdErrTail))
        {
            if (text.Length > 0)
                text.AppendLine();

            text.AppendLine("stderr:");
            AppendTail(text, stdErrTail);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendTail(StringBuilder text, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return;

        text.Append(trimmed.Length <= MaxOutputCharsPerStream ? trimmed : "..." + trimmed[^MaxOutputCharsPerStream..]);
    }
}
