namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>That the folder AgenticWorkflow keeps its runs in can be written. The folder is created if it is not there yet, as the first run would.</summary>
internal sealed class StateRootCheck : IDoctorCheck
{
    public Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        DoctorCheck result;

        try
        {
            Directory.CreateDirectory(context.StateRoot);

            var probe = Path.Combine(context.StateRoot, $".doctor-{Guid.NewGuid():N}.tmp");

            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            result = DoctorCheck.Pass("state", $"{context.StateRoot} is writable");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result = DoctorCheck.Fail("state", $"{context.StateRoot} cannot be written: {e.Message}", "Run history and tasks are stored there. Check the folder's permissions, or set AGENTICWORKFLOW_HOME to another absolute path.");
        }

        return Task.FromResult<IReadOnlyList<DoctorCheck>>([result]);
    }
}

/// <summary>
/// That a controller can be reached (starting one is the normal path, so the doctor does too), speaks this build's version and can own agent work durably. Reports counts only: no task,
/// path or conversation.
/// </summary>
internal sealed class ControllerCheck : IDoctorCheck
{
    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var observed = await context.ObserveController(cancellationToken).ConfigureAwait(false);

        if (!observed.Reachable)
            return [DoctorCheck.Fail("controller", observed.Problem ?? "no controller could be reached or started", "See controller\\controller.log in the state folder. 'aw ctl stop' then retry.")];

        var controller = observed.Controller;
        var runs = observed.Supervision?.ActiveRuns ?? 0;
        var how = observed.StartedForCheck ? "started for this check (it stops by itself when idle)" : "already running";
        var summary = $"{how}; version {controller?.Version ?? "unknown"}, pid {controller?.ProcessId.ToString() ?? "?"}, {runs} active run{(runs == 1 ? string.Empty : "s")}";

        var checks = new List<DoctorCheck>();

        if (observed.Supervision is { Durable: false })
        {
            checks.Add(DoctorCheck.Fail("controller", $"{summary}; it would end together with the process that started it, so it refuses to own agent work", "Stop it ('aw ctl stop --force') and let the cockpit start a fresh one."));
        }
        else if (controller is not null && !string.Equals(controller.Version, context.Version, StringComparison.Ordinal))
        {
            checks.Add(DoctorCheck.Warn("controller", $"{summary}; this aw is {context.Version}", runs == 0 ? "A controller from another version is running. 'aw ctl stop' lets the next use start the right one." : "Finish or abort the active runs, then 'aw ctl stop'."));
        }
        else
        {
            checks.Add(DoctorCheck.Pass("controller", summary));
        }

        if (observed.Problem is not null)
            checks.Add(DoctorCheck.Warn("controller health", observed.Problem));

        return checks;
    }
}
