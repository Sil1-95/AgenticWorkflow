using System.Text;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Cli.Runtime;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw show</c>: resolves and validates the project exactly as <c>aw run</c> would, then prints what a run would use, without starting
/// Herdr or the agent and without a model call. Read-only: it creates no run and writes nothing.
/// </summary>
internal sealed class ShowCommand(CliServices services)
{
    public async Task<int> ExecuteAsync(ShowRequest request, CancellationToken cancellationToken)
    {
        var project = ProjectResolver.Resolve(request.Workspace, request.Config, services.CurrentDirectory);
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        var problems = new List<string>();
        var prerequisites = services.RuntimeFactory.CheckPrerequisites(project);
        problems.AddRange(prerequisites.Where(p => !p.IsMet).Select(p => $"{p.Name}: {p.Problem}"));

        if (RunStore.IsInside(store.Root, project.Workspace))
            problems.Add($"The state root '{store.Root}' is inside the workspace. Run state must live outside the repository; set {CliEnvironment.HomeVariable}.");

        var git = await DescribeWorkspaceAsync(project.Workspace, problems, cancellationToken).ConfigureAwait(false);

        services.Out.Write(Render(project, store, prerequisites, git));

        if (problems.Count == 0)
            return ExitCodes.Success;

        services.Error.WriteLine("Not ready to run:");

        foreach (var problem in problems)
            services.Error.WriteLine($"  - {problem}");

        return ExitCodes.UsageOrConfiguration;
    }

    private async Task<string> DescribeWorkspaceAsync(string workspace, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            var state = await services.Workspace.CaptureAsync(workspace, cancellationToken).ConfigureAwait(false);

            return $"Git repository root, HEAD {Format.ShortFingerprint(state.BaseIdentity, 10)}, {state.PathDigests.Count} uncommitted path(s)";
        }
        catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException)
        {
            problems.Add($"workspace: {e.Message}");
            return "not usable (see below)";
        }
    }

    private static string Render(ResolvedProject project, RunStore store, IReadOnlyList<Prerequisite> prerequisites, string git)
    {
        var text = new StringBuilder();

        text.AppendLine($"Workspace: {project.Workspace}");
        text.AppendLine($"Git: {git}");
        text.AppendLine($"Config: {project.ConfigPath} (sha256 {project.ConfigSha256[..12]})");
        text.AppendLine($"Role: {project.RoleId}");
        text.AppendLine($"Agent profile: {project.ProfileId} ({project.Profile.Description ?? "no description"}; {RunCommand.Describe(project.Profile.ToExecution())})");
        text.AppendLine(project.ReviewerProfile is { } reviewer
            ? $"Reviewer profile: {reviewer.Id} ({reviewer.Description ?? "no description"}; {RunCommand.Describe(reviewer.ToExecution())}; bound to the reviewer role when a run starts)"
            : "Reviewer profile: none (--require review needs --reviewer-profile; a review an agent asks for mid-run waits for 'aw bind')");
        text.AppendLine(project.ProverProfile is { } prover
            ? $"Prover profile: {prover.Id} ({prover.Description ?? "no description"}; {RunCommand.Describe(prover.ToExecution())}; bound to the prover role when a run starts)"
            : "Prover profile: none (--require prove needs --prover-profile; a proof an agent asks for mid-run waits for 'aw bind')");
        text.AppendLine("Profiles available (a run binds each role to one of these, recorded with the run):");
        foreach (var available in project.Catalog.All)
            text.AppendLine($"  {available.Id}: {RunCommand.Describe(available.ToExecution())}{(Harnesses.IsSupported(available.Harness) ? string.Empty : "  [cannot be run by this build]")}");
        text.AppendLine($"Toolchain: {project.Toolchain.Id}");
        text.AppendLine($"Max turns: {project.Config.MaxTurns}");
        text.AppendLine($"Completion boundary: {CompletionBoundary.WorkingTree}");
        text.AppendLine($"Acceptance (default for new runs): {AcceptancePolicy.Describe(project.Config.Acceptance)} ('{AcceptancePolicy.Word(project.Config.Acceptance)}'; override per run with --acceptance)");

        text.AppendLine("Checks (run by the controller):");
        foreach (var check in project.Checks)
            text.AppendLine($"  {check.Id}: {check.Executable}{(check.Arguments.Count == 0 ? string.Empty : " " + string.Join(' ', check.Arguments))}  (timeout {Format.Duration(check.Timeout)})");

        text.AppendLine("Writable scope:");
        text.AppendLine(project.Scope.AllowedPaths.Count == 0 ? "  anywhere not protected" : string.Join(Environment.NewLine, project.Scope.AllowedPaths.Select(p => $"  {p}")));

        text.AppendLine("Protected scope:");
        text.AppendLine(project.Scope.ProtectedPaths.Count == 0 ? "  none" : string.Join(Environment.NewLine, project.Scope.ProtectedPaths.Select(p => $"  {p}")));

        text.AppendLine("Agent may run without asking:");
        foreach (var tool in project.ToolPolicy.AllowedTools)
            text.AppendLine($"  {tool}");

        text.AppendLine($"Operating notes: {(project.Config.OperatingNotes is null ? "toolchain default" : "toolchain default + project notes")}");
        text.AppendLine($"State storage: {store.Root}");

        text.AppendLine("Prerequisites:");
        foreach (var prerequisite in prerequisites)
            text.AppendLine($"  {prerequisite.Name}: {(prerequisite.IsMet ? prerequisite.Location : "MISSING")}");

        return text.ToString();
    }
}
