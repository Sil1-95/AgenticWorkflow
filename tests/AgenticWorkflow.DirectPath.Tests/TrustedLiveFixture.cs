namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// The result of checking an explicitly supplied live-fixture path. Nothing is touched while evaluating.
/// </summary>
internal sealed record FixtureGuard(string? CanonicalPath, IReadOnlyList<string> Problems)
{
    public bool IsSafe => Problems.Count == 0 && CanonicalPath is not null;

    public string Describe()
        => string.Join(Environment.NewLine, Problems.Select(p => "  - " + p));
}

/// <summary>
/// Live-test-only infrastructure. Claude Code treats a Git repository root as its own trust boundary, so a freshly created
/// random repository can never be trusted automatically. The live test therefore reuses ONE dedicated repository that a
/// person created, trusted in Claude Code, and marked with <see cref="MarkerFileName"/>. The marker is that person's explicit
/// opt-in that the directory is disposable test infrastructure: this class never creates it.
/// <para>
/// Everything destructive (<see cref="ResetToBaseline"/>) requires a passing <see cref="FixtureGuard"/>, only ever deletes
/// direct children of the canonical root (never the root, never <c>.git</c>, never the marker) and never follows links.
/// </para>
/// </summary>
internal static class TrustedLiveFixture
{
    public const string WorkspaceVariable = "AGENTICWORKFLOW_LIVE_DIRECT_WORKSPACE";

    public const string MarkerFileName = ".agenticworkflow-live-fixture";

    /// <summary>The marker's exact content. Only trailing line terminators are tolerated (editors and Git add them).</summary>
    public const string MarkerText = "AgenticWorkflow dedicated LiveDirect fixture";

    private const string BaselineMessage = "LiveDirect fixture baseline";

    /// <summary>Extra protected locations, separated like <c>PATH</c> entries (for example the directory that holds your real work).</summary>
    public const string ProtectedLocationsVariable = "AGENTICWORKFLOW_LIVE_PROTECTED_LOCATIONS";

