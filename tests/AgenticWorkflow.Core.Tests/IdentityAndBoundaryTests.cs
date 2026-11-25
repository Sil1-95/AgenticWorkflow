using System.Reflection;

namespace AgenticWorkflow.Core.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void Guid_identities_reject_the_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => new TaskId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SemanticRevisionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ImplementationRevisionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new EventId(Guid.Empty));
    }

    [Fact]
    public void Identities_of_different_kinds_are_different_types_with_value_equality()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(new TaskId(guid), new TaskId(guid));
        Assert.NotEqual<object>(new TaskId(guid), new EventId(guid));
    }

    [Theory]
    [InlineData("code_edit")]
    [InlineData("a")]
    [InlineData("0-first-digit")]
    [InlineData("vendor.cap:sub-kind_1")]
    public void Valid_stable_identifiers_are_accepted(string value)
        => Assert.Equal(value, new ProfileCapability(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Code_Edit")]
    [InlineData("has space")]
    [InlineData("_leading")]
    [InlineData("-leading")]
    [InlineData("slash/path")]
    [InlineData("émoji")]
    public void Invalid_stable_identifiers_are_rejected(string value)
        => Assert.Throws<ArgumentException>(() => new ProfileCapability(value));

    [Fact]
    public void Over_long_identifier_is_rejected()
        => Assert.Throws<ArgumentException>(() => new ProfileCapability(new string('a', StableIdentifier.MaxLength + 1)));

    [Fact]
    public void Same_text_in_different_identifier_kinds_is_not_equal()
        => Assert.NotEqual<object>(new RoleId("reviewer"), new ProfileId("reviewer"));

    [Fact]
    public void Identifier_sets_order_ordinally_and_compare_by_value()
    {
        var set = IdentifierSet<ActionId>.Of(new ActionId("b"), new ActionId("a"), new ActionId("B".ToLowerInvariant()));

        Assert.Equal(["a", "b"], set.Select(a => a.Value));
        Assert.Equal(set.GetHashCode(), IdentifierSet<ActionId>.Of(new ActionId("a"), new ActionId("b")).GetHashCode());
    }

    [Fact]
    public void Guid_id_source_does_not_repeat()
    {
        var source = new GuidIdSource();

        Assert.NotEqual(source.Next(), source.Next());
    }
}

/// <summary>Architecture guard: Core stays runtime/provider-neutral with no third-party dependencies.</summary>
public sealed class DependencyBoundaryTests
{
    private static readonly Assembly Core = typeof(TaskStream).Assembly;

    [Fact]
    public void Core_references_only_the_base_class_library()
    {
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard" || name == "mscorlib",
            $"Unexpected non-BCL reference: {name}"));
    }

    [Theory]
    [InlineData("Herdr")]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Sqlite")]
    [InlineData("LibGit2")]
    [InlineData("Newtonsoft")]
    public void Core_has_no_reference_to_runtime_provider_or_storage_libraries(string forbidden)
        => Assert.DoesNotContain(
            Core.GetReferencedAssemblies(),
            a => a.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Core_project_file_has_no_package_or_project_references()
    {
        var directory = AppContext.BaseDirectory;
        string? csproj = null;
        for (var dir = new DirectoryInfo(directory); dir is not null && csproj is null; dir = dir.Parent)
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
