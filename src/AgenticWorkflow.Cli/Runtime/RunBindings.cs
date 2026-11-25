using System.Diagnostics;
using System.Text.RegularExpressions;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Runtime;

/// <summary>One role's binding as the run recorded it, in the form a runtime launches from: the profile and exactly the execution configuration recorded with it.</summary>
/// <param name="IsLegacy">True for a binding recorded before execution configuration existed: it has none recorded, and is launched as it always was (the built-in profile, no model, no reasoning).</param>
internal sealed record LaunchBinding(RoleId Role, ProfileId Profile, ExecutionConfiguration Execution, bool IsLegacy);

/// <summary>
/// The bindings a run launches from, read from the run's own history, never from the project's configuration: a later change to a profile (or its removal) does not reach a run that
/// already recorded its bindings. A runtime factory is given these and nothing else about which agent to start.
/// </summary>
internal sealed class RunBindings
{
    private readonly IReadOnlyList<LaunchBinding> _bindings;

    public RunBindings(IReadOnlyList<LaunchBinding> bindings) => _bindings = bindings;

    public IReadOnlyList<LaunchBinding> All => _bindings;

    public LaunchBinding? Find(RoleId role) => _bindings.FirstOrDefault(b => b.Role == role);

    /// <summary>
    /// The run's recorded bindings. A binding with no recorded execution configuration is a legacy one and is understood only for the one profile that existed then; any other is
    /// refused rather than guessed.
    /// </summary>
    public static RunBindings From(WorkflowTask task)
    {
        var bindings = new List<LaunchBinding>();

        foreach (var binding in task.RoleBindings.Values.OrderBy(b => b.Role.Id.Value, StringComparer.Ordinal))
        {
            if (binding.Execution is { } execution)
            {
                bindings.Add(new LaunchBinding(binding.Role.Id, binding.Profile.Id, execution, IsLegacy: false));
            }
            else if (binding.Profile.Id == AgentProfileEntry.ClaudeCodeLocal.Id)
            {
                bindings.Add(new LaunchBinding(binding.Role.Id, binding.Profile.Id, AgentProfileEntry.ClaudeCodeLocal.ToExecution(), IsLegacy: true));
            }
            else
            {
                throw new ConfigurationException(
                    $"The run's '{binding.Role.Id}' role is bound to profile '{binding.Profile.Id}', which has no recorded execution configuration and is not a profile this build knows how to launch without one.");
            }
        }

        return new RunBindings(bindings);
    }
}

/// <summary>
/// What the installed Claude Code says it accepts, read from its own <c>--help</c>: whether it has a model option and which reasoning ("effort") levels it lists. Nothing is assumed
/// beyond what the program reports, so a build that lacks an option, or drops a level, refuses a profile that asks for it instead of passing a flag it would not understand.
/// </summary>
internal sealed partial class ClaudeCliCapabilities(bool supportsModel, IReadOnlyList<string>? effortLevels)
{
    public bool SupportsModel { get; } = supportsModel;

    /// <summary>The levels <c>--effort</c> accepts; null when the program has no such option.</summary>
    public IReadOnlyList<string>? EffortLevels { get; } = effortLevels;

    public static ClaudeCliCapabilities Parse(string help)
    {
        ArgumentNullException.ThrowIfNull(help);

        var supportsModel = ModelOption().IsMatch(help);
        IReadOnlyList<string>? levels = null;

        if (EffortOption().Match(help) is { Success: true } effort)
        {
            // The levels are the parenthesised list that follows the option's name, before the next option begins.
            var description = help[effort.Index..];
            var next = NextOption().Match(description, effort.Length);
            var section = next.Success ? description[..next.Index] : description;

            if (Levels().Match(section) is { Success: true } list)
                levels = [.. list.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }

        return new ClaudeCliCapabilities(supportsModel, levels);
    }

    /// <summary>Asks the program at <paramref name="executable"/> for its help, without a shell and with a time limit. Null when it could not be asked.</summary>
    public static ClaudeCliCapabilities? Probe(string executable, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--help");

            using var process = Process.Start(start);

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);

                return null;
            }

            return process.ExitCode == 0 ? Parse(output.GetAwaiter().GetResult()) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s+(?:-\w,\s+)?--model\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ModelOption();

    [GeneratedRegex(@"^\s+(?:-\w,\s+)?--effort\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EffortOption();

    [GeneratedRegex(@"^\s+(?:-\w,\s+)?--[a-z]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NextOption();

    [GeneratedRegex(@"\(([A-Za-z0-9_.\- ,]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Levels();
}
