using AgenticWorkflow.Application.Persistence;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Persistence.Sqlite.Serialization;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflow.Persistence.Sqlite;

/// <summary>
/// Durable, append-only storage of per-task event streams in one SQLite file. It stores the envelopes Core
/// produced exactly as given (it never invents events, ids or timestamps) and hands them back in order. It does
/// not judge them: whether a history is a valid task is decided by <c>TaskStream.Replay</c>.
/// <para>
/// The store holds no connection. Each operation opens its own, so instances are cheap, thread-safe, and
/// "closing" the database simply means no operation is running. Obtain one with <see cref="OpenAsync"/>, which
/// creates or validates the database first, so a store instance always sits on a supported schema.
/// </para>
/// </summary>
public sealed class SqliteEventStore : ITaskEventStore
{
    /// <summary>Default time a writer waits for another writer's transaction to finish before failing with a busy error.</summary>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    private const int SqliteConstraintErrorCode = 19;

    private readonly SqliteConnectionFactory _connections;

    private SqliteEventStore(SqliteConnectionFactory connections) => _connections = connections;

    /// <summary>
    /// Creates the database at <paramref name="databasePath"/> if it does not exist (schema version 1, WAL), or
    /// validates and opens an existing one. An existing database is never wiped or altered when its schema
    /// version is unsupported; <see cref="UnsupportedSchemaException"/> is thrown instead. The parent directory
    /// must already exist.
    /// </summary>
    public static async Task<SqliteEventStore> OpenAsync(
        string databasePath,
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database file path is required.", nameof(databasePath));

        if (databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Only plain file paths are supported: every operation opens its own connection, which an in-memory database cannot share.",
                nameof(databasePath));

        var timeout = busyTimeout ?? DefaultBusyTimeout;
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(busyTimeout), timeout, "The busy timeout cannot be negative.");

