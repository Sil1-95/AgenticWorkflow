using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>What the host needs of the controller's workflow beyond the typed operations: bringing interrupted runs back at start-up, and stopping the work at the end.</summary>
internal interface ISupervisedWorkflow : IControllerWorkflow
{
    /// <summary>Puts every run an earlier controller left unfinished into a defined state, resuming what can be resumed safely. Runs once, at start-up.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);

    /// <summary>Stops the supervised work (waiting at most <paramref name="grace"/>); what it leaves is durable and resumed by the next controller.</summary>
    Task StopAsync(TimeSpan grace);
}

/// <summary>
/// The controller's typed workflow operations: the one place a client's decision becomes a recorded decision and, when work follows, supervised work. Every operation does the
/// same four things in the same order: it checks the decision's bindings against the run's durable gate (nothing is recorded if they do not match what the client was shown),
/// it runs the very preparation <c>aw approve</c> / <c>aw answer</c> / <c>aw request-changes</c> / <c>aw continue</c> / <c>aw run</c> run (which refuses what cannot be done, records the
/// decision once, and takes the run's live lock), it hands the stretch to the <see cref="RunSupervisor"/>, and it returns. It never waits for the agent.
/// <para>
/// There is no second implementation of any rule here: the refusals, the baselines, the conversation rules and the recording are the commands' own, so the CLI and the controller
/// cannot disagree about what a decision means. Natural-language text reaches this class only as an answer, feedback or an objective, each handed on as data.
/// </para>
/// </summary>
internal sealed class ControllerWorkflow(CliServices services, RunStore store, RunSupervisor supervisor, RunReconciler reconciler, Action<string> log) : ISupervisedWorkflow
{
    /// <summary>How long a decision waits for the stretch that just ended at its gate to let go of the run. A wind-down, not work: the agent is not waited for.</summary>
    private static readonly TimeSpan WindDownGrace = TimeSpan.FromSeconds(15);

    public int ActiveRuns => supervisor.ActiveRuns;

    public Task RecoverAsync(CancellationToken cancellationToken) => reconciler.ReconcileAllAsync(cancellationToken);

    public Task StopAsync(TimeSpan grace) => supervisor.StopAsync(grace);

    public async Task<WorkflowOutcome> ExecuteAsync(WorkflowInstruction instruction, CancellationToken cancellationToken)
    {
        try
        {
            return instruction switch
            {
                StartTaskInstruction start => await StartAsync(start, cancellationToken).ConfigureAwait(false),
                AnswerClarificationInstruction answer => await AnswerAsync(answer, cancellationToken).ConfigureAwait(false),
                ApproveProposalInstruction approve => await ApproveAsync(approve, cancellationToken).ConfigureAwait(false),
                RequestChangesInstruction changes => await RequestChangesAsync(changes, cancellationToken).ConfigureAwait(false),
                AcceptInstruction accept => await AcceptAsync(accept, cancellationToken).ConfigureAwait(false),
                AbortTaskInstruction abort => await AbortAsync(abort, cancellationToken).ConfigureAwait(false),
                ContinueTaskInstruction resume => await ContinueAsync(resume, cancellationToken).ConfigureAwait(false),
                BindRoleInstruction bind => await BindAsync(bind, cancellationToken).ConfigureAwait(false),
                _ => WorkflowOutcome.Refused(ControllerErrorCodes.UnsupportedOperation, "This controller has no such operation."),
            };
        }
        catch (Exception e) when (WorkflowRefusals.Map(e) is { } refusal)
        {
            // A refusal is an answer, and nothing was recorded. The message is the command's own, which names no secret: paths and ids the client already has.
            log($"{instruction.Operation} refused ({refusal.Code})");

            return WorkflowOutcome.Refused(refusal.Code, refusal.Message);
        }
    }

    private async Task<WorkflowOutcome> StartAsync(StartTaskInstruction start, CancellationToken cancellationToken)
    {
        var request = new RunRequest(
            start.Workspace,
            start.Config,
            start.Objective,
            TaskFile: null,
            Presence(start.Presence),
            start.Acceptance,
            start.RequireWhat,
            start.RequireHow,
            start.RequireReview,
            start.RequireProve,
            start.Bindings);

        // The caller's directory resolves a relative path and finds the project, exactly as it would in the caller's own shell.
        var stretch = await new RunCommand(services.ForSupervision(TextWriter.Null, start.WorkingDirectory))
            .PrepareAsync(request, TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);

        return Adopt(stretch);
    }

