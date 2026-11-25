using System.Reflection;
using System.Xml.Linq;

namespace AgenticWorkflow.Application.Tests;

/// <summary>Architecture guard for the whole solution's dependency direction.</summary>
public sealed class DependencyBoundaryTests
{
    private static readonly string[] Forbidden =
        ["Sqlite", "Herdr", "Claude", "Codex", "Git", "Persistence", "EntityFramework", "Dapper", "MediatR", "DependencyInjection"];

    [Fact]
    public void Application_references_core_and_the_base_class_library_only()
    {
        var referenced = typeof(WorkflowTaskService).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("AgenticWorkflow.Core", referenced);
        Assert.All(referenced, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib" or "AgenticWorkflow.Core",
            $"Unexpected reference: {name}"));
    }

    [Theory]
    [MemberData(nameof(ForbiddenNames))]
    public void Application_has_no_provider_runtime_or_framework_dependency(string forbidden)
        => Assert.DoesNotContain(
            typeof(WorkflowTaskService).Assembly.GetReferencedAssemblies(),
            a => a.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    public static TheoryData<string> ForbiddenNames() => new(Forbidden);

    [Fact]
    public void Core_does_not_reference_application_or_anything_else_in_the_solution()
        => Assert.DoesNotContain(
            typeof(TaskStream).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("AgenticWorkflow", StringComparison.Ordinal));

    [Fact]
    public void Core_project_file_has_no_package_or_project_references()
    {
        var (packages, projects) = References("AgenticWorkflow.Core");

        Assert.Empty(packages);
        Assert.Empty(projects);
    }

    [Fact]
    public void Application_project_file_references_core_only_and_no_packages()
    {
        var (packages, projects) = References("AgenticWorkflow.Application");

        Assert.Empty(packages);
        Assert.Equal(["AgenticWorkflow.Core"], projects);
    }

    [Fact]
    public void Sqlite_project_file_references_application_core_and_microsoft_data_sqlite()
    {
        var (packages, projects) = References("AgenticWorkflow.Persistence.Sqlite");

        Assert.Equal(["Microsoft.Data.Sqlite"], packages);
        Assert.Equal(["AgenticWorkflow.Application", "AgenticWorkflow.Core"], projects);
    }

    [Fact]
    public void Herdr_project_file_references_application_and_core_only_with_no_packages_and_no_sqlite_or_git_adapter()
    {
        var (packages, projects) = References("AgenticWorkflow.Runtime.Herdr");

        Assert.Empty(packages);
        Assert.Equal(["AgenticWorkflow.Application", "AgenticWorkflow.Core"], projects);
    }

    [Fact]
    public void Local_execution_project_file_references_application_and_core_only_with_no_packages_and_no_herdr_or_sqlite()
    {
        var (packages, projects) = References("AgenticWorkflow.Execution.Local");

        Assert.Empty(packages);
        Assert.Equal(["AgenticWorkflow.Application", "AgenticWorkflow.Core"], projects);
    }

    [Fact]
    public void The_claude_evidence_hook_is_a_host_that_joins_the_adapters_with_no_packages_and_nothing_references_it()
    {
        var (packages, projects) = References("AgenticWorkflow.ClaudeEvidenceHook");

        Assert.Empty(packages);
        Assert.Equal(
            ["AgenticWorkflow.Application", "AgenticWorkflow.Core", "AgenticWorkflow.Execution.Local", "AgenticWorkflow.Runtime.Herdr"],
            projects);

        // No library project may depend on the host; only test projects may use it, and only to locate the executable.
        foreach (var library in new[] { "AgenticWorkflow.Core", "AgenticWorkflow.Application", "AgenticWorkflow.Persistence.Sqlite", "AgenticWorkflow.Runtime.Herdr", "AgenticWorkflow.Execution.Local" })
            Assert.DoesNotContain("AgenticWorkflow.ClaudeEvidenceHook", References(library).Projects);
    }

    [Fact]
    public void The_cli_is_the_composition_root_that_joins_every_adapter_and_nothing_references_it()
    {
        var (packages, projects) = References("AgenticWorkflow.Cli");

        Assert.Empty(packages);
        Assert.Equal(
            [
                "AgenticWorkflow.Application",
                "AgenticWorkflow.ClaudeEvidenceHook",
                "AgenticWorkflow.Core",
                "AgenticWorkflow.Execution.Local",
                "AgenticWorkflow.Persistence.Sqlite",
                "AgenticWorkflow.Runtime.Herdr",
            ],
            projects);

        foreach (var library in new[] { "AgenticWorkflow.Core", "AgenticWorkflow.Application", "AgenticWorkflow.Persistence.Sqlite", "AgenticWorkflow.Runtime.Herdr", "AgenticWorkflow.Execution.Local", "AgenticWorkflow.ClaudeEvidenceHook" })
            Assert.DoesNotContain("AgenticWorkflow.Cli", References(library).Projects);
    }

    [Fact]
    public void The_provider_neutral_evidence_model_lives_in_application_and_names_no_harness()
    {
        var evidenceTypes = typeof(WorkflowTaskService).Assembly.GetTypes()
            .Where(t => t.Namespace == "AgenticWorkflow.Application.Evidence")
            .ToList();

        Assert.NotEmpty(evidenceTypes);

        var members = evidenceTypes.SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Concat(evidenceTypes.Select(t => t.Name));

        Assert.All(members, name => Assert.DoesNotContain("Claude", name, StringComparison.OrdinalIgnoreCase));
    }

    private static (List<string> Packages, List<string> Projects) References(string project)
    {
        var path = FindUp(Path.Combine("src", project, project + ".csproj"));
        var doc = XDocument.Load(path);

        var packages = doc.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).Order().ToList();
        var projects = doc.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .Order()
            .ToList();
        return (packages, projects);
    }

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {relative} above {AppContext.BaseDirectory}.");
    }
}
