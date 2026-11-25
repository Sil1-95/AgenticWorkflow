using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticWorkflow.Application;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Core;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.State;

/// <summary>Descriptive facts about a run, written once before the agent starts. It holds no workflow state: the event stream is the only authority for that.</summary>
internal sealed record RunMetadata(
    [property: JsonPropertyName("metadataVersion")] int MetadataVersion,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("configPath")] string ConfigPath,
    [property: JsonPropertyName("configSha256")] string ConfigSha256,
    [property: JsonPropertyName("semanticsSha256")] string? SemanticsSha256 = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// How the CLI process that executed the run ended, recorded for <c>aw status</c> as an annotation. It is a fact about a process, not about
/// the workflow (runtime status such as "the agent was blocked" is deliberately not workflow state), so it can never override what the
/// event stream says.
/// </summary>
internal sealed record RunOutcomeRecord(
    [property: JsonPropertyName("outcomeVersion")] int OutcomeVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("endedAt")] DateTimeOffset EndedAt,
    [property: JsonPropertyName("elapsedSeconds")] double ElapsedSeconds)
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// How the agent's conversation was obtained in that process, when it was a continuation ("resumed native conversation", "fresh session ...", "resume failed");
    /// absent for a first stretch and for records written before it existed. Provenance of a process, like the rest of this record.
    /// </summary>
    [JsonPropertyName("continuation")]
    public string? Continuation { get; init; }
}

/// <summary>A run's durable state could not be located or trusted. The message says which, and nothing is invented.</summary>
internal sealed class RunStateException(RunStateProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public RunStateProblem Problem { get; } = problem;
}

internal enum RunStateProblem
{
    /// <summary>There is no such run in the state root.</summary>
    NotFound,

    /// <summary>The run exists but its durable state cannot be read or replayed.</summary>
    Unreadable,
}

/// <summary>
/// The durable per-run layout under the state root: <c>runs/&lt;run-id&gt;/workflow.db</c> (the authoritative SQLite event stream),
/// <c>metadata.json</c> and <c>outcome.json</c>. The run id is the workflow's <see cref="TaskId"/>. Nothing is ever placed in the target repository.
/// </summary>
internal sealed class RunStore(string root)
{
    public const string DatabaseFileName = "workflow.db";
    public const string MetadataFileName = "metadata.json";
    public const string OutcomeFileName = "outcome.json";
    public const string LiveLockFileName = "live.lock";
    public const string NativeInteractionFileName = "native-interaction.json";
    public const string SupervisionFileName = "supervision.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public string RunDirectory(TaskId runId) => Path.Combine(Root, "runs", runId.ToString());

    public string DatabasePath(TaskId runId) => Path.Combine(RunDirectory(runId), DatabaseFileName);

