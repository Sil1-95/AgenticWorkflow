using System.Security.Cryptography;
using System.Text;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Execution.Local;

/// <summary>
/// Captures the exact working-tree state of a Git repository, read-only. The fingerprint is a SHA-256 over:
/// <list type="bullet">
/// <item>the commit <c>HEAD</c> points at,</item>
/// <item>every path whose working-tree content differs from HEAD (staged or not, tracked), and</item>
/// <item>every untracked, non-ignored file,</item>
/// </list>
/// each with a SHA-256 of its <em>content</em> (or <c>deleted</c>). Ignored files (build output, caches) are
/// deliberately excluded: they are not part of the implementation. File times, the index state and permission bits
/// are not read, so touching a file without changing it does not change the fingerprint, and changing a single byte
/// of any included file does.
/// <para>
/// Read-only: only <c>rev-parse</c>, <c>diff</c> and <c>ls-files</c> run, with <c>--no-optional-locks</c> and
/// <c>GIT_OPTIONAL_LOCKS=0</c>, so nothing is written to <c>.git</c> (no index refresh, no lock file). Never calls
/// <c>git add</c>, <c>stash</c>, <c>checkout</c>, <c>reset</c> or <c>commit</c>.
/// </para>
/// </summary>
public sealed class GitImplementationStateProvider : IImplementationStateProvider
{
    public const string UnbornHead = "(unborn)";
    public const int MaxChangedPaths = 5000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private const int MaxGitOutputChars = 8_000_000;

    private readonly string _git;

    public GitImplementationStateProvider(string gitExecutable = "git")
    {
        if (string.IsNullOrWhiteSpace(gitExecutable))
            throw new ArgumentException("A git executable is required.", nameof(gitExecutable));

        _git = gitExecutable;
    }

    public async Task<ImplementationState> CaptureAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Workspace '{root}' does not exist.");

        await EnsureRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false);

        var head = await ReadHeadAsync(root, cancellationToken).ConfigureAwait(false);

        var changed = new SortedSet<string>(StringComparer.Ordinal);

        if (head != UnbornHead)
        {
            foreach (var path in await ListAsync(root, ["diff", "HEAD", "--name-only", "-z", "--no-renames", "--no-ext-diff"], cancellationToken).ConfigureAwait(false))
                changed.Add(path);
        }
        else
        {
            // No commit yet: every tracked (i.e. staged) file counts as changed.
            foreach (var path in await ListAsync(root, ["ls-files", "-z"], cancellationToken).ConfigureAwait(false))
                changed.Add(path);
        }

        foreach (var path in await ListAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken).ConfigureAwait(false))
            changed.Add(path);

        if (changed.Count > MaxChangedPaths)
            throw new InvalidOperationException($"The workspace has {changed.Count} changed paths; more than {MaxChangedPaths} is not a lightweight direct task.");

        var digests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in changed)
            digests[path] = await DigestAsync(root, path, cancellationToken).ConfigureAwait(false);

        return new ImplementationState(
            new ImplementationFingerprint(Fingerprint(head, digests)),
            head,
            digests);
    }

    internal static string Fingerprint(string head, IReadOnlyDictionary<string, string> digests)
    {
        var canonical = new StringBuilder("agenticworkflow/git-worktree/v1\n");
        canonical.Append("head=").Append(head).Append('\n');

        foreach (var (path, digest) in digests.OrderBy(d => d.Key, StringComparer.Ordinal))
            canonical.Append(path).Append('\0').Append(digest).Append('\n');

        return "git-worktree-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private async Task EnsureRepositoryRootAsync(string root, CancellationToken cancellationToken)
    {
        var top = (await RunGitAsync(root, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false)).Trim();

        if (!SamePath(top, root))
            throw new InvalidOperationException($"Workspace '{root}' must be the root of a Git repository (the root is '{top}').");
    }

    private async Task<string> ReadHeadAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 ? result.StdOut.Trim() : UnbornHead;
    }

    private async Task<IReadOnlyList<string>> ListAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(root, arguments, cancellationToken).ConfigureAwait(false);

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<string> RunGitAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(root, arguments, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed (exit {result.ExitCode?.ToString() ?? "none"}): {FirstLine(result.StdErr)}");

        return result.StdOut;
    }

    private async Task<ProcessResult> RunRawAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessExecutor.RunAsync(
            new ProcessSpec(
                _git,
                ["--no-optional-locks", .. arguments],
                root,
                GitTimeout,
                GitEnvironment,
                MaxGitOutputChars),
            cancellationToken).ConfigureAwait(false);

        if (result.StartError is not null)
            throw new InvalidOperationException($"Could not run git: {result.StartError}");

        if (result.TimedOut)
            throw new InvalidOperationException($"git {arguments[0]} timed out after {GitTimeout}.");

        if (result.OutputTruncated)
            throw new InvalidOperationException($"git {arguments[0]} produced more output than the capture limit.");

        return result;
    }

    internal static IReadOnlyDictionary<string, string?> GitEnvironment { get; } = new Dictionary<string, string?>
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_DIR"] = null,
        ["GIT_WORK_TREE"] = null,
        ["GIT_INDEX_FILE"] = null,
    };

    private static async Task<string> DigestAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));

        // A path from git must stay inside the workspace; never read outside it.
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"git reported a path outside the workspace: {relativePath}");

        var info = new FileInfo(full);

        if (info.LinkTarget is not null)
            return "link:" + info.LinkTarget;

        if (Directory.Exists(full))
            return "directory"; // a submodule or similar; its own content is not part of this repository's state

        if (!info.Exists)
            return "deleted";

        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        return "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('/', '\\'),
            Path.GetFullPath(b).TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
        => text.Split('\n', 2)[0].Trim();
}
