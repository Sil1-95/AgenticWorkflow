using System.Security.Cryptography;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Cli.Tests;

/// <summary><c>aw runs</c>: discovering recent runs after the run id was lost. Model-free; the state is the real durable layout.</summary>
public sealed class RunsCliTests
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A run that verified and now awaits the human's ACCEPT (exit 17).</summary>
    private static async Task<TaskId> StartAwaitingRunAsync(CliHarness harness, string task)
    {
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        harness.ResetOutput();
        Assert.Equal(17, await harness.RunAsync("run", task));

        return harness.PrintedRunId();
    }

    /// <summary>A run that stopped at a native interaction nobody could answer (exit 15): BLOCKED.</summary>
    private static async Task<TaskId> StartBlockedRunAsync(CliHarness harness, string task)
    {
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait());

        harness.ResetOutput();
        Assert.Equal(15, await harness.RunAsync("run", task));

        return harness.PrintedRunId();
    }

    [Fact]
    public async Task With_no_runs_it_says_so_and_creates_nothing()
    {
        using var harness = new CliHarness();

        var exit = await harness.RunAsync("runs");

        Assert.Equal(0, exit);
        Assert.Contains("No runs", harness.Out.ToString());
        Assert.False(Directory.Exists(Path.Combine(harness.Home, "runs")));
        Assert.Equal(0, harness.Factory.Created);
    }

    [Fact]
    public async Task It_lists_recent_runs_newest_first_with_enough_to_tell_them_apart()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var first = await StartAwaitingRunAsync(harness, "Rename GetMeal to GetFood");
        var second = await StartBlockedRunAsync(harness, "Add logging\nwith a second line that is not shown");
        harness.ResetOutput();

        var exit = await harness.RunAsync("runs");

        var output = harness.Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains($"{first}  AWAITING_HUMAN (awaits your decision)", output);
        Assert.Contains($"{second}  BLOCKED", output);
        Assert.True(output.IndexOf(second.ToString(), StringComparison.Ordinal) < output.IndexOf(first.ToString(), StringComparison.Ordinal), "the newest run is listed first");
        Assert.Contains("acceptance human", output);
        Assert.Contains($"workspace: {harness.Workspace}", output);
        Assert.Contains("task: Rename GetMeal to GetFood", output);
        Assert.Contains("task: Add logging", output);
        Assert.DoesNotContain("second line", output);
        Assert.Contains("aw status <run-id>", output);
        Assert.Equal(2, harness.Factory.Created); // listing started no runtime
    }

    [Fact]
    public async Task The_limit_keeps_only_the_newest_runs_and_says_how_many_were_left_out()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var older = await StartAwaitingRunAsync(harness, "older task");
        var newer = await StartBlockedRunAsync(harness, "newer task");
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs", "--limit", "1"));

        var output = harness.Out.ToString();
        Assert.Contains(newer.ToString(), output);
        Assert.DoesNotContain(older.ToString(), output);
        Assert.Contains("(1 of 2)", output);
        Assert.Contains("1 older run(s) not shown", output);
    }

    [Fact]
    public async Task A_long_task_is_cut_to_one_short_line_and_control_characters_are_not_echoed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        await StartBlockedRunAsync(harness, "\u001b[31m" + new string('x', 200));
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var taskLine = harness.Out.ToString().Split('\n').Single(l => l.TrimStart().StartsWith("task: ", StringComparison.Ordinal)).TrimEnd();
        Assert.DoesNotContain('\u001b', harness.Out.ToString());
        Assert.EndsWith("...", taskLine);
        Assert.True(taskLine.Length <= "  task: ".Length + 80);
    }

    [Fact]
    public async Task It_never_modifies_the_run_files_and_does_not_print_configuration_details()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var runId = await StartAwaitingRunAsync(harness, "task");
        var store = new RunStore(harness.Home);
        var files = Directory.GetFiles(store.RunDirectory(runId));
        var before = files.Select(Hash).ToArray();
        var (metadata, _) = store.ReadMetadata(runId);
        harness.ResetOutput();

        await harness.RunAsync("runs");

        Assert.Equal(files, Directory.GetFiles(store.RunDirectory(runId)));
        Assert.Equal(before, files.Select(Hash).ToArray());
        Assert.DoesNotContain(metadata!.ConfigSha256, harness.Out.ToString());
        Assert.DoesNotContain(metadata.ConfigPath, harness.Out.ToString());
    }

    [Fact]
    public async Task An_unreadable_run_is_listed_as_such_and_does_not_hide_the_others()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var healthy = await StartAwaitingRunAsync(harness, "healthy task");
        var broken = await StartBlockedRunAsync(harness, "broken task");
        var database = new RunStore(harness.Home).DatabasePath(broken);
        File.WriteAllText(database, "this is not a sqlite database at all, just text that is long enough to look like a file");
        var before = Hash(database);
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var output = harness.Out.ToString();
        Assert.Contains($"{broken}  UNREADABLE", output);
        Assert.Contains($"{healthy}  AWAITING_HUMAN", output);
        Assert.Equal(before, Hash(database));
    }

    [Fact]
    public async Task A_run_directory_without_an_event_stream_is_listed_unreadable_and_no_database_is_created()
    {
        using var harness = new CliHarness();
        var store = new RunStore(harness.Home);
        var id = new TaskId(Guid.CreateVersion7());
        Directory.CreateDirectory(store.RunDirectory(id));
        Directory.CreateDirectory(Path.Combine(store.Root, "runs", "not-a-run"));

        Assert.Equal(0, await harness.RunAsync("runs"));

        Assert.Contains($"{id}  UNREADABLE", harness.Out.ToString());
        Assert.DoesNotContain("not-a-run", harness.Out.ToString());
        Assert.False(File.Exists(store.DatabasePath(id)));
    }

    private const string NotADatabase = "this is not a sqlite database at all, just text that is long enough to look like a file";

    /// <summary>Every file and directory under <paramref name="root"/> with a content hash and last write time, so "nothing was touched" is a plain equality.</summary>
    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                entry => Path.GetRelativePath(root, entry),
                entry => File.Exists(entry) ? $"{Hash(entry)} {File.GetLastWriteTimeUtc(entry).Ticks}" : "<directory>");

    private sealed record MixedState(TaskId Awaiting, TaskId Blocked, TaskId CorruptDatabase, TaskId MissingDatabase, TaskId EmptyDatabase, TaskId DatabaseIsDirectory, TaskId NothingReadable)
    {
        public IEnumerable<TaskId> Healthy => [Awaiting, Blocked];

        public IEnumerable<TaskId> Damaged => [CorruptDatabase, MissingDatabase, EmptyDatabase, DatabaseIsDirectory, NothingReadable];
    }

    /// <summary>Two healthy runs and five damaged in different ways; the one with nothing readable has no start time at all.</summary>
    private static async Task<MixedState> CreateMixedStateAsync(CliHarness harness)
    {
        var store = new RunStore(harness.Home);
        var awaiting = await StartAwaitingRunAsync(harness, "healthy awaiting task");
        var blocked = await StartBlockedRunAsync(harness, "healthy blocked task");
        var corrupt = await StartBlockedRunAsync(harness, "corrupt database task");
        var missing = await StartBlockedRunAsync(harness, "missing database task");
        var empty = await StartBlockedRunAsync(harness, "empty database task");
        var directory = await StartBlockedRunAsync(harness, "database is a directory task");
        var nothing = await StartBlockedRunAsync(harness, "nothing readable task");

        File.WriteAllText(store.DatabasePath(corrupt), NotADatabase);
        File.Delete(store.DatabasePath(missing));
        File.WriteAllText(store.DatabasePath(empty), string.Empty);
        File.Delete(store.DatabasePath(directory));
        Directory.CreateDirectory(store.DatabasePath(directory));
        File.WriteAllText(store.DatabasePath(nothing), NotADatabase);
        File.WriteAllText(Path.Combine(store.RunDirectory(nothing), RunStore.MetadataFileName), "{ not json");

        harness.ResetOutput();

        return new MixedState(awaiting, blocked, corrupt, missing, empty, directory, nothing);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"metadataVersion":1,"workspace":5}""")]
    [InlineData("""{"metadataVersion":1,"runId":"x","workspace":null,"createdAt":"2026-01-01T00:00:00Z","configPath":"c","configSha256":"h"}""")]
    [InlineData("\u0000\u0001\u0002 binary �")]
    public async Task A_malformed_metadata_file_does_not_stop_the_listing_and_the_run_still_shows_its_replayed_state(string metadata)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var healthy = await StartAwaitingRunAsync(harness, "healthy task");
        var damaged = await StartBlockedRunAsync(harness, "damaged metadata task");
        var path = Path.Combine(new RunStore(harness.Home).RunDirectory(damaged), RunStore.MetadataFileName);
        File.WriteAllText(path, metadata);
        var before = Hash(path);
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var output = harness.Out.ToString();
        Assert.Contains($"{healthy}  AWAITING_HUMAN", output);
        Assert.Contains($"{damaged}  BLOCKED", output); // the event stream is the authority; only the workspace is unknown
        Assert.Contains("workspace: unknown", output);
        Assert.Contains($"workspace: {harness.Workspace}", output);
        Assert.Equal(before, Hash(path));
        Assert.Equal(string.Empty, harness.Error.ToString());
    }

    [Fact]
    public async Task A_missing_empty_or_misplaced_database_is_unreadable_and_a_healthy_run_is_still_listed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var healthy = await StartAwaitingRunAsync(harness, "healthy task");
        var store = new RunStore(harness.Home);
        var missing = await StartBlockedRunAsync(harness, "missing database");
        var empty = await StartBlockedRunAsync(harness, "empty database");
        var directory = await StartBlockedRunAsync(harness, "database is a directory");
        File.Delete(store.DatabasePath(missing));
        File.WriteAllText(store.DatabasePath(empty), string.Empty);
        File.Delete(store.DatabasePath(directory));
        Directory.CreateDirectory(store.DatabasePath(directory));
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var output = harness.Out.ToString();
        Assert.Contains($"{healthy}  AWAITING_HUMAN", output);
        Assert.Contains($"{missing}  UNREADABLE", output);
        Assert.Contains($"{empty}  UNREADABLE", output);
        Assert.Contains($"{directory}  UNREADABLE", output);
        Assert.False(File.Exists(store.DatabasePath(missing)));
        Assert.Equal(0, new FileInfo(store.DatabasePath(empty)).Length);
    }

    [Fact]
    public async Task Several_unreadable_runs_do_not_suppress_the_healthy_ones_and_the_order_is_deterministic()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var state = await CreateMixedStateAsync(harness);

        Assert.Equal(0, await harness.RunAsync("runs"));
        var first = harness.Out.ToString();
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("runs"));
        var second = harness.Out.ToString();

        Assert.Equal(first, second);
        Assert.Contains("(7 of 7)", first);

        foreach (var id in state.Damaged)
            Assert.Contains($"{id}  UNREADABLE", first);

        Assert.Contains($"{state.Awaiting}  AWAITING_HUMAN", first);
        Assert.Contains($"{state.Blocked}  BLOCKED", first);

        // A run with no usable start time at all sorts after every run that has one.
        var last = first.IndexOf(state.NothingReadable.ToString(), StringComparison.Ordinal);
        Assert.All(state.Healthy.Concat(state.Damaged).Where(id => id != state.NothingReadable), id => Assert.True(first.IndexOf(id.ToString(), StringComparison.Ordinal) < last));
        Assert.Matches($"{state.NothingReadable}  UNREADABLE\\r?\\n", first);
        Assert.Equal(string.Empty, harness.Error.ToString());
    }

    [Fact]
    public async Task Listing_damaged_runs_is_read_only_for_the_whole_state_root()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        await CreateMixedStateAsync(harness);
        var before = Snapshot(harness.Home);

        Assert.Equal(0, await harness.RunAsync("runs"));
        Assert.Equal(0, await harness.RunAsync("runs", "--limit", "2"));

        Assert.Equal(before, Snapshot(harness.Home));
    }

    [Fact]
    public async Task A_database_the_file_system_will_not_open_is_unreadable_and_the_others_are_still_listed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var healthy = await StartAwaitingRunAsync(harness, "healthy task");
        var locked = await StartBlockedRunAsync(harness, "locked database");
        var database = new RunStore(harness.Home).DatabasePath(locked);
        var before = Hash(database);
        harness.ResetOutput();

        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(0, await harness.RunAsync("runs"));

        var output = harness.Out.ToString();
        Assert.Contains($"{locked}  UNREADABLE", output);
        Assert.Contains($"{healthy}  AWAITING_HUMAN", output);
        Assert.Equal(before, Hash(database));
    }

    [Fact]
    public async Task Cancellation_is_not_a_damaged_run_and_stops_the_listing()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        await StartBlockedRunAsync(harness, "a task");
        harness.ResetOutput();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exit = await harness.RunAsync(cancelled.Token, "runs");

        Assert.Equal(130, exit);
        Assert.DoesNotContain("UNREADABLE", harness.Out.ToString());
    }

    [Theory]
    [InlineData("runs", "extra")]
    [InlineData("runs", "--limit", "0")]
    [InlineData("runs", "--limit", "-1")]
    [InlineData("runs", "--limit", "many")]
    [InlineData("runs", "--limit", "1001")]
    [InlineData("runs", "--limit")]
    [InlineData("runs", "--unknown")]
    public async Task Bad_runs_command_lines_are_usage_errors(params string[] args)
    {
        using var harness = new CliHarness();

        Assert.Equal(2, await harness.RunAsync(args));
        Assert.NotEqual(string.Empty, harness.Error.ToString());
        Assert.Equal(0, harness.Factory.Created);
    }

    [Fact]
    public async Task Help_documents_the_command()
    {
        using var harness = new CliHarness();

        Assert.Equal(0, await harness.RunAsync("--help"));
        Assert.Contains("aw runs [--limit <n>]", harness.Out.ToString());

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("runs", "--help"));
        Assert.Contains("Usage: aw runs", harness.Out.ToString());
    }
}
