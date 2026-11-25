using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// The run's resumable agent conversation, as persisted: which run and profile it belongs to, the kind of runtime that issued it and its opaque
/// reference. Runtime-operational metadata, deliberately not a workflow event: it is not part of what the task means, replay never needs it, and losing it
/// costs only the ability to resume (a later continuation is then an explicit fresh session). It holds no secret, no path and no transcript.
/// </summary>
internal sealed record ConversationRecord(
    [property: JsonPropertyName("conversationVersion")] int ConversationVersion,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("recordedAt")] DateTimeOffset RecordedAt)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// <see cref="IAgentConversationStore"/> over the run directory: <c>runs/&lt;run-id&gt;/conversation.json</c>, written atomically beside the run's other
/// per-run files and in no shared location, so one run can never see another's. Every read checks that the record names exactly this run; anything
/// unreadable, unversioned, malformed or foreign is <see cref="InvalidAgentConversationException"/>, never a guess and never a reason to start fresh.
/// </summary>
internal sealed class RunConversationStore(RunStore store, IClock clock) : IAgentConversationStore
{
    public const string FileName = "conversation.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public string PathOf(TaskId run) => Path.Combine(store.RunDirectory(run), FileName);

    public Task<AgentConversationRecord?> FindAsync(TaskId task, CancellationToken cancellationToken = default)
        => Task.FromResult(Find(task));

    public Task SaveAsync(AgentConversationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var persisted = new ConversationRecord(
            ConversationRecord.CurrentVersion,
            record.Task.ToString(),
            record.Conversation.Profile.Value,
            record.Conversation.Runtime,
            record.Conversation.Reference,
            clock.UtcNow);

        var path = PathOf(record.Task);
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(persisted, Json));
        File.Move(temporary, path, overwrite: true);

        return Task.CompletedTask;
    }

    /// <summary>The run's conversation, or null when none was recorded. Throws <see cref="InvalidAgentConversationException"/> for anything that cannot be trusted.</summary>
    public AgentConversationRecord? Find(TaskId run)
    {
        var path = PathOf(run);

        if (!File.Exists(path))
            return null;

        ConversationRecord? persisted;

        try
        {
            persisted = JsonSerializer.Deserialize<ConversationRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidAgentConversationException($"'{FileName}' is unreadable ({e.GetType().Name})", e);
        }

        if (persisted is null)
            throw new InvalidAgentConversationException($"'{FileName}' is empty");

        if (persisted.ConversationVersion != ConversationRecord.CurrentVersion)
            throw new InvalidAgentConversationException($"'{FileName}' has an unsupported version");

        // The record must name exactly the run whose directory holds it: a copied or edited file is refused, not followed.
        if (!string.Equals(persisted.RunId, run.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidAgentConversationException($"'{FileName}' belongs to run {persisted.RunId}, not to run {run}");

        try
        {
            return new AgentConversationRecord(run, new AgentConversationRef(new ProfileId(persisted.Profile), persisted.Runtime, persisted.Reference));
        }
        catch (ArgumentException e)
        {
            throw new InvalidAgentConversationException($"'{FileName}' holds an invalid conversation ({e.Message})", e);
        }
    }
}
