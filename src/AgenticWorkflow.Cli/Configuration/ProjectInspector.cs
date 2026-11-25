using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Gates;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>The state of a workspace's <c>agenticworkflow.json</c>.</summary>
internal static class ProjectConfigState
{
    public const string Missing = "missing";
    public const string Invalid = "invalid";
    public const string Ready = "ready";
}

internal sealed record ProjectInspection(
    string Workspace,
    bool WorkspaceExists,
    string ConfigPath,
    string ConfigState,
    IReadOnlyList<string> Problems,
    ProjectSummary? Project,
    IReadOnlyList<ProfileSummary> Profiles,
    ProjectSuggestions Suggestions,
    IReadOnlyList<PrerequisiteSummary> Prerequisites,
    IReadOnlyList<string> Harnesses,
    IReadOnlyList<string> Toolchains,
    IReadOnlyList<string> Acceptance);

internal sealed record ProjectSummary(
    string Toolchain,
    int MaxTurns,
    string Acceptance,
    string RootProfile,
    string? ReviewerProfile,
    string? ProverProfile,
    IReadOnlyList<CheckSummary> Checks,
    IReadOnlyList<string> Writable,
    IReadOnlyList<string> Protected,
    IReadOnlyList<string> AllowCommands,
    bool HasOperatingNotes);

internal sealed record CheckSummary(string Id, string Executable, IReadOnlyList<string> Arguments, int TimeoutSeconds);

/// <summary>A profile as a person reads it: id, harness, model and reasoning (null = the harness's own setting), and whether the installed harness can honour it right now, with the reasons if not.</summary>
internal sealed record ProfileSummary(string Id, string Harness, string? Model, string? Reasoning, string? Description, bool BuiltIn, bool Usable, IReadOnlyList<string> Problems);

internal sealed record PrerequisiteSummary(string Name, bool Met, string? Location, string? Problem);

/// <summary>What the workspace suggests, with the evidence for it. A suggestion is only ever shown: it becomes part of a configuration when a person confirms it.</summary>
internal sealed record ProjectSuggestions(string? Toolchain, IReadOnlyList<string> Evidence, IReadOnlyList<CheckSummary> Checks, IReadOnlyList<string> Protected);

/// <summary>
/// Reads a workspace and says, as structured data, what a person needs to start a task there: whether the project is configured, what its profiles are and whether each can run on this machine,
/// and (for a project with no configuration) what the files suggest. Read-only and Git-free; it never creates or changes a file.
/// </summary>
internal static class ProjectInspector
{
    public static ProjectInspection Inspect(string? workspaceOption, string? configOption, string currentDirectory, IAgentRuntimeFactory runtime)
    {
        var workspace = Path.GetFullPath(workspaceOption ?? currentDirectory, currentDirectory).TrimEnd('\\', '/');
        var exists = Directory.Exists(workspace);
        var configPath = configOption is null ? Path.Combine(workspace, ProjectResolver.DefaultConfigFileName) : Path.GetFullPath(configOption, currentDirectory);
        var suggestions = exists ? Suggest(workspace) : new ProjectSuggestions(null, [], [], []);

        if (!exists)
            return Report(workspace, false, configPath, ProjectConfigState.Missing, [$"The workspace '{workspace}' does not exist or is not a directory."], null, [], suggestions, []);

        if (!File.Exists(configPath))
            return Report(workspace, true, configPath, ProjectConfigState.Missing, [], null, BuiltInOnly(runtime), suggestions, []);

        ResolvedProject project;

        try
        {
            project = ProjectResolver.Resolve(workspace, configOption, currentDirectory);
        }
        catch (ConfigurationException e)
        {
            return Report(workspace, true, configPath, ProjectConfigState.Invalid, e.Problems, null, BuiltInOnly(runtime), suggestions, []);
        }

        var prerequisites = runtime.CheckPrerequisites(project).Select(p => new PrerequisiteSummary(p.Name, p.IsMet, p.Location, p.Problem)).ToList();
        var profiles = project.Catalog.All.Select(p => Summarize(p, runtime)).ToList();

        var summary = new ProjectSummary(
            project.Toolchain.Id,
            project.Config.MaxTurns,
            AcceptancePolicy.Word(project.Config.Acceptance),
            project.ProfileId.Value,
            project.ReviewerProfile?.Id.Value,
            project.ProverProfile?.Id.Value,
            [.. project.Config.Verification.Select(c => new CheckSummary(c.Id, c.Executable, c.Arguments, c.TimeoutSeconds))],
            project.Config.Writable,
            project.Config.Protected,
            project.Config.AllowCommands,
            project.Config.OperatingNotes is not null);

        return Report(workspace, true, configPath, ProjectConfigState.Ready, [], summary, profiles, suggestions, prerequisites);
    }

