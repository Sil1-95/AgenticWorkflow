using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Runtime.Herdr;
using Xunit.Abstractions;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>Runs only when <c>AGENTICWORKFLOW_LIVE_DIRECT=1</c>: the live test makes real model calls.</summary>
public sealed class LiveDirectFactAttribute : FactAttribute
{
    public const string Variable = "AGENTICWORKFLOW_LIVE_DIRECT";

    public LiveDirectFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Live direct-path test (real Claude Code, real model calls). Set {Variable}=1 to run it.";
    }
}

/// <summary>
/// The first real direct-path workflow, end to end, on a dedicated trusted repository: a native Claude Code session in an
/// isolated Herdr core renames <c>Menu.GetMeal()</c> to <c>GetFood()</c>; the controller records the exact implementation
/// revision, runs its own verification (symbol checks and <c>dotnet test</c>), repairs in the SAME session if needed, and only
/// then records <c>Completed</c> in a real SQLite event store. Nothing outside the fixture is modified, nothing is committed or
/// pushed, no dialog is answered.
/// <para>
/// Claude Code treats a Git repository root as its own trust boundary, so the workspace is NOT created per run. It is the
/// repository named by <c>AGENTICWORKFLOW_LIVE_DIRECT_WORKSPACE</c>: one a person created, trusted in Claude Code and marked
/// with <c>.agenticworkflow-live-fixture</c> (see <see cref="TrustedLiveFixture"/> for the guards). The test rebuilds its
/// baseline contents before and after the run and never deletes the root or its <c>.git</c>.
/// </para>
/// <para>
/// Run with <c>AGENTICWORKFLOW_LIVE_DIRECT=1</c> and <c>AGENTICWORKFLOW_LIVE_DIRECT_WORKSPACE=&lt;repo&gt;</c>, then
/// <c>dotnet test --filter Category=LiveDirect</c>. If Claude Code asks for something a person must decide, the run ends
/// BLOCKED and says what is needed.
/// </para>
/// </summary>
[Trait("Category", "LiveDirect")]
public sealed class LiveDirectPathTests(ITestOutputHelper output)
{
    [LiveDirectFact]
    public async Task Claude_renames_the_method_the_controller_verifies_it_and_only_then_records_completion()
    {
        var guard = LiveFixtureSupport.RequireTrustedFixture("LIVE DIRECT PATH");

        var database = Path.Combine(Path.GetTempPath(), "aw-live-direct-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Guards have passed: from here on the fixture is rebuilt to its deterministic baseline.
        using var fixture = TrustedLiveFixture.ResetToBaseline(guard);
        var baselineHead = fixture.Git("rev-parse", "HEAD").Trim();
        var baselineBranch = fixture.Git("branch", "--show-current").Trim();
        var baselineStash = fixture.Git("stash", "list");

        output.WriteLine($"fixture (trusted, reused): {fixture.Root}");
        output.WriteLine($"fixture baseline commit: {baselineHead} on '{baselineBranch}'");
        output.WriteLine($"herdr session: agenticworkflow-m2b-{suffix}");

        try
        {
            await LiveFixtureSupport.AssertBaselineIsKnownGoodAsync(fixture);

            var service = await DirectPathSetup.OpenServiceAsync(database);
            await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood in the fixture repository");

            await using var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
            {
                Session = $"agenticworkflow-m2b-{suffix}",
                Profiles = [ClaudeCodeLaunch.Profile(DirectPathSetup.ClaudeProfile)],
                StartupTimeout = TimeSpan.FromSeconds(120),
            });

            var runner = new DirectPathRunner(
                service,
                runtime,
                new GitImplementationStateProvider(),
                new LocalVerificationRunner(),
                new DirectRunOptions { TurnTimeout = TimeSpan.FromMinutes(10) });

            var result = await runner.RunAsync(
                DirectPathSetup.TaskId,
                DirectPathSetup.Definition(fixture.Root, DirectPathSetup.SymbolAndTestChecks(), maxTurns: 3));

            LiveFixtureSupport.Report(output.WriteLine, result, fixture);

            LiveFixtureSupport.AssertNotStoppedByAPerson(result);
            Assert.True(result.Status == DirectRunStatus.AwaitingAcceptance, $"direct run ended {result.Status}: {result.Reason}");

            // ---- evidence, from a brand-new service over the persisted file ---------------------------------------------
            var reopened = await DirectPathSetup.OpenServiceAsync(database);
            var task = (await reopened.LoadAsync(DirectPathSetup.TaskId)).Task;

            Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);

            var implementation = task.CurrentImplementationRevision!;
            var verification = task.CurrentVerification!;
            Assert.Equal(implementation.Id, verification.ImplementationRevisionId);
            Assert.Equal(task.CurrentSemanticRevisionId, implementation.SemanticRevisionId);
            Assert.Equal(VerificationOutcome.Passed, verification.Outcome);
            Assert.Equal(
                [DirectPathSetup.OldSymbolAbsent.Value, DirectPathSetup.NewSymbolPresent.Value, DirectPathSetup.DotnetTest.Value],
                verification.Checks.Select(c => c.CheckId.Value).ToArray());

            // the recorded fingerprint is exactly the state on disk right now
            var final = await new GitImplementationStateProvider().CaptureAsync(fixture.Root);
            Assert.Equal(final.Fingerprint, implementation.Fingerprint);

            // the controller's own, independent re-check of the result
            Assert.DoesNotContain("GetMeal", fixture.Read("src/Meals/Menu.cs"));
            Assert.Contains("GetFood", fixture.Read("src/Meals/Menu.cs"));
            Assert.Contains("GetFood", fixture.Read("src/Meals/Waiter.cs"));
            Assert.Contains("GetFood", fixture.Read("tests/Meals.Tests/MenuTests.cs"));

            // nothing the agent was forbidden to do happened
            Assert.Equal(baselineStash, fixture.Git("stash", "list"));
            Assert.Equal(baselineBranch, fixture.Git("branch", "--show-current").Trim());
            Assert.Equal(baselineHead, fixture.Git("rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, fixture.Git("diff", "--cached", "--name-only"));

            // one session, every turn on it
            Assert.NotNull(result.Session);
            Assert.All(result.Turns, t => Assert.Equal(result.Session, t.Session));
        }
        finally
        {
            LiveFixtureSupport.RestoreBaseline(guard, output.WriteLine);

            LiveFixtureSupport.DeleteDatabase(database);
        }
    }
}
