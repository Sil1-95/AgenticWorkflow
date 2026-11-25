using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Execution.Local;

/// <summary>
/// Runs one configured check as a child process in the workspace: arguments are an argument list (never a shell
/// string), the working directory is confined to the workspace, there is a hard timeout (the process tree is killed)
/// and output is bounded. It reports how the process ended; whether that counts as a pass is the caller's rule
/// (expected exit code), and the result is never taken from what the agent says.
/// <para>
/// The environment is the controller's own with two changes: <c>CLAUDE*</c> variables are removed (a check never needs
/// an agent's session identity) and .NET build servers are disabled so a finished <c>dotnet</c> command leaves no
/// background process holding the output pipes open.
/// </para>
/// </summary>
public sealed class LocalVerificationRunner : IVerificationRunner
{
    private const int MaxTailChars = 4000;

    private static readonly IReadOnlyDictionary<string, string?> BuildQuietEnvironment = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["UseSharedCompilation"] = "false",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    public async Task<CheckExecution> RunAsync(string workspacePath, VerificationCheckSpec check, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);

        var root = Path.GetFullPath(workspacePath);
        var directory = ResolveWorkingDirectory(root, check.WorkingDirectory);

        if (directory is null)
            return new CheckExecution(null, false, TimeSpan.Zero, string.Empty, string.Empty, $"working directory '{check.WorkingDirectory}' is not inside the workspace or does not exist");

        var result = await ProcessExecutor.RunAsync(
            new ProcessSpec(check.Executable, check.Arguments, directory, check.Timeout, Environment(), MaxTailChars),
            cancellationToken).ConfigureAwait(false);

        return new CheckExecution(
            result.ExitCode,
            result.TimedOut,
            result.Duration,
            result.StdOut,
            result.StdErr,
            result.StartError);
    }

    internal static string? ResolveWorkingDirectory(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return Directory.Exists(root) ? root : null;

        if (Path.IsPathRooted(relative))
            return null;

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full) ? full : null;
    }

    internal static IReadOnlyDictionary<string, string?> Environment()
    {
        var overrides = new Dictionary<string, string?>(BuildQuietEnvironment);

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
                overrides[name] = null;
        }

        return overrides;
    }
}