    private async Task<WorkflowOutcome> AnswerAsync(AnswerClarificationInstruction answer, CancellationToken cancellationToken)
    {
        var task = await SettledAsync(answer.TaskId, cancellationToken).ConfigureAwait(false);

        if (CheckGate(task, answer.GateId, GateType.Clarify, answer.SemanticRevisionId) is { } refusal)
            return refusal;

        var stretch = await new AnswerCommand(Quiet())
            .PrepareAsync(new AnswerRequest(answer.TaskId.ToString(), answer.GateId.ToString(), answer.Answer, Presence(answer.Presence)), TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);

        return Adopt(stretch);
    }

    private async Task<WorkflowOutcome> ApproveAsync(ApproveProposalInstruction approve, CancellationToken cancellationToken)
    {
        var task = await SettledAsync(approve.TaskId, cancellationToken).ConfigureAwait(false);
        var type = approve.Proposal == ProposalKind.What ? GateType.What : GateType.How;

        if (CheckGate(task, approve.GateId, type, approve.SemanticRevisionId) is { } refusal)
            return refusal;

        var gate = task.Gates.First(g => g.Id == approve.GateId);
        var contentHash = type == GateType.What ? gate.What!.ContentHash : gate.How!.ContentHash;

        if (!string.Equals(contentHash, approve.ContentHash, StringComparison.Ordinal))
            return WorkflowOutcome.Refused(ControllerErrorCodes.StaleBinding, "The proposal is not the one the decision was made about (its content differs); nothing was approved.");

        var stretch = await new ApproveCommand(Quiet())
            .PrepareAsync(new ApproveRequest(approve.TaskId.ToString(), approve.GateId.ToString(), Presence(approve.Presence)), TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);

        return Adopt(stretch);
    }

    private async Task<WorkflowOutcome> RequestChangesAsync(RequestChangesInstruction changes, CancellationToken cancellationToken)
    {
        var task = await SettledAsync(changes.TaskId, cancellationToken).ConfigureAwait(false);
        var type = changes.Gate switch
        {
            DecisionGateKind.What => GateType.What,
            DecisionGateKind.How => GateType.How,
            _ => GateType.Accept,
        };

        if (CheckGate(task, changes.GateId, type, changes.SemanticRevisionId) is { } refusal)
            return refusal;

        if (type == GateType.Accept && CheckAcceptBinding(task.Gates.First(g => g.Id == changes.GateId), changes.ImplementationRevisionId, changes.VerificationRunId) is { } stale)
            return stale;

        var stretch = await new RequestChangesCommand(Quiet())
            .PrepareAsync(new RequestChangesRequest(changes.TaskId.ToString(), changes.GateId.ToString(), changes.Feedback, Presence(changes.Presence)), TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);

        return Adopt(stretch);
    }

    private async Task<WorkflowOutcome> AcceptAsync(AcceptInstruction accept, CancellationToken cancellationToken)
    {
        var task = await SettledAsync(accept.TaskId, cancellationToken).ConfigureAwait(false);

        if (CheckGate(task, accept.GateId, GateType.Accept, accept.SemanticRevisionId) is { } refusal)
            return refusal;

        if (CheckAcceptBinding(task.Gates.First(g => g.Id == accept.GateId), accept.ImplementationRevisionId, accept.VerificationRunId) is { } stale)
            return stale;

        // The acceptance is complete in itself: it completes the task and starts no work. The service re-checks that the gate is still the open, current one and that the working
        // tree is still exactly the verified state, in the same atomic append that records it.
        await new AcceptCommand(Quiet()).RecordAsync(accept.TaskId, accept.GateId, cancellationToken).ConfigureAwait(false);

        return WorkflowOutcome.Recorded(accept.TaskId, workScheduled: false);
    }

