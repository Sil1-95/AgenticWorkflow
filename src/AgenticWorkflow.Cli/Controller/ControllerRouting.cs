using System.Text.Json;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// Sends the CLI's mutating commands (<c>run</c>, <c>approve</c>, <c>request-changes</c>, <c>answer</c>, <c>accept</c>, <c>abort</c>, <c>continue</c>) through the local controller
/// when one is already running and able to own work, so there is one owner of every run and one definition of every decision: the controller's typed operations. This is the
/// same operation a Herdr mod sends; the command line only fills in what the person left implicit (the gate that is open, the revision and content it shows), from the task as the
/// controller reports it, and the controller re-checks every one of those bindings against the durable state before it records anything.
/// <para>
/// A command is routed only to a controller that already runs: the command line never starts one just to hand it work. Without one, or with one that cannot own work (it reports
/// itself not durable), or with <c>--direct</c>, the command runs in this process exactly as before. That path is the administrative and recovery fallback; it cannot create a second
/// owner, because a run's live lock is held by whoever works on it, and a direct command on a run the controller owns is refused by that lock.
/// </para>
/// <para>
/// A routed command returns as soon as the controller has recorded the decision and scheduled the work (exit 0, "accepted"); it does not wait for the agent, so it does not report
/// the run's eventual outcome. Follow the run with <c>aw status</c>. Direct mode runs to the outcome, as it always did.
/// </para>
/// </summary>
internal sealed class ControllerRouting(CliServices services)
{
    /// <summary>The exit code of the routed command, or null when it is not routed (nothing was sent; the caller runs it directly).</summary>
    public async Task<int?> TryRouteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        if (!IsRoutable(request))
            return null;

        var root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        var connector = new ControllerConnector(
            ControllerEndpoints.For(root, services.GetEnvironmentVariable),
            services.ControllerLauncher ?? new ProcessControllerLauncher(),
            new ControllerPaths(root),
            root);

        ControllerClientSession? session;

        try
        {
            session = await connector.ConnectAsync(startIfAbsent: false, cancellationToken).ConfigureAwait(false);
        }
        catch (ControllerUnavailableException)
        {
            return null;
        }

        if (session is null)
            return null;

