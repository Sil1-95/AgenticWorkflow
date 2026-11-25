using System.Reflection;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>Architecture guard: the adapter depends on Application, Core and SQLite only; Core depends on nothing.</summary>
public sealed class DependencyBoundaryTests
{
    private static readonly Assembly Adapter = typeof(SqliteEventStore).Assembly;

    [Fact]
    public void The_adapter_references_application_core_sqlite_and_the_base_class_library_only()
    {
        var referenced = Adapter.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("AgenticWorkflow.Application", referenced);
        Assert.Contains("AgenticWorkflow.Core", referenced);
        Assert.Contains("Microsoft.Data.Sqlite", referenced);
        Assert.All(referenced, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal)
            || name is "netstandard" or "mscorlib" or "AgenticWorkflow.Application" or "AgenticWorkflow.Core" or "Microsoft.Data.Sqlite",
            $"Unexpected reference: {name}"));
    }

    [Theory]
    [InlineData("Herdr")]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("EntityFramework")]
    [InlineData("Dapper")]
    public void The_adapter_has_no_runtime_provider_or_orm_dependency(string forbidden)
        => Assert.DoesNotContain(
            Adapter.GetReferencedAssemblies(),
            a => a.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Core_does_not_reference_the_adapter_sqlite_or_a_json_library()
    {
        var core = typeof(TaskStream).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(core, n => n.Contains("Persistence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(core, n => n.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(core, n => n.Contains("Json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_declares_no_serialization_attributes()
    {
        var offenders = typeof(TaskStream).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData().Concat(t.GetProperties().SelectMany(p => p.GetCustomAttributesData())))
            .Where(a => a.AttributeType.Namespace?.Contains("Serialization", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_project_file_still_has_no_package_or_project_references()
    {
        string? csproj = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && csproj is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AgenticWorkflow.Core", "AgenticWorkflow.Core.csproj");
            if (File.Exists(candidate))
                csproj = candidate;
        }

        Assert.NotNull(csproj);
        var text = File.ReadAllText(csproj);
        Assert.DoesNotContain("PackageReference", text);
        Assert.DoesNotContain("ProjectReference", text);
    }
}
