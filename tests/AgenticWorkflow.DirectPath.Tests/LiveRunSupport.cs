using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Execution.Local;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// Why a direct run did NOT complete, in the categories a person acts on differently. Derived from the run's structured
/// status and, for the one case that shares a status, its stable reason code. Test/report vocabulary only.
/// </summary>
public enum RunClassification
{
    AwaitingAcceptance,

    /// <summary>The agent never reported a ready status: a trust, login or update dialog (or a crash) before the first turn. Nothing was answered.</summary>
    StartupBlock,

    /// <summary>The agent waited for a person in its own interface and nobody was attending (<see cref="DirectRunStatus.NativeInteractionUnavailable"/>; the workflow ends BLOCKED). Nothing was answered.</summary>
    NativeInteractionUnavailable,

    /// <summary>The agent finished but the controller's checks still fail after the permitted turns: the repair loop was exhausted.</summary>
    VerificationFailure,

    /// <summary>The runtime or the agent process failed (error, exit, timeout, lost connection).</summary>
    RuntimeError,

    /// <summary>The workflow moved under the run (optimistic concurrency); nothing was applied.</summary>
    StaleWorkflowState,

    /// <summary>Anything else the runner reports (rejected before launch, interrupted, indeterminate write...).</summary>
    Other,
}

internal static class RunClassifier
{
    public static RunClassification Classify(DirectRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            DirectRunStatus.AwaitingAcceptance => RunClassification.AwaitingAcceptance,
            DirectRunStatus.NativeInteractionUnavailable => RunClassification.NativeInteractionUnavailable,
            DirectRunStatus.VerificationUnresolved => RunClassification.VerificationFailure,
            DirectRunStatus.StaleWorkflowState => RunClassification.StaleWorkflowState,
            DirectRunStatus.StartupBlocked => RunClassification.StartupBlock,
            DirectRunStatus.ExecutionFailure or DirectRunStatus.AgentError or DirectRunStatus.AgentCancelled => RunClassification.RuntimeError,
            _ => RunClassification.Other,
        };
    }

    /// <summary>One line for a failure message and the test log.</summary>
    public static string Describe(DirectRunResult result)
        => $"{Classify(result)} (status {result.Status}): {result.Reason}";
}

/// <summary>
/// A test-only decorator that records everything the runner asks of an <see cref="IAgentRuntime"/> and can run test code at
/// one point only: right after a turn's outcome is known and before the runner sees it. Used to inject a controlled workspace
/// disturbance in the live repair test and to observe a blocked terminal before the runner stops it. It forwards everything
/// and changes no outcome. It exists only in this test project.
/// </summary>
internal sealed class ObservingAgentRuntime(IAgentRuntime inner) : IAgentRuntime
{
    public List<AgentSessionRequest> StartRequests { get; } = [];

    public List<AgentSessionId> Started { get; } = [];

    public List<(AgentSessionId Session, AgentTurnNumber Turn, string Prompt)> Sent { get; } = [];

    public List<(AgentTurnNumber Turn, AgentTurnOutcome Outcome)> Outcomes { get; } = [];

    public List<AgentSessionId> Stopped { get; } = [];

    /// <summary>Test code to run once a turn has ended, before the runner continues. It must not alter the outcome.</summary>
    public Func<AgentSessionId, AgentTurnNumber, AgentTurnOutcome, Task>? AfterTurn { get; set; }

    public async Task<AgentSessionId> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
    {
        StartRequests.Add(request);
        var session = await inner.StartSessionAsync(request, cancellationToken).ConfigureAwait(false);
        Started.Add(session);
        return session;
    }

    public async Task<AgentTurnNumber> SendTurnAsync(AgentSessionId session, string prompt, CancellationToken cancellationToken = default)
    {
        var turn = await inner.SendTurnAsync(session, prompt, cancellationToken).ConfigureAwait(false);
        Sent.Add((session, turn, prompt));
        return turn;
    }

    public async Task<AgentTurnOutcome> WaitForTurnAsync(
        AgentSessionId session,
        AgentTurnNumber turn,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var outcome = await inner.WaitForTurnAsync(session, turn, timeout, cancellationToken).ConfigureAwait(false);
        Outcomes.Add((turn, outcome));

        if (AfterTurn is { } hook)
            await hook(session, turn, outcome).ConfigureAwait(false);

        return outcome;
    }

    public async Task StopSessionAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        Stopped.Add(session);
        await inner.StopSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Read-only look at a live Herdr terminal through the <c>hc</c> CLI (the production runtime never reads this way).</summary>
internal static class HerdrInspection
{
    /// <summary>The terminal's lifecycle state, the program's own status word and its turn counter, via <c>hc get --json</c>.</summary>
    public static async Task<TerminalFacts> GetAsync(string herdrSession, string terminalName)
    {
        var info = new ProcessStartInfo("hc")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.Environment["HC_SESSION"] = herdrSession;
        info.ArgumentList.Add("get");
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add(terminalName);

        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(name);

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"hc get failed ({process.ExitCode}): {await error}");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetProperty("state").GetString() : null;

