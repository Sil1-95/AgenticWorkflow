namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// Owns an isolated herdr-core (its own <c>HC_SESSION</c>, hence its own named pipe) for live tests, so a
/// live run can never see, signal, or send input to the user's real terminals. Every terminal created
/// through <see cref="Track"/> is killed and removed on disposal, even after a partial failure, and the
/// isolated core is stopped last. The user's default core is never contacted.
/// </summary>
public sealed class LiveHerdrFixture : IAsyncLifetime
{
    private readonly List<string> _createdAddresses = [];
    private bool _enabled;

    /// <summary>Unique marker embedded in the session, names and labels of everything this run creates.</summary>
    public string Marker { get; } = "AGENTICWORKFLOW_M0_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public string Session => Marker.ToLowerInvariant().Replace('_', '-');

    internal HcClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _enabled = Environment.GetEnvironmentVariable(LiveFactAttribute.HerdrVariable) == "1"
            || Environment.GetEnvironmentVariable(LiveFactAttribute.ClaudeVariable) == "1";

        if (!_enabled)
            return;

        // CLAUDE* is scrubbed because this suite may itself be driven by a Claude Code session; a hosted
        // agent must not inherit that session's messaging socket, token or id.
        Client = new HcClient(new HcClientOptions { Session = Session, ScrubbedEnvironmentPrefixes = ["CLAUDE"] });

        // Starts only the isolated core named by Session; the default core is not touched.
        await Client.StartServerAsync();
    }

    /// <summary>Remembers a terminal so it is cleaned up on disposal.</summary>
    internal HerdrTerminal Track(HerdrTerminal terminal)
    {
        _createdAddresses.Add(terminal.Address);

        return terminal;
    }

    internal HerdrRunRequest Request(string suffix, params string[] argv) => new(
        argv,
        Name: $"m0-{Marker[^8..].ToLowerInvariant()}-{suffix}",
        Labels: new Dictionary<string, string> { ["aw_m0"] = Marker });

    public async Task DisposeAsync()
    {
        if (!_enabled)
            return;

        foreach (var address in _createdAddresses)
        {
            await TryAsync(() => Client.KillAsync(address));
            await TryAsync(() => Client.RemoveAsync(address));
        }

        await TryAsync(() => Client.StopServerAsync());
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HerdrException)
        {
            // Best-effort cleanup: already finished / already removed / core already gone.
        }
    }
}