        await using (session.ConfigureAwait(false))
        {
            try
            {
                var health = await session.SendAsync(new HealthRequest(), cancellationToken).ConfigureAwait(false);

                if (health.Result is not HealthResult { Supervision.Durable: true })
                    return null;

                var (typed, refusal) = await BuildAsync(request, session, cancellationToken).ConfigureAwait(false);

                if (refusal is not null)
                    return Refuse(refusal);

                if (typed is null)
                    return null;

                var reply = await session.SendAsync(typed, cancellationToken).ConfigureAwait(false);

                return Report(reply);
            }
            catch (Exception e) when (e is ControllerFrameException or IOException or JsonException)
            {
                // Whether the controller recorded anything is unknown, so the command is not repeated directly: the person looks at the run.
                services.Error.WriteLine($"The controller connection failed ({e.Message}); the command may or may not have been recorded. Check with 'aw status'.");

                return ExitCodes.ControllerUnavailable;
            }
        }
    }

    private static bool IsRoutable(CliRequest request) => request is RunRequest or ApproveRequest or RequestChangesRequest or AnswerRequest or AcceptRequest or AbortRequest or ContinueRequest or BindRequest;

    private async Task<(ControllerRequest? Request, ControllerError? Refusal)> BuildAsync(CliRequest request, ControllerClientSession session, CancellationToken cancellationToken)
    {
        switch (request)
        {
            case RunRequest run:
                return (ToStart(run), null);

            case AbortRequest abort:
                return (new AbortTaskRequest { TaskId = abort.RunId }, null);

            case BindRequest bind:
                return (new BindRoleRequest { TaskId = bind.RunId, Role = bind.Role.Value, Profile = bind.Profile.Value }, null);

            case ContinueRequest resume:
                return (new ContinueTaskRequest { TaskId = resume.RunId, Presence = ToPresence(resume.Presence) }, null);
        }

        var runId = request switch
        {
            ApproveRequest approve => approve.RunId,
            RequestChangesRequest changes => changes.RunId,
            AnswerRequest answer => answer.RunId,
            AcceptRequest accept => accept.RunId,
            _ => throw new InvalidOperationException("Not a routed request."),
        };

        var read = await session.SendAsync(new ReadTaskRequest { TaskId = runId }, cancellationToken).ConfigureAwait(false);

        if (!read.Ok || read.Result is not TaskResult { Task: var task })
            return (null, read.Error ?? new ControllerError(ControllerErrorCodes.InternalError, "The controller did not report the task."));

        var gateText = request switch
        {
            ApproveRequest approve => approve.Gate,
            RequestChangesRequest changes => changes.Gate,
            AnswerRequest answer => answer.Question,
            AcceptRequest accept => accept.Gate,
            _ => null,
        };

        var gate = FindGate(task, gateText);

        if (gate is null)
            return (null, new ControllerError(ControllerErrorCodes.NoSuchGate, gateText is null ? "The task has no open gate to decide." : "The task has no such gate."));

        var semantic = gate.Binding.SemanticRevisionId.ToString();
        var id = gate.Id.ToString();

        switch (request)
        {
            case ApproveRequest approve when gate.Type is GateType.What or GateType.How:
                return (new ApproveProposalRequest
                {
                    Proposal = gate.Type == GateType.What ? ProposalKind.What : ProposalKind.How,
                    TaskId = runId,
                    GateId = id,
                    SemanticRevisionId = semantic,
                    ContentHash = (gate.Type == GateType.What ? gate.What?.ContentHash : gate.How?.ContentHash) ?? string.Empty,
                    Presence = ToPresence(approve.Presence),
                }, null);

            case ApproveRequest:
                return (null, new ControllerError(ControllerErrorCodes.WrongGateKind, "Only a WHAT or HOW proposal is approved; an ACCEPT gate is decided with 'accept'."));

            case AnswerRequest answer when gate.Type == GateType.Clarify:
                return (new AnswerClarificationRequest { TaskId = runId, GateId = id, SemanticRevisionId = semantic, Answer = answer.Text, Presence = ToPresence(answer.Presence) }, null);

            case AnswerRequest:
                return (null, new ControllerError(ControllerErrorCodes.WrongGateKind, "Only a CLARIFY question is answered."));

            case RequestChangesRequest changes:
                return (new RequestTaskChangesRequest
                {
                    Gate = gate.Type switch { GateType.What => DecisionGateKind.What, GateType.How => DecisionGateKind.How, _ => DecisionGateKind.Accept },
                    TaskId = runId,
                    GateId = id,
                    SemanticRevisionId = semantic,
                    Feedback = changes.Feedback,
                    ImplementationRevisionId = gate.Type == GateType.Accept ? gate.Binding.ImplementationRevisionId?.ToString() : null,
                    VerificationRunId = gate.Type == GateType.Accept ? gate.Binding.VerificationRunId?.ToString() : null,
                    Presence = ToPresence(changes.Presence),
                }, null);

            case AcceptRequest when gate.Type == GateType.Accept && gate.Binding is { ImplementationRevisionId: { } implementation, VerificationRunId: { } verification }:
                return (new AcceptTaskRequest
                {
                    TaskId = runId,
                    GateId = id,
                    SemanticRevisionId = semantic,
                    ImplementationRevisionId = implementation.ToString(),
                    VerificationRunId = verification.ToString(),
                }, null);

            default:
                return (null, new ControllerError(ControllerErrorCodes.WrongGateKind, "That gate is not an ACCEPT gate with a verified implementation."));
        }
    }

    /// <summary>The gate the command names, or (when it names none) the task's only open gate. Null when neither exists.</summary>
    private static GateView? FindGate(TaskReadModel task, string? gateText)
    {
        if (gateText is not null)
        {
            return Guid.TryParse(gateText, out var id)
                ? task.Gates.FirstOrDefault(g => g.Id == id)
                : null;
        }

        return task.PendingRequest?.GateId is { } open ? task.Gates.FirstOrDefault(g => g.Id == open) : null;
    }

    private StartTaskRequest? ToStart(RunRequest run)
    {
        var objective = run.TaskText;

        // A task file is read here, where its path was written; a mistake in the request (both, neither, unreadable) is the direct path's to report, in its own words.
        if (run.TaskFile is not null)
        {
            if (run.TaskText is not null)
                return null;

            try
            {
                objective = File.ReadAllText(Path.GetFullPath(run.TaskFile, services.CurrentDirectory));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(objective))
            return null;

        return new StartTaskRequest
        {
            Objective = objective,
            WorkingDirectory = services.CurrentDirectory,
            Workspace = run.Workspace,
            Config = run.Config,
            Acceptance = run.Acceptance,
            RequireWhat = run.RequireWhat,
            RequireHow = run.RequireHow,
            RequireReview = run.RequireReview,
            RequireProve = run.RequireProve,
            Bindings = run.Bindings is { Count: > 0 } bindings ? bindings.ToDictionary(b => b.Key.Value, b => b.Value.Value) : null,
            Presence = ToPresence(run.Presence),
        };
    }

    private TaskPresence? ToPresence(HumanPresence presence) => presence switch
    {
        HumanPresence.Interactive => TaskPresence.Attended,
        HumanPresence.NonInteractive => TaskPresence.Unattended,
        _ => services.IsTerminal ? TaskPresence.Attended : TaskPresence.Unattended,
    };

    private int Report(ControllerReply reply)
    {
        if (!reply.Ok || reply.Result is not WorkflowAcceptedResult accepted)
            return Refuse(reply.Error ?? new ControllerError(ControllerErrorCodes.InternalError, "The controller sent neither a result nor an error."));

        var id = accepted.Task.TaskId;

        services.Out.WriteLine(accepted.WorkScheduled
            ? $"Accepted. The controller is now working on run {id} in the background. Follow it with 'aw status {id}'."
            : $"Recorded for run {id}. See 'aw status {id}'.");

        return ExitCodes.Success;
    }

    private int Refuse(ControllerError error)
    {
        services.Error.WriteLine($"The controller refused the command ({error.Code}): {error.Message}");
        services.Error.WriteLine("Nothing was recorded.");

        return CtlCommand.ExitCode(error.Code);
    }
}
