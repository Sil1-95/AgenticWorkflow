using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>
/// How one workflow <see cref="ProfileId"/> is launched: an executable and a fixed argument list. The runtime
/// launches only configured profiles and never substitutes one for another.
/// </summary>
public sealed class AgentLaunchProfile
{
    public AgentLaunchProfile(
        ProfileId profile,
        IReadOnlyList<string> argv,
        string agentApp,
        ClaudeEvidenceOptions? evidence = null,
        IAgentConversationLaunch? conversation = null,
        AgentSessionPurpose purpose = AgentSessionPurpose.Implementation)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
            throw new ArgumentException("A launch profile needs an executable.", nameof(argv));

        if (string.IsNullOrWhiteSpace(agentApp))
            throw new ArgumentException("A launch profile needs the app name the agent reports.", nameof(agentApp));

        Profile = profile;
        Argv = argv.ToArray();
        AgentApp = agentApp;
        Evidence = evidence;
        Conversation = conversation;
        Purpose = purpose;
    }

    public ProfileId Profile { get; }

    /// <summary>
    /// What sessions launched with this configuration are for. The same workflow profile may have one configuration per purpose: an explicit reviewer binding to the
    /// implementer's own profile still launches the reviewer with its own, read-only configuration and review-only capability.
    /// </summary>
    public AgentSessionPurpose Purpose { get; }

    /// <summary>Executable followed by arguments. Passed to Herdr as an argument list, never through a shell.</summary>
    public IReadOnlyList<string> Argv { get; }

    /// <summary>The <c>status.app</c> the program reports about itself (<c>claude-code</c>). Used as a sanity check, not for lifecycle.</summary>
    public string AgentApp { get; }

    /// <summary>
    /// Optional: when set, a session of this profile is launched with AgenticWorkflow's session-only, observational evidence hooks
    /// (see <see cref="ClaudeEvidenceChannel"/>). Null means no evidence capability: the controller runs every check itself.
    /// </summary>
    public ClaudeEvidenceOptions? Evidence { get; }

    /// <summary>
    /// Optional: how this program names and re-enters its own native conversation. When set, every session of the profile is launched with a conversation
    /// reference chosen up front and can later be resumed from a fresh process. Null means the profile cannot resume (the runtime says so).
    /// </summary>
    public IAgentConversationLaunch? Conversation { get; }
}

/// <summary>Tool permissions handed to a native Claude Code session on its command line (session-local; nothing global is written).</summary>
public sealed record ClaudeCodeToolPolicy
{
    /// <summary>The .NET build and test commands (the default toolchain).</summary>
    public static IReadOnlyList<string> DotnetTools { get; } = ["Bash(dotnet test:*)", "Bash(dotnet build:*)"];

    /// <summary>Read-only Git inspection. History, index and remote changes are never in this list.</summary>
    public static IReadOnlyList<string> ReadOnlyGitTools { get; } =
    [
        "Bash(git grep:*)",
        "Bash(git diff:*)",
        "Bash(git status:*)",
        "Bash(git ls-files:*)",
        "Bash(git log:*)",
        "Bash(git show:*)",
    ];

    /// <summary>What every session refuses whatever else is allowed: history, branches, remotes, network.</summary>
    public static IReadOnlyList<string> BaselineDeniedTools { get; } =
    [
        "Bash(git add:*)",
        "Bash(git commit:*)",
        "Bash(git push:*)",
        "Bash(git pull:*)",
        "Bash(git fetch:*)",
        "Bash(git clone:*)",
        "Bash(git checkout:*)",
        "Bash(git switch:*)",
        "Bash(git branch:*)",
        "Bash(git merge:*)",
        "Bash(git rebase:*)",
        "Bash(git reset:*)",
        "Bash(git restore:*)",
        "Bash(git clean:*)",
        "Bash(git stash:*)",
        "Bash(git tag:*)",
        "Bash(git remote:*)",
        "Bash(git config:*)",
        "Bash(dotnet nuget:*)",
        "Bash(dotnet publish:*)",
        "Bash(dotnet pack:*)",
        "Bash(curl:*)",
        "Bash(wget:*)",
        "WebFetch",
        "WebSearch",
    ];

    /// <summary>
    /// Tools allowed without a prompt in addition to what the <see cref="PermissionMode"/> already allows
    /// (with the default <c>acceptEdits</c>: reading, and editing inside the working directory). Anything not listed that needs
    /// permission is asked by the agent itself, in its own prompt, to a person; nobody answers it automatically.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [.. DotnetTools, .. ReadOnlyGitTools];

