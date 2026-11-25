using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Pi;

/// <summary>
/// How a Pi session's workflow signals reach the controller, and how its launch is checked. Pi has no per-session hook payload, but it does let a per-launch extension
/// (<c>-e</c>) host an MCP server and observe every tool result together with its OWN session manager's session id. The extension AgenticWorkflow ships hands that id, and
/// the tool call, to the same hook executable Claude Code uses, which writes the same record: so the signal meaning, the identity check (the record's session id must be the
/// one the controller named), the turn windows and the de-duplication are exactly the Claude path's. The extension is written into the session's own channel directory
/// (outside the workspace), is loaded for that one session only, and nothing global or project-level is read or written.
/// </summary>
public sealed class PiChannelHarness : IEvidenceChannelHarness
{
    public const string ExtensionFile = "aw-pi-extension.mjs";
    public const string BridgeFile = "pi-bridge.json";
    public const string LaunchReportFile = "pi-launch.json";

    private static readonly TimeSpan DefaultReportTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReportPoll = TimeSpan.FromMilliseconds(50);

    private const string ResourceName = "AgenticWorkflow.Runtime.Herdr.Pi.aw-pi-extension.mjs";

    /// <summary>True when the Pi extension this product launches Pi with is part of this build (it is embedded, so a complete installation always has it).</summary>
    public static bool IsExtensionEmbedded => typeof(PiChannelHarness).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    private readonly int _programLength;
    private readonly string? _expectedModel;
    private readonly string? _expectedReasoning;
    private readonly IReadOnlyList<string> _deniedCommands;
    private readonly TimeSpan _reportTimeout;

    /// <param name="programLength">How many leading elements of the launch line are the program itself (the executable, and the script it runs).</param>
    /// <param name="expectedModel">The provider-qualified model the session was asked to start with, or null when the profile chose none (Pi's own setting applies).</param>
    /// <param name="expectedReasoning">The thinking level the session was asked to start with, or null when the profile chose none.</param>
    /// <param name="deniedCommands">Shell command prefixes the session refuses before they run (lower case, words separated by single spaces).</param>
    /// <param name="reportTimeout">How long to wait for Pi's own start-up report once it is ready; 20 seconds when null.</param>
    public PiChannelHarness(int programLength, string? expectedModel, string? expectedReasoning, IReadOnlyList<string> deniedCommands, TimeSpan? reportTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(programLength, 1);
        ArgumentNullException.ThrowIfNull(deniedCommands);

        _programLength = programLength;
        _expectedModel = expectedModel;
        _expectedReasoning = expectedReasoning;
        _deniedCommands = deniedCommands;
        _reportTimeout = reportTimeout ?? DefaultReportTimeout;
    }

    public bool RequiresChannel => true;

    public bool ObservesExecutions => false;

    public ChannelLaunchFiles Prepare(string hookExecutable, string directory, ChannelOffers offers)
    {
        var extensionPath = Path.Combine(directory, ExtensionFile);

        using (var resource = typeof(PiChannelHarness).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException($"The embedded Pi extension '{ResourceName}' is missing."))
        using (var file = File.Create(extensionPath))
            resource.CopyTo(file);

        File.WriteAllText(
            Path.Combine(directory, BridgeFile),
            JsonSerializer.Serialize(new { hook = Path.GetFullPath(hookExecutable), channel = directory, mcp = offers.Any, deniedCommands = _deniedCommands }));

        return new ChannelLaunchFiles(extensionPath, null);
    }

    public IReadOnlyList<string> Augment(IReadOnlyList<string> argv, ChannelLaunchFiles files, string sessionId, bool nameSession)
    {
        List<string> added = ["-e", files.LaunchFilePath];

        if (nameSession)
            added.AddRange(["--session-id", sessionId]);

        return [.. argv.Take(_programLength), .. added, .. argv.Skip(_programLength)];
    }

    /// <summary>
    /// Pi accepts a model or thinking level it cannot honour by warning and using another (and clamps a thinking level the model does not support), so what it reports
    /// about itself is compared with what was asked for. A missing report is a failure too: a launch that cannot be verified is not used.
    /// </summary>
    public async Task<string?> VerifyStartedAsync(string directory, string sessionId, CancellationToken cancellationToken)
    {
        // Pi reports itself ready as soon as it is idle, which can be a moment before its extensions have run their start-up handlers: the report is waited for, boundedly.
        var deadline = DateTime.UtcNow + _reportTimeout;
        PiLaunchReport? report = null;

        while (true)
        {
            try
            {
                report = JsonSerializer.Deserialize<PiLaunchReport>(File.ReadAllText(Path.Combine(directory, LaunchReportFile)));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // not written yet (or being written): look again until the deadline
            }

            if (report is not null || DateTime.UtcNow >= deadline)
                break;

            await Task.Delay(ReportPoll, cancellationToken).ConfigureAwait(false);
        }

        if (report is null)
            return "Pi did not report how it started, so the requested model and reasoning cannot be confirmed";

        if (!string.Equals(report.SessionId, sessionId, StringComparison.Ordinal))
            return "Pi started a different conversation than the one the controller named";

        if (_expectedModel is not null && !string.Equals(report.Model, _expectedModel, StringComparison.Ordinal))
            return $"Pi started with model '{report.Model ?? "none"}', not the requested '{_expectedModel}'";

        if (_expectedReasoning is not null && !string.Equals(report.Thinking, _expectedReasoning, StringComparison.Ordinal))
            return $"Pi started with reasoning '{report.Thinking ?? "none"}', not the requested '{_expectedReasoning}'";

        return null;
    }

