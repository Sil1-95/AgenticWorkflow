using AgenticWorkflow.Cli.Diagnostics;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw doctor</c>: a read-only diagnosis of this installation and machine. It starts no agent and makes no model call; the one thing it may do is what any client does, start the controller
/// if none is running, which proves the installed program can.
/// </summary>
internal sealed class DoctorCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(DoctorRequest request, CancellationToken cancellationToken)
    {
        var stateRoot = DoctorContext.ResolveStateRoot(services);
        var install = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var processPath = Environment.ProcessPath ?? Path.Combine(install, "aw");
        var awPath = string.Equals(Path.GetFileNameWithoutExtension(processPath), "aw", StringComparison.OrdinalIgnoreCase) ? processPath : Path.Combine(install, OperatingSystem.IsWindows() ? "aw.exe" : "aw");
        var workspace = Path.GetFullPath(request.Workspace ?? services.CurrentDirectory, services.CurrentDirectory).TrimEnd('\\', '/');

        var context = new DoctorContext(
            CliApp.Version(),
            install,
            awPath,
            stateRoot,
            DoctorContext.ResolveHerdrHome(services.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            workspace,
            request.Workspace is not null,
            services.GetEnvironmentVariable,
            new ExecutableLocator(services.GetEnvironmentVariable),
            new ProcessToolProbe(),
            services.RuntimeFactory,
            token => ControllerObserver.ObserveAsync(services, stateRoot, token));

        var report = await Doctor.RunAsync(context, cancellationToken).ConfigureAwait(false);

        services.Out.Write(request.Json ? report.ToJson() : report.ToText());

        return report.Healthy ? ExitCodes.Success : ExitCodes.DiagnosisFailed;
    }
}
