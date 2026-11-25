using AgenticWorkflow.Cli.Runtime;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>
/// The programs AgenticWorkflow drives: Git and Herdr's <c>hc</c> are required; Claude Code and Pi are each optional, but at least one must be there, since a task needs an agent.
/// Each is only asked for its version: nothing is started that talks to a model.
/// </summary>
internal sealed class ToolsCheck : IDoctorCheck
{
    /// <summary>The oldest Herdr core known to speak the protocol AgenticWorkflow uses; the release is tested with a newer one.</summary>
    private static readonly Version MinimumHerdr = new(0, 1, 19);

    private const string TestedHerdr = "0.1.22";

    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var git = Required(context, "git", "git", ["--version"], "Git is needed to fingerprint the working tree. Install Git for Windows.");
        var herdr = CheckHerdr(context);
        var claude = Optional(context, "claude", ["--version"], "needed only for profiles that use claude-code");
        var pi = CheckPi(context);

        var checks = new List<DoctorCheck> { git, herdr, claude, pi };

        if (claude.Status != DoctorStatus.Pass && pi.Status != DoctorStatus.Pass)
            checks.Add(DoctorCheck.Fail("agent", "neither Claude Code nor Pi was found", "Install at least one agent harness; a task cannot run without one."));

        return Task.FromResult<IReadOnlyList<DoctorCheck>>(checks);
    }

    private static DoctorCheck Required(DoctorContext context, string name, string program, string[] arguments, string hint)
    {
        if (context.Locator.Find(program) is not { } path)
            return DoctorCheck.Fail(name, $"'{program}' was not found on PATH", hint);

        return context.Tools.FirstLine([path, .. arguments], DoctorContext.ProbeTimeout) is { } version
            ? DoctorCheck.Pass(name, $"{version} ({path})")
            : DoctorCheck.Fail(name, $"'{path}' did not report its version", hint);
    }

    private static DoctorCheck Optional(DoctorContext context, string program, string[] arguments, string why)
    {
        if (context.Locator.Find(program) is not { } path)
            return DoctorCheck.Warn(program, $"not found on PATH ({why})");

        return context.Tools.FirstLine([path, .. arguments], DoctorContext.ProbeTimeout) is { } version
            ? DoctorCheck.Pass(program, $"{version} ({path})")
            : DoctorCheck.Warn(program, $"'{path}' did not report its version", "Run it once in a terminal to see what it needs (a login, an update).");
    }

    private static DoctorCheck CheckHerdr(DoctorContext context)
    {
        const string hint = "Install Herdr and make sure 'hc' is on PATH.";

        if (context.Locator.Find("hc") is not { } path)
            return DoctorCheck.Fail("herdr (hc)", "'hc' was not found on PATH", hint);

        var line = context.Tools.FirstLine([path, "--version"], DoctorContext.ProbeTimeout);

        if (line is null)
            return DoctorCheck.Fail("herdr (hc)", $"'{path}' did not report its version", hint);

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 || parts[0] != "hc" || !Version.TryParse(parts[1], out var version))
            return DoctorCheck.Warn("herdr (hc)", $"unrecognized version output '{line}' ({path})", $"AgenticWorkflow is tested with Herdr {TestedHerdr}.");

        return version < MinimumHerdr
            ? DoctorCheck.Warn("herdr (hc)", $"{line} ({path})", $"This is older than the versions AgenticWorkflow is known to work with ({MinimumHerdr} and newer; tested with {TestedHerdr}). Update Herdr.")
            : DoctorCheck.Pass("herdr (hc)", $"{line} ({path})");
    }

    private static DoctorCheck CheckPi(DoctorContext context)
    {
        if (context.Locator.Find("pi") is null)
            return DoctorCheck.Warn("pi", "not found on PATH (needed only for profiles that use pi)");

        if (PiProgram.Resolve(context.Locator, out var problem) is not { } program)
            return DoctorCheck.Warn("pi", $"found, but it cannot be launched directly: {problem}", "AgenticWorkflow starts Pi without a command shell; see docs/pi.md.");

        return context.Tools.FirstLine([.. program, "--version"], DoctorContext.ProbeTimeout) is { } version
            ? DoctorCheck.Pass("pi", $"{version} ({program[^1]})")
            : DoctorCheck.Warn("pi", $"'{program[^1]}' did not report its version", "Run it once in a terminal to see what it needs.");
    }
}
