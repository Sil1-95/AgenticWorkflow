using System.Diagnostics;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Runtime.Herdr;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;
using Xunit.Abstractions;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>Runs only when <c>AGENTICWORKFLOW_LIVE_CLAUDE_EVIDENCE=1</c>: the live test makes real model calls.</summary>
public sealed class LiveEvidenceFactAttribute : FactAttribute
{
    public const string Variable = "AGENTICWORKFLOW_LIVE_CLAUDE_EVIDENCE";

    public LiveEvidenceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Live evidence-reuse test (real Claude Code, real model calls). Set {Variable}=1 and {TrustedLiveFixture.WorkspaceVariable}=<trusted fixture> to run it.";
    }
}

/// <summary>Counts, per check id, what the CONTROLLER's own runner was asked to execute, and how long it took.</summary>
internal sealed class CountingVerificationRunner(IVerificationRunner inner) : IVerificationRunner
{
    public List<(string CheckId, TimeSpan Duration)> Executions { get; } = [];

    public async Task<CheckExecution> RunAsync(string workspacePath, VerificationCheckSpec check, CancellationToken cancellationToken = default)
    {
        var execution = await inner.RunAsync(workspacePath, check, cancellationToken).ConfigureAwait(false);
        Executions.Add((check.Id.Value, execution.Duration));
        return execution;
    }

    public int CountOf(string checkId) => Executions.Count(e => e.CheckId == checkId);
}

/// <summary>
/// M2D end to end against native Claude Code: the agent runs the project's own build and test commands (told exactly which, as
/// plain separate commands), the session-only hooks observe them, and the controller REUSES those executions as verification
/// evidence when they ran, successfully, against exactly the implementation state it recorded. The two symbol checks are never
/// run by the agent, so the controller still executes them itself.
/// <para>
/// Proof, not narration: a counting wrapper around the controller's own runner shows how often it executed each check, and the
/// persisted verification records say where each passing result came from. Nothing is committed, pushed, approved or answered.
/// Run with <c>AGENTICWORKFLOW_LIVE_CLAUDE_EVIDENCE=1</c> and <c>AGENTICWORKFLOW_LIVE_DIRECT_WORKSPACE=&lt;trusted fixture&gt;</c>, then
/// <c>dotnet test --filter Category=LiveClaudeEvidence</c>.
/// </para>
/// </summary>
[Trait("Category", "LiveClaudeEvidence")]
public sealed class LiveEvidenceReuseTests(ITestOutputHelper output)
{
    private static readonly VerificationCheckId Build = new("dotnet_build");

    private static readonly VerificationCheckId Test = new("dotnet_test_exact");

    private const string BuildCommand = "dotnet build tests/Meals.Tests/Meals.Tests.csproj";

    private const string TestCommand = "dotnet test tests/Meals.Tests/Meals.Tests.csproj";

    private static IReadOnlyList<VerificationCheckSpec> Checks()
        =>
        [
            .. DirectPathSetup.SymbolChecks(),
            new(Build, "the test project builds", "dotnet", ["build", "tests/Meals.Tests/Meals.Tests.csproj"], TimeSpan.FromMinutes(5)),
            new(Test, "the test project's tests pass", "dotnet", ["test", "tests/Meals.Tests/Meals.Tests.csproj"], TimeSpan.FromMinutes(5)),
        ];

