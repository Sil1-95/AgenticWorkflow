using System.Text.Json;

namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// Live M0 smoke against a real herdr-core in an isolated session. Run with
/// <c>AGENTICWORKFLOW_LIVE_HERDR=1 dotnet test --filter Category=Live</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class HerdrLiveSmokeTests : IClassFixture<LiveHerdrFixture>
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    private readonly LiveHerdrFixture _fixture;

    public HerdrLiveSmokeTests(LiveHerdrFixture fixture)
    {
        _fixture = fixture;
    }

    private HcClient Client => _fixture.Client;

    [LiveFact]
    public async Task Hc_is_reachable_and_speaks_structured_json()
    {
        var version = await Client.GetVersionAsync();
        var status = await Client.GetServerStatusAsync();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(version, status.GetProperty("version").GetString());

        // The isolated core holds only terminals created by this run (each carries the unique marker label).
        Assert.All(await Client.ListAsync(includeFinished: true), terminal => Assert.Equal(_fixture.Marker, terminal.Labels["aw_m0"]));
    }

    [LiveFact]
    public async Task Plain_terminal_lifecycle_create_identify_query_send_wait_read_cleanup()
    {
        // Program prints a marker, reads one line, echoes it, then exits.
        var marker = _fixture.Marker;
        var request = _fixture.Request("life", "cmd.exe", "/v:on", "/d", "/c", $"echo READY_{marker} & set /p x= & echo GOT:!x!");

        // 1-3: create, obtain stable identity, labelled with the unique marker.
        var created = _fixture.Track(await Client.RunBackgroundAsync(request));
        Assert.Matches(@"^t\d+$", created.Address);
        Assert.Equal(marker, created.Labels["aw_m0"]);

        // 4: structured state query by address and by name returns the same terminal.
        var byAddress = await Client.GetAsync(created.Address);
        var byName = await Client.GetAsync(request.Name!);
        Assert.True(byAddress.IsLive);
        Assert.Equal(byAddress.Address, byName.Address);
        Assert.Contains(await Client.ListAsync(), terminal => terminal.Address == created.Address);

        // 5-6: send input (it is buffered until set /p reads it), then wait on lifecycle, not on sleep.
        await Client.SendAsync(created.Address, $"PING_{marker}");
        var waited = await Client.WaitAsync(created.Address, Bounded);
        Assert.True(waited.Ended);
        Assert.Equal("exit", waited.Reason);
        Assert.Equal(0, waited.ExitCode);

        // 7: structured terminal facts after the exit, and the output it produced.
        var finished = await Client.GetAsync(created.Address);
        Assert.Equal("finished", finished.State);
        Assert.Equal("exited", finished.Reason);
        Assert.Equal(0, finished.ExitCode);

        var read = await Client.ReadAsync(created.Address);
        Assert.Contains($"READY_{marker}", read.Text);
        Assert.Contains($"GOT:PING_{marker}", read.Text);

        // 8: remove only what this test created; it disappears from the full listing.
        await Client.RemoveAsync(created.Address);
        Assert.DoesNotContain(await Client.ListAsync(includeFinished: true), terminal => terminal.Address == created.Address);
    }

    [LiveFact]
    public async Task Wait_for_output_then_kill_leaves_a_finished_terminal()
    {
        var marker = _fixture.Marker;

        // The marker prints ~3s after start so the wait is guaranteed to be registered first
        // (hc only matches output printed AFTER the wait begins).
        var created = _fixture.Track(await Client.RunBackgroundAsync(_fixture.Request(
            "kill", "cmd.exe", "/v:on", "/d", "/c", $"ping -n 4 127.0.0.1 >nul & echo LATE_{marker} & set /p x=")));

        var waited = await Client.WaitAsync(created.Address, Bounded, output: $"LATE_{marker}");
        Assert.True(waited.Ended);
        Assert.Equal("output", waited.Reason);
        Assert.True((await Client.GetAsync(created.Address)).IsLive);

        await Client.KillAsync(created.Address);
        var killed = await Client.GetAsync(created.Address);
        Assert.Equal("finished", killed.State);
        Assert.Equal("killed", killed.Reason);

        await Client.RemoveAsync(created.Address);
    }

    [LiveFact]
    public async Task Wait_with_no_exit_is_a_timeout_check_in()
    {
        var created = _fixture.Track(await Client.RunBackgroundAsync(_fixture.Request(
            "idle", "cmd.exe", "/v:on", "/d", "/c", "set /p x=")));

        var waited = await Client.WaitAsync(created.Address, TimeSpan.FromSeconds(1));

        Assert.False(waited.Ended);
        Assert.Equal("timeout", waited.Reason);
    }

    [LiveFact]
    public async Task Missing_terminal_is_a_structured_not_found_error()
    {
        var exception = await Assert.ThrowsAsync<HerdrCommandException>(() => Client.GetAsync("aw-m0-no-such-terminal"));

        Assert.Equal("not_found", exception.Kind);
        Assert.Equal(3, exception.ExitCode);
    }

    [LiveFact]
    public async Task Sending_to_a_finished_terminal_is_refused_with_kind_finished()
    {
        var created = _fixture.Track(await Client.RunBackgroundAsync(_fixture.Request(
            "done", "cmd.exe", "/d", "/c", "exit /b 5")));
        var waited = await Client.WaitAsync(created.Address, Bounded);
        Assert.Equal(5, waited.ExitCode);

        var exception = await Assert.ThrowsAsync<HerdrCommandException>(() => Client.SendAsync(created.Address, "late"));

        Assert.Equal("finished", exception.Kind);
        Assert.Equal(11, exception.ExitCode);
    }

    [LiveFact]
    public async Task Direct_pipe_handshake_and_listing_match_the_cli()
    {
        await using var pipe = await HerdrPipeConnection.ConnectAsync(_fixture.Session, TimeSpan.FromSeconds(5));

        Assert.Equal(1, pipe.Hello.GetProperty("generation").GetInt32());
        var methods = pipe.Hello.GetProperty("methods").EnumerateArray().Select(method => method.GetString()).ToList();
        Assert.Contains("terminal.run", methods);
        Assert.Contains("events.subscribe", methods);
        var features = pipe.Hello.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()).ToList();
        Assert.Contains("events", features);
        Assert.Contains("status", features);

        var created = _fixture.Track(await Client.RunBackgroundAsync(_fixture.Request(
            "pipe", "cmd.exe", "/v:on", "/d", "/c", "set /p x=")));

        var viaPipe = await pipe.CallAsync("terminal.get", new { terminal = created.Address });
        var viaCli = await Client.GetAsync(created.Address);
        Assert.Equal(viaCli.Address, viaPipe.GetProperty("address").GetString());
        Assert.Equal(viaCli.State, viaPipe.GetProperty("state").GetString());

        var missing = await Assert.ThrowsAsync<HerdrCommandException>(
            () => pipe.CallAsync("terminal.get", new { terminal = "aw-m0-no-such-terminal" }));
        Assert.Equal("not_found", missing.Kind);
    }

    [LiveFact]
    public async Task Full_lifecycle_works_over_the_direct_pipe_without_the_cli()
    {
        await using var pipe = await HerdrPipeConnection.ConnectAsync(_fixture.Session, TimeSpan.FromSeconds(5));
        var marker = _fixture.Marker;

        var run = await pipe.CallAsync("terminal.run", new
        {
            argv = new[] { "cmd.exe", "/v:on", "/d", "/c", $"echo READY_{marker} & set /p x= & echo GOT:!x!" },
            name = $"m0-{marker[^8..].ToLowerInvariant()}-rpc",
            labels = new Dictionary<string, string> { ["aw_m0"] = marker },
            no_parent = true,
        });
        var address = run.GetProperty("address").GetString()!;
        _fixture.Track(new HerdrTerminal(address, null, "live", null, null, 0, null, new Dictionary<string, string>()));

        await pipe.CallAsync("terminal.send", new { terminal = address, text = $"RPC_{marker}" });
        var waited = HerdrWaitResult.FromJson(await pipe.CallAsync("terminal.wait", new { terminal = address, timeout = 30 }));
        Assert.True(waited.Ended);
        Assert.Equal("exit", waited.Reason);
        Assert.Equal(0, waited.ExitCode);

        var read = await pipe.CallAsync("terminal.read", new { terminal = address });
        Assert.Contains($"GOT:RPC_{marker}", read.GetProperty("text").GetString());

        var removed = await pipe.CallAsync("terminal.rm", new { terminal = address });
        Assert.Equal(address, removed.GetProperty("removed").GetString());
    }

    [LiveFact]
    public async Task Event_subscription_streams_created_and_finished_without_polling()
    {
        await using var events = await HerdrPipeConnection.ConnectAsync(_fixture.Session, TimeSpan.FromSeconds(5));
        var snapshot = await events.CallAsync("events.subscribe", new { labels = new Dictionary<string, string> { ["aw_m0"] = _fixture.Marker } });
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("terminals").ValueKind);

        var created = _fixture.Track(await Client.RunBackgroundAsync(_fixture.Request(
            "evt", "cmd.exe", "/v:on", "/d", "/c", "set /p x= & echo done")));
        await Client.SendAsync(created.Address, "go");

        using var cancellation = new CancellationTokenSource(Bounded);
        var seen = new List<string>();

        while (!seen.Contains("finished"))
        {
            var message = await events.ReadMessageAsync(cancellation.Token);

            if (!message.TryGetProperty("method", out var method) || method.GetString() != "event")
                continue;

            var parameters = message.GetProperty("params");

            if (parameters.GetProperty("terminal").GetString() == created.Address)
                seen.Add(parameters.GetProperty("type").GetString()!);
        }

        Assert.Contains("created", seen);
        Assert.Equal("finished", seen[^1]);
    }
}
