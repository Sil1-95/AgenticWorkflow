using System.Text.RegularExpressions;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>
/// Ends an isolated Herdr core that an earlier process of AgenticWorkflow started and did not get to stop (it was killed). It is the only thing outside
/// <see cref="HerdrAgentRuntime"/> that stops a core, and it can only name a core in AgenticWorkflow's own isolated-session namespace: the user's default core, and any
/// session that was not created by AgenticWorkflow, are refused before <c>hc</c> is run.
/// </summary>
public static partial class HerdrIsolatedCore
{
    /// <summary>The prefix of every isolated core AgenticWorkflow creates for a run.</summary>
    public const string SessionPrefix = "agenticworkflow-aw-";

    /// <summary>The name of an isolated core for a run: the prefix and eight hexadecimal digits. Nothing else is ever stopped by <see cref="StopAsync"/>.</summary>
    public static bool IsOwnSessionName(string? session) => session is not null && OwnSession().IsMatch(session);

    /// <summary>A new name in the isolated-session namespace.</summary>
    public static string NewSessionName() => SessionPrefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Stops the core of <paramref name="session"/> and reports whether it is confirmed gone afterwards: true when the core answers no more (stopped, or it was not there),
    /// false when it still answers or cannot be asked. A name that is not an AgenticWorkflow isolated session is refused with <see cref="ArgumentException"/>.
    /// </summary>
    public static async Task<bool> StopAsync(string session, string executable, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        if (!IsOwnSessionName(session))
            throw new ArgumentException("Only an isolated AgenticWorkflow session can be stopped.", nameof(session));

        var client = new HcClient(new HcClientOptions { Executable = executable, Session = session, CommandTimeout = commandTimeout });

        try
        {
            await client.StopServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HerdrException)
        {
            // Whether it stopped is decided by asking it, below.
        }

        try
        {
            await client.GetServerStatusAsync(cancellationToken).ConfigureAwait(false);

            return false;
        }
        catch (HerdrCommandException e) when (string.Equals(e.Kind, "unavailable", StringComparison.Ordinal))
        {
            return true;
        }
        catch (HerdrException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^agenticworkflow-aw-[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OwnSession();
}
