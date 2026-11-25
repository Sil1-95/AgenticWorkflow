using System.Text.Json;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// Reads one JSON object member by member and collects every problem instead of stopping at the first. It is what makes the
/// configuration strict: a member that is read is known, a member that is never read is reported as unknown by
/// <see cref="RejectUnknownMembers"/>, and a name that appears twice is reported (a plain <c>JsonDocument</c> would silently use one of them).
/// </summary>
internal sealed class JsonObjectReader
{
    private readonly string _path;
    private readonly List<string> _problems;
    private readonly Dictionary<string, JsonElement> _members = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    private JsonObjectReader(JsonElement element, string path, List<string> problems)
    {
        _path = path;
        _problems = problems;

        foreach (var property in element.EnumerateObject())
        {
            if (!_members.TryAdd(property.Name, property.Value))
                problems.Add($"{Describe(property.Name)} appears more than once.");
        }
    }

    /// <summary>Null (and a problem recorded) when <paramref name="element"/> is not a JSON object.</summary>
    public static JsonObjectReader? Open(JsonElement element, string path, List<string> problems)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return new JsonObjectReader(element, path, problems);

        problems.Add($"{(path.Length == 0 ? "The configuration" : $"'{path}'")} must be a JSON object.");
        return null;
    }

    public string? RequiredString(string name)
    {
        var value = OptionalString(name);

        if (value is null && !_members.ContainsKey(name))
            _problems.Add($"{Describe(name)} is required.");

        return value;
    }

    public string? OptionalString(string name)
    {
        if (!TryGet(name, out var element))
            return null;

        if (element.ValueKind != JsonValueKind.String)
        {
            _problems.Add($"{Describe(name)} must be a string.");
            return null;
        }

        var text = element.GetString()!;

        if (string.IsNullOrWhiteSpace(text))
        {
            _problems.Add($"{Describe(name)} must not be blank.");
            return null;
        }

        return text;
    }

    public int? OptionalInteger(string name, int minimum, int maximum)
    {
        if (!TryGet(name, out var element))
            return null;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            _problems.Add($"{Describe(name)} must be a whole number.");
            return null;
        }

        if (value < minimum || value > maximum)
        {
            _problems.Add($"{Describe(name)} must be between {minimum} and {maximum}, not {value}.");
            return null;
        }

        return value;
    }

    public int? RequiredInteger(string name, int minimum, int maximum)
    {
        var value = OptionalInteger(name, minimum, maximum);

        if (value is null && !_members.ContainsKey(name))
            _problems.Add($"{Describe(name)} is required.");

        return value;
    }

    public JsonObjectReader? RequiredObject(string name)
    {
        if (!TryGet(name, out var element))
        {
            _problems.Add($"{Describe(name)} is required.");
            return null;
        }

        return Open(element, Join(name), _problems);
    }

    /// <summary>
    /// The members of an object whose member names are data (a map, such as the profiles by id), each opened as an object of its own; null when it is absent. A member that is not an
    /// object, and a name that appears twice, are problems.
    /// </summary>
    public IReadOnlyList<(string Name, JsonObjectReader Reader)>? ObjectMap(string name)
    {
        if (!TryGet(name, out var element))
            return null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            _problems.Add($"{Describe(name)} must be a JSON object.");
            return null;
        }

        var entries = new List<(string, JsonObjectReader)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                _problems.Add($"'{Join(name)}.{property.Name}' appears more than once.");
                continue;
            }

            if (Open(property.Value, $"{Join(name)}.{property.Name}", _problems) is { } reader)
                entries.Add((property.Name, reader));
        }

        return entries;
    }

    /// <summary>The elements of an array member, or null (with a problem when it is required, or present but not an array).</summary>
    public IReadOnlyList<JsonElement>? Array(string name, bool required)
    {
        if (!TryGet(name, out var element))
        {
            if (required)
                _problems.Add($"{Describe(name)} is required.");

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            _problems.Add($"{Describe(name)} must be an array.");
            return null;
        }

        return element.EnumerateArray().ToList();
    }

    /// <summary>An array of strings; each wrong item is a problem naming its position.</summary>
    public IReadOnlyList<string>? StringArray(string name, bool required, bool allowBlankItems = false)
    {
        if (Array(name, required) is not { } items)
            return null;

        var values = new List<string>();

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ValueKind != JsonValueKind.String)
            {
                _problems.Add($"{Describe(name)}[{i}] must be a string.");
                continue;
            }

            var text = items[i].GetString()!;

            if (!allowBlankItems && string.IsNullOrWhiteSpace(text))
            {
                _problems.Add($"{Describe(name)}[{i}] must not be blank.");
                continue;
            }

            values.Add(text);
        }

        return values;
    }

    /// <summary>Reports every member nobody read. Call once, after every known member has been read.</summary>
    public void RejectUnknownMembers()
    {
        foreach (var name in _members.Keys.Where(n => !_read.Contains(n)))
            _problems.Add($"{Describe(name)} is not a recognised setting.");
    }

    public string Describe(string name) => $"'{Join(name)}'";

    public string Join(string name) => _path.Length == 0 ? name : $"{_path}.{name}";

    private bool TryGet(string name, out JsonElement element)
    {
        _read.Add(name);
        return _members.TryGetValue(name, out element);
    }
}
