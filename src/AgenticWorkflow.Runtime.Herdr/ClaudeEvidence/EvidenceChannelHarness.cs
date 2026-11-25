namespace AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

/// <summary>The workflow signal tools one session was given. The meaning of each is common to every harness; only how a harness is told about them differs.</summary>
public sealed record ChannelOffers(
    bool Clarification = false,
    bool What = false,
    bool How = false,
    bool Reclassification = false,
    bool ReviewRequest = false,
    bool Review = false,
    bool ProveRequest = false,
    bool Prove = false)
{
    public bool Any => Clarification || What || How || Reclassification || ReviewRequest || Review || ProveRequest || Prove;
}

/// <summary>What a harness needs to be pointed at: the file it is launched with (Claude: the settings file; Pi: the extension), and the MCP configuration file when it takes one.</summary>
public sealed record ChannelLaunchFiles(string LaunchFilePath, string? McpConfigPath);

/// <summary>
/// The harness-specific half of a session's evidence channel. The recording half (the channel directory, the per-session identity check, one record per observed
/// call, turn windows, collection, de-duplication) is the same for every harness and lives in <see cref="ClaudeEvidenceChannel"/>; this is only how one harness is told
/// about the channel, how its launch line is extended, and how its own report of how it started is checked. Signal MEANING is common; session ATTRIBUTION is
/// harness-specific, and it always ends in the same record carrying the harness's own session id.
/// </summary>
public interface IEvidenceChannelHarness
{
    /// <summary>True when a session of this harness cannot be launched safely without its channel (the channel is also how the launch is verified), so a channel that cannot be set up stops the launch instead of running without it.</summary>
    bool RequiresChannel { get; }

    /// <summary>True when the harness's own tool executions are observed and can stand in for a controller run of a configured check. False means the controller runs every check.</summary>
    bool ObservesExecutions { get; }

    /// <summary>Writes whatever the harness needs into <paramref name="directory"/> (outside the workspace) and says which files it is launched with.</summary>
    ChannelLaunchFiles Prepare(string hookExecutable, string directory, ChannelOffers offers);

    /// <summary>The launch line with the channel's files added. <paramref name="nameSession"/> is false when the line already names or resumes the channel's conversation.</summary>
    IReadOnlyList<string> Augment(IReadOnlyList<string> argv, ChannelLaunchFiles files, string sessionId, bool nameSession);

    /// <summary>A problem, in words, when the launched session did not start as it was asked to (checked once it reports ready); null when it did or when there is nothing to check. May wait, boundedly, for the harness's own report.</summary>
    Task<string?> VerifyStartedAsync(string directory, string sessionId, CancellationToken cancellationToken);
}

/// <summary>Claude Code: the channel is a session-only <c>--settings</c> file of observational hooks plus a session-only MCP configuration.</summary>
public sealed class ClaudeChannelHarness : IEvidenceChannelHarness
{
    public static ClaudeChannelHarness Instance { get; } = new();

    public bool RequiresChannel => false;

    public bool ObservesExecutions => true;

    public ChannelLaunchFiles Prepare(string hookExecutable, string directory, ChannelOffers offers)
    {
        var settingsPath = Path.Combine(directory, ClaudeEvidenceFormat.SettingsFile);
        File.WriteAllText(settingsPath, ClaudeEvidenceChannel.SettingsJson(hookExecutable, directory, offers.Clarification, offers.What, offers.How, offers.Reclassification, offers.ReviewRequest, offers.Review, offers.ProveRequest, offers.Prove));

        string? mcpConfigPath = null;

        if (offers.Any)
        {
            mcpConfigPath = Path.Combine(directory, ClaudeEvidenceFormat.McpConfigFile);
            File.WriteAllText(mcpConfigPath, ClaudeEvidenceChannel.McpConfigJson(hookExecutable, directory));
        }

        return new ChannelLaunchFiles(settingsPath, mcpConfigPath);
    }

    public IReadOnlyList<string> Augment(IReadOnlyList<string> argv, ChannelLaunchFiles files, string sessionId, bool nameSession)
    {
        List<string> added = ["--settings", files.LaunchFilePath];

        // The workflow tools are a session-only MCP server (nothing global or project-level is written); the agent still needs the harness's permission
        // to call it, which the settings file grants for exactly those tools.
        if (files.McpConfigPath is not null)
            added.AddRange(["--mcp-config", files.McpConfigPath]);

        if (nameSession)
            added.AddRange(["--session-id", sessionId]);

        return [argv[0], .. added, .. argv.Skip(1)];
    }

    public Task<string?> VerifyStartedAsync(string directory, string sessionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
