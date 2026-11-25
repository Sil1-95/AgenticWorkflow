namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// The guards and the bounded reset of the live fixture, proven offline against throw-away temp repositories. These
/// tests never touch the real trusted fixture and make no model calls.
/// </summary>
public sealed class TrustedLiveFixtureTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "aw-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public TrustedLiveFixtureTests() => Directory.CreateDirectory(_parent);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string NewMarkedRepo(string name = "fixture", string? markerContent = null)
    {
        var root = Path.Combine(_parent, name);
        Directory.CreateDirectory(root);
        DirectFixtureRepo.RunGit(root, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(root, TrustedLiveFixture.MarkerFileName), markerContent ?? TrustedLiveFixture.MarkerText + "\r\n");
        return root;
    }

    private static readonly IReadOnlyList<string> NoForbidden = [];

    [Fact]
    public void An_unset_variable_blocks_with_an_actionable_message_and_names_the_variable()
    {
        foreach (var value in new string?[] { null, "", "   " })
        {
            var guard = TrustedLiveFixture.Evaluate(value, NoForbidden);

            Assert.False(guard.IsSafe);
            Assert.Contains(TrustedLiveFixture.WorkspaceVariable, guard.Describe());
            Assert.Contains("manually", guard.Describe());
        }
    }

    [Fact]
    public void A_correctly_marked_git_root_is_accepted_and_trailing_line_terminators_do_not_matter()
    {
        foreach (var ending in new[] { string.Empty, "\n", "\r\n" })
        {
            var root = NewMarkedRepo("ok-" + ending.Length, TrustedLiveFixture.MarkerText + ending);
            var guard = TrustedLiveFixture.Evaluate(root + Path.DirectorySeparatorChar, NoForbidden);

            Assert.True(guard.IsSafe, guard.Describe());
            Assert.Equal(Path.GetFullPath(root), guard.CanonicalPath, ignoreCase: true);
        }
    }

    [Fact]
    public void A_missing_directory_is_blocked()
    {
        var guard = TrustedLiveFixture.Evaluate(Path.Combine(_parent, "nope"), NoForbidden);

        Assert.False(guard.IsSafe);
        Assert.Contains("does not exist", guard.Describe());
        Assert.False(Directory.Exists(Path.Combine(_parent, "nope"))); // nothing was created
    }

    [Fact]
    public void A_missing_marker_is_blocked_and_never_created()
    {
        var root = NewMarkedRepo();
        File.Delete(Path.Combine(root, TrustedLiveFixture.MarkerFileName));

        var guard = TrustedLiveFixture.Evaluate(root, NoForbidden);

        Assert.False(guard.IsSafe);
        Assert.Contains(TrustedLiveFixture.MarkerFileName, guard.Describe());
        Assert.False(File.Exists(Path.Combine(root, TrustedLiveFixture.MarkerFileName)));
    }

    [Theory]
    [InlineData("something else")]
    [InlineData("AgenticWorkflow dedicated LiveDirect fixture and more")]
    [InlineData("")]
    public void A_marker_with_any_other_content_is_blocked(string content)
    {
        var guard = TrustedLiveFixture.Evaluate(NewMarkedRepo(markerContent: content), NoForbidden);

        Assert.False(guard.IsSafe);
        Assert.Contains("does not contain exactly", guard.Describe());
    }

    [Fact]
    public void A_directory_that_is_not_a_git_root_is_blocked_even_inside_a_repository()
    {
        var outer = NewMarkedRepo("outer");
        var inner = Path.Combine(outer, "sub");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, TrustedLiveFixture.MarkerFileName), TrustedLiveFixture.MarkerText);

        var guard = TrustedLiveFixture.Evaluate(inner, NoForbidden);

        Assert.False(guard.IsSafe);
        Assert.Contains(".git", guard.Describe());
    }

    [Fact]
    public void A_repository_with_a_remote_is_blocked()
    {
        var root = NewMarkedRepo();
        DirectFixtureRepo.RunGit(root, "remote", "add", "origin", "https://example.invalid/x.git");

        var guard = TrustedLiveFixture.Evaluate(root, NoForbidden);

        Assert.False(guard.IsSafe);
        Assert.Contains("remote", guard.Describe());
    }

    [Fact]
    public void Protected_locations_their_parents_and_their_contents_are_blocked()
    {
        var protectedRepo = NewMarkedRepo("protected");
        var nested = NewMarkedRepo(Path.Combine("protected", "nested"));

        Assert.Contains("protected location", TrustedLiveFixture.Evaluate(protectedRepo, [protectedRepo]).Describe()); // the location itself
        Assert.Contains("protected location", TrustedLiveFixture.Evaluate(_parent, [protectedRepo]).Describe()); // its parent
        Assert.Contains("protected location", TrustedLiveFixture.Evaluate(nested, [protectedRepo]).Describe()); // inside it
    }

    [Fact]
    public void The_repository_under_test_is_a_default_protected_location_without_requiring_anything_to_be_configured()
    {
        var root = TrustedLiveFixture.SolutionRoot();
        Assert.NotNull(root);

        foreach (var path in new[] { root, Path.Combine(root, "tests") })
        {
            var guard = TrustedLiveFixture.Evaluate(path);

            Assert.False(guard.IsSafe, path);
            Assert.Contains("protected location", guard.Describe());
        }
    }

    [Fact]
    public void A_sibling_directory_sharing_the_repositorys_name_prefix_is_not_mistaken_for_a_protected_location()
    {
        var root = TrustedLiveFixture.SolutionRoot();
        Assert.NotNull(root);

        var guard = TrustedLiveFixture.Evaluate(root + "-LiveFixtures" + Path.DirectorySeparatorChar + "LiveDirectFixture");

        Assert.DoesNotContain("protected location", guard.Describe());
    }

    [Fact]
    public void Drive_roots_and_the_home_directory_and_its_parents_are_blocked()
    {
        var root = Path.GetPathRoot(_parent)!;
        Assert.Contains("drive root", TrustedLiveFixture.Evaluate(root, NoForbidden).Describe());

        var home = Path.Combine(_parent, "home", "someone");
        Directory.CreateDirectory(home);

        Assert.Contains("home directory", TrustedLiveFixture.Evaluate(home, NoForbidden, homeDirectory: home).Describe());
        Assert.Contains("home directory", TrustedLiveFixture.Evaluate(Path.GetDirectoryName(home), NoForbidden, homeDirectory: home).Describe());
    }

    [Fact]
    public void A_failed_guard_makes_the_reset_refuse_and_delete_nothing()
    {
        var root = NewMarkedRepo();
        File.Delete(Path.Combine(root, TrustedLiveFixture.MarkerFileName));
        File.WriteAllText(Path.Combine(root, "precious.txt"), "keep");

        var guard = TrustedLiveFixture.Evaluate(root, NoForbidden);

        Assert.Throws<InvalidOperationException>(() => TrustedLiveFixture.ResetToBaseline(guard));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "precious.txt")));
    }

    [Fact]
    public void Reset_builds_the_baseline_and_preserves_the_root_the_git_directory_and_the_marker()
    {
        var root = NewMarkedRepo();
        File.WriteAllText(Path.Combine(root, "stray.txt"), "junk");
        Directory.CreateDirectory(Path.Combine(root, "obj", "deep"));
        File.WriteAllText(Path.Combine(root, "obj", "deep", "x.dll"), "x");
        File.SetAttributes(Path.Combine(root, "obj", "deep", "x.dll"), FileAttributes.ReadOnly);

        var gitDirectoryCreated = Directory.GetCreationTimeUtc(Path.Combine(root, ".git"));
        var guard = TrustedLiveFixture.Evaluate(root, NoForbidden);

        using var repo = TrustedLiveFixture.ResetToBaseline(guard);

        Assert.True(Directory.Exists(root));
        Assert.Equal(gitDirectoryCreated, Directory.GetCreationTimeUtc(Path.Combine(root, ".git")));
        Assert.Equal(TrustedLiveFixture.MarkerText, File.ReadAllText(Path.Combine(root, TrustedLiveFixture.MarkerFileName)).TrimEnd('\r', '\n'));
        Assert.False(File.Exists(Path.Combine(root, "stray.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        Assert.Contains("GetMeal", repo.Read("src/Meals/Menu.cs"));
        Assert.Equal(string.Empty, repo.Git("status", "--porcelain=v1"));
        Assert.Equal("1", repo.Git("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public void Reset_restores_a_modified_fixture_and_does_not_add_commits_when_the_baseline_is_unchanged()
    {
        var root = NewMarkedRepo();
        var guard = TrustedLiveFixture.Evaluate(root, NoForbidden);

        string head;
        using (var first = TrustedLiveFixture.ResetToBaseline(guard))
            head = first.Git("rev-parse", "HEAD").Trim();

        // "Claude" renames the method and adds a file
        File.WriteAllText(Path.Combine(root, "src", "Meals", "Menu.cs"), "public string GetFood() => \"x\";\n");
        File.WriteAllText(Path.Combine(root, "src", "Meals", "Extra.cs"), "// extra\n");

        using var second = TrustedLiveFixture.ResetToBaseline(guard);

        Assert.Equal(head, second.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, second.Git("status", "--porcelain=v1"));
        Assert.Contains("GetMeal", second.Read("src/Meals/Menu.cs"));
        Assert.False(File.Exists(Path.Combine(root, "src", "Meals", "Extra.cs")));
    }
}
