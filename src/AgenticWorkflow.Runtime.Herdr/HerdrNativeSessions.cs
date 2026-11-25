namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>An agent terminal AgenticWorkflow started in one of its own isolated Herdr cores, as the core itself describes it right now.</summary>
/// <param name="HostSession">The isolated core (<see cref="HerdrIsolatedCore.IsOwnSessionName"/>).</param>
/// <param name="Terminal">The terminal's address in that core.</param>
/// <param name="AgentState">The state the harness itself reported to Herdr (<c>working</c>, <c>idle</c>, <c>blocked</c>, ...), or null when it reported none.</param>
/// <param name="AgentMessage">What the harness said about that state, when it said anything.</param>
public sealed record NativeSessionInfo(string HostSession, string Terminal, string? AgentState, string? AgentMessage);

/// <summary>
/// Finds the live agent terminals of one of AgenticWorkflow's own isolated cores, so a person can be taken to the agent's own session. Read-only: it lists, and it never sends,
/// answers or stops anything. Like <see cref="HerdrIsolatedCore"/> it refuses a session that is not in AgenticWorkflow's namespace, so the user's default core is never listed.
/// </summary>
public static class HerdrNativeSessions
{
    /// <summary>The live terminals AgenticWorkflow's runtime started in <paramref name="session"/> (those carrying its own label), oldest first. Empty when the core is gone or has none.</summary>
    public static async Task<IReadOnlyList<NativeSessionInfo>> FindAsync(string session, string executable, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        if (!HerdrIsolatedCore.IsOwnSessionName(session))
            throw new ArgumentException("Only an isolated AgenticWorkflow session can be listed.", nameof(session));

        var client = new HcClient(new HcClientOptions { Executable = executable, Session = session, CommandTimeout = commandTimeout });

        try
        {
            var listed = await client.ListAsync(includeFinished: false, cancellationToken).ConfigureAwait(false);
            var found = new List<NativeSessionInfo>();

            // The listing is a summary (no labels, and a bare status word), so each live terminal is asked for its full description: only a terminal carrying the runtime's own label is ours.
            foreach (var summary in listed.Where(t => t.IsLive))
            {
                var terminal = await client.GetAsync(summary.Address, cancellationToken).ConfigureAwait(false);

                if (terminal.IsLive && terminal.Labels.ContainsKey("aw_runtime"))
                    found.Add(new NativeSessionInfo(session, terminal.Address, terminal.Status?.State, terminal.Status?.Message));
            }

            return found;
        }
        catch (HerdrException)
        {
            // A core that cannot be asked has no session to show; the caller reports "unavailable", never a guess.
            return [];
        }
    }
}
