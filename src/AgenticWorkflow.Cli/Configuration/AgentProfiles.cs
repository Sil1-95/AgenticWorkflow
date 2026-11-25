using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// The harnesses an agent profile can name. A harness is the program that hosts the agent. Only the ones this build can run are <see cref="IsSupported"/>; a harness known by name but not
/// supported can be written and read back, and is refused, naming the harness, when a role is bound to one. Whether the program is installed and offers what a launch needs is asked of its
/// adapter when a role is bound (<c>unsupported_configuration</c>). There is no mapping from one harness to another.
/// </summary>
internal static class Harnesses
{
    public const string ClaudeCode = "claude-code";

    /// <summary>Pi (the pi-coding-agent), run through its own adapter. A missing or incompatible installation is refused when a role is bound; it is never replaced by another harness.</summary>
    public const string Pi = "pi";

    public static IReadOnlyList<string> Known { get; } = [ClaudeCode, Pi];

    public static bool IsKnown(string harness) => Known.Contains(harness, StringComparer.Ordinal);

    public static bool IsSupported(string harness) => string.Equals(harness, ClaudeCode, StringComparison.Ordinal) || string.Equals(harness, Pi, StringComparison.Ordinal);

    public static string KnownList => string.Join(", ", Known.Select(h => $"'{h}'"));
}

/// <summary>
/// What a model or reasoning value in a profile may look like: one plain token (letters, digits and <c>. _ : / @ + [ ] -</c>), so it can only ever be one command-line argument. It
/// is not a list of known models or levels: what a harness accepts is decided by that harness's adapter, asked before a role is bound.
/// </summary>
internal static partial class LaunchValues
{
    public const int MaxLength = 100;

    public static bool IsPlain(string value) => value.Length <= MaxLength && Token().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\[\]-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

/// <summary>A profile as the project's configuration defines it: nothing resolved, nothing checked against a harness.</summary>
internal sealed record ProfileConfig(string Id, string Harness, string? Model, string? Reasoning, string? Description);

/// <summary>
/// A reusable agent profile: a name and how to run it (harness, model, reasoning). It says nothing about roles; which role uses it is a binding, decided separately and recorded with the
/// run. <see cref="Model"/> and <see cref="Reasoning"/> are opaque text; null means the profile chooses none, which is passed on as "none" (the harness's own setting applies), never filled in.
/// </summary>
internal sealed record AgentProfileEntry(ProfileId Id, string Harness, string? Model, string? Reasoning, string? Description, bool IsBuiltIn = false)
{
    /// <summary>
    /// The one profile every earlier configuration used: native Claude Code with no model or reasoning of its own. It is always available, so a configuration that names it keeps working
    /// unchanged and launches exactly as it always did. A project cannot redefine it.
    /// </summary>
    public static AgentProfileEntry ClaudeCodeLocal { get; } = new(new ProfileId("claude-code-local"), Harnesses.ClaudeCode, null, null, "native Claude Code, hosted by Herdr", IsBuiltIn: true);

    /// <summary>Identifies the definition: a hash of the harness, model and reasoning. The description is not part of it.</summary>
    public string Fingerprint
    {
        get
        {
            var text = $"aw-profile-v1\nharness={Harness}\nmodel={Model ?? string.Empty}\nreasoning={Reasoning ?? string.Empty}";

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }

    /// <summary>What is recorded with a binding: the resolved launch values and the identity of the definition they came from.</summary>
    public ExecutionConfiguration ToExecution() => new(Harness, Model, Reasoning, Fingerprint);

    /// <summary>The profile as Core records it for <paramref name="role"/>: it provides what the role requires (the harness is checked separately, and a binding to one that cannot be run is refused).</summary>
    public Profile ToCoreProfile(Role role) => new(Id, role.RequiredCapabilities, Description);
}

/// <summary>The profiles available to a project: the built-in one plus the ones its configuration defines. An unknown name is an error: there is no default and no fallback.</summary>
internal sealed class ProfileCatalog
{
    private readonly IReadOnlyList<AgentProfileEntry> _entries;

    private ProfileCatalog(IReadOnlyList<AgentProfileEntry> entries) => _entries = entries;

    public IReadOnlyList<AgentProfileEntry> All => _entries;

    public static ProfileCatalog From(IEnumerable<ProfileConfig> defined)
        => new([AgentProfileEntry.ClaudeCodeLocal, .. defined.Select(p => new AgentProfileEntry(new ProfileId(p.Id), p.Harness, p.Model, p.Reasoning, p.Description))]);

    public AgentProfileEntry? Find(string id) => _entries.FirstOrDefault(p => string.Equals(p.Id.Value, id, StringComparison.Ordinal));

    public string KnownIds => string.Join(", ", _entries.Select(p => $"'{p.Id}'"));
}
