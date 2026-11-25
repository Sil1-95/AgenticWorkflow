using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// What a person confirmed in order to create <c>agenticworkflow.json</c>. Every value is the person's: nothing here is defaulted, detected or filled in on their behalf (suggestions are shown
/// beforehand and only become values when confirmed). There is deliberately no member for harness arguments, environment, shell commands or anything else the strict configuration format lacks.
/// </summary>
internal sealed record ProjectInitChoices
{
    public string? Toolchain { get; init; }

    public IReadOnlyList<ProjectInitProfile>? Profiles { get; init; }

    public string? Profile { get; init; }

    public string? ReviewerProfile { get; init; }

    public string? ProverProfile { get; init; }

    public IReadOnlyList<ProjectInitCheck>? Verification { get; init; }

    public IReadOnlyList<string>? Writable { get; init; }

    public IReadOnlyList<string>? Protected { get; init; }

    public string? Acceptance { get; init; }

    public int? MaxTurns { get; init; }
}

internal sealed record ProjectInitProfile(string? Id, string? Harness, string? Model, string? Reasoning, string? Description);

internal sealed record ProjectInitCheck(string? Id, string? Executable, IReadOnlyList<string>? Arguments, int? TimeoutSeconds, string? Description);

/// <summary>
/// Turns a <see cref="ProjectInitChoices"/> into the text of a configuration file, and only if the strict <see cref="ProjectConfigParser"/> accepts that text: the file that is written is exactly
/// what <c>aw run</c> would read, so a created configuration can never be one the product then refuses. It also refuses a check that would run a command interpreter, which the format would
/// otherwise allow as an ordinary program name: a typed form is not a way to run an arbitrary shell command.
/// </summary>
internal static class ProjectInitializer
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The request as typed values, or why the text is not one.</summary>
    public static (ProjectInitChoices? Request, IReadOnlyList<string> Problems) Decode(byte[] json)
    {
        try
        {
            return (JsonSerializer.Deserialize<ProjectInitChoices>(json, ReadOptions) ?? throw new JsonException("The request is empty."), []);
        }
        catch (JsonException e)
        {
            return (null, [$"The request is not a valid project description: {e.Message}"]);
        }
    }

    /// <summary>The configuration text for <paramref name="request"/>, or every reason it cannot be created.</summary>
    public static (string? Text, IReadOnlyList<string> Problems) Build(ProjectInitChoices request)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Toolchain))
            problems.Add("Choose a toolchain.");

        if (string.IsNullOrWhiteSpace(request.Profile))
            problems.Add("Choose the profile the implementer role runs under.");

        foreach (var check in request.Verification ?? [])
        {
            if (check.Executable is { } executable && ProjectConfigParser.CommandInterpreters.Contains(Path.GetFileNameWithoutExtension(executable.Trim()), StringComparer.OrdinalIgnoreCase))
                problems.Add($"Verification check '{check.Id}': '{executable}' starts other programs from text, so it cannot be a check. Name the program the check runs.");
        }

        if (problems.Count > 0)
            return (null, problems);

        var ids = (request.Profiles ?? []).Select(p => p.Id ?? string.Empty).ToList();

        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"Profile id '{duplicate.Key}' is used more than once.");

        if (problems.Count > 0)
            return (null, problems);

        var document = Members(
            ("version", ProjectConfig.SupportedVersion),
            ("role", "root"),
            ("profile", request.Profile),
            ("reviewerProfile", request.ReviewerProfile),
            ("proverProfile", request.ProverProfile),
            ("toolchain", request.Toolchain),
            ("maxTurns", request.MaxTurns),
            ("acceptance", request.Acceptance),
            ("verification", (request.Verification ?? []).Select(c => Members(
                ("id", c.Id),
                ("executable", c.Executable),
                ("arguments", c.Arguments),
                ("timeoutSeconds", c.TimeoutSeconds),
                ("description", c.Description))).ToList()),
            ("scope", Members(("writable", request.Writable ?? []), ("protected", request.Protected))),
            ("profiles", (request.Profiles ?? []).Count == 0
                ? null
                : (request.Profiles ?? []).ToDictionary(
                    p => p.Id ?? string.Empty,
                    p => (object?)Members(("harness", p.Harness), ("model", p.Model), ("reasoning", p.Reasoning), ("description", p.Description)))));

        var text = JsonSerializer.Serialize(document, WriteOptions) + "\n";

        try
        {
            ProjectConfigParser.Parse(text);
        }
        catch (ConfigurationException e)
        {
            return (null, e.Problems);
        }

        return (text, []);
    }

    /// <summary>A JSON object of the members that have a value: an absent choice is left out of the file, never written as null.</summary>
    private static Dictionary<string, object?> Members(params (string Name, object? Value)[] members)
        => members.Where(m => m.Value is not null).ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);
}
