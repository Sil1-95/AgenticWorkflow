using AgenticWorkflow.Cli.Configuration;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>
/// When run inside a project (or given one with <c>--workspace</c>): that its configuration is valid and every profile its roles are bound to can run on this machine. Without a
/// configuration in the folder there is nothing to check and nothing is wrong.
/// </summary>
internal sealed class ProjectCheck : IDoctorCheck
{
    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DoctorCheck>>(Check(context));

    private static List<DoctorCheck> Check(DoctorContext context)
    {
        var inspection = ProjectInspector.Inspect(context.Workspace, null, context.Workspace, context.Runtime);

        if (!inspection.WorkspaceExists)
            return [DoctorCheck.Fail("project", $"'{context.Workspace}' is not a folder")];

        if (inspection.ConfigState == ProjectConfigState.Missing)
        {
            return context.WorkspaceGiven
                ? [DoctorCheck.Warn("project", $"no {ProjectResolver.DefaultConfigFileName} in {context.Workspace}", "The cockpit's 'Set up this project' creates one.")]
                : [DoctorCheck.Pass("project", $"no {ProjectResolver.DefaultConfigFileName} in this folder (run 'aw doctor' inside a project to check its profiles)")];
        }

        if (inspection.ConfigState == ProjectConfigState.Invalid)
            return [DoctorCheck.Fail("project", $"{inspection.ConfigPath} is not valid: {string.Join("; ", inspection.Problems)}", "Fix the file, or see docs/configuration.md.")];

        var checks = new List<DoctorCheck> { DoctorCheck.Pass("project", $"{inspection.ConfigPath} is valid") };
        var project = inspection.Project!;
        var bound = new[] { ("implementer", project.RootProfile), ("reviewer", project.ReviewerProfile), ("prover", project.ProverProfile) };

        foreach (var (role, id) in bound.Where(binding => binding.Item2 is not null))
        {
            var profile = inspection.Profiles.FirstOrDefault(p => p.Id == id);
            var name = $"profile {id} ({role})";

            if (profile is null)
                checks.Add(DoctorCheck.Fail(name, "not found in the project's profiles"));
            else if (profile.Usable)
                checks.Add(DoctorCheck.Pass(name, Describe(profile)));
            else
                checks.Add(DoctorCheck.Fail(name, $"{Describe(profile)} cannot run here: {string.Join("; ", profile.Problems)}", "Install or update the harness, or bind the role to another profile."));
        }

        checks.AddRange(inspection.Prerequisites.Where(p => !p.Met).Select(p => DoctorCheck.Fail($"needs {p.Name}", p.Problem ?? "missing")));

        return checks;
    }

    private static string Describe(ProfileSummary profile)
        => $"{profile.Harness}, model {profile.Model ?? "native default"}, reasoning {profile.Reasoning ?? "native default"}";
}
