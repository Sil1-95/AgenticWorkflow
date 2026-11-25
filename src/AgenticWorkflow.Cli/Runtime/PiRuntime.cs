using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgenticWorkflow.Cli.Runtime;

/// <summary>
/// What the installed Pi says it accepts, read from its own <c>--help</c> and <c>--list-models</c>: the options AgenticWorkflow launches it with, the reasoning ("thinking") levels, and the
/// provider-qualified models it lists (and whether each supports reasoning). Nothing is assumed beyond what the program reports, and Pi itself would only warn about a model or level it
/// cannot use and carry on with another, so every value is checked here, before a launch.
/// </summary>
internal sealed partial class PiCliCapabilities
{
    /// <summary>The options a Pi launch from AgenticWorkflow depends on. A Pi without any of them cannot be driven faithfully and is refused as a whole.</summary>
    public static IReadOnlyList<string> RequiredOptions { get; } = ["--model", "--thinking", "--session", "--session-id", "--extension", "--tools"];

    public PiCliCapabilities(IReadOnlyList<string> missingOptions, IReadOnlyList<string>? thinkingLevels, IReadOnlyDictionary<string, bool> models)
    {
        MissingOptions = missingOptions;
        ThinkingLevels = thinkingLevels;
        Models = models;
    }

    /// <summary>Required options the program does not list; empty when it can be launched as AgenticWorkflow needs.</summary>
    public IReadOnlyList<string> MissingOptions { get; }

    /// <summary>The levels <c>--thinking</c> accepts; null when the program does not say.</summary>
    public IReadOnlyList<string>? ThinkingLevels { get; }

    /// <summary>Every model <c>pi --list-models</c> shows, as <c>provider/id</c>, and whether it supports reasoning.</summary>
    public IReadOnlyDictionary<string, bool> Models { get; }

    public static PiCliCapabilities Parse(string help, string listedModels)
    {
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(listedModels);

        var missing = RequiredOptions.Where(option => !Regex.IsMatch(help, @"(^|[\s,])" + Regex.Escape(option) + @"(?=[\s<,]|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)).ToList();

        IReadOnlyList<string>? levels = null;

        if (ThinkingOption().Match(help) is { Success: true } thinking)
            levels = [.. thinking.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        var models = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var line in listedModels.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // provider  model  context  max-out  thinking  images
            if (columns.Length >= 5 && !string.Equals(columns[0], "provider", StringComparison.Ordinal))
                models[$"{columns[0]}/{columns[1]}"] = string.Equals(columns[4], "yes", StringComparison.OrdinalIgnoreCase);
        }

        return new PiCliCapabilities(missing, levels, models);
    }

    /// <summary>Asks the Pi at <paramref name="program"/> for its help and its models, without a shell and with a time limit. Null when it could not be asked.</summary>
    public static PiCliCapabilities? Probe(IReadOnlyList<string> program, TimeSpan timeout)
    {
        var help = Run(program, ["--help"], timeout);
        var models = help is null ? null : Run(program, ["--offline", "--list-models"], timeout);

        return help is null || models is null ? null : Parse(help, models);
    }

    private static string? Run(IReadOnlyList<string> program, IEnumerable<string> arguments, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(program[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };

            foreach (var argument in program.Skip(1).Concat(arguments))
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);

            if (process is null)
                return null;

            // Pi reads standard input when it is not a terminal; it is closed at once so the probe can never wait on it.
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);

                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"--thinking\s+<[^>]+>[^\r\n]*?:\s*([A-Za-z0-9_.\- ,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ThinkingOption();
}

/// <summary>
/// Finds the program that runs Pi, without a shell. On Windows Pi is installed as a <c>.cmd</c> shim, which would put every argument through <c>cmd.exe</c> quoting; the shim is read for the
/// script it runs and Pi is launched as <c>node &lt;that script&gt;</c> instead. Anything that cannot be resolved that way is not launched at all.
/// </summary>
internal static partial class PiProgram
{
    private const string Executable = "pi";
    private const string NodeExecutable = "node";

    /// <summary>The executable and its leading arguments, or null (with <paramref name="problem"/>) when Pi cannot be launched directly.</summary>
    public static IReadOnlyList<string>? Resolve(ExecutableLocator locator, out string? problem)
    {
        problem = null;

        if (locator.Find(Executable) is not { } found)
        {
            problem = "'pi' was not found on PATH";

            return null;
        }

        if (!found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !found.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return [found];

        try
        {
            var directory = Path.GetDirectoryName(found)!;

            if (Script().Match(File.ReadAllText(found)) is not { Success: true } match)
            {
                problem = $"'{found}' is a command shim whose script could not be determined, so Pi cannot be launched without a shell";

                return null;
            }

            var script = Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));

            if (!File.Exists(script))
            {
                problem = $"the script '{script}' that '{found}' runs does not exist";

                return null;
            }

            var bundledNode = Path.Combine(directory, "node.exe");
            var node = File.Exists(bundledNode) ? bundledNode : locator.Find(NodeExecutable);

            if (node is null)
            {
                problem = "'node' was not found on PATH, and Pi needs it";

                return null;
            }

            return [node, script];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = $"'{found}' could not be read: {e.Message}";

            return null;
        }
    }

    // The shim's last line ends with: "%dp0%\node_modules\<package>\dist\bundle\cli.js" %*
    [GeneratedRegex(@"""%dp0%\\(node_modules\\[^""]+?\.m?js)""\s+%\*", RegexOptions.CultureInvariant)]
    private static partial Regex Script();
}
