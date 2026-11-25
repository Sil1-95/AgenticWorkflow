namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>Runs the diagnosis: every check, in a fixed order, so the report reads the same on every machine. A check that throws is a failed check, never a crash.</summary>
internal static class Doctor
{
    private static readonly IReadOnlyList<IDoctorCheck> Checks =
    [
        new PlatformCheck(),
        new InstallationCheck(),
        new StateRootCheck(),
        new ControllerCheck(),
        new ToolsCheck(),
        new CockpitCheck(),
        new ProjectCheck(),
    ];

    public static async Task<DoctorReport> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var results = new List<DoctorCheck>();

        foreach (var check in Checks)
        {
            try
            {
                results.AddRange(await check.RunAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                results.Add(DoctorCheck.Fail(check.GetType().Name.Replace("Check", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), $"the check itself failed: {e.GetType().Name}: {e.Message}"));
            }
        }

        return new DoctorReport(context.Version, results);
    }
}
