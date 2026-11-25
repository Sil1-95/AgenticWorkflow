using System.Diagnostics;
using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Cli.Controller;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>Runs a program once to read its version, without a shell and within a time limit. Never starts anything that talks to a model.</summary>
internal interface IToolProbe
{
    /// <summary>The first line of the program's output, or null when it could not be run or did not answer in time.</summary>
    string? FirstLine(IReadOnlyList<string> command, TimeSpan timeout);
}

internal sealed class ProcessToolProbe : IToolProbe
{
    public string? FirstLine(IReadOnlyList<string> command, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(command[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in command.Skip(1))
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);

            if (process is null)
                return null;

            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);

                return null;
            }

            var text = process.ExitCode == 0 ? output.GetAwaiter().GetResult() : string.Empty;
            var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                       ?? error.GetAwaiter().GetResult().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            return process.ExitCode == 0 ? line : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>What the doctor saw of the controller: whether one answered, whether the doctor had to start it, and what it reports. Counts and versions only.</summary>
internal sealed record ControllerObservation(bool Reachable, bool StartedForCheck, ControllerInfo? Controller, SupervisionStatus? Supervision, string? Problem);

/// <summary>Attaches to the controller the way every other client does (starting one if none is running) and reads its health. Starts no run and makes no model call.</summary>
internal static class ControllerObserver
{
    public static async Task<ControllerObservation> ObserveAsync(CliServices services, string stateRoot, CancellationToken cancellationToken)
    {
        var connector = new ControllerConnector(
            ControllerEndpoints.For(stateRoot, services.GetEnvironmentVariable),
            services.ControllerLauncher ?? new ProcessControllerLauncher(),
            new ControllerPaths(stateRoot),
            stateRoot);

        try
        {
            var existing = await connector.ConnectAsync(startIfAbsent: false, cancellationToken).ConfigureAwait(false);
            var started = existing is null;

            await using var session = existing ?? await connector.ConnectAsync(startIfAbsent: true, cancellationToken).ConfigureAwait(false);

            if (session is null)
                return new ControllerObservation(false, false, null, null, "No controller could be reached.");

            var reply = await session.SendAsync(new HealthRequest(), cancellationToken).ConfigureAwait(false);

            return reply.Result is HealthResult health
                ? new ControllerObservation(true, started, health.Controller, health.Supervision, null)
                : new ControllerObservation(true, started, session.Controller, null, "The controller did not answer a health request.");
        }
        catch (ControllerUnavailableException e)
        {
            return new ControllerObservation(false, false, null, null, e.Message);
        }
        catch (Exception e) when (e is ControllerFrameException or IOException or System.Text.Json.JsonException)
        {
            return new ControllerObservation(false, false, null, null, $"The controller connection failed: {e.Message}");
        }
    }
}

/// <summary>Everything a check may look at, supplied by the command and replaceable in tests. A check reads from here and never reaches for the machine itself.</summary>
internal sealed record DoctorContext(
    string Version,
    string InstallDirectory,
    string AwPath,
    string StateRoot,
    string HerdrHome,
    string Workspace,
    bool WorkspaceGiven,
    Func<string, string?> GetEnvironmentVariable,
    ExecutableLocator Locator,
    IToolProbe Tools,
    IAgentRuntimeFactory Runtime,
    Func<CancellationToken, Task<ControllerObservation>> ObserveController)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public static string ResolveHerdrHome(Func<string, string?> environment, string userProfile)
    {
        var configured = environment("HERDR_APP_HOME");

        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(userProfile, ".config", "herdr-app");
    }

    public static string ResolveStateRoot(CliServices services) => RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData);
}

internal interface IDoctorCheck
{
    Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken);
}