        return new TerminalFacts(
            root.GetProperty("state").GetString() ?? string.Empty,
            status,
            root.TryGetProperty("turn", out var turn) ? turn.GetInt64() : 0);
    }

    public sealed record TerminalFacts(string State, string? StatusWord, long Turn);
}

/// <summary>What the live tests share: the trusted-fixture guard, the known-good baseline and the restore.</summary>
internal static class LiveFixtureSupport
{
    public static FixtureGuard RequireTrustedFixture(string title)
    {
        var guard = TrustedLiveFixture.Evaluate(Environment.GetEnvironmentVariable(TrustedLiveFixture.WorkspaceVariable));

        if (!guard.IsSafe)
        {
            Assert.Fail(
                $"{title} BLOCKED before anything was touched: the trusted fixture workspace is not usable." + Environment.NewLine
                + guard.Describe() + Environment.NewLine
                + $"Set {TrustedLiveFixture.WorkspaceVariable} to the dedicated, manually trusted Git repository root that contains '{TrustedLiveFixture.MarkerFileName}'.");
        }

        return guard;
    }

    /// <summary>The baseline must be known-good before Claude runs: its tests pass, it has the old symbol and not the new.</summary>
    public static async Task AssertBaselineIsKnownGoodAsync(DirectFixtureRepo fixture)
    {
        var runner = new LocalVerificationRunner();

        var tests = await runner.RunAsync(fixture.Root, DirectPathSetup.SymbolAndTestChecks()[2]);
        Assert.True(
            tests is { ExitCode: 0, TimedOut: false },
            $"the baseline's 'dotnet test' must pass before the run. exit={tests.ExitCode} {tests.StdOutTail} {tests.StdErrTail}");

        // `git grep -q` exits 0 when it finds the symbol and 1 when it does not
        Assert.Equal(0, (await runner.RunAsync(fixture.Root, DirectPathSetup.SymbolChecks()[0])).ExitCode); // GetMeal is present
        Assert.Equal(1, (await runner.RunAsync(fixture.Root, DirectPathSetup.SymbolChecks()[1])).ExitCode); // GetFood is not
    }

    public static void RestoreBaseline(FixtureGuard guard, Action<string> log)
    {
        if (Environment.GetEnvironmentVariable("AGENTICWORKFLOW_KEEP_FIXTURE") == "1")
        {
            log("AGENTICWORKFLOW_KEEP_FIXTURE=1: fixture left as the run ended.");
            return;
        }

        try
        {
            using var restored = TrustedLiveFixture.ResetToBaseline(guard);
            log("fixture restored to its baseline.");
        }
        catch (Exception exception)
        {
            log("fixture restore failed (it is rebuilt at the start of every run): " + exception.Message);
        }
    }

    public static void DeleteDatabase(string database)
    {
        foreach (var file in new[] { database, database + "-wal", database + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Fails with a message that says WHICH kind of stop this was. A startup block means a person has to establish trust (or
    /// answer whatever Claude Code is showing); a native wait means the task needs something outside what the agent's permission mode grants.
    /// Neither is ever answered by the workflow.
    /// </summary>
    public static void AssertNotStoppedByAPerson(DirectRunResult result)
    {
        switch (RunClassifier.Classify(result))
        {
            case RunClassification.NativeInteractionUnavailable:
                Assert.Fail(
                    "WAITING FOR A PERSON (in-turn). Claude Code asked to do something outside what its own settings pre-approve; "
                    + $"it asks in its own prompt and the controller never answers it. Reason: {result.Reason}");
                break;

            case RunClassification.StartupBlock:
                Assert.Fail(
                    "BLOCKED AT STARTUP. Claude Code needs a person to decide something before its first turn, and nothing here answers dialogs. "
                    + $"Reason: {result.Reason} "
                    + $"The workspace in {TrustedLiveFixture.WorkspaceVariable} must be a Git repository root that you trusted in Claude Code yourself.");
                break;
        }
    }

    public static void Report(Action<string> log, DirectRunResult result, DirectFixtureRepo fixture)
    {
        log($"classification: {RunClassifier.Classify(result)}");
        log($"status: {result.Status}");
        log($"reason: {result.Reason}");
        log($"turns: {result.TurnsUsed}, session: {result.Session}");

        foreach (var turn in result.Turns)
        {
            log(
                $"  turn {turn.Turn}: agent={turn.AgentStatus} implementation={turn.ImplementationRevisionId} verification={turn.Verification} scope-violations={turn.ScopeViolations.Count}");
        }

        foreach (var check in result.LastVerification)
            log($"  check {check.CheckId}: {check.Outcome} ({check.Summary})");

        foreach (var note in result.Notes)
            log($"  note: {note}");

        if (result.Task is { } snapshot)
        {
            log($"workflow state: {snapshot.Task.WorkflowState} (version {snapshot.Version})");
            log($"verification runs recorded: {snapshot.Task.Verifications.Length}");
        }

        log("git status after run:");
        log(fixture.Git("status", "--short"));
        log(fixture.Git("diff", "--stat", "HEAD"));
    }
}