    private sealed class PiLaunchReport
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("thinking")]
        public string? Thinking { get; init; }
    }
}

/// <summary>
/// Pi's own public session flags: <c>--session-id &lt;uuid&gt;</c> names a new conversation up front, and <c>--session &lt;uuid&gt;</c> re-enters it from a fresh process (the
/// same conversation, not a fork). <c>--session-id</c> would silently CREATE a conversation that does not exist, so it is never used to resume: <c>--session</c> refuses a
/// conversation it cannot find, which is how a missing one is told apart. Only canonical UUIDs are ever passed (<c>--session</c> also matches partial ids and paths). Nothing about Pi's
/// session files or storage is touched.
/// </summary>
public sealed class PiConversationLaunch(int programLength = 1) : IAgentConversationLaunch
{
    public string Runtime => PiLaunch.AppName;

    public string NewReference() => Guid.NewGuid().ToString();

    public bool IsValidReference(string reference)
        => Guid.TryParseExact(reference, "D", out var parsed) && string.Equals(parsed.ToString(), reference, StringComparison.Ordinal);

    public IReadOnlyList<string> ForNewConversation(IReadOnlyList<string> argv, string reference)
        => Insert(argv, "--session-id", reference);

    public IReadOnlyList<string> ForResume(IReadOnlyList<string> argv, string reference)
        => Insert(argv, "--session", reference);

    public bool IndicatesMissingConversation(string finalOutput, string reference)
        => finalOutput.Contains($"No session found matching '{reference}'", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> Insert(IReadOnlyList<string> argv, string flag, string reference)
        => [.. argv.Take(programLength), flag, reference, .. argv.Skip(programLength)];
}

public static class PiLaunch
{
    public const string AppName = "pi";

    /// <summary>The built-in tools an independent reviewer or prover has: reading and searching only. No file can be edited and no command can be run.</summary>
    public static IReadOnlyList<string> ReadOnlyTools { get; } = ["read", "grep", "find", "ls"];

    /// <summary>
    /// A native, interactive Pi session: the program, the profile's model and reasoning as separate arguments (exactly what was asked for, or nothing), and for a reviewer or
    /// prover the read-only tool allowlist. Pi has no permission prompts to configure and none is bypassed; no prompt is passed (the session starts idle and every task arrives as a turn).
    /// </summary>
    /// <param name="program">The executable and any script it runs (<c>node</c> and Pi's <c>cli.js</c>), resolved by the caller, never a shell shim.</param>
    /// <param name="evidence">The workflow signals this session is offered; its channel is the session's extension.</param>
    /// <param name="deniedCommands">Shell command prefixes the session refuses (see <see cref="PiChannelHarness"/>).</param>
    /// <param name="readOnly">True for a reviewer or prover.</param>
    public static AgentLaunchProfile Profile(
        ProfileId profile,
        IReadOnlyList<string> program,
        ClaudeEvidenceOptions evidence,
        IReadOnlyList<string> deniedCommands,
        AgentSessionPurpose purpose = AgentSessionPurpose.Implementation,
        string? model = null,
        string? reasoning = null,
        bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(evidence);

        if (program.Count == 0)
            throw new ArgumentException("A Pi launch needs a program.", nameof(program));

        var argv = new List<string>(program);

        if (model is not null)
        {
            argv.Add("--model");
            argv.Add(LaunchValue(model, nameof(model)));
        }

        if (reasoning is not null)
        {
            argv.Add("--thinking");
            argv.Add(LaunchValue(reasoning, nameof(reasoning)));
        }

        if (readOnly)
        {
            argv.Add("--tools");
            argv.Add(string.Join(',', ReadOnlyTools));
        }

        var harness = new PiChannelHarness(program.Count, model, reasoning, deniedCommands);

        return new AgentLaunchProfile(profile, argv, AppName, evidence with { Harness = harness }, new PiConversationLaunch(program.Count), purpose);
    }

    /// <summary>The shell command prefixes a set of Claude-style deny rules (<c>Bash(git commit:*)</c>) names; other rules (the web tools) have no Pi counterpart and are ignored.</summary>
    public static IReadOnlyList<string> DeniedCommandsOf(IEnumerable<string> denyRules)
    {
        ArgumentNullException.ThrowIfNull(denyRules);

        var commands = new List<string>();

        foreach (var rule in denyRules)
        {
            if (rule.StartsWith("Bash(", StringComparison.Ordinal) && rule.EndsWith(":*)", StringComparison.Ordinal))
                commands.Add(string.Join(' ', rule["Bash(".Length..^":*)".Length].Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant());
        }

        return [.. commands.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A model or reasoning value is data for one argument, never a flag and never more than one argument.</summary>
    private static string LaunchValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"The {name} '{value}' cannot be passed to Pi as one plain value.", name);

        return value;
    }
}