    /// <summary>Explicitly refused whatever else is allowed anywhere (a deny rule always wins): history, branches, remotes, network.</summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = BaselineDeniedTools;

    /// <summary>
    /// The agent's own permission mode. Production profiles keep the default, <c>acceptEdits</c>: anything beyond the allow list prompts a
    /// person natively. A test that must run unattended may choose <c>auto</c> (the agent's own classifier decides non-denied actions); the
    /// deny list above still wins in every mode, so this never widens what the agent is refused. AgenticWorkflow never answers a prompt in
    /// either mode.
    /// </summary>
    public string PermissionMode { get; init; } = "acceptEdits";

    /// <summary>The tools an independent reviewer never has: everything that edits a file.</summary>
    public static IReadOnlyList<string> ReviewerDeniedTools { get; } = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    /// <summary>
    /// The policy of an independent reviewer derived from the implementer's: the same read-only inspection and toolchain commands (so a narrow targeted check is
    /// possible), every file-editing tool denied on top of the baseline denials, and never the edit-accepting mode (it would also approve file-changing shell commands without
    /// asking). This is one layer of three: the instructions and the controller's before/after workspace fingerprint are the others.
    /// </summary>
    public ClaudeCodeToolPolicy ForReviewer() => this with
    {
        DeniedTools = [.. DeniedTools, .. ReviewerDeniedTools.Where(tool => !DeniedTools.Contains(tool))],
        PermissionMode = string.Equals(PermissionMode, "acceptEdits", StringComparison.Ordinal) ? "default" : PermissionMode,
    };

    /// <summary>
    /// The policy of an independent prover: exactly the reviewer's read-only policy. A prover reads and may make a narrow targeted observation with the same toolchain
    /// commands the implementer has, but every file-editing tool is denied and the edit-accepting mode is never used. As for the reviewer this is one layer of three: the
    /// instructions and the controller's before/after workspace fingerprint are the others.
    /// </summary>
    public ClaudeCodeToolPolicy ForProver() => ForReviewer();
}

public static class ClaudeCodeLaunch
{
    public const string AppName = "claude-code";

    /// <summary>
    /// A native, interactive Claude Code session with the least permissive session-local flags this code uses:
    /// <c>--permission-mode</c> (<c>acceptEdits</c> unless the policy says otherwise) plus explicit allow and deny lists. Deliberately absent: any
    /// permission-bypass flag, <c>--add-dir</c>, MCP configuration, and any edit of global Claude settings. No prompt is
    /// passed: the session starts idle and every task arrives as a turn.
    /// <para>
    /// With <paramref name="evidence"/>, each session additionally gets its own settings file through <c>--settings</c> (session-only;
    /// no settings file of the user or the project is read for writing) that registers AgenticWorkflow's observational hooks.
    /// </para>
    /// </summary>
    public static AgentLaunchProfile Profile(
        ProfileId profile,
        ClaudeCodeToolPolicy? policy = null,
        string executable = "claude",
        ClaudeEvidenceOptions? evidence = null,
        AgentSessionPurpose purpose = AgentSessionPurpose.Implementation,
        string? model = null,
        string? reasoning = null)
    {
        policy ??= new ClaudeCodeToolPolicy();

        var argv = new List<string> { executable, "--permission-mode", policy.PermissionMode };

        // The profile's own model and reasoning, as separate arguments: exactly what was asked for, or nothing at all. Nothing is defaulted and nothing is dropped.
        if (model is not null)
        {
            argv.Add("--model");
            argv.Add(LaunchValue(model, nameof(model)));
        }

        if (reasoning is not null)
        {
            argv.Add("--effort");
            argv.Add(LaunchValue(reasoning, nameof(reasoning)));
        }

        if (policy.AllowedTools.Count > 0)
        {
            argv.Add("--allowedTools");
            argv.AddRange(policy.AllowedTools);
        }

        if (policy.DeniedTools.Count > 0)
        {
            argv.Add("--disallowedTools");
            argv.AddRange(policy.DeniedTools);
        }

        return new AgentLaunchProfile(profile, argv, AppName, evidence, new ClaudeConversationLaunch(), purpose);
    }

    /// <summary>A model or reasoning value is data for one argument, never a flag and never more than one argument.</summary>
    private static string LaunchValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"The {name} '{value}' cannot be passed to Claude Code as one plain value.", name);

        return value;
    }
}
