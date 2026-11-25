using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// Proves the production runtime keeps ONE native Claude Code process across turns: the second turn can recall something
/// only the first turn said. Read-only prompts, run in the already-trusted repository folder, in an isolated core that
/// only this test starts. Never answers a dialog; a blocked session fails the test with the reason.
/// Run with <c>AGENTICWORKFLOW_LIVE_CLAUDE_SESSION=1 dotnet test --filter Category=LiveClaudeSession</c>.
/// </summary>
[Trait("Category", "LiveClaudeSession")]
public sealed class ClaudeSameSessionLiveTests
{
    private static readonly ProfileId Profile = new("claude-code-local");

    [LiveFact(LiveFactAttribute.ClaudeSessionVariable)]
    public async Task Two_turns_in_one_persistent_native_session_share_context_and_the_turn_counter_advances()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var session = $"agenticworkflow-m2b-{suffix}";
        var codeWord = "AWCODE" + suffix.ToUpperInvariant();

        await using var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
        {
            Session = session,
            Profiles = [ClaudeCodeLaunch.Profile(Profile)],
            StartupTimeout = TimeSpan.FromSeconds(120),
        });

        var id = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, RepositoryRoot(), "live second turn"));

        var first = await runtime.SendTurnAsync(
            id,
            $"Remember this code word for later in this conversation: {codeWord}. Reply with exactly the single word noted. Do not use any tools.");
        var firstOutcome = await runtime.WaitForTurnAsync(id, first, TimeSpan.FromMinutes(3));
        Assert.True(firstOutcome.Status == AgentTurnStatus.Done, $"turn 1 ended {firstOutcome.Status}: {firstOutcome.Reason}");

        // The code word is spelled with hyphens in the reply, so it can only be on screen if Claude actually recalled it
        // (the prompt that asks for it never contains the hyphenated form).
        var second = await runtime.SendTurnAsync(
            id,
            "What was the code word I asked you to remember? Reply with only that code word, with a hyphen between every character. Do not use any tools.");
        var secondOutcome = await runtime.WaitForTurnAsync(id, second, TimeSpan.FromMinutes(3));
        Assert.True(secondOutcome.Status == AgentTurnStatus.Done, $"turn 2 ended {secondOutcome.Status}: {secondOutcome.Reason}");

        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);

        // Inspection only (the runtime itself never reads the screen): same terminal, turn counter advanced, context shared.
        var client = new HcClient(new HcClientOptions { Session = session, ScrubbedEnvironmentPrefixes = ["CLAUDE"] });
        var terminals = await client.ListAsync();
        var ours = Assert.Single(terminals, t => t.Name == id.Value);
        Assert.True(ours.IsLive);
        Assert.True(ours.Turn >= 2, $"expected the terminal turn counter to be at least 2, was {ours.Turn}");

        // (`ls` omits the reporting app; `get` has it.)
        Assert.Equal("claude-code", (await client.GetAsync(ours.Address)).Status?.App);

        var screen = (await client.ReadAsync(id.Value)).Text;
        var hyphenated = string.Join('-', codeWord.ToCharArray());
        Assert.Contains(hyphenated, screen);

        await runtime.StopSessionAsync(id);
        Assert.DoesNotContain(await client.ListAsync(), t => t.Name == id.Value);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
