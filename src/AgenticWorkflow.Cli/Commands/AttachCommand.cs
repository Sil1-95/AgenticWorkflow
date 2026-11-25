using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Runtime.Herdr;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// <c>aw attach &lt;run-id&gt;</c>: takes a person to the agent's own interface for a run. The agent lives in an isolated Herdr core AgenticWorkflow created and recorded for the run; this finds
/// that core and the one live agent terminal in it and attaches to it (<c>hc attach</c>), so a permission, a trust question or a login the agent raises can be answered where the agent asks it.
/// With <c>--describe</c> it only reports, as one JSON document, whether such a session exists and what the harness says it is doing. It lists and attaches; it never types, answers or stops
/// anything, and it only ever names a core in AgenticWorkflow's own namespace. A session it cannot identify with certainty is reported as unavailable, never guessed.
/// </summary>
internal sealed class AttachCommand(CliServices services)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<int> ExecuteAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        var runId = CommandLine.ParseRunId(request.RunId);
        var store = new RunStore(RunStore.ResolveRoot(services.GetEnvironmentVariable, services.LocalApplicationData));

        if (!Directory.Exists(store.RunDirectory(runId)))
            return Refuse(request, $"There is no run {runId}.", ExitCodes.RunNotFound);

        var hc = new ExecutableLocator(services.GetEnvironmentVariable).Find("hc");

        if (hc is null)
            return Refuse(request, "'hc' (Herdr) was not found on PATH, so the agent's session cannot be shown.", ExitCodes.UsageOrConfiguration);

        var (found, problem) = await FindAsync(store, runId, hc, cancellationToken).ConfigureAwait(false);

        if (found is null)
            return Refuse(request, problem!, ExitCodes.GateDecisionRefused);

        if (request.Describe)
        {
            services.Out.Write(JsonSerializer.Serialize(new Description(true, found.HostSession, found.Terminal, found.AgentState, found.AgentMessage, null), Json) + "\n");

            return ExitCodes.Success;
        }

        return await AttachAsync(hc, found, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The agent terminal of the run, or why there is none. The run must be held by a process (otherwise its agent has been stopped), and exactly one live agent terminal must exist in its recorded core.</summary>
    private async Task<(NativeSessionInfo? Found, string? Problem)> FindAsync(RunStore store, AgenticWorkflow.Core.Identity.TaskId runId, string hc, CancellationToken cancellationToken)
    {
        if (!store.IsLive(runId))
            return (null, "No process is working on this run, so there is no agent session to show.");

        var sessions = new List<string>();

        if (store.ReadSupervision(runId).Record is { } supervision)
            sessions.AddRange(supervision.Sessions.Where(HerdrIsolatedCore.IsOwnSessionName));

        var interaction = store.ReadNativeInteraction(runId).Record;

        if (interaction?.HostSession is { } named && HerdrIsolatedCore.IsOwnSessionName(named) && !sessions.Contains(named, StringComparer.Ordinal))
            sessions.Add(named);

        if (sessions.Count == 0)
            return (null, "The run has not recorded an agent session yet.");

        var candidates = new List<NativeSessionInfo>();

        foreach (var session in sessions)
            candidates.AddRange(await HerdrNativeSessions.FindAsync(session, hc, ListTimeout, cancellationToken).ConfigureAwait(false));

        // The terminal the agent itself said it is waiting in is the one to show, when it is still there; otherwise only a single live agent terminal is certain enough.
        if (interaction is { Terminal: { } waiting } && candidates.FirstOrDefault(c => string.Equals(c.Terminal, waiting, StringComparison.Ordinal)) is { } match)
            return (match, null);

        return candidates.Count switch
        {
            1 => (candidates[0], null),
            0 => (null, "The agent session of this run is not running right now."),
            _ => (null, "More than one agent terminal is live for this run, so which one to show cannot be told."),
        };
    }

    private async Task<int> AttachAsync(string hc, NativeSessionInfo found, CancellationToken cancellationToken)
    {
        // The identifiers come from the core's own listing, but they become process arguments, so they are held to the same plain-token rule as every printed attach line.
        if (!NativeAttachGuidance.IsUsable(found.HostSession, found.Terminal))
        {
            services.Error.WriteLine("The agent session has an identifier that is not safe to attach to; nothing was attached.");

            return ExitCodes.GateDecisionRefused;
        }

        var start = new ProcessStartInfo { FileName = hc, UseShellExecute = false };
        start.ArgumentList.Add("attach");
        start.ArgumentList.Add(found.Terminal);
        start.Environment["HC_SESSION"] = found.HostSession;

        using var process = Process.Start(start);

        if (process is null)
        {
            services.Error.WriteLine("'hc attach' could not be started.");

            return ExitCodes.RuntimeError;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it had ended
            }

            throw;
        }

        return process.ExitCode == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private int Refuse(AttachRequest request, string reason, int exitCode)
    {
        if (request.Describe)
            services.Out.Write(JsonSerializer.Serialize(new Description(false, null, null, null, null, reason), Json) + "\n");
        else
            services.Error.WriteLine(reason);

        // A description is always a successful answer to "is there a session" (the reason is in the document); only attaching can fail.
        return request.Describe ? ExitCodes.Success : exitCode;
    }

    private sealed record Description(bool Available, string? HostSession, string? Terminal, string? AgentState, string? AgentMessage, string? Reason);
}
