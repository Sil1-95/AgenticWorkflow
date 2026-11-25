using System.Text.Json.Serialization;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// <c>runs/&lt;run-id&gt;/supervision.json</c>: what the controller that supervises a stretch of a run leaves for the next one in case it dies mid-stretch. It is an operational
/// annotation, never workflow state: the event stream stays the only authority on what the run is, and the live lock stays the only proof that somebody is working on it.
/// <para>
/// It exists for the one thing neither of those can say: <em>what that dead process started and left behind</em>. An isolated agent host (a Herdr core) outlives the process
/// that created it, and its name is not derivable later, so the controller writes the name here before the host is started. A replacement controller reads it, stops
/// what is named (only sessions in AgenticWorkflow's own namespace), and resumes the run only when every one is confirmed gone, so a recovered run never has two agents
/// working on it. <see cref="Attempts"/> bounds how many times successive controllers resume a stretch that keeps killing them.
/// </para>
/// The file is removed when a stretch ends in an orderly way (its host was stopped by its owner), so a file that exists means a stretch did not.
/// </summary>
internal sealed record SupervisionRecord(
    [property: JsonPropertyName("supervisionVersion")] int Version,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("controllerInstance")] string ControllerInstance,
    [property: JsonPropertyName("controllerProcess")] int ControllerProcess,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("stretch")] string Stretch,
    [property: JsonPropertyName("sessions")] IReadOnlyList<string> Sessions,
    [property: JsonPropertyName("attempts")] int Attempts)
{
    public const int CurrentVersion = 1;

    /// <summary>How many times successive controllers resume the same interrupted stretch before the run is left BLOCKED for a person.</summary>
    public const int MaxAutomaticAttempts = 2;

    public SupervisionRecord WithSession(string session) => Sessions.Contains(session, StringComparer.Ordinal) ? this : this with { Sessions = [.. Sessions, session] };
}
