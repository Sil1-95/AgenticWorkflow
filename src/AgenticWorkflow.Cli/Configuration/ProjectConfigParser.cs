using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// Reads <c>agenticworkflow.json</c> strictly. It accepts exactly the version-1 members, rejects everything else (unknown or duplicate
/// members, comments, trailing commas, wrong types, unsupported versions) and reports every problem in one pass. A configuration that
/// parses here has no ambiguity left for the agent to inherit. It needs no Git, Herdr or model: it is a pure function of the text.
/// </summary>
internal static partial class ProjectConfigParser
{
    /// <summary>Check ids the direct-path runner records for its own sensors; a configured check may not shadow them.</summary>
    private static readonly string[] ReservedCheckIds = [DirectPathRunner.ScopeCheckId.Value, DirectPathRunner.WorkspaceStableCheckId.Value];

    /// <summary>Programs that run other programs from a string. Pre-approving one would approve everything it can start.</summary>
    internal static readonly string[] CommandInterpreters =["bash", "sh", "zsh", "fish", "cmd", "powershell", "pwsh", "wsl", "env", "xargs", "sudo", "start"];

    public static ProjectConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var problems = new List<string>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        }
        catch (JsonException e)
        {
            throw new ConfigurationException($"The configuration is not valid JSON: {e.Message}");
        }

        using (document)
        {
            var config = Read(document.RootElement, problems);

            if (problems.Count > 0 || config is null)
                throw new ConfigurationException(problems);

            return config;
        }
    }

    private static ProjectConfig? Read(JsonElement root, List<string> problems)
    {
        if (JsonObjectReader.Open(root, string.Empty, problems) is not { } reader)
            return null;

        var version = reader.RequiredInteger("version", int.MinValue, int.MaxValue);
        if (version is { } v && v != ProjectConfig.SupportedVersion)
            problems.Add($"Configuration version {v} is not supported; this build reads version {ProjectConfig.SupportedVersion}.");

        var role = reader.RequiredString("role");
        if (role is not null)
        {
            if (!IsStableIdentifier(role, id => new RoleId(id)))
                problems.Add($"'role' must be a lowercase identifier (letters, digits, '_', '.', ':', '-'), not '{role}'.");
            else if (role != DirectPathRoles.RootRoleId.Value)
                problems.Add($"Role '{role}' is not supported: the direct path runs the '{DirectPathRoles.RootRoleId}' role only.");
        }

        var profiles = ReadProfiles(reader, problems);
        var catalog = ProfileCatalog.From(profiles);

        var profile = reader.RequiredString("profile");
        if (profile is not null)
        {
            if (!IsStableIdentifier(profile, id => new ProfileId(id)))
                problems.Add($"'profile' must be a lowercase identifier (letters, digits, '_', '.', ':', '-'), not '{profile}'.");
            else if (catalog.Find(profile) is null)
                problems.Add($"Profile '{profile}' is not defined (known: {catalog.KnownIds}; define it under 'profiles'). There is no fallback to another profile.");
        }

        var reviewerProfile = reader.OptionalString("reviewerProfile");
        if (reviewerProfile is not null)
        {
            if (!IsStableIdentifier(reviewerProfile, id => new ProfileId(id)))
                problems.Add($"'reviewerProfile' must be a lowercase identifier (letters, digits, '_', '.', ':', '-'), not '{reviewerProfile}'.");
            else if (catalog.Find(reviewerProfile) is null)
                problems.Add($"Reviewer profile '{reviewerProfile}' is not defined (known: {catalog.KnownIds}; define it under 'profiles'). There is no fallback to the implementer's profile.");
        }

        var proverProfile = reader.OptionalString("proverProfile");
        if (proverProfile is not null)
        {
            if (!IsStableIdentifier(proverProfile, id => new ProfileId(id)))
                problems.Add($"'proverProfile' must be a lowercase identifier (letters, digits, '_', '.', ':', '-'), not '{proverProfile}'.");
            else if (catalog.Find(proverProfile) is null)
                problems.Add($"Prover profile '{proverProfile}' is not defined (known: {catalog.KnownIds}; define it under 'profiles'). There is no fallback to the implementer's or the reviewer's profile.");
        }

        var toolchain = reader.RequiredString("toolchain");
        if (toolchain is not null && Toolchain.Find(toolchain) is null)
            problems.Add($"Toolchain '{toolchain}' is not supported (known: {Toolchain.KnownIds}). Toolchains are chosen explicitly, never detected.");

        var maxTurns = reader.OptionalInteger("maxTurns", 1, DirectTaskDefinition.MaxAllowedTurns);

        var checks = ReadChecks(reader, problems);

        IReadOnlyList<string> writable = [];
        IReadOnlyList<string> protectedPaths = [];

        if (reader.RequiredObject("scope") is { } scope)
        {
            writable = scope.StringArray("writable", required: true) ?? [];
            protectedPaths = scope.StringArray("protected", required: false) ?? [];
            scope.RejectUnknownMembers();

            if (writable.Count == 0 && !problems.Any(p => p.StartsWith("'scope.writable'", StringComparison.Ordinal)))
                problems.Add("'scope.writable' must list at least one path: an empty list would mean \"anywhere\"; write \"**\" if that is what you intend.");
        }

        var allowCommands = reader.StringArray("allowCommands", required: false) ?? [];
        ValidateAllowCommands(allowCommands, problems);

        var acceptance = ReadAcceptance(reader, problems);

        var notes = reader.OptionalString("operatingNotes");
        if (notes is { Length: > ProjectConfig.MaxOperatingNotesLength })
            problems.Add($"'operatingNotes' must not exceed {ProjectConfig.MaxOperatingNotesLength} characters.");

        reader.RejectUnknownMembers();

        if (problems.Count > 0)
            return null;

        return new ProjectConfig(role!, profile!, toolchain!, maxTurns ?? ProjectConfig.DefaultMaxTurns, checks, writable, protectedPaths, allowCommands, notes, acceptance, reviewerProfile, proverProfile, profiles);
    }

    /// <summary>Longest description a profile may carry.</summary>
    private const int MaxProfileDescriptionLength = 200;

    /// <summary>How many profiles one configuration may define.</summary>
    private const int MaxProfiles = 32;

    /// <summary>
    /// The profiles the configuration defines: each a harness and, optionally, a model, a reasoning level and a description, and nothing else. There is deliberately no member for arguments,
    /// environment variables or commands: how a harness is launched is the adapter's, built from these typed values. A profile of a harness this build knows by name but cannot run
    /// (<see cref="Harnesses.Pi"/>) is accepted here and refused when a role is bound to it.
    /// </summary>
    private static List<ProfileConfig> ReadProfiles(JsonObjectReader reader, List<string> problems)
    {
        var profiles = new List<ProfileConfig>();

        if (reader.ObjectMap("profiles") is not { } entries)
            return profiles;

        if (entries.Count > MaxProfiles)
            problems.Add($"'profiles' defines {entries.Count} profiles; at most {MaxProfiles} are allowed.");

        foreach (var (id, entry) in entries)
        {
            var valid = true;

            if (!IsStableIdentifier(id, value => new ProfileId(value)))
            {
                problems.Add($"'profiles.{id}': a profile id must be a lowercase identifier (letters, digits, '_', '.', ':', '-').");
                valid = false;
            }
            else if (string.Equals(id, AgentProfileEntry.ClaudeCodeLocal.Id.Value, StringComparison.Ordinal))
            {
                problems.Add($"'profiles.{id}' redefines a built-in profile; choose another id (the built-in profile is always available as it is).");
                valid = false;
            }

            var harness = entry.RequiredString("harness");
            if (harness is not null && !Harnesses.IsKnown(harness))
            {
                problems.Add($"{entry.Describe("harness")} must be one of {Harnesses.KnownList}, not '{harness}'.");
                valid = false;
            }

            var model = ReadLaunchValue(entry, "model", problems);
            var reasoning = ReadLaunchValue(entry, "reasoning", problems);

            var description = entry.OptionalString("description");
            if (description is { Length: > MaxProfileDescriptionLength })
                problems.Add($"{entry.Describe("description")} must not exceed {MaxProfileDescriptionLength} characters.");

            entry.RejectUnknownMembers();

            if (valid && harness is not null)
                profiles.Add(new ProfileConfig(id, harness, model, reasoning, description));
        }

        return profiles;
    }

    /// <summary>A model or reasoning value: one plain token, nothing that could be read as a flag or as more than one argument. What a harness accepts is its adapter's to decide, later.</summary>
    private static string? ReadLaunchValue(JsonObjectReader entry, string name, List<string> problems)
    {
        var value = entry.OptionalString(name);

        if (value is not null && !LaunchValues.IsPlain(value))
        {
            problems.Add($"{entry.Describe(name)} must be one plain value of letters, digits and . _ : / @ + [ ] - (at most {LaunchValues.MaxLength} characters), not '{value}'.");
            return null;
        }

        return value;
    }

    /// <summary>The project's default acceptance requirement: absent means <see cref="AcceptancePolicy.Default"/>; anything but one of the two words is refused, never guessed.</summary>
    private static AcceptanceRequirement ReadAcceptance(JsonObjectReader reader, List<string> problems)
    {
        if (reader.OptionalString("acceptance") is not { } word)
            return AcceptancePolicy.Default;

        if (AcceptancePolicy.Parse(word) is { } requirement)
            return requirement;

        problems.Add($"'acceptance' must be one of {AcceptancePolicy.KnownWords}, not '{word}'.");
        return AcceptancePolicy.Default;
    }

    private static List<CheckConfig> ReadChecks(JsonObjectReader reader, List<string> problems)
    {
        var checks = new List<CheckConfig>();

        if (reader.Array("verification", required: true) is not { } items)
            return checks;

        if (items.Count == 0)
            problems.Add("'verification' must list at least one check: completion is decided by the controller's own checks.");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            if (JsonObjectReader.Open(items[i], $"verification[{i}]", problems) is not { } check)
                continue;

            var id = check.RequiredString("id");
            var executable = check.RequiredString("executable");
            var arguments = check.StringArray("arguments", required: false, allowBlankItems: true) ?? [];
            var timeout = check.OptionalInteger("timeoutSeconds", 1, ProjectConfig.MaxCheckTimeoutSeconds);
            var description = check.OptionalString("description");
            check.RejectUnknownMembers();

            if (id is not null)
            {
                if (!IsStableIdentifier(id, value => new VerificationCheckId(value)))
                    problems.Add($"{check.Describe("id")} must be a lowercase identifier (letters, digits, '_', '.', ':', '-'), not '{id}'.");
                else if (ReservedCheckIds.Contains(id, StringComparer.Ordinal))
                    problems.Add($"{check.Describe("id")} '{id}' is reserved for the controller's own sensor.");
                else if (!seen.Add(id))
                    problems.Add($"Verification check id '{id}' is used more than once.");
            }

            if (executable is not null)
            {
                if (executable != executable.Trim() || executable.Any(char.IsControl))
                    problems.Add($"{check.Describe("executable")} must be a program name or path without surrounding whitespace or control characters.");
            }

            for (var a = 0; a < arguments.Count; a++)
            {
                if (arguments[a].Any(c => char.IsControl(c)))
                    problems.Add($"{check.Describe("arguments")}[{a}] must not contain control characters (newlines, tabs, ...).");
            }

            if (id is not null && executable is not null)
                checks.Add(new CheckConfig(id, executable, arguments, timeout ?? ProjectConfig.DefaultCheckTimeoutSeconds, description));
        }

        return checks;
    }

    /// <summary>
    /// Extra command forms the agent may run without asking, each a plain command prefix ("flutter pub get") that becomes
    /// <c>Bash(flutter pub get:*)</c>. Deliberately narrow: no wildcards, no shells or other command launchers, no Git (Git access is fixed
    /// read-only), and the baseline deny list still wins.
    /// </summary>
    private static void ValidateAllowCommands(IReadOnlyList<string> commands, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in commands)
        {
            if (!CommandForm().IsMatch(command))
            {
                problems.Add($"'allowCommands' entry '{command}' must be words made of letters, digits and '- _ . / =' separated by single spaces (no wildcards, quotes or shell syntax).");
                continue;
            }

            var program = command.Split(' ')[0];

            if (program.Equals("git", StringComparison.OrdinalIgnoreCase))
                problems.Add($"'allowCommands' entry '{command}': Git access is fixed (read-only inspection) and cannot be extended.");
            else if (CommandInterpreters.Contains(program, StringComparer.OrdinalIgnoreCase))
                problems.Add($"'allowCommands' entry '{command}': '{program}' starts other programs, so approving it would approve everything it can start.");
            else if (!seen.Add(command))
                problems.Add($"'allowCommands' entry '{command}' appears more than once.");
        }
    }

    private static bool IsStableIdentifier<T>(string value, Func<string, T> create)
    {
        try
        {
            create(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_./=-]+( [A-Za-z0-9_./=-]+)*$")]
    private static partial Regex CommandForm();
}
