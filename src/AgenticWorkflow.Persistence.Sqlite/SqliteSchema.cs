using Microsoft.Data.Sqlite;

namespace AgenticWorkflow.Persistence.Sqlite;

/// <summary>
/// Database creation and schema versioning. The schema version is SQLite's <c>PRAGMA user_version</c>:
/// <c>0</c> = never initialized, <c>N</c> = schema version N. <see cref="Steps"/> holds, per version, the script
/// that brings a database from version N-1 to N; today there is only the creation script (version 1). A future
/// schema change is added as the next step, deliberately, and applied here inside the same transaction.
/// An unknown or newer version is rejected and the file is left exactly as found.
/// </summary>
internal static class SqliteSchema
{
    private static readonly string[] Steps =
    [
        // Version 1.
        // task_streams.version is concurrency/index metadata only (the last sequence appended); the events are the
        // authoritative record and Load cross-checks the two. STRICT enforces column types. WITHOUT ROWID means
        // there is no hidden insertion counter that could be mistaken for a global order.
        """
        CREATE TABLE task_streams (
            task_id TEXT NOT NULL PRIMARY KEY,
            version INTEGER NOT NULL CHECK (version >= 1)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE task_events (
            task_id                TEXT    NOT NULL REFERENCES task_streams (task_id),
            sequence               INTEGER NOT NULL CHECK (sequence >= 1),
            event_id               TEXT    NOT NULL UNIQUE,
            timestamp_utc          TEXT    NOT NULL,
            event_kind             TEXT    NOT NULL CHECK (event_kind <> ''),
            payload_schema_version INTEGER NOT NULL CHECK (payload_schema_version >= 1),
            payload_json           TEXT    NOT NULL,
            PRIMARY KEY (task_id, sequence)
        ) STRICT, WITHOUT ROWID;
        """,
    ];

    private static readonly string[] RequiredTables = ["task_streams", "task_events"];

    /// <summary>The schema version this build creates and reads.</summary>
    public static int CurrentVersion => Steps.Length;

    public static async Task EnsureInitializedAsync(SqliteConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // IMMEDIATE takes the write lock up front, so two processes initializing at once are serialized and the
        // second one sees the first one's result when it reads the version below.
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var version = await ReadVersionAsync(connection, cancellationToken);

            if (version > CurrentVersion)
                throw new UnsupportedSchemaException(
                    $"The database has schema version {version}, newer than the {CurrentVersion} this build supports. It was left untouched.");

            if (version < 0)
                throw new UnsupportedSchemaException($"The database reports an invalid schema version {version}.");

            if (version == 0 && await CountUserObjectsAsync(connection, cancellationToken) > 0)
                throw new UnsupportedSchemaException(
                    "The file is a SQLite database that was not created by AgenticWorkflow (schema version 0 but it already contains tables). It was left untouched.");

            for (var step = version; step < CurrentVersion; step++)
            {
                await ExecuteAsync(connection, Steps[step], cancellationToken);
                await ExecuteAsync(connection, $"PRAGMA user_version = {step + 1};", cancellationToken);
            }

            if (version > 0)
                await VerifyShapeAsync(connection, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        // Outside any transaction. Persistent in the database file; a no-op if already WAL.
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
    }

    private static async Task VerifyShapeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in RequiredTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);

            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new EventStoreCorruptionException(
                    $"The database claims schema version {CurrentVersion} but table '{table}' is missing.");
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> CountUserObjectsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
