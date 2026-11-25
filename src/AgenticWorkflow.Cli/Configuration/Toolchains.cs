using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// A built-in composition preset: which commands a native agent may run without asking and a line of operating guidance for it.
/// It is a host convenience, not a workflow concept: it never selects the agent profile, it is never detected, and it grants nothing
/// that <see cref="ClaudeCodeToolPolicy.BaselineDeniedTools"/> refuses (a deny rule always wins).
/// </summary>
internal sealed record Toolchain(
    string Id,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AdditionalDeniedTools,
    string OperatingNotes)
{
    private const string CommonNotes =
        "Tooling: you may only run commands that are pre-approved, one plain command per call. Search with `git grep` (not grep/rg/find). "
        + "Do not chain commands with `;`, `&&` or `|`, and do not redirect output: anything else needs an approval nobody can give and will stop the task. ";

    public static Toolchain Dotnet { get; } = new(
        "dotnet",
        [.. ClaudeCodeToolPolicy.DotnetTools, .. ClaudeCodeToolPolicy.ReadOnlyGitTools],
        [],
        CommonNotes + "Run `dotnet build` or `dotnet test` on its own, naming the project or solution.");

    public static Toolchain Flutter { get; } = new(
        "flutter",
        ["Bash(flutter analyze:*)", "Bash(flutter test:*)", .. ClaudeCodeToolPolicy.ReadOnlyGitTools],
        ["Bash(flutter pub:*)", "Bash(dart pub:*)"],
        CommonNotes + "Run `flutter analyze` and `flutter test --no-pub` each on its own. Do not run `flutter pub`: dependencies are not changed by this task.");

    /// <summary>
    /// For an environment AgenticWorkflow has no built-in knowledge of (a game engine, a polyglot repository, an unusual build system): no build or test command is pre-approved and
    /// none is guessed. What the project verifies, and which commands the agent may run, is exactly what its configuration lists under <c>verification</c> and <c>allowCommands</c>.
    /// Only the read-only Git inspection every toolchain has, and the note on how pre-approval works, remain.
    /// </summary>
    public static Toolchain Custom { get; } = new(
        "custom",
        [.. ClaudeCodeToolPolicy.ReadOnlyGitTools],
        [],
        CommonNotes.TrimEnd());

    public static IReadOnlyList<Toolchain> All { get; } = [Dotnet, Flutter, Custom];

    public static Toolchain? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public static string KnownIds => string.Join(", ", All.Select(t => $"'{t.Id}'"));
}
