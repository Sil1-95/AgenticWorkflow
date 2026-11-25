namespace AgenticWorkflow.Cli;

/// <summary>Names of the environment variables the CLI reads. Product and test plumbing; none of them carries workflow meaning.</summary>
internal static class CliEnvironment
{
    /// <summary>Overrides the state root (default <c>%LOCALAPPDATA%\AgenticWorkflow</c>); an absolute path. Lets tests use a disposable root.</summary>
    public const string HomeVariable = "AGENTICWORKFLOW_HOME";

    /// <summary>When <c>1</c>, <c>aw run</c> prints where its own time went (setup, agent run, reporting) to stderr.</summary>
    public const string TimingVariable = "AGENTICWORKFLOW_TIMING";

    /// <summary>
    /// When <c>toast</c>, an attended run that reaches a native agent interaction also shows one best-effort Windows desktop notification, so a person who is not
    /// reading the console can notice it. Off by default; any other value (or none) means no signal outside the console. It never answers anything.
    /// </summary>
    public const string NotifyVariable = "AGENTICWORKFLOW_NOTIFY";
}