    /// <summary>
    /// Marks the run as live for as long as the returned handle is held: an exclusive lock on a file only the running process can hold, released
    /// by the operating system if that process dies. It is what makes "the agent session is still waiting for you" an honest statement.
    /// </summary>
    public IDisposable AcquireLiveLock(TaskId runId)
        => new FileStream(Path.Combine(RunDirectory(runId), LiveLockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    /// <summary>True while a process holds the run's live lock, that is, while the run's agent session can still be answered.</summary>
    public bool IsLive(TaskId runId)
    {
        var path = Path.Combine(RunDirectory(runId), LiveLockFileName);

        if (!File.Exists(path))
            return false;

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Creates the run's directory and metadata. The directory must not exist: a run id is never reused.</summary>
    public void Create(TaskId runId, RunMetadata metadata)
    {
        var directory = RunDirectory(runId);

        if (Directory.Exists(directory))
            throw new IOException($"The run directory '{directory}' already exists.");

        Directory.CreateDirectory(directory);
        WriteAtomically(Path.Combine(directory, MetadataFileName), JsonSerializer.Serialize(metadata, Json));
    }

    public void WriteOutcome(TaskId runId, RunOutcomeRecord outcome)
        => WriteAtomically(Path.Combine(RunDirectory(runId), OutcomeFileName), JsonSerializer.Serialize(outcome, Json));

    /// <summary>Records, for display only, that this run's live process waits for a person in the agent's own interface (<see cref="NativeInteractionRecord"/>).</summary>
    public void WriteNativeInteraction(TaskId runId, NativeInteractionRecord record)
        => WriteAtomically(Path.Combine(RunDirectory(runId), NativeInteractionFileName), JsonSerializer.Serialize(record, Json));

    /// <summary>The annotation of a native wait, or null plus the reason it is unavailable. A display aid: nothing is decided from it.</summary>
    public (NativeInteractionRecord? Record, string? Problem) ReadNativeInteraction(TaskId runId)
        => ReadJson<NativeInteractionRecord>(
            Path.Combine(RunDirectory(runId), NativeInteractionFileName),
            r => r.Version == NativeInteractionRecord.CurrentVersion && string.Equals(r.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the annotation of a native wait (and any half-written temporary). Nothing happens when there is none.</summary>
    public void ClearNativeInteraction(TaskId runId)
    {
        var path = Path.Combine(RunDirectory(runId), NativeInteractionFileName);

        File.Delete(path + ".tmp");
        File.Delete(path);
    }

    /// <summary>Records what the controller supervising this run's stretch started, for the next controller if this one dies (<see cref="SupervisionRecord"/>).</summary>
    public void WriteSupervision(TaskId runId, SupervisionRecord record)
        => WriteAtomically(Path.Combine(RunDirectory(runId), SupervisionFileName), JsonSerializer.Serialize(record, Json));

    /// <summary>The supervision record, or null plus the reason it is unavailable (a missing file is a reason, not a fault: it means no stretch was left unfinished).</summary>
    public (SupervisionRecord? Record, string? Problem) ReadSupervision(TaskId runId)
        => ReadJson<SupervisionRecord>(
            Path.Combine(RunDirectory(runId), SupervisionFileName),
            r => r.Version == SupervisionRecord.CurrentVersion && string.Equals(r.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes the supervision record (and any half-written temporary): the stretch ended in an orderly way.</summary>
    public void ClearSupervision(TaskId runId)
    {
        var path = Path.Combine(RunDirectory(runId), SupervisionFileName);

        File.Delete(path + ".tmp");
        File.Delete(path);
    }

    /// <summary>The metadata, or null plus the reason it is unavailable. Missing metadata never prevents replaying the stream.</summary>
    public (RunMetadata? Metadata, string? Problem) ReadMetadata(TaskId runId)
        => ReadJson<RunMetadata>(Path.Combine(RunDirectory(runId), MetadataFileName), m => m.MetadataVersion == RunMetadata.CurrentVersion);

    public (RunOutcomeRecord? Outcome, string? Problem) ReadOutcome(TaskId runId)
        => ReadJson<RunOutcomeRecord>(Path.Combine(RunDirectory(runId), OutcomeFileName), o => o.OutcomeVersion == RunOutcomeRecord.CurrentVersion);

    /// <summary>Opens the service over an existing run's event stream for reading. Never creates a run or a database.</summary>
    public async Task<WorkflowTaskService> OpenExistingAsync(TaskId runId, CancellationToken cancellationToken)
    {
        var directory = RunDirectory(runId);

        if (!Directory.Exists(directory))
            throw new RunStateException(RunStateProblem.NotFound, $"There is no run '{runId}' in '{Root}'.");

        var database = DatabasePath(runId);

        // An empty file would be initialised as a brand-new store by the SQLite adapter; a run that exists must already have history.
        if (!File.Exists(database) || new FileInfo(database).Length == 0)
            throw new RunStateException(RunStateProblem.Unreadable, $"The run '{runId}' has no event stream ('{database}' is missing or empty).");

        try
        {
            return Compose(await SqliteEventStore.OpenAsync(database, cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is EventStoreException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new RunStateException(RunStateProblem.Unreadable, $"The run's event stream '{database}' cannot be opened: {e.Message}", e);
        }
    }

    /// <summary>Opens the run's event stream and replays it to the run's current state. Read-only; anything that prevents a trustworthy replay is <see cref="RunStateProblem.Unreadable"/>.</summary>
    public async Task<WorkflowTask> ReplayAsync(TaskId runId, CancellationToken cancellationToken)
    {
        var service = await OpenExistingAsync(runId, cancellationToken).ConfigureAwait(false);

        try
        {
            var snapshot = await service.FindAsync(runId, cancellationToken).ConfigureAwait(false);

            return snapshot?.Task
                   ?? throw new RunStateException(RunStateProblem.Unreadable, $"The run's event stream holds no history for '{runId}'.");
        }
        catch (Exception e) when (e is EventStoreException or DomainException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new RunStateException(RunStateProblem.Unreadable, $"The run's event stream cannot be replayed: {e.Message}", e);
        }
    }

    /// <summary>The ids of the runs in the state root, found by their directory names (no run is opened). Empty, and nothing created, when the root has no runs.</summary>
    public IReadOnlyList<TaskId> ListRunIds()
    {
        var runs = Path.Combine(Root, "runs");

        if (!Directory.Exists(runs))
            return [];

        return Directory.EnumerateDirectories(runs)
            .Select(directory => Path.GetFileName(directory))
            .Where(name => Guid.TryParseExact(name, "D", out var guid) && guid != Guid.Empty)
            .Select(name => new TaskId(Guid.ParseExact(name, "D")))
            .ToList();
    }

    /// <summary>Opens the service over a new run's event stream, creating the database.</summary>
    public async Task<WorkflowTaskService> OpenNewAsync(TaskId runId, CancellationToken cancellationToken)
        => Compose(await SqliteEventStore.OpenAsync(DatabasePath(runId), cancellationToken: cancellationToken).ConfigureAwait(false));

    private static WorkflowTaskService Compose(SqliteEventStore store) => new(store, new GuidIdSource(), new SystemClock());

    /// <summary>The state root, from <c>AGENTICWORKFLOW_HOME</c> when set (an absolute path), otherwise <c>%LOCALAPPDATA%\AgenticWorkflow</c>.</summary>
    public static string ResolveRoot(Func<string, string?> getEnvironmentVariable, string localApplicationData)
    {
        var configured = getEnvironmentVariable(CliEnvironment.HomeVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new ConfigurationException($"{CliEnvironment.HomeVariable} must be an absolute path, not '{configured}'.");

            return Path.GetFullPath(configured).TrimEnd('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new ConfigurationException($"The local application data folder is unknown; set {CliEnvironment.HomeVariable} to choose where run state is kept.");

        return Path.Combine(localApplicationData, "AgenticWorkflow");
    }

    /// <summary>Run state must never live in the repository the agent edits.</summary>
    public static bool IsInside(string path, string container)
    {
        var parent = Path.GetFullPath(container).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        return (Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static (T? Value, string? Problem) ReadJson<T>(string path, Func<T, bool> versionIsSupported)
        where T : class
    {
        if (!File.Exists(path))
            return (null, $"'{Path.GetFileName(path)}' is missing");

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));

            if (value is null)
                return (null, $"'{Path.GetFileName(path)}' is empty");

            return versionIsSupported(value)
                ? (value, null)
                : (null, $"'{Path.GetFileName(path)}' has an unsupported version");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, $"'{Path.GetFileName(path)}' is unreadable ({e.GetType().Name})");
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
