namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// Hosts one disposable native Claude Code session in the isolated core and observes it through Herdr's
/// structured status. Never answers a dialog: a blocked session fails the test with the reason.
/// Run with <c>AGENTICWORKFLOW_LIVE_CLAUDE=1 dotnet test --filter Category=LiveClaude</c>.
/// </summary>
[Trait("Category", "LiveClaude")]
public sealed class ClaudeCodeLiveSmokeTests : IClassFixture<LiveHerdrFixture>
{
    private const string ExpectedToken = "AGENTICWORKFLOW_M0_CLAUDE_OK";

    private readonly LiveHerdrFixture _fixture;

    public ClaudeCodeLiveSmokeTests(LiveHerdrFixture fixture)
    {
        _fixture = fixture;
    }

    [LiveFact(LiveFactAttribute.ClaudeVariable)]
    public async Task Disposable_claude_session_is_hosted_observed_and_cleaned_up()
    {
        // The token is split in the prompt so it can appear on screen only if Claude actually replied.
        var prompt = "Reply with exactly one token: the text AGENTICWORKFLOW_M0_CLAUDE immediately followed by _OK, "
            + "with nothing between them and nothing else in your reply. Do not use any tools and do not modify any files.";

        await using var events = await HerdrPipeConnection.ConnectAsync(_fixture.Session, TimeSpan.FromSeconds(5));
        await events.CallAsync("events.subscribe", new { labels = new Dictionary<string, string> { ["aw_m0"] = _fixture.Marker } });

        var created = _fixture.Track(await _fixture.Client.RunBackgroundAsync(
            _fixture.Request("claude", "claude", prompt) with { WorkingDirectory = RepositoryRoot() }));

        // Event-driven: no polling and no screen parsing to learn that the turn ended.
        var finalStatus = await WaitForTerminalStatusAsync(events, created.Address, TimeSpan.FromMinutes(2));
        Assert.True(
            finalStatus.State == "done",
            $"Claude did not finish its turn: status '{finalStatus.State}' kind '{finalStatus.Kind}' msg '{finalStatus.Message}'. "
            + "A blocked session needs a person; this test never answers dialogs.");

        var terminal = await _fixture.Client.GetAsync(created.Address);
        Assert.True(terminal.IsLive);
        Assert.Equal("claude-code", terminal.Status?.App);
        Assert.True(terminal.Turn >= 1);

        var screen = await _fixture.Client.ReadAsync(created.Address);
        Assert.Contains(ExpectedToken, screen.Text);

        await _fixture.Client.KillAsync(created.Address);
        var killed = await _fixture.Client.GetAsync(created.Address);
        Assert.Equal("finished", killed.State);
        await _fixture.Client.RemoveAsync(created.Address);
    }

    private static async Task<HerdrStatus> WaitForTerminalStatusAsync(
        HerdrPipeConnection events,
        string address,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        while (true)
        {
            var message = await events.ReadMessageAsync(cancellation.Token);

            if (!message.TryGetProperty("method", out var method) || method.GetString() != "event")
                continue;

            var parameters = message.GetProperty("params");

            if (parameters.GetProperty("terminal").GetString() != address)
                continue;

            var type = parameters.GetProperty("type").GetString();

            if (type == "finished")
                throw new Xunit.Sdk.XunitException("Claude exited before finishing its turn.");

            if (type != "status")
                continue;

            var status = HerdrStatus.FromJson(parameters.GetProperty("data").GetProperty("status"));

            if (status is not null && status.State is "done" or "blocked" or "error")
                return status;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
