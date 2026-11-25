using System.Security.Cryptography;
using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Execution.Local.Tests;

public sealed class GitImplementationStateProviderTests : IDisposable
{
    private readonly TempRepo _repo = TempRepo.Create();
    private readonly GitImplementationStateProvider _provider = new();

    public void Dispose() => _repo.Dispose();

    private Task<ImplementationState> CaptureAsync() => _provider.CaptureAsync(_repo.Root);

    [Fact]
    public async Task A_clean_repository_has_a_stable_fingerprint_and_no_changed_paths()
    {
        var first = await CaptureAsync();
        var second = await CaptureAsync();

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Empty(first.PathDigests);
        Assert.Equal(_repo.Git("rev-parse", "HEAD").Trim(), first.BaseIdentity);
    }

    [Fact]
    public async Task Editing_a_tracked_file_changes_the_fingerprint_and_lists_the_path()
    {
        var before = await CaptureAsync();

        _repo.Write("src/Greeting.cs", "public string GetFood() => \"hello\";\n");
        var after = await CaptureAsync();

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(["src/Greeting.cs"], after.PathDigests.Keys.ToArray());
    }

    [Fact]
    public async Task One_byte_of_difference_changes_the_fingerprint()
    {
        _repo.Write("src/Greeting.cs", "a");
        var a = await CaptureAsync();
        _repo.Write("src/Greeting.cs", "b");
        var b = await CaptureAsync();

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public async Task Reverting_the_edit_returns_to_the_original_fingerprint()
    {
        var original = await CaptureAsync();
        var content = _repo.Read("src/Greeting.cs");

        _repo.Write("src/Greeting.cs", "changed");
        _repo.Write("src/Greeting.cs", content);

        Assert.Equal(original.Fingerprint, (await CaptureAsync()).Fingerprint);
    }

    [Fact]
    public async Task Rewriting_identical_content_does_not_change_the_fingerprint()
    {
        _repo.Write("src/Greeting.cs", "edited");
        var before = await CaptureAsync();

        _repo.Write("src/Greeting.cs", "edited");
        File.SetLastWriteTimeUtc(_repo.PathOf("src/Greeting.cs"), DateTime.UtcNow.AddHours(3));

        Assert.Equal(before.Fingerprint, (await CaptureAsync()).Fingerprint);
    }

    [Fact]
    public async Task A_new_untracked_file_is_part_of_the_implementation_and_its_content_matters()
    {
        var before = await CaptureAsync();

        _repo.Write("src/New.cs", "one");
        var one = await CaptureAsync();
        _repo.Write("src/New.cs", "two");
        var two = await CaptureAsync();

        Assert.Contains("src/New.cs", one.PathDigests.Keys);
        Assert.NotEqual(before.Fingerprint, one.Fingerprint);
        Assert.NotEqual(one.Fingerprint, two.Fingerprint);
    }

    [Fact]
    public async Task Ignored_files_are_excluded()
    {
        var before = await CaptureAsync();

        _repo.Write("bin/Debug/out.dll", "binary");
        _repo.Write("obj/project.assets.json", "{}");
        var after = await CaptureAsync();

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Empty(after.PathDigests);
    }

    [Fact]
    public async Task A_deleted_tracked_file_is_a_change()
    {
        var before = await CaptureAsync();

        File.Delete(_repo.PathOf("README.md"));
        var after = await CaptureAsync();

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal("deleted", after.PathDigests["README.md"]);
    }

    [Fact]
    public async Task Staged_and_unstaged_edits_are_both_seen_relative_to_head()
    {
        _repo.Write("README.md", "staged edit\n");
        _repo.Git("add", "README.md");
        _repo.Write("src/Greeting.cs", "unstaged edit\n");

        var state = await CaptureAsync();

        Assert.Equal(["README.md", "src/Greeting.cs"], state.PathDigests.Keys.ToArray());
    }

    [Fact]
    public async Task Committing_the_same_content_changes_the_fingerprint_because_head_moved()
    {
        _repo.Write("README.md", "edit\n");
        var edited = await CaptureAsync();

        _repo.Git("commit", "-q", "-am", "commit the edit");
        var committed = await CaptureAsync();

        Assert.NotEqual(edited.Fingerprint, committed.Fingerprint);
        Assert.NotEqual(edited.BaseIdentity, committed.BaseIdentity);
        Assert.Empty(committed.PathDigests);
    }

    [Fact]
    public async Task Paths_with_spaces_and_unicode_round_trip()
    {
        _repo.Write("src/with space/été.cs", "x");

        var state = await CaptureAsync();

        Assert.Contains("src/with space/été.cs", state.PathDigests.Keys);
    }

    [Fact]
    public async Task Capturing_is_read_only_the_git_directory_is_untouched()
    {
        _repo.Write("src/Greeting.cs", "edited");
        _repo.Write("src/New.cs", "new");
        var before = Snapshot();

        await CaptureAsync();
        await CaptureAsync();

        Assert.Equal(before, Snapshot());
        Assert.False(File.Exists(Path.Combine(_repo.Root, ".git", "index.lock")));
    }

    [Fact]
    public async Task An_unborn_repository_is_captured_too()
    {
        using var empty = TempEmpty();
        File.WriteAllText(Path.Combine(empty, "a.txt"), "x");

        var state = await _provider.CaptureAsync(empty);

        Assert.Equal(GitImplementationStateProvider.UnbornHead, state.BaseIdentity);
        Assert.Contains("a.txt", state.PathDigests.Keys);
    }

    [Fact]
    public async Task A_subdirectory_of_a_repository_is_refused_so_paths_stay_unambiguous()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.CaptureAsync(_repo.PathOf("src")));

        Assert.Contains("root of a Git repository", error.Message);
    }

    [Fact]
    public async Task A_missing_directory_is_refused()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _provider.CaptureAsync(_repo.PathOf("nope")));
    }

    [Fact]
    public async Task A_missing_git_executable_is_a_clear_failure()
    {
        var provider = new GitImplementationStateProvider("git-that-does-not-exist-xyz");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CaptureAsync(_repo.Root));

        Assert.Contains("Could not run git", error.Message);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Every file under .git with a content hash: any write by a "read-only" capture would show up.</summary>
    private string Snapshot()
        => string.Join(
            "\n",
            Directory.EnumerateFiles(Path.Combine(_repo.Root, ".git"), "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(f => $"{f}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    private static TempDir TempEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenticworkflow-exec-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);

        var git = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "init", "-q" })
            git.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(git)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }

        return new TempDir(path);
    }

    private sealed class TempDir(string path) : IDisposable
    {
        public static implicit operator string(TempDir dir) => dir.Path;

        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
