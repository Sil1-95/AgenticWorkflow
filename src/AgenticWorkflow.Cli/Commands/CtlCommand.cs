using System.Text.Json;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Application.ReadModel;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw ctl</c>: the thin, short-lived client of the local controller, and <c>aw ctl serve</c>, the controller itself. A client attaches to the controller (starting one if
/// none is running), makes one typed request, prints the reply as JSON and exits. It never reads a run itself, so what it prints is what the controller says, and the
/// controller's task read model is the one <c>aw status --json</c> prints.
/// </summary>
internal sealed class CtlCommand(CliServices services)
{
    public Task<int> ExecuteAsync(CtlRequest request, CancellationToken cancellationToken)
        => request.Action == CtlAction.Serve ? ServeAsync(request, cancellationToken) : RequestAsync(request, cancellationToken);

    private async Task<int> RequestAsync(CtlRequest request, CancellationToken cancellationToken)
    {
        var root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        // The request to forward is read (and a request file consumed) before anything is started: a malformed request never starts a controller, and the text a person
        // typed does not outlive the one attempt to send it.
        ControllerRequest? sent = null;

        if (request.Action == CtlAction.Send)
        {
            var (decoded, error) = request.RequestFile is { } file
                ? await ReadRequestFileAsync(file, cancellationToken).ConfigureAwait(false)
                : await ReadRequestAsync(cancellationToken).ConfigureAwait(false);

            if (decoded is null)
                return Fail(error!.Code, error.Message);

            sent = decoded;
        }

        var connector = new ControllerConnector(
            ControllerEndpoints.For(root, services.GetEnvironmentVariable),
            services.ControllerLauncher ?? new ProcessControllerLauncher(),
            new ControllerPaths(root),
            root);

        try
        {
            // 'stop' never starts a controller in order to stop it.
            await using var session = await connector.ConnectAsync(startIfAbsent: !request.NoStart && request.Action != CtlAction.Stop, cancellationToken).ConfigureAwait(false);

            if (session is null)
            {
                if (request.Action == CtlAction.Stop)
                {
                    WriteResult(new ShutdownResult { Accepted = false });

                    return ExitCodes.Success;
                }

                return Fail(ControllerErrorCodes.ControllerUnavailable, "No controller is running (and --no-start was given).");
            }

            var typed = request.Action == CtlAction.Send ? sent! : ToRequest(request);
            var reply = await session.SendAsync(typed, cancellationToken).ConfigureAwait(false);

            return Report(reply);
        }
        catch (ControllerUnavailableException e)
        {
            return Fail(e.Code, e.Message);
        }
        catch (Exception e) when (e is ControllerFrameException or IOException or JsonException)
        {
            return Fail(ControllerErrorCodes.ControllerUnresponsive, $"The controller connection failed: {e.Message}");
        }
    }

    /// <summary>
    /// The one typed request <c>aw ctl send</c> forwards, read from standard input (at most one frame's worth) and decoded exactly as the controller decodes it, so an integration
    /// learns of a malformed or unsupported request before anything is sent and from the same rules.
    /// </summary>
    private async Task<(ControllerRequest? Request, ControllerError? Error)> ReadRequestAsync(CancellationToken cancellationToken)
    {
        var buffer = new char[ControllerProtocol.MaxRequestBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await services.Input.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);

            if (read == 0)
                break;

            length += read;
        }

        if (length > ControllerProtocol.MaxRequestBytes)
            return (null, new ControllerError(ControllerErrorCodes.MalformedRequest, $"A request is at most {ControllerProtocol.MaxRequestBytes} bytes."));

        return ControllerWire.DecodeRequest(System.Text.Encoding.UTF8.GetBytes(buffer, 0, length));
    }

    /// <summary>The same request, read from a file the caller wrote (<c>--request-file</c>) for a caller that cannot feed standard input; see <see cref="RequestFile"/>.</summary>
    private static async Task<(ControllerRequest? Request, ControllerError? Error)> ReadRequestFileAsync(string path, CancellationToken cancellationToken)
    {
        var (bytes, problem) = await RequestFile.ConsumeAsync(path, ControllerProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);

        return bytes is null
            ? (null, new ControllerError(ControllerErrorCodes.MalformedRequest, problem!))
            : ControllerWire.DecodeRequest(bytes);
    }

    private static ControllerRequest ToRequest(CtlRequest request) => request.Action switch
    {
        CtlAction.Health => new HealthRequest(),
        CtlAction.Tasks => new ListTasksRequest { Limit = request.Limit },
        CtlAction.Task => new ReadTaskRequest { TaskId = request.TaskId! },
        CtlAction.Stop => new ShutdownRequest { Force = request.Force },
        _ => throw new InvalidOperationException("Not a request to the controller."),
    };

    private int Report(ControllerReply reply)
    {
        if (!reply.Ok || reply.Result is null)
        {
            var error = reply.Error ?? new ControllerError(ControllerErrorCodes.InternalError, "The controller sent neither a result nor an error.");

            return Fail(error.Code, error.Message);
        }

        // The task is printed exactly as 'aw status --json' prints it: the same serializer, the same text.
        if (reply.Result is TaskResult task)
            services.Out.Write(TaskReadModelJson.Serialize(task.Task));
        else
            WriteResult(reply.Result);

        return ExitCodes.Success;
    }

    private void WriteResult(ControllerResult result)
        => services.Out.Write(JsonSerializer.Serialize(result, ResultOptions) + "\n");

    private int Fail(string code, string message)
    {
        services.Out.Write(TaskReadModelJson.Serialize(new TaskReadFailure(code, message)));

        return ExitCode(code);
    }

    internal static int ExitCode(string code) => code switch
    {
        ControllerErrorCodes.InvalidTaskId or ControllerErrorCodes.InvalidArgument or ControllerErrorCodes.MalformedRequest or ControllerErrorCodes.UnsupportedOperation => ExitCodes.UsageOrConfiguration,
        ControllerErrorCodes.InvalidGateId => ExitCodes.UsageOrConfiguration,
        ControllerErrorCodes.MissingBinding or ControllerErrorCodes.UnknownProfile or ControllerErrorCodes.UnsupportedHarness or ControllerErrorCodes.UnsupportedConfiguration => ExitCodes.UsageOrConfiguration,
        ControllerErrorCodes.AlreadyBound => ExitCodes.GateDecisionRefused,
        ControllerErrorCodes.NoSuchGate or ControllerErrorCodes.AlreadyDecided or ControllerErrorCodes.StaleGate or ControllerErrorCodes.StaleBinding or ControllerErrorCodes.WrongGateKind
            or ControllerErrorCodes.IllegalState or ControllerErrorCodes.RunOwned or ControllerErrorCodes.WorkspaceChanged or ControllerErrorCodes.StateMoved or ControllerErrorCodes.PreconditionFailed
            => ExitCodes.GateDecisionRefused,
        ControllerErrorCodes.NotFound => ExitCodes.RunNotFound,
        ControllerErrorCodes.StateUnreadable => ExitCodes.RunStateUnreadable,
        ControllerErrorCodes.IncompatibleProtocol => ExitCodes.ControllerIncompatible,
        ControllerErrorCodes.ControllerUnavailable or ControllerErrorCodes.ControllerUnresponsive or ControllerErrorCodes.ShuttingDown or ControllerErrorCodes.ControllerNotDurable => ExitCodes.ControllerUnavailable,
        _ => ExitCodes.InternalError,
    };

    private static readonly JsonSerializerOptions ResultOptions = TaskReadModelJson.CreateOptions(indented: true);

    /// <summary>
    /// The controller process. Foreground (<c>aw ctl serve</c>) it reports where it is and stops on Ctrl+C. Background (<c>--background</c>, how a client starts it) it prints
    /// nothing and ignores the hang-up of the terminal that happened to start it. Both stop only when asked, when idle, or on a termination signal; neither starts a run.
    /// </summary>
    private async Task<int> ServeAsync(CtlRequest request, CancellationToken cancellationToken)
    {
        var root = RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
        var paths = new ControllerPaths(root);
        var log = new ControllerLog(paths.Log, request.Background ? null : services.Error);

        // A controller started in the background must not die with the terminal that started it; and one that would be killed with its parent (a kill-on-close job) is not allowed
        // to own work, because it could not keep it.
        if (request.Background)
            ControllerDetachment.StartNewSession();

        var durable = ControllerDetachment.IsDurable();

        if (!durable)
            log.Write("this process would be ended together with its parent; it serves reads but refuses to own agent work");

        var store = new RunStore(root);
        var instanceId = Guid.NewGuid();
        await using var supervisor = new RunSupervisor(services, store, instanceId, log.Write);
        var reconciler = new RunReconciler(services, store, supervisor, log.Write);

        // A stretch that ended in a way nothing could report is put into a defined state (and, if it was only interrupted, resumed) instead of being left RUNNING with nobody on it.
        supervisor.Stranded = id => reconciler.ReconcileAsync(id, CancellationToken.None);

        var host = new ControllerHost(
            ControllerEndpoints.For(root, services.GetEnvironmentVariable),
            new RunTaskCatalog(services, store),
            new ControllerHostOptions
            {
                InstanceLockPath = paths.InstanceLock,
                Version = CliApp.Version(),
                InstanceId = instanceId,
                IdleTimeout = request.IdleTimeout ?? ControllerHostOptions.DefaultIdleTimeout,
            },
            log,
            new ControllerWorkflow(services, store, supervisor, reconciler, log.Write),
            durable);

        using var signals = ControllerSignals.Attach(request.Background);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, signals.Token);

        try
        {
            await host.RunAsync(
                info =>
                {
                    if (!request.Background)
                        services.Error.WriteLine($"AgenticWorkflow controller {info.InstanceId} is serving this user over a local {info.Transport} (pid {info.ProcessId}). Ctrl+C or 'aw ctl stop' stops it.");
                },
                linked.Token).ConfigureAwait(false);

            // Every way of stopping is a normal end, and a second controller that found the first one running has done what was asked of it: a controller is running.
            return ExitCodes.Success;
        }
        catch (ControllerEndpointException e)
        {
            log.Write($"controller not started: {e.Message}");
            services.Error.WriteLine($"The controller could not be started: {e.Message}");

            return ExitCodes.ControllerUnavailable;
        }
    }
}
