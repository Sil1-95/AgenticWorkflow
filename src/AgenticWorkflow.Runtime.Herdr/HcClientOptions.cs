namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>Where and how to invoke <c>hc</c>.</summary>
internal sealed record HcClientOptions
{
    /// <summary>Executable name or full path of <c>hc</c>.</summary>
    public string Executable { get; init; } = "hc";

    /// <summary>
    /// Value for <c>HC_SESSION</c>. Null talks to the user's default core; any other value talks to
    /// (and may start) a separate, isolated core reachable at its own named pipe.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>
    /// Environment variable name prefixes removed from the <c>hc</c> process environment. A core inherits the
    /// environment of the process that starts it, and so do its terminals; scrubbing keeps a hosted agent from
    /// inheriting identifiers of the agent session that happens to be driving this code.
    /// </summary>
    public IReadOnlyList<string> ScrubbedEnvironmentPrefixes { get; init; } = [];

    /// <summary>Time budget for ordinary, non-waiting commands.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
