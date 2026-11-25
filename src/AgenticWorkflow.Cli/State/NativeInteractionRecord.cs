using System.Text.Json.Serialization;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// An operational annotation, written while a live run waits for a person in the agent's own interface so a fresh <c>aw status</c> can say how to attach. It is
/// never workflow state and is read only to display: the event stream stays authoritative, and the live lock says whether a process still holds the run. Removed
/// when the wait ends. The host session and terminal are absent when the runtime did not say where the person answers.
/// </summary>
internal sealed record NativeInteractionRecord(
    [property: JsonPropertyName("nativeInteractionVersion")] int Version,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("hostSession")] string? HostSession,
    [property: JsonPropertyName("terminal")] string? Terminal,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt)
{
    public const int CurrentVersion = 1;

    /// <summary>The longest description kept (already bounded when written; bounded again when read, since the file is not trusted).</summary>
    public const int MaxDescriptionLength = 300;
}
