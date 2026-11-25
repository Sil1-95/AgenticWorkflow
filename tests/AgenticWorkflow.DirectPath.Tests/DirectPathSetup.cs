using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>The wiring a host does for the direct path: store, service, role binding, task definition, checks.</summary>
internal static class DirectPathSetup
{
    public static readonly ProfileId ClaudeProfile = new("claude-code-local");

    public static readonly TaskId TaskId = new(Guid.Parse("a1b2c3d4-0000-4000-8000-00000000d1f3"));

    public static readonly VerificationCheckId OldSymbolAbsent = new("old_symbol_absent");
    public static readonly VerificationCheckId NewSymbolPresent = new("new_symbol_present");
    public static readonly VerificationCheckId DotnetTest = new("dotnet_test");

    public static Profile ClaudeProfileDefinition { get; } = new(
        ClaudeProfile,
        IdentifierSet<ProfileCapability>.Of(DirectPathRoles.CodeEdit, DirectPathRoles.RepositoryTools));

    public static async Task<WorkflowTaskService> OpenServiceAsync(string databasePath)
        => new(await SqliteEventStore.OpenAsync(databasePath), new GuidIdSource(), new SystemClock());

    /// <summary>Creates the task with an explicit working-tree boundary and the one explicit role binding.</summary>
    public static async Task<TaskSnapshot> CreateTaskAsync(
        WorkflowTaskService service,
        string intent,
        Profile? profile = null,
        AcceptanceRequirement acceptance = AcceptanceRequirement.HumanRequired)
    {
        var created = await service.CreateAsync(TaskId, intent, CompletionBoundary.WorkingTree, acceptance);
        var bound = await service.BindRoleAsync(created.Snapshot, DirectPathRoles.Root, profile ?? ClaudeProfileDefinition);
        return bound.Snapshot;
    }

    /// <summary>"GetMeal" must be gone and "GetFood" present, by the controller's own search (absent = exit 1).</summary>
    public static IReadOnlyList<VerificationCheckSpec> SymbolChecks()
        =>
        [
            new(OldSymbolAbsent, "no occurrence of GetMeal remains", "git", ["grep", "--untracked", "-q", "-F", "GetMeal"], TimeSpan.FromSeconds(30), expectedExitCode: 1),
            new(NewSymbolPresent, "GetFood is used", "git", ["grep", "--untracked", "-q", "-F", "GetFood"], TimeSpan.FromSeconds(30)),
        ];

    public static IReadOnlyList<VerificationCheckSpec> SymbolAndTestChecks()
        =>
        [
            .. SymbolChecks(),
            new(DotnetTest, "dotnet test passes", "dotnet", ["test", DirectFixtureRepo.TestProject, "--nologo", "--verbosity", "minimal"], TimeSpan.FromMinutes(5)),
        ];

    public static DirectTaskDefinition Definition(string workspace, IReadOnlyList<VerificationCheckSpec> checks, int maxTurns = 3)
        => new(
            "Rename the public method Menu.GetMeal() to GetFood() and update every reference to it (source and tests). Keep behavior identical.",
            workspace,
            DirectPathRoles.RootRoleId,
            ClaudeProfile,
            new WorkspaceScope(
                allowedPaths: ["src/", "tests/"],
                protectedPaths: ["src/Meals/Meals.csproj", "tests/Meals.Tests/Meals.Tests.csproj", ".gitignore"]),
            checks,
            maxTurns)
        {
            OperatingNotes =
                "Tooling: you may only run commands that are pre-approved, one plain command per call. Search with `git grep` (not grep/rg/find), "
                + "and run `dotnet test <project>` or `dotnet build <project>` on its own. Do not chain commands with `;`, `&&` or `|`, and do not redirect output: "
                + "anything else needs an approval nobody can give and will stop the task.",
        };
}