    private async Task<WorkflowOutcome> AbortAsync(AbortTaskInstruction abort, CancellationToken cancellationToken)
    {
        await SettledAsync(abort.TaskId, cancellationToken).ConfigureAwait(false);

        if (supervisor.Owns(abort.TaskId))
            return WorkflowOutcome.Refused(ControllerErrorCodes.RunOwned, "The controller is working on this run now; an abort is for a run that waits or is blocked.");

        await new AbortCommand(Quiet()).RecordAsync(abort.TaskId, cancellationToken, abort.GateId).ConfigureAwait(false);

        return WorkflowOutcome.Recorded(abort.TaskId, workScheduled: false);
    }

    private async Task<WorkflowOutcome> BindAsync(BindRoleInstruction bind, CancellationToken cancellationToken)
    {
        await SettledAsync(bind.TaskId, cancellationToken).ConfigureAwait(false);

        // A launched agent's configuration is fixed when it starts, so a role is bound only while nobody works on the run. (The command takes the run's lock as well.)
        if (supervisor.Owns(bind.TaskId))
            return WorkflowOutcome.Refused(ControllerErrorCodes.RunOwned, "The controller is working on this run now; bind the role when the work has stopped.");

        await new BindCommand(Quiet()).RecordAsync(bind.TaskId, bind.Role, bind.Profile, cancellationToken).ConfigureAwait(false);

        return WorkflowOutcome.Recorded(bind.TaskId, workScheduled: false);
    }

    private async Task<WorkflowOutcome> ContinueAsync(ContinueTaskInstruction resume, CancellationToken cancellationToken)
    {
        await SettledAsync(resume.TaskId, cancellationToken).ConfigureAwait(false);

        if (supervisor.Owns(resume.TaskId))
            return WorkflowOutcome.Refused(ControllerErrorCodes.RunOwned, "The controller is already working on this run.");

        // The same preparation as 'aw continue': only a blocked continuation (or that same work left RUNNING by a dead owner) is accepted, and the agent host the interrupted attempt
        // left behind must be confirmed stopped first.
        var stretch = await new ContinueCommand(Quiet())
            .PrepareAsync(new ContinueRequest(resume.TaskId.ToString(), Presence(resume.Presence)), TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);

        return Adopt(stretch);
    }

    private WorkflowOutcome Adopt(PreparedStretch stretch)
    {
        var runId = stretch.RunId;

        supervisor.Adopt(stretch, attempts: 1);

        return WorkflowOutcome.Recorded(runId, workScheduled: true);
    }

    private CliServices Quiet() => services.ForSupervision(TextWriter.Null);

    /// <summary>
    /// The task as it durably is, after a stretch that has already recorded where it stopped (the task no longer RUNNING) has finished letting go of the run. A task that is still
    /// RUNNING under this controller is really being worked on, and is not waited for: the operation that follows is refused as owned.
    /// </summary>
    private async Task<WorkflowTask> SettledAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var task = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (task.WorkflowState == WorkflowState.Running || !supervisor.Owns(taskId))
            return task;

        await supervisor.WaitForReleaseAsync(taskId, WindDownGrace, cancellationToken).ConfigureAwait(false);

        return await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowTask> LoadAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var tasks = await store.OpenExistingAsync(taskId, cancellationToken).ConfigureAwait(false);

