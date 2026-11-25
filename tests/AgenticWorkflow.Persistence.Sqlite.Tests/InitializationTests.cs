using Microsoft.Data.Sqlite;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

public sealed class InitializationTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task New_database_is_created_at_schema_version_1_in_wal_mode()
    {
        Assert.False(File.Exists(_db.Path));

        await _db.OpenStoreAsync();

        Assert.True(File.Exists(_db.Path));
        Assert.Equal(1, _db.Scalar<int>("PRAGMA user_version"));
        Assert.Equal("wal", _db.Scalar<string>("PRAGMA journal_mode"));
        Assert.Equal(
            ["task_events", "task_streams"],
            Names("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
    }

    [Fact]
    public async Task Reopening_preserves_data_and_is_idempotent()
    {
        var stream = RichCompletedTask();
        var first = await _db.OpenStoreAsync();
        await PersistAsync(first, stream);

        var second = await _db.OpenStoreAsync();
        var third = await _db.OpenStoreAsync();

        Assert.Equal(1, _db.Scalar<int>("PRAGMA user_version"));
        Assert.Equal(stream.Events, await second.LoadAsync(TaskOne));
        Assert.Equal(stream.Events.Count, await third.GetVersionAsync(TaskOne));
    }

    [Fact]
    public async Task Initialization_does_not_erase_an_existing_database()
    {
        var stream = NewTask();
        await PersistAsync(await _db.OpenStoreAsync(), stream);
        var rowsBefore = _db.Scalar<long>("SELECT COUNT(*) FROM task_events");

        for (var i = 0; i < 3; i++)
            await _db.OpenStoreAsync();

        Assert.Equal(rowsBefore, _db.Scalar<long>("SELECT COUNT(*) FROM task_events"));
        Assert.Equal(1, _db.Scalar<long>("SELECT COUNT(*) FROM task_streams"));
    }

    [Fact]
    public async Task Unsupported_future_schema_version_is_rejected_and_left_untouched()
    {
        var stream = NewTask();
        await PersistAsync(await _db.OpenStoreAsync(), stream);
        _db.Execute("PRAGMA user_version = 2");

        var error = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => _db.OpenStoreAsync());

        Assert.Contains("2", error.Message);
        Assert.Equal(2, _db.Scalar<int>("PRAGMA user_version"));
        Assert.Equal(1, _db.Scalar<long>("SELECT COUNT(*) FROM task_events"));
    }

    [Fact]
    public async Task A_foreign_sqlite_database_is_rejected_not_adopted_or_wiped()
    {
        _db.Execute("CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT)");
        _db.Execute("INSERT INTO customers (name) VALUES ('keep me')");

        await Assert.ThrowsAsync<UnsupportedSchemaException>(() => _db.OpenStoreAsync());

        Assert.Equal(["customers"], Names("SELECT name FROM sqlite_master WHERE type = 'table'"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT name FROM customers"));
        Assert.Equal(0, _db.Scalar<int>("PRAGMA user_version"));
    }

    [Fact]
    public async Task A_zero_byte_file_is_a_new_database()
    {
        File.WriteAllBytes(_db.Path, []);

        await _db.OpenStoreAsync();

        Assert.Equal(1, _db.Scalar<int>("PRAGMA user_version"));
    }

    [Fact]
    public async Task A_version_1_database_missing_its_tables_is_reported_as_corrupt()
    {
        await _db.OpenStoreAsync();
        _db.Execute("DROP TABLE task_events");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => _db.OpenStoreAsync());
    }

    [Fact]
    public async Task Concurrent_first_time_initialization_converges_on_one_schema()
    {
        var opens = Enumerable.Range(0, 6).Select(_ => Task.Run(() => _db.OpenStoreAsync()));

        await Task.WhenAll(opens);

        Assert.Equal(1, _db.Scalar<int>("PRAGMA user_version"));
        Assert.Equal(2, _db.Scalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":memory:")]
    [InlineData("file:shared?mode=memory&cache=shared")]
    public async Task Only_plain_file_paths_are_accepted(string path)
        => await Assert.ThrowsAsync<ArgumentException>(() => SqliteEventStore.OpenAsync(path));

    [Fact]
    public async Task Cancelled_initialization_creates_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SqliteEventStore.OpenAsync(_db.Path, null, cancelled.Token));

        Assert.True(!File.Exists(_db.Path) || _db.Scalar<int>("PRAGMA user_version") == 0);
    }

    [Fact]
    public async Task No_connection_outlives_an_operation()
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, RichCompletedTask());
        await store.LoadAsync(TaskOne);

        // On Windows a held handle would make this throw. Deleting the files proves nothing is left open.
        foreach (var file in new[] { _db.Path, _db.Path + "-wal", _db.Path + "-shm" })
            File.Delete(file);

        Assert.False(File.Exists(_db.Path));
    }

    [Fact]
    public async Task Schema_declares_the_required_uniqueness_guarantees()
    {
        var store = await _db.OpenStoreAsync();
        var stream = NewTask();
        await PersistAsync(store, stream);
        var first = stream.Events[0];

        // (task_id, sequence) is the primary key, event_id is unique: raw inserts that repeat either are refused.
        var sameSequence = Assert.Throws<SqliteException>(() => InsertRaw(Guid.NewGuid().ToString("D"), first.TaskId, 1));
        var sameEventId = Assert.Throws<SqliteException>(() => InsertRaw(first.Id.Value.ToString("D"), first.TaskId, 2));

        Assert.Equal(19, sameSequence.SqliteErrorCode);
        Assert.Equal(19, sameEventId.SqliteErrorCode);
    }

    private void InsertRaw(string eventId, TaskId taskId, long sequence)
        => _db.Execute(
            "INSERT INTO task_events VALUES ($t, $s, $e, '2026-01-01T12:00:00.0000000Z', 'task_created', 1, '{}')",
            ("$t", taskId.Value.ToString("D")),
            ("$s", sequence),
            ("$e", eventId));

    private List<string> Names(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }
}
