using System.Text;

namespace AgenticWorkflow.Execution.Local;

/// <summary>
/// Windows toolchains such as Flutter, npm and Gradle wrappers ship their entry point as <c>.bat</c>/<c>.cmd</c> files. <c>CreateProcess</c>
/// only ever appends <c>.exe</c> to a bare name, so <c>flutter</c> never finds <c>flutter.bat</c>; and given a script path it starts
/// <c>cmd.exe</c> implicitly with Win32 argument quoting that <c>cmd.exe</c> parses differently (the "BatBadBut" class of injection).
/// This type resolves such scripts and builds the one command line the controller is willing to hand to <c>cmd.exe</c>.
/// <para>
/// Rules: a native executable is always started directly and first. A script is involved only (a) when the executable itself names a
/// <c>.cmd</c>/<c>.bat</c>, or (b) as a fallback after the native start failed with "file not found" for a bare name, searched on
/// <c>PATH</c> only (never the repository) for <see cref="FallbackExtensions"/> and nothing else. Scripts are then run as
/// <c>cmd.exe /d /v:off /s /c ""script" args"</c> with every argument validated first: anything <c>cmd.exe</c> could interpret is
/// rejected, not escaped, because escaping that is correct for both <c>cmd.exe</c> and the script's own program is not provable.
/// </para>
/// </summary>
internal static class WindowsScripts
{
    /// <summary>Tried in this order, per PATH directory (the default PATHEXT order, minus <c>.exe</c> which the native start already covered).</summary>
    internal static readonly string[] FallbackExtensions = [".com", ".bat", ".cmd"];

    private static readonly string[] ScriptExtensions = [".bat", ".cmd"];

    /// <summary>Characters <c>cmd.exe</c> interprets even in a quoted argument (<c>%</c>, and <c>!</c> under delayed expansion) or that end a quote or a command.</summary>
    private static readonly char[] CmdMetacharacters = ['&', '|', '<', '>', '^', '%', '!', '"'];

    /// <summary>Arguments made only of these are passed bare; anything else is wrapped in quotes so <c>cmd.exe</c> keeps it as one token.</summary>
    private const string BareSafe = "-_./\\:+@~";

    /// <summary><c>cmd.exe</c> silently truncates a command line beyond 8191 characters; refuse long before that.</summary>
    private const int MaxCommandLineLength = 8000;

    /// <summary>True when the executable names a batch script. Trailing dots and spaces are ignored because Win32 ignores them too.</summary>
    public static bool IsBatchScript(string executable)
        => HasExtension(executable.TrimEnd(' ', '.'), ScriptExtensions);

    /// <summary>A bare command name: no directory, no drive, no extension (what <c>flutter</c> is; <c>flutter.bat</c> and <c>.\flutter</c> are not).</summary>
    public static bool IsBareName(string executable)
        => !string.IsNullOrWhiteSpace(executable)
           && executable.IndexOfAny(['\\', '/', ':']) < 0
           && !Path.HasExtension(executable);

    /// <summary>The first <c>name.com|.bat|.cmd</c> found on the effective PATH, or null.</summary>
    public static string? FindFallback(string bareName, IReadOnlyDictionary<string, string?>? environment)
    {
        foreach (var directory in PathEntries(environment))
        {
            foreach (var extension in FallbackExtensions)
            {
                var candidate = Path.Combine(directory, bareName + extension);

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The full path of the script an executable that names a <c>.cmd</c>/<c>.bat</c> refers to, or null when there is no such file.
    /// A path is resolved against the controller's current directory (as <c>CreateProcess</c> does), a bare file name against PATH only.
    /// </summary>
    public static string? ResolveScript(string executable, IReadOnlyDictionary<string, string?>? environment)
    {
        var name = executable.TrimEnd(' ', '.');

        try
        {
            if (name.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                var full = Path.GetFullPath(name);

                return File.Exists(full) ? full : null;
            }

            foreach (var directory in PathEntries(environment))
            {
                var candidate = Path.Combine(directory, name);

                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            // an unrepresentable path is a script that does not exist
        }

        return null;
    }

    /// <summary>The <c>cmd.exe</c> to use: the system's own, never one named by an environment variable.</summary>
    public static string CommandInterpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// The argument string for <see cref="CommandInterpreter"/> that runs <paramref name="scriptPath"/> with exactly
    /// <paramref name="arguments"/>. Throws <see cref="InvalidOperationException"/> instead of building anything <c>cmd.exe</c> could misread.
    /// </summary>
    public static string BuildCommandLine(string scriptPath, IReadOnlyList<string> arguments)
    {
        RejectIfUnsafe(scriptPath, $"the script path '{scriptPath}'");

        var inner = new StringBuilder().Append('"').Append(scriptPath).Append('"');

        foreach (var argument in arguments)
        {
            RejectIfUnsafe(argument, $"the argument '{argument}'");
            inner.Append(' ').Append(Quote(argument));
        }

        // /d: no AutoRun commands. /v:off: no delayed expansion whatever the registry says. /s: strip exactly the outer quote pair.
        var commandLine = $"/d /v:off /s /c \"{inner}\"";

        if (commandLine.Length > MaxCommandLineLength)
            throw new InvalidOperationException($"'{scriptPath}' is a batch script and its command line is longer than cmd.exe handles reliably; it was not started.");

        return commandLine;
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || BareSafe.Contains(c)))
            return argument;

        // A trailing backslash would escape the closing quote for the program the script forwards to (Win32 rules); not provably safe.
        if (argument.EndsWith('\\'))
            throw new InvalidOperationException($"the argument '{argument}' ends in a backslash and needs quoting for a batch script; it was not started.");

        return $"\"{argument}\"";
    }

    private static void RejectIfUnsafe(string value, string what)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c) || Array.IndexOf(CmdMetacharacters, c) >= 0)
            {
                throw new InvalidOperationException(
                    $"{what} contains a character ({Describe(c)}) that cmd.exe would interpret, and the executable is a batch script; it was not started.");
            }
        }
    }

    private static string Describe(char c) => char.IsControl(c) ? $"U+{(int)c:X4}" : $"'{c}'";

    private static bool HasExtension(string path, string[] extensions)
        => extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PathEntries(IReadOnlyDictionary<string, string?>? environment)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase))
                    path = value;
            }
        }

        return (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(entry));
    }
}