    [LiveEvidenceFact]
    public async Task The_controller_reuses_the_agents_own_passing_build_and_test_runs_and_still_executes_the_checks_the_agent_did_not_run()
    {
        var guard = LiveFixtureSupport.RequireTrustedFixture("LIVE EVIDENCE REUSE");

        var database = Path.Combine(Path.GetTempPath(), "aw-live-evidence-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var hook = HookExecutable();

        using var fixture = TrustedLiveFixture.ResetToBaseline(guard);
        var baselineHead = fixture.Git("rev-parse", "HEAD").Trim();

        output.WriteLine($"fixture (trusted, reused): {fixture.Root}");
        output.WriteLine($"hook: {hook}");
        output.WriteLine($"herdr session: agenticworkflow-m2d-{suffix}");

        try
        {
            await LiveFixtureSupport.AssertBaselineIsKnownGoodAsync(fixture);

            var service = await DirectPathSetup.OpenServiceAsync(database);
            await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood in the fixture repository (evidence reuse)");

            await using var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
            {
                Session = $"agenticworkflow-m2d-{suffix}",
                Profiles = [ClaudeCodeLaunch.Profile(DirectPathSetup.ClaudeProfile, evidence: new ClaudeEvidenceOptions { HookExecutable = hook })],
                StartupTimeout = TimeSpan.FromSeconds(120),
            });

            var observing = new ObservingAgentRuntime(runtime);
            var verifier = new CountingVerificationRunner(new LocalVerificationRunner());

            var runner = new DirectPathRunner(
                service,
                observing,
                new GitImplementationStateProvider(),
                verifier,
                new DirectRunOptions { TurnTimeout = TimeSpan.FromMinutes(10) },
                evidence: runtime);

            var definition = DirectPathSetup.Definition(fixture.Root, Checks(), maxTurns: 3) with
            {
                OperatingNotes =
                    "Tooling: you may only run commands that are pre-approved, one plain command per call. Search with `git grep` (not grep/rg/find). "
                    + "Do not chain commands with `;`, `&&` or `|`, and do not redirect output: anything else needs an approval nobody can give and will stop the task. "
                    + "Make your tool calls strictly one at a time (never several tool calls in one step). "
                    + "When ALL your edits are finished, verify exactly like this, each command alone as its own tool call, written exactly as shown: "
                    + $"`{BuildCommand}` and then `{TestCommand}`. Make no edit and run no other command after them.",
            };

            var clock = Stopwatch.StartNew();
            var result = await runner.RunAsync(DirectPathSetup.TaskId, definition);
            clock.Stop();

            LiveFixtureSupport.Report(output.WriteLine, result, fixture);
            output.WriteLine($"run wall time: {clock.Elapsed.TotalSeconds:F1} s");

            foreach (var decision in result.EvidenceDecisions)
                output.WriteLine($"  evidence decision {decision.CheckId}: reusable={decision.Reusable} ({decision.Reason})");

            output.WriteLine("controller executions: " + string.Join(", ", verifier.Executions.Select(e => $"{e.CheckId} {e.Duration.TotalSeconds:F1}s")));

            LiveFixtureSupport.AssertNotStoppedByAPerson(result);
            Assert.True(result.Status == DirectRunStatus.AwaitingAcceptance, $"direct run ended {result.Status}: {result.Reason}");

            // ---- authoritative history from a brand-new service over the persisted file --------------------------------------
            var reopened = await DirectPathSetup.OpenServiceAsync(database);
            var task = (await reopened.LoadAsync(DirectPathSetup.TaskId)).Task;

            Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);

            var final = task.CurrentVerification!;
            Assert.Equal(VerificationOutcome.Passed, final.Outcome);
            Assert.Equal(task.CurrentImplementationRevision!.Id, final.ImplementationRevisionId);

            var fingerprintOnDisk = (await new GitImplementationStateProvider().CaptureAsync(fixture.Root)).Fingerprint;
            Assert.Equal(fingerprintOnDisk, task.CurrentImplementationRevision.Fingerprint);

            // ---- the reused checks: provenance persisted, bound to the verified state ------------------------------------------
            var reused = final.Checks.Where(c => c.Source == VerificationEvidenceSource.AgentObserved).ToList();
            output.WriteLine("reused (agent-observed): " + string.Join(", ", reused.Select(c => $"{c.CheckId}[{c.Observation!.ToolUseId}, {c.Observation.DurationMilliseconds} ms]")));
            Assert.True(reused.Count >= 2, $"expected the build and the tests to be reused, got {reused.Count}: {string.Join("; ", result.EvidenceDecisions.Select(d => $"{d.CheckId}={d.Reason}"))}");
            Assert.Contains(reused, c => c.CheckId == Build);
            Assert.Contains(reused, c => c.CheckId == Test);

            foreach (var check in reused)
            {
                Assert.Equal(VerificationOutcome.Passed, check.Outcome);
                Assert.Equal("claude-code", check.Observation!.Runtime);
                Assert.StartsWith("toolu_", check.Observation.ToolUseId);
                Assert.Equal(task.CurrentImplementationRevision.Fingerprint, check.Observation.StateFingerprint);
                Assert.Equal(check.CheckId == Build ? BuildCommand : TestCommand, check.Observation.CommandIdentity);
            }

            // ---- proof that what was reused was NOT executed again by the controller -----------------------------------------
            // Across all verification runs of the task, the controller executed a check exactly as often as it was recorded as controller-executed.
            foreach (var check in Checks())
            {
                var recordedAsControllerExecuted = task.Verifications.Count(v => v.Checks.Single(c => c.CheckId == check.Id).Source == VerificationEvidenceSource.ControllerExecuted);
                Assert.Equal(recordedAsControllerExecuted, verifier.CountOf(check.Id.Value));
            }

            // the checks the agent was never asked to run were run by the controller, in the final verification
            foreach (var controllerOnly in new[] { DirectPathSetup.OldSymbolAbsent, DirectPathSetup.NewSymbolPresent })
            {
                var recorded = final.Checks.Single(c => c.CheckId == controllerOnly);
                Assert.Equal(VerificationEvidenceSource.ControllerExecuted, recorded.Source);
                Assert.Equal(VerificationOutcome.Passed, recorded.Outcome);
                Assert.True(verifier.CountOf(controllerOnly.Value) >= 1);
            }

            var saved = reused.Sum(c => c.Observation!.DurationMilliseconds ?? 0);
            output.WriteLine($"controller execution avoided: {saved / 1000.0:F1} s of agent-observed build+test time");

            // ---- the agent did nothing it was forbidden to do; the hooks left nothing behind -----------------------------------
            Assert.Equal(baselineHead, fixture.Git("rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, fixture.Git("diff", "--cached", "--name-only"));
            Assert.All(
                fixture.Git("status", "--short", "--untracked-files=all").Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => Assert.StartsWith(" M ", line)); // only the agent's edits to tracked files: the hooks wrote nothing into the repository

            var session = Assert.Single(observing.Started);
            Assert.Equal(session, Assert.Single(observing.Stopped));
        }
        finally
        {
            LiveFixtureSupport.RestoreBaseline(guard, output.WriteLine);
            LiveFixtureSupport.DeleteDatabase(database);
        }
    }

    private static string HookExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(root, "src", "AgenticWorkflow.ClaudeEvidenceHook", "bin", configuration, "net10.0", "AgenticWorkflow.ClaudeEvidenceHook.exe");

            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("The hook executable has not been built (build the solution first).");
    }
}