        return (await tasks.LoadAsync(taskId, cancellationToken).ConfigureAwait(false)).Task;
    }

    /// <summary>
    /// The decision's own checks, made against the durable gate before the commands run: the gate exists and is of the kind this operation decides, it is open (a gate is decided once),
    /// it still describes the task, and it is the gate of the semantic revision the client was shown. Null when all hold. The service repeats the open-and-current check atomically
    /// when it records, so this is what lets the refusal say which of these it was; it is never the only guard.
    /// </summary>
    private static WorkflowOutcome? CheckGate(WorkflowTask task, GateId gateId, GateType expected, Guid semanticRevision)
    {
        var gate = task.Gates.FirstOrDefault(g => g.Id == gateId);

        if (gate is null)
            return WorkflowOutcome.Refused(ControllerErrorCodes.NoSuchGate, "The task has no such gate.");

        if (gate.Type != expected)
            return WorkflowOutcome.Refused(ControllerErrorCodes.WrongGateKind, $"The gate is a {gate.Type.ToString().ToUpperInvariant()} gate, not the {expected.ToString().ToUpperInvariant()} gate this operation decides.");

        switch (task.StatusOf(gate))
        {
            case GateStatus.Open:
                break;

            case GateStatus.Invalidated:
                return WorkflowOutcome.Refused(ControllerErrorCodes.StaleGate, "The approval of this gate was invalidated by newer evidence; it decides nothing now.");

            default:
                return WorkflowOutcome.Refused(ControllerErrorCodes.AlreadyDecided, "The gate was already decided; a gate is decided once.");
        }

        if (!task.IsCurrent(gate))
            return WorkflowOutcome.Refused(ControllerErrorCodes.StaleGate, "The gate no longer describes the task (the task moved on); read the task again.");

        if (gate.SemanticRevisionId.Value != semanticRevision)
            return WorkflowOutcome.Refused(ControllerErrorCodes.StaleBinding, "The semantic revision is not the one this gate belongs to; the decision was made about something else.");

        return null;
    }

    private static WorkflowOutcome? CheckAcceptBinding(Gate gate, Guid? implementation, Guid? verification)
        => gate.ImplementationRevisionId?.Value == implementation && gate.VerificationRunId?.Value == verification
            ? null
            : WorkflowOutcome.Refused(ControllerErrorCodes.StaleBinding, "The implementation or the verification is not the one this gate asks to be reviewed; the decision was made about something else.");

    private static HumanPresence Presence(TaskPresence presence) => presence == TaskPresence.Attended ? HumanPresence.Interactive : HumanPresence.NonInteractive;
}

/// <summary>
/// How the failures a workflow operation can end in are told to a client. Every one of them means nothing was recorded; a failure that is not one of these is a fault of the
/// controller, not of the request, and is left to the dispatcher (a generic internal error, detail only in the controller's log).
/// </summary>
internal static class WorkflowRefusals
{
    public static ControllerError? Map(Exception e) => e switch
    {
        BindingRefusedException refused => new ControllerError(refused.Code, refused.Message),
        GateRefusedException refused => new ControllerError(Code(refused.Reason), refused.Message),
        ContinuationRefusedException refused => new ControllerError(Code(refused.Reason), refused.Message),
        RunStateException { Problem: RunStateProblem.NotFound } state => new ControllerError(ControllerErrorCodes.NotFound, state.Message),
        RunStateException state => new ControllerError(ControllerErrorCodes.StateUnreadable, state.Message),
        ConfigurationException configuration => new ControllerError(ControllerErrorCodes.PreconditionFailed, configuration.Message),
        UsageException usage when usage.Message.Contains("still live", StringComparison.Ordinal) => new ControllerError(ControllerErrorCodes.RunOwned, "Something is working on this run now; try again when it has finished."),
        UsageException usage => new ControllerError(ControllerErrorCodes.IllegalState, usage.Message),
        StaleWorkflowStateException => new ControllerError(ControllerErrorCodes.StateMoved, "The task changed while the operation was being recorded; nothing was recorded."),
        EventStoreException or DomainException or Microsoft.Data.Sqlite.SqliteException or TaskNotFoundException
            => new ControllerError(ControllerErrorCodes.StateUnreadable, "The task's durable state cannot be read or changed; nothing was repaired or guessed."),
        _ => null,
    };

    private static string Code(GateRefusal reason) => reason switch
    {
        GateRefusal.UnknownGate => ControllerErrorCodes.NoSuchGate,
        GateRefusal.AlreadyDecided => ControllerErrorCodes.AlreadyDecided,
        GateRefusal.NotCurrent => ControllerErrorCodes.StaleGate,
        GateRefusal.WrongGateKind => ControllerErrorCodes.WrongGateKind,
        GateRefusal.WorkspaceChanged => ControllerErrorCodes.WorkspaceChanged,
        GateRefusal.StateMoved => ControllerErrorCodes.StateMoved,
        GateRefusal.WorkspaceUnavailable or GateRefusal.ConversationUnusable => ControllerErrorCodes.PreconditionFailed,
        _ => ControllerErrorCodes.IllegalState,
    };

    private static string Code(ContinuationRefusal reason) => reason switch
    {
        ContinuationRefusal.StateMoved => ControllerErrorCodes.StateMoved,
        ContinuationRefusal.BaselineUnavailable or ContinuationRefusal.SessionNotStopped => ControllerErrorCodes.PreconditionFailed,
        _ => ControllerErrorCodes.IllegalState,
    };
}
