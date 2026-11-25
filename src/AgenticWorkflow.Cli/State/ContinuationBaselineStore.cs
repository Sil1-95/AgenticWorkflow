using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Cli.State;

/// <summary>
/// The workspace a continuation of human-requested changes is judged against, as persisted: what the working tree looked like when the person asked for the
/// changes, path by path. Operational, not semantic (see <see cref="ContinuationBaselineStore"/> for why it exists at all). It names the gate whose decision it
/// belongs to; it holds file paths and digests only, no file content, no secret and no transcript.
/// </summary>
internal sealed record ContinuationBaselineRecord(
    [property: JsonPropertyName("baselineVersion")] int BaselineVersion,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("gateId")] string GateId,
    [property: JsonPropertyName("baseIdentity")] string BaseIdentity,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("pathDigests")] Dictionary<string, string> PathDigests,
    [property: JsonPropertyName("recordedAt")] DateTimeOffset RecordedAt)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// <c>runs/&lt;run-id&gt;/continuation.json</c>: the one operational fact a retry of a blocked continuation cannot get from the event stream.
/// <para>
/// The controller judges what the agent changed as the difference between the workspace before the stretch and after it. For a stretch that was interrupted
/// by a runtime failure the agent may already have changed files, so a retry that captured a fresh "before" would absorb those changes into its baseline and
/// never see, for example, a protected path the interrupted attempt had already touched. The state recorded here, at the moment the person asked for the
/// changes and before any agent work, keeps the whole pending request judged against one baseline however many attempts it takes. The event stream holds only
/// a fingerprint of each implementation, not the per-path digests the scope rules need, hence this record.
/// </para>
/// It is written before the decision is recorded (so a decision is never recorded without it) and bound to the gate that decision closes; a retry refuses a
/// record that is missing, unreadable or belongs to another run or gate rather than guessing a baseline. Written atomically beside the run's other files.
/// </summary>
internal sealed class ContinuationBaselineStore(RunStore store, IClock clock)
{
    public const string FileName = "continuation.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public string PathOf(TaskId run) => Path.Combine(store.RunDirectory(run), FileName);

    public void Save(TaskId run, GateId gate, ImplementationState baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var record = new ContinuationBaselineRecord(
            ContinuationBaselineRecord.CurrentVersion,
            run.ToString(),
            gate.ToString(),
            baseline.BaseIdentity,
            baseline.Fingerprint.Value,
            new Dictionary<string, string>(baseline.PathDigests, StringComparer.Ordinal),
            clock.UtcNow);

        var path = PathOf(run);
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The baseline recorded for <paramref name="gate"/> of <paramref name="run"/>, or null plus the reason it cannot be trusted. Never throws for a bad file:
    /// a baseline that cannot be trusted means the retry is refused, not that one is invented.
    /// </summary>
    public (ImplementationState? Baseline, string? Problem) Find(TaskId run, GateId gate)
    {
        var path = PathOf(run);

        if (!File.Exists(path))
            return (null, $"'{FileName}' is missing");

        ContinuationBaselineRecord? record;

        try
        {
            record = JsonSerializer.Deserialize<ContinuationBaselineRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, $"'{FileName}' is unreadable ({e.GetType().Name})");
        }

        if (record is null)
            return (null, $"'{FileName}' is empty");

        if (record.BaselineVersion != ContinuationBaselineRecord.CurrentVersion)
            return (null, $"'{FileName}' has an unsupported version");

        if (!string.Equals(record.RunId, run.ToString(), StringComparison.OrdinalIgnoreCase))
            return (null, $"'{FileName}' belongs to run {record.RunId}, not to run {run}");

        if (!string.Equals(record.GateId, gate.ToString(), StringComparison.OrdinalIgnoreCase))
            return (null, $"'{FileName}' was recorded for another decision (gate {record.GateId}), not for gate {gate}");

        if (string.IsNullOrWhiteSpace(record.BaseIdentity) || string.IsNullOrWhiteSpace(record.Fingerprint) || record.PathDigests is null)
            return (null, $"'{FileName}' is incomplete");

        return (new ImplementationState(new ImplementationFingerprint(record.Fingerprint), record.BaseIdentity, new Dictionary<string, string>(record.PathDigests, StringComparer.Ordinal)), null);
    }
}
