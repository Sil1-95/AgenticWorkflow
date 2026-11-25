using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// The one place that says how a person attaches to the agent's own (Herdr) session, shared by the run console notice, the desktop notification and
/// <c>aw status</c>. It only formats a pointer: the identifiers are validated first, and one that is not a plain token yields no command at all rather than
/// text that could be pasted into a shell unsafely. Attaching and answering is Herdr's and the agent's; nothing here can answer for anyone.
/// </summary>
internal static class NativeAttachGuidance
{
    public const int MaxIdentifierLength = 128;

    /// <summary>Only <c>[A-Za-z0-9._-]</c>, starting with a letter or digit (so it can never read as an option), and bounded.</summary>
    public static bool IsSafeIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
           && value.Length <= MaxIdentifierLength
           && char.IsAsciiLetterOrDigit(value[0])
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool IsUsable(string? hostSession, string? terminal)
        => IsSafeIdentifier(hostSession) && IsSafeIdentifier(terminal);

    /// <summary>The PowerShell command pair, or null when an identifier is not safe to print.</summary>
    public static string? PowerShell(string? hostSession, string? terminal)
        => IsUsable(hostSession, terminal) ? $"$env:HC_SESSION = \"{hostSession}\"; hc attach {terminal}" : null;

    /// <summary>The Git Bash command pair, or null when an identifier is not safe to print.</summary>
    public static string? GitBash(string? hostSession, string? terminal)
        => IsUsable(hostSession, terminal) ? $"export HC_SESSION={hostSession}; hc attach {terminal}" : null;

    /// <summary>The labelled, shell-specific attach lines. Empty (never partial) when the target is unknown or an identifier is unsafe.</summary>
    public static IReadOnlyList<string> Lines(string? hostSession, string? terminal)
        => PowerShell(hostSession, terminal) is { } powerShell && GitBash(hostSession, terminal) is { } gitBash
            ? [$"PowerShell:   {powerShell}", $"Git Bash:     {gitBash}"]
            : [];

    public static IReadOnlyList<string> Lines(NativeAttachTarget? target) => Lines(target?.HostSession, target?.Terminal);
}
