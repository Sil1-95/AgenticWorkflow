using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticWorkflow.Application.ReadModel;

/// <summary>
/// The one JSON form of the read model, shared by every consumer (the CLI today, a controller later), so a consumer never sees two shapes. Property names are
/// camelCase, enums are snake_case strings, identifiers are GUID strings and times are ISO 8601. Every property is always written, as <c>null</c> when it has no value
/// and as <c>[]</c> when it has no items, so the shape never depends on the data. Property order is the declaration order, so the same model always serializes to the
/// same text.
/// </summary>
public static class TaskReadModelJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions(indented: true);

    /// <summary>
    /// A new set of serializer options with this contract's conventions, for another document that embeds a read model (the controller's wire replies) and must not
    /// invent its own. <paramref name="indented"/> is false for a compact wire form.
    /// </summary>
    public static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = indented,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The model as one JSON document, ending in a newline.</summary>
    public static string Serialize(TaskReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return JsonSerializer.Serialize(model, Options) + "\n";
    }

    /// <summary>A failed read as one JSON document, ending in a newline.</summary>
    public static string Serialize(TaskReadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return JsonSerializer.Serialize(failure, Options) + "\n";
    }

    /// <summary>Reads a model back (for tests and for consumers written in .NET). Throws <see cref="JsonException"/> for text that is not a model of this shape.</summary>
    public static TaskReadModel Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<TaskReadModel>(json, Options) ?? throw new JsonException("The document is empty.");
    }
}

/// <summary>Stable machine-readable codes of a failed read.</summary>
public static class TaskReadFailureCodes
{
    /// <summary>The identifier is not a task id.</summary>
    public const string InvalidTaskId = "invalid_task_id";

    /// <summary>There is no such task.</summary>
    public const string NotFound = "not_found";

    /// <summary>The task exists but its durable state cannot be read or replayed. Nothing was repaired or guessed.</summary>
    public const string StateUnreadable = "state_unreadable";
}

/// <summary>
/// The document a failed read produces instead of a <see cref="TaskReadModel"/>: the same <see cref="SchemaVersion"/> and an <see cref="Error"/> with a stable code
/// (see <see cref="TaskReadFailureCodes"/>) next to a message for a person. A model never has an <c>error</c> property, so its presence tells the two apart.
/// </summary>
public sealed record TaskReadFailure
{
    public TaskReadFailure(string code, string message) => Error = new TaskReadError(code, message);

    public int SchemaVersion => TaskReadModel.CurrentSchemaVersion;

    public TaskReadError Error { get; }
}

public sealed record TaskReadError(string Code, string Message);