    /// <summary>The repository these tests run from: the nearest parent of the test output that holds the solution file, or null.</summary>
    public static string? SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        return directory?.FullName;
    }

    public static IReadOnlyList<string> DefaultForbiddenLocations()
    {
        var forbidden = new List<string>();

        if (SolutionRoot() is { } root)
            forbidden.Add(root);

        if (Environment.GetEnvironmentVariable(ProtectedLocationsVariable) is { Length: > 0 } extra)
            forbidden.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return forbidden;
    }

    /// <summary>
    /// Checks every precondition for modifying the fixture. Any problem means: do not delete or modify anything.
    /// </summary>
    public static FixtureGuard Evaluate(string? configured, IReadOnlyList<string>? forbiddenLocations = null, string? homeDirectory = null)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(configured))
        {
            problems.Add(
                $"{WorkspaceVariable} is not set. The live direct-path test needs a manually created and manually trusted dedicated Git repository "
                + "(one that contains the marker file and that you have opened once in Claude Code and trusted). It will not create one: "
                + "Claude Code cannot be made to trust a new repository automatically.");

            return new FixtureGuard(null, problems);
        }

        string canonical;

        try
        {
            canonical = Canonicalize(configured);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add($"{WorkspaceVariable} is not a valid path: {exception.Message}");
            return new FixtureGuard(null, problems);
        }

        var forbidden = (forbiddenLocations ?? DefaultForbiddenLocations()).Select(Canonicalize).ToList();
        var home = Canonicalize(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        if (Path.GetPathRoot(canonical) is { Length: > 0 } driveRoot && string.Equals(Canonicalize(driveRoot), canonical, StringComparison.OrdinalIgnoreCase))
            problems.Add($"'{canonical}' is a drive root.");

        if (IsSameOrInside(home, canonical))
            problems.Add($"'{canonical}' is the user's home directory or one of its parents.");

        foreach (var location in forbidden)
        {
            if (IsSameOrInside(location, canonical))
                problems.Add($"'{canonical}' is, or contains, the protected location '{location}'.");
            else if (IsSameOrInside(canonical, location))
                problems.Add($"'{canonical}' is inside the protected location '{location}'.");
        }

        if (!Directory.Exists(canonical))
        {
            problems.Add($"'{canonical}' does not exist or is not a directory.");
            return new FixtureGuard(canonical, problems);
        }

        var marker = Path.Combine(canonical, MarkerFileName);

        if (!File.Exists(marker))
            problems.Add($"'{canonical}' does not contain the marker file '{MarkerFileName}'. It is not created automatically: adding it is your explicit opt-in that the repository is disposable.");
        else if (File.ReadAllText(marker).TrimEnd('\r', '\n') != MarkerText)
            problems.Add($"The marker file '{marker}' does not contain exactly '{MarkerText}'.");

        var dotGit = Path.Combine(canonical, ".git");

        if (!Directory.Exists(dotGit))
            problems.Add($"'{canonical}' has no '.git' directory, so it is not the root of a Git repository.");
        else
        {
            var (exit, output, _) = DirectFixtureRepo.RunGit(canonical, "rev-parse", "--show-toplevel");

            if (exit != 0 || !string.Equals(Canonicalize(output.Trim()), canonical, StringComparison.OrdinalIgnoreCase))
                problems.Add($"'{canonical}' is not the root of a Git repository (git reports '{output.Trim()}').");

            var (remoteExit, remotes, _) = DirectFixtureRepo.RunGit(canonical, "remote");

            if (remoteExit == 0 && !string.IsNullOrWhiteSpace(remotes))
                problems.Add($"The fixture repository has a remote configured ({remotes.Trim().ReplaceLineEndings(", ")}); a disposable fixture must have none.");
        }

        return new FixtureGuard(canonical, problems);
    }

    /// <summary>
    /// Rebuilds the deterministic baseline inside the guarded repository and commits it when it differs from HEAD, so the
    /// working tree is clean and HEAD is the baseline. The repository root, its <c>.git</c> directory (and therefore its
    /// identity and Claude's trust) and the marker are preserved.
    /// </summary>
    public static DirectFixtureRepo ResetToBaseline(FixtureGuard guard)
    {
        if (!guard.IsSafe)
            throw new InvalidOperationException("Refusing to modify the fixture: " + Environment.NewLine + guard.Describe());

        var root = guard.CanonicalPath!;
        DeleteContentsExceptGitAndMarker(root);

        var repo = DirectFixtureRepo.Open(root);
        repo.WriteBaseline(DirectFixtureRepo.DotNetMealsFiles);
        repo.ConfigureLocally();
        repo.Git("add", "-A");

        var hasHead = DirectFixtureRepo.RunGit(root, "rev-parse", "--verify", "-q", "HEAD").ExitCode == 0;
        var differs = !hasHead || DirectFixtureRepo.RunGit(root, "diff", "--cached", "--quiet", "HEAD").ExitCode != 0;

        if (differs)
            repo.Git("commit", "-q", "-m", BaselineMessage);

        var status = repo.Git("status", "--porcelain=v1", "--ignored=no");

        return string.IsNullOrWhiteSpace(status)
            ? repo
            : throw new InvalidOperationException("The fixture is not clean after resetting to the baseline:" + Environment.NewLine + status);
    }

    private static void DeleteContentsExceptGitAndMarker(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name, MarkerFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            // A link is removed as a link; its target is never entered.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo linkedDirectory)
                    linkedDirectory.Delete(recursive: false);
                else
                    entry.Delete();

                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                ClearReadOnly(directory);
                directory.Delete(recursive: true);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo directory)
    {
        foreach (var info in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                info.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // "C:" would be drive-relative; a drive root keeps its separator.
        return trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    /// <summary>True when <paramref name="inner"/> equals <paramref name="outer"/> or lies beneath it.</summary>
    private static bool IsSameOrInside(string inner, string outer)
    {
        if (string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