        var connections = new SqliteConnectionFactory(databasePath, timeout);
        await SqliteSchema.EnsureInitializedAsync(connections, cancellationToken);
        return new SqliteEventStore(connections);
    }

    /// <summary>
    /// Atomically appends <paramref name="events"/> to the end of <paramref name="taskId"/>'s stream.
    /// <paramref name="expectedVersion"/> is the sequence the caller believes the stream currently ends at
    /// (0 for a task that has no stream yet). If another writer has moved the stream, nothing is written and
    /// <see cref="StreamVersionConflictException"/> says what the version actually is. All events are stored or
    /// none are. Returns the new stream version (the last appended sequence).
    /// <para>
    /// The envelopes must belong to <paramref name="taskId"/> and carry the contiguous sequences
    /// <c>expectedVersion + 1 ...</c>; violations are <see cref="ArgumentException"/>s raised before any I/O.
    /// </para>
    /// </summary>
    public async Task<long> AppendAsync(
        TaskId taskId,
        long expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken cancellationToken = default)
    {
        var rows = PrepareRows(taskId, expectedVersion, events);
        var newVersion = expectedVersion + rows.Count;

        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // BEGIN IMMEDIATE: the write lock is taken before the version is read, so two writers cannot both read
        // version N and both proceed. The loser waits, then reads the winner's version and conflicts. Enforced by
        // SQLite's file locking, hence valid across processes. Disposing without Commit rolls everything back.
        await using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            var actualVersion = await ReadVersionAsync(connection, taskId, cancellationToken);
            if (actualVersion != expectedVersion)
                throw new StreamVersionConflictException(taskId, expectedVersion, actualVersion);

            await UpsertStreamAsync(connection, taskId, expectedVersion, newVersion, cancellationToken);
            await InsertEventsAsync(connection, taskId, rows, cancellationToken);

            // Last cancellation point. Once Commit starts it either completes or fails as a unit.
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraintErrorCode)
        {
            throw new AppendRejectedException(
                $"Task {taskId}: the store's constraints rejected the append (for example an event id that already exists); nothing was written. {e.Message}",
                e);
        }

        return newVersion;
    }

    /// <summary>
    /// Returns the task's complete event history in sequence order, or an empty list if the task has no stream.
    /// The read is one consistent snapshot. Stored data that cannot be trusted raises
    /// <see cref="EventStoreCorruptionException"/>; nothing is repaired or skipped.
    /// </summary>
    public async Task<IReadOnlyList<EventEnvelope>> LoadAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var version = await ReadVersionAsync(connection, taskId, cancellationToken);
        if (version == 0)
            return [];

        var events = new List<EventEnvelope>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, timestamp_utc, event_kind, payload_schema_version, payload_json
            FROM task_events
            WHERE task_id = $task
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$task", Text(taskId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var expectedSequence = events.Count + 1;
            var sequence = reader.GetInt64(0);
            if (sequence != expectedSequence)
                throw new EventStoreCorruptionException(
                    $"The stored sequence is broken: expected {expectedSequence} but found {sequence}.",
                    taskId,
                    sequence);

            events.Add(ReadEnvelope(taskId, sequence, reader));
        }

        if (events.Count != version)
            throw new EventStoreCorruptionException(
                $"The stream metadata says version {version} but {events.Count} events are stored.",
                taskId);

        return events;
    }

    /// <summary>The sequence the task's stream currently ends at; 0 if it has no stream. Useful as an expected version.</summary>
    public async Task<long> GetVersionAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        return await ReadVersionAsync(connection, taskId, cancellationToken);
    }

    private static List<PendingRow> PrepareRows(TaskId taskId, long expectedVersion, IReadOnlyList<EventEnvelope> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (expectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), expectedVersion, "The expected version cannot be negative.");

        if (events.Count == 0)
            throw new ArgumentException("There is nothing to append.", nameof(events));

        var seen = new HashSet<EventId>();
        var rows = new List<PendingRow>(events.Count);

        for (var i = 0; i < events.Count; i++)
        {
            var envelope = events[i];
            var expectedSequence = expectedVersion + 1 + i;

            if (envelope.TaskId != taskId)
                throw new ArgumentException(
                    $"Event {envelope.Id} belongs to task {envelope.TaskId}, not {taskId}.", nameof(events));

            if (envelope.Sequence != expectedSequence)
                throw new ArgumentException(
                    $"Event {envelope.Id} has sequence {envelope.Sequence} but {expectedSequence} is required (expected version {expectedVersion}).",
                    nameof(events));

            if (!seen.Add(envelope.Id))
                throw new ArgumentException($"Event id {envelope.Id} appears more than once in the batch.", nameof(events));

            // Serialization happens here, before the database is touched, so an unserializable event can never
            // leave a partial batch behind.
            rows.Add(new PendingRow(envelope, EventCodec.Encode(envelope.Payload)));
        }

        return rows;
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection, TaskId taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM task_streams WHERE task_id = $task";
        command.Parameters.AddWithValue("$task", Text(taskId));

        return await command.ExecuteScalarAsync(cancellationToken) is long version ? version : 0;
    }

    private static async Task UpsertStreamAsync(
        SqliteConnection connection,
        TaskId taskId,
        long expectedVersion,
        long newVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$task", Text(taskId));
        command.Parameters.AddWithValue("$new", newVersion);

        if (expectedVersion == 0)
        {
            command.CommandText = "INSERT INTO task_streams (task_id, version) VALUES ($task, $new)";
        }
        else
        {
            command.CommandText = "UPDATE task_streams SET version = $new WHERE task_id = $task AND version = $expected";
            command.Parameters.AddWithValue("$expected", expectedVersion);
        }

        // Defense in depth: under the write lock this always affects exactly one row.
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new StreamVersionConflictException(taskId, expectedVersion, await ReadVersionAsync(connection, taskId, cancellationToken));
    }

    private static async Task InsertEventsAsync(
        SqliteConnection connection,
        TaskId taskId,
        List<PendingRow> rows,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_events (task_id, sequence, event_id, timestamp_utc, event_kind, payload_schema_version, payload_json)
            VALUES ($task, $sequence, $event, $timestamp, $kind, $version, $payload)
            """;

        var task = command.Parameters.Add("$task", SqliteType.Text);
        var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
        var eventId = command.Parameters.Add("$event", SqliteType.Text);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var version = command.Parameters.Add("$version", SqliteType.Integer);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (var (envelope, encoded) in rows)
        {
            task.Value = Text(taskId);
            sequence.Value = envelope.Sequence;
            eventId.Value = Text(envelope.Id);
            timestamp.Value = Timestamps.ToText(envelope.Timestamp);
            kind.Value = encoded.Kind;
            version.Value = encoded.SchemaVersion;
            payload.Value = encoded.Json;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static EventEnvelope ReadEnvelope(TaskId taskId, long sequence, SqliteDataReader reader)
    {
        try
        {
            var eventId = new EventId(PayloadMapper.ParseGuid(reader.GetString(1)));
            var timestamp = PayloadMapper.ParseInstant(reader.GetString(2));
            var kind = reader.GetString(3);
            var schemaVersion = reader.GetInt32(4);
            var json = reader.GetString(5);

            var payload = EventCodec.Decode(taskId, sequence, kind, schemaVersion, json);
            return new EventEnvelope(eventId, taskId, sequence, timestamp, payload);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException)
        {
            throw new EventStoreCorruptionException($"A stored column is invalid: {e.Message}", taskId, sequence, e);
        }
    }

    private static string Text(TaskId id) => PayloadMapper.Text(id.Value);

    private static string Text(EventId id) => PayloadMapper.Text(id.Value);

    private readonly record struct PendingRow(EventEnvelope Envelope, EncodedPayload Encoded);
}
