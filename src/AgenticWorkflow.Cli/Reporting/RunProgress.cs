using System.Diagnostics;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>Prints the few events worth a person's attention while a run is going: never raw protocol, hook or transcript data.</summary>
internal sealed class RunProgress(TextWriter output)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Line(string text)
    {
        lock (_gate)
            output.WriteLine($"  [{Format.Duration(_clock.Elapsed)}] {text}");
    }

    /// <summary>A notice of several lines, written together so nothing interleaves with it: the first line is stamped like <see cref="Line"/>, the rest are indented under it.</summary>
    public void Block(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        lock (_gate)
        {
            var stamp = $"  [{Format.Duration(_clock.Elapsed)}] ";

            output.WriteLine(stamp + lines[0]);

            foreach (var line in lines.Skip(1))
                output.WriteLine(new string(' ', stamp.Length) + line);
        }
    }
}

/// <summary>
/// Reports session and turn transitions of whatever <see cref="IAgentRuntime"/> the run uses, and forwards every call unchanged. It decides
/// nothing and alters no outcome.
/// </summary>
internal sealed class ProgressAgentRuntime(IAgentRuntime inner, RunProgress progress, bool humanAttending) : IAgentRuntime
{
    private readonly Dictionary<int, Stopwatch> _turns = [];

    public async Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
    {
        progress.Line($"starting agent ({request.Profile})");

        var session = await inner.StartSessionAsync(request, cancellationToken).ConfigureAwait(false);

        progress.Line("agent ready");
        return session;
    }

    public async Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
    {
        var turn = await inner.SendTurnAsync(session, prompt, cancellationToken).ConfigureAwait(false);

        _turns[turn.Value] = Stopwatch.StartNew();
        progress.Line(turn.Value == 1 ? "turn 1: working" : $"turn {turn}: repair after failed verification");

        return turn;
    }

    public async Task<AgentTurnOutcome> WaitForTurnAsync(
        AgentSessionId session,
        AgentTurnNumber turn,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var outcome = await inner.WaitForTurnAsync(session, turn, timeout, cancellationToken).ConfigureAwait(false);

        // A native interaction is an observation, not the end of the turn, and the run keeps waiting. What it says to an attending person (and where to
        // answer) is the native-interaction observer's notice, printed once the workflow has recorded the wait; here only "nobody is attending" is said.
        if (outcome.Status == AgentTurnStatus.AwaitingHuman && outcome.Interaction is { } interaction)
        {
            if (!humanAttending)
                ReportUnattended(interaction);

            return outcome;
        }

        if (outcome.Status == AgentTurnStatus.Resumed)
        {
            progress.Line($"turn {turn}: agent resumed after the native interaction");
            return outcome;
        }

        var took = _turns.TryGetValue(turn.Value, out var watch) ? $" ({Format.Duration(watch.Elapsed)})" : string.Empty;

        var what = outcome.Status == AgentTurnStatus.Done ? "finished" : outcome.Status.ToString().ToLowerInvariant();

        progress.Line($"turn {turn}: agent {what}{took}");

        return outcome;
    }

    public Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
        => inner.StopSessionAsync(session, cancellationToken);

    private void ReportUnattended(AgentInteraction interaction)
    {
        var what = $"{interaction.Kind.ToString().ToLowerInvariant()}: {Format.Printable(interaction.Description)}";

        progress.Line($"AWAITING HUMAN - the agent needs a person in its own interface ({what}); nobody is attending this run, so it will stop");
    }
}

/// <summary>Reports that an earlier native conversation is being resumed, and forwards every call unchanged. It decides nothing.</summary>
internal sealed class ProgressConversationRuntime(IAgentConversationRuntime inner, RunProgress progress) : IAgentConversationRuntime
{
    public bool CanResume(ProfileId profile) => inner.CanResume(profile);

    public AgentConversationRef? ConversationOf(AgentSessionId session) => inner.ConversationOf(session);

    public async Task<AgentSessionId> ResumeSessionAsync(AgentSessionRequest request, AgentConversationRef conversation, CancellationToken cancellationToken = default)
    {
        progress.Line($"resuming the agent's native conversation ({request.Profile})");

        var session = await inner.ResumeSessionAsync(request, conversation, cancellationToken).ConfigureAwait(false);

        progress.Line("agent ready (native conversation resumed)");
        return session;
    }
}

/// <summary>Reports each check the controller itself executes, and forwards the call unchanged. A check reused from the agent's own run is never seen here.</summary>
internal sealed class ProgressVerificationRunner(IVerificationRunner inner, RunProgress progress) : IVerificationRunner
{
    public async Task<CheckExecution> RunAsync(string workspacePath, VerificationCheckSpec check, CancellationToken cancellationToken = default)
    {
        progress.Line($"verifying {check.Id}");

        var execution = await inner.RunAsync(workspacePath, check, cancellationToken).ConfigureAwait(false);

        var passed = execution is { StartError: null, TimedOut: false } && execution.ExitCode == check.ExpectedExitCode;
        progress.Line($"{(passed ? "PASS" : "FAIL")} {check.Id} ({Format.Duration(execution.Duration)})");

        return execution;
    }
}
