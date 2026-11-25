namespace AgenticWorkflow.Cli.Reporting;

/// <summary>What to tell a person who may not be looking at the console: a short title and a few lines (separated by <c>\n</c>). Display text only.</summary>
internal sealed record HumanAttentionNotice(string Title, string Body);

/// <summary>
/// An optional, best-effort signal OUTSIDE the terminal for a person who is not reading it (<c>AGENTICWORKFLOW_NOTIFY</c>). It only draws attention: it never
/// answers anything and its outcome can never change a run. A failure is reported as a short reason, never thrown.
/// </summary>
internal interface IHumanAttentionNotifier
{
    /// <summary>Returns null when the notice was shown (or deliberately not shown: the no-op notifier), otherwise a short reason it could not be.</summary>
    Task<string?> NotifyAsync(HumanAttentionNotice notice, CancellationToken cancellationToken);
}

/// <summary>The default: no signal outside the console.</summary>
internal sealed class NoHumanAttentionNotifier : IHumanAttentionNotifier
{
    public static NoHumanAttentionNotifier Instance { get; } = new();

    public Task<string?> NotifyAsync(HumanAttentionNotice notice, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>Chooses the notifier from the environment: off unless <c>AGENTICWORKFLOW_NOTIFY</c> says <c>toast</c>.</summary>
internal static class HumanAttentionNotifiers
{
    public const string Toast = "toast";

    public static IHumanAttentionNotifier Select(Func<string, string?> getEnvironmentVariable)
        => string.Equals(getEnvironmentVariable(CliEnvironment.NotifyVariable)?.Trim(), Toast, StringComparison.OrdinalIgnoreCase)
            ? new WindowsToastNotifier(new SystemToastProcessLauncher())
            : NoHumanAttentionNotifier.Instance;
}