    private static ProjectInspection Report(
        string workspace,
        bool exists,
        string configPath,
        string state,
        IReadOnlyList<string> problems,
        ProjectSummary? project,
        IReadOnlyList<ProfileSummary> profiles,
        ProjectSuggestions suggestions,
        IReadOnlyList<PrerequisiteSummary> prerequisites)
        => new(
            workspace,
            exists,
            configPath,
            state,
            problems,
            project,
            profiles,
            suggestions,
            prerequisites,
            Configuration.Harnesses.Known,
            [.. Toolchain.All.Select(t => t.Id)],
            [AcceptancePolicy.None, AcceptancePolicy.Human]);

    /// <summary>Without a readable configuration the only profile that exists is the built-in one; the others are the person's to define.</summary>
    private static List<ProfileSummary> BuiltInOnly(IAgentRuntimeFactory runtime) => [Summarize(AgentProfileEntry.ClaudeCodeLocal, runtime)];

    private static ProfileSummary Summarize(AgentProfileEntry profile, IAgentRuntimeFactory runtime)
    {
        var problems = Configuration.Harnesses.IsSupported(profile.Harness)
            ? runtime.ValidateExecution(profile.Id, profile.ToExecution())
            : [$"harness '{profile.Harness}' cannot be run by this build"];

        return new ProfileSummary(profile.Id.Value, profile.Harness, profile.Model, profile.Reasoning, profile.Description, profile.IsBuiltIn, problems.Count == 0, problems);
    }

    /// <summary>The toolchain the root of the workspace points at and the checks that toolchain conventionally uses, with the files that led there. Nothing is suggested when the files do not say.</summary>
    internal static ProjectSuggestions Suggest(string workspace)
    {
        var solutions = Files(workspace, "*.sln").Concat(Files(workspace, "*.slnx")).ToList();
        var projects = Files(workspace, "*.csproj").ToList();
        var pubspec = File.Exists(Path.Combine(workspace, "pubspec.yaml"));
        var dotnet = solutions.Count > 0 || projects.Count > 0;

        string[] protectedPaths = [ProjectResolver.DefaultConfigFileName];

        if (dotnet == pubspec)
        {
            var evidence = dotnet
                ? new List<string> { "both .NET and Flutter files are present, so the toolchain is for you to choose" }
                : [];

            return new ProjectSuggestions(null, evidence, [], protectedPaths);
        }

        if (pubspec)
        {
            return new ProjectSuggestions(
                Toolchain.Flutter.Id,
                ["pubspec.yaml"],
                [
                    new CheckSummary("analyze", "flutter", ["analyze"], ProjectConfig.DefaultCheckTimeoutSeconds),
                    new CheckSummary("test", "flutter", ["test", "--no-pub"], ProjectConfig.DefaultCheckTimeoutSeconds),
                ],
                protectedPaths);
        }

        var targets = solutions.Count > 0 ? solutions : projects;
        var evidenceFiles = targets.Select(Path.GetFileName).OfType<string>().ToList();

        // A build and a test of one named target; with several candidates the choice of target is the person's.
        IReadOnlyList<CheckSummary> checks = targets.Count == 1
            ? [
                new CheckSummary("build", "dotnet", ["build", evidenceFiles[0]], ProjectConfig.DefaultCheckTimeoutSeconds),
                new CheckSummary("test", "dotnet", ["test", evidenceFiles[0]], ProjectConfig.DefaultCheckTimeoutSeconds),
            ]
            : [];

        return new ProjectSuggestions(Toolchain.Dotnet.Id, evidenceFiles, checks, protectedPaths);
    }

    private static IEnumerable<string> Files(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
