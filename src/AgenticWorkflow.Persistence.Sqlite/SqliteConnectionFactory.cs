using Microsoft.Data.Sqlite;

namespace AgenticWorkflow.Persistence.Sqlite;

/// <summary>
/// Opens short-lived, individually configured connections to one database file. There is no shared or static
/// connection: every store operation opens its own and disposes it, so no handle outlives an operation.
/// </summary>
internal sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;

    public SqliteConnectionFactory(string databasePath, TimeSpan busyTimeout)
    {
        _busyTimeoutMilliseconds = checked((int)busyTimeout.TotalMilliseconds);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // No pooling: disposing a connection really closes the file (a pooled handle would keep the file
            // locked and would make "close, then reopen" not mean what it says).
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, _busyTimeoutMilliseconds / 1000),
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            // Per-connection settings. journal_mode=WAL is persistent in the file and is set once, at initialization.
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}; PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
