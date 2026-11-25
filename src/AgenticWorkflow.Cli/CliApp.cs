using System.Reflection;
using AgenticWorkflow.Cli.Commands;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.Reporting;

namespace AgenticWorkflow.Cli;

/// <summary>
/// Dispatches one command line to its command and turns every way a command can end into a stable exit code and a message. No exception,
/// and no raw process or exception code, ever escapes to the caller.
/// </summary>
internal sealed class CliApp(CliServices services)
{
    /// <summary>The flag that makes a mutating command run in this process instead of through a running controller (the administrative and recovery path).</summary>
    private const string DirectFlag = "--direct";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var (arguments, direct) = TakeDirectFlag(args);
            var parsed = CommandLine.Parse(arguments);

            if (!direct && await new ControllerRouting(services).TryRouteAsync(parsed, cancellationToken).ConfigureAwait(false) is { } routed)
                return routed;

            switch (parsed)
            {
                case HelpRequest help:
                    services.Out.Write(Help.For(help.Command));
                    return ExitCodes.Success;

                case VersionRequest:
                    services.Out.WriteLine($"aw {Version()}");
                    return ExitCodes.Success;

                case RunRequest run:
                    return await new RunCommand(services).ExecuteAsync(run, cancellationToken).ConfigureAwait(false);

                case StatusRequest status:
                    return await new StatusCommand(services).ExecuteAsync(status, cancellationToken).ConfigureAwait(false);

                case RunsRequest runs:
                    return await new RunsCommand(services).ExecuteAsync(runs, cancellationToken).ConfigureAwait(false);

                case AbortRequest abort:
                    return await new AbortCommand(services).ExecuteAsync(abort, cancellationToken).ConfigureAwait(false);

                case AcceptRequest accept:
                    return await new AcceptCommand(services).ExecuteAsync(accept, cancellationToken).ConfigureAwait(false);

                case ApproveRequest approve:
                    return await new ApproveCommand(services).ExecuteAsync(approve, cancellationToken).ConfigureAwait(false);

                case RequestChangesRequest requestChanges:
                    return await new RequestChangesCommand(services).ExecuteAsync(requestChanges, cancellationToken).ConfigureAwait(false);

                case AnswerRequest answer:
                    return await new AnswerCommand(services).ExecuteAsync(answer, cancellationToken).ConfigureAwait(false);

                case BindRequest bind:
                    return await new BindCommand(services).ExecuteAsync(bind, cancellationToken).ConfigureAwait(false);

                case ContinueRequest recover:
                    return await new ContinueCommand(services).ExecuteAsync(recover, cancellationToken).ConfigureAwait(false);

                case CtlRequest ctl:
                    return await new CtlCommand(services).ExecuteAsync(ctl, cancellationToken).ConfigureAwait(false);

                case ProjectRequest project:
                    return await new ProjectCommand(services).ExecuteAsync(project, cancellationToken).ConfigureAwait(false);

                case DoctorRequest doctor:
                    return await new DoctorCommand(services).ExecuteAsync(doctor, cancellationToken).ConfigureAwait(false);

                case AttachRequest attach:
                    return await new AttachCommand(services).ExecuteAsync(attach, cancellationToken).ConfigureAwait(false);

                case ShowRequest show:
                    return await new ShowCommand(services).ExecuteAsync(show, cancellationToken).ConfigureAwait(false);

                default:
                    throw new InvalidOperationException("Unhandled request type.");
            }
        }
        catch (UsageException e)
        {
            services.Error.WriteLine($"usage error: {e.Message}");
            services.Error.WriteLine("Try 'aw --help'.");
            return ExitCodes.UsageOrConfiguration;
        }
        catch (BindingRefusedException e)
        {
            services.Error.WriteLine($"binding refused ({e.Code}; nothing was launched):");
            services.Error.WriteLine($"  - {e.Message}");

            return ExitCodes.UsageOrConfiguration;
        }
        catch (ConfigurationException e)
        {
            services.Error.WriteLine("configuration error (nothing was launched):");

            foreach (var problem in e.Problems)
                services.Error.WriteLine($"  - {problem}");

            return ExitCodes.UsageOrConfiguration;
        }
        catch (OperationCanceledException)
        {
            services.Error.WriteLine("cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception e)
        {
            services.Error.WriteLine($"internal error: {e.GetType().Name}: {e.Message}");
            return ExitCodes.InternalError;
        }
    }

    /// <summary>Removes <c>--direct</c> from the options (not from the text after a bare <c>--</c>, which is the person's own).</summary>
    private static (string[] Arguments, bool Direct) TakeDirectFlag(string[] args)
    {
        var end = Array.IndexOf(args, "--");
        var options = end < 0 ? args.Length : end;
        var direct = Array.IndexOf(args, DirectFlag, 0, options) >= 0;

        if (!direct)
            return (args, false);

        return ([.. args.Where((argument, index) => index >= options || argument != DirectFlag)], true);
    }

    internal static string Version()
    {
        var informational = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return informational?.Split('+')[0] ?? typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
