namespace AgenticWorkflow.Runtime.Herdr.Tests;

public sealed class HcClientTests
{
    private const string FinishedTerminalJson =
        """{"address":"t3","name":"m0-x","state":"finished","exit_code":1,"reason":"killed","turn":0,"status":null,"labels":{"m0":"x"}}""";

    private static HcClient ClientFor(FakeHcDirectory fake, TimeSpan? timeout = null) =>
        new(new HcClientOptions { Executable = fake.ExecutablePath, CommandTimeout = timeout ?? TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task Parses_terminal_list()
    {
        using var fake = new FakeHcDirectory(
            """echo {"hidden_finished":0,"terminals":[{"address":"t1","name":"a","state":"live","labels":{"k":"v"},"turn":2,"status":{"state":"working"}}]}""");

        var terminals = await ClientFor(fake).ListAsync();

        var terminal = Assert.Single(terminals);
        Assert.Equal("t1", terminal.Address);
        Assert.True(terminal.IsLive);
        Assert.Equal("v", terminal.Labels["k"]);
        Assert.Equal("working", terminal.Status?.State);
        Assert.Equal(2, terminal.Turn);
    }

    [Fact]
    public async Task Parses_a_finished_terminal_with_exit_code()
    {
        using var fake = new FakeHcDirectory($"echo {FinishedTerminalJson}");

        var terminal = await ClientFor(fake).GetAsync("t3");

        Assert.False(terminal.IsLive);
        Assert.Equal(1, terminal.ExitCode);
        Assert.Equal("killed", terminal.Reason);
        Assert.Null(terminal.Status);
    }

    [Fact]
    public async Task Treats_a_wait_timeout_as_a_check_in_not_an_error()
    {
        using var fake = new FakeHcDirectory(
            """echo {"children":[],"ended":false,"exit_code":null,"reason":"timeout","signal":null,"status":null,"terminal":"t3","turn":0}""");

        var result = await ClientFor(fake).WaitAsync("t3", TimeSpan.FromSeconds(1));

        Assert.False(result.Ended);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task Maps_a_structured_error_to_a_command_exception_with_kind_and_exit_code()
    {
        using var fake = new FakeHcDirectory(
            """echo {"error":{"hint":"see hc ls --all","kind":"not_found","message":"no terminal nope"}} 1>&2& exit /b 3""");

        var exception = await Assert.ThrowsAsync<HerdrCommandException>(() => ClientFor(fake).GetAsync("nope"));

        Assert.Equal(3, exception.ExitCode);
        Assert.Equal("not_found", exception.Kind);
        Assert.Equal("see hc ls --all", exception.Hint);
    }

    [Fact]
    public async Task Maps_a_non_json_failure_to_a_command_exception_without_kind()
    {
        using var fake = new FakeHcDirectory("echo boom 1>&2& exit /b 2");

        var exception = await Assert.ThrowsAsync<HerdrCommandException>(() => ClientFor(fake).ListAsync());

        Assert.Equal(2, exception.ExitCode);
        Assert.Null(exception.Kind);
        Assert.Contains("boom", exception.Message);
    }

    [Fact]
    public async Task Rejects_malformed_json_from_a_successful_exit()
    {
        using var fake = new FakeHcDirectory("echo this is not json");

        await Assert.ThrowsAsync<HerdrProtocolException>(() => ClientFor(fake).ListAsync());
    }

    [Fact]
    public async Task Reports_a_missing_hc_executable()
    {
        var client = new HcClient(new HcClientOptions { Executable = "aw-m0-definitely-not-installed" });

        await Assert.ThrowsAsync<HcNotFoundException>(() => client.ListAsync());
    }

    [Fact]
    public async Task Times_out_a_hung_command()
    {
        using var fake = new FakeHcDirectory("ping -n 30 127.0.0.1 >nul");

        await Assert.ThrowsAsync<HcTimeoutException>(() => ClientFor(fake, TimeSpan.FromMilliseconds(500)).ListAsync());
    }

    [Fact]
    public async Task Parses_the_version_token()
    {
        using var fake = new FakeHcDirectory("echo hc 0.1.19");

        Assert.Equal("0.1.19", await ClientFor(fake).GetVersionAsync());
    }

    [Fact]
    public async Task Rejects_an_unexpected_version_banner()
    {
        using var fake = new FakeHcDirectory("echo something else entirely");

        await Assert.ThrowsAsync<HerdrProtocolException>(() => ClientFor(fake).GetVersionAsync());
    }

    [Theory]
    [InlineData(null, "herdr-core-{user}-default.sock")]
    [InlineData("m0-abc", "herdr-core-{user}-m0-abc.sock")]
    public void Derives_the_pipe_name_from_the_session(string? session, string expectedTemplate)
    {
        var expected = expectedTemplate.Replace("{user}", Environment.UserName);

        Assert.Equal(expected, HerdrPipeConnection.PipeName(session));
    }

    [Fact]
    public async Task Fails_to_connect_when_no_core_listens_on_the_pipe()
    {
        var session = "m0-absent-" + Guid.NewGuid().ToString("N")[..8];

        var exception = await Assert.ThrowsAsync<HerdrProtocolException>(
            () => HerdrPipeConnection.ConnectAsync(session, TimeSpan.FromMilliseconds(300)));

        Assert.Contains(HerdrPipeConnection.PipeName(session), exception.Message);
    }
}
