using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Persistence.Sqlite;
using AgenticWorkflow.Runtime.Herdr;
using Xunit.Abstractions;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>Runs only when <c>AGENTICWORKFLOW_LIVE_DIRECT_REPAIR=1</c>: the live repair test makes real model calls.</summary>
public sealed class LiveDirectRepairFactAttribute : FactAttribute
{
    public const string Variable = "AGENTICWORKFLOW_LIVE_DIRECT_REPAIR";

    public LiveDirectRepairFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Live direct-path REPAIR test (real Claude Code, real model calls). Set {Variable}=1 and {TrustedLiveFixture.WorkspaceVariable}=<trusted fixture> to run it.";
    }
}

/// <summary>
/// The real same-agent repair path, end to end, against native Claude Code.
/// <code>
/// Claude turn 1 (valid rename)
///   -> test-only disturbance puts one reference back to GetMeal            (the workspace is now objectively invalid)
///   -> the controller observes THAT state, records it, runs its real checks -> FAILED
///   -> production repair feedback -> the SAME Claude session
/// Claude turn 2 (real edit) -> new implementation revision -> verification PASSED -> Completed
/// </code>
/// The disturbance is a test-only hook in <see cref="ObservingAgentRuntime"/>: it runs after the agent's turn has ended and before
/// the runner observes the workspace, so the controller fingerprints, verifies and records exactly the disturbed state (nothing is
/// altered between recording and verifying). The verification failure comes from the normal verification runner (a real
/// <c>git grep</c> and a real <c>dotnet test</c>); no <c>VerificationRun</c> is fabricated. Production code has no fault injector.
/// </summary>
[Trait("Category", "LiveDirectRepair")]
public sealed class LiveDirectRepairTests(ITestOutputHelper output)
{
    private const string Disturbed = "tests/Meals.Tests/MenuTests.cs";

    [LiveDirectRepairFact]
    public async Task A_verification_failure_is_repaired_by_the_same_native_session_and_only_the_repaired_revision_completes()
    {
        var guard = LiveFixtureSupport.RequireTrustedFixture("LIVE DIRECT REPAIR");

        var database = Path.Combine(Path.GetTempPath(), "aw-live-repair-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        using var fixture = TrustedLiveFixture.ResetToBaseline(guard);
        var baselineHead = fixture.Git("rev-parse", "HEAD").Trim();
        var baselineBranch = fixture.Git("branch", "--show-current").Trim();
        var baselineStash = fixture.Git("stash", "list");

        output.WriteLine($"fixture (trusted, reused): {fixture.Root}");
        output.WriteLine($"herdr session: agenticworkflow-m2c-{suffix}");

        try
        {
            await LiveFixtureSupport.AssertBaselineIsKnownGoodAsync(fixture);

            var service = await DirectPathSetup.OpenServiceAsync(database);
            await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood in the fixture repository (repair path)");

            await using var herdr = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
            {
                Session = $"agenticworkflow-m2c-{suffix}",
                Profiles = [ClaudeCodeLaunch.Profile(DirectPathSetup.ClaudeProfile)],
                StartupTimeout = TimeSpan.FromSeconds(120),
            });

            // ---- test composition only: one controlled disturbance, after turn 1, before the controller looks ----------
            var runtime = new ObservingAgentRuntime(herdr);
            Disturbance? disturbance = null;
            runtime.AfterTurn = (_, turn, outcome) =>
            {
                if (turn.Value == 1 && outcome.Status == AgentTurnStatus.Done && disturbance is null)
                    disturbance = Disturb(fixture);

                return Task.CompletedTask;
            };

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
            Assert.True(result.Status == DirectRunStatus.AwaitingAcceptance, $"repair run did not complete: {RunClassifier.Describe(result)}");

            // ---- the disturbance happened once, at turn 1 ----------------------------------------------------------------------
            Assert.NotNull(disturbance);
            output.WriteLine($"disturbance: after turn 1 one reference in {Disturbed} was put back from GetFood to GetMeal");

            // ---- same native session, turn counter advanced, no replacement ---------------------------------------------------
            var session = Assert.Single(runtime.Started);
            Assert.True(result.TurnsUsed >= 2, "the repair needs at least a second turn");
            Assert.All(runtime.Sent, sent => Assert.Equal(session, sent.Session));
            Assert.Equal(Enumerable.Range(1, runtime.Sent.Count), runtime.Sent.Select(s => s.Turn.Value));
            Assert.All(runtime.Outcomes, o => Assert.Equal(AgentTurnStatus.Done, o.Outcome.Status));
            Assert.Equal(session, Assert.Single(runtime.Stopped));
            Assert.Empty(result.Notes);
            output.WriteLine($"session {session}: turns {string.Join(", ", runtime.Sent.Select(s => s.Turn.Value))}; outcomes {string.Join(", ", runtime.Outcomes.Select(o => o.Outcome.Status))}");

            // ---- the repair feedback is the production message, only failed checks, bounded ----------------------------------
            var repair = runtime.Sent[1].Prompt;
            output.WriteLine("repair message sent to the same session:" + Environment.NewLine + repair);
            Assert.StartsWith("Task:", runtime.Sent[0].Prompt);
            Assert.StartsWith("Controller verification failed", repair);
            Assert.Contains($"check: {DirectPathSetup.OldSymbolAbsent}", repair);
            Assert.Contains($"check: {DirectPathSetup.DotnetTest}", repair);
            Assert.DoesNotContain($"check: {DirectPathSetup.NewSymbolPresent}", repair); // it passed, so it is not sent back
            Assert.True(repair.Length < 4500, $"repair feedback must stay bounded, was {repair.Length} characters");

            // ---- authoritative history, from a brand-new service over the persisted file --------------------------------------
            var store = await SqliteEventStore.OpenAsync(database);
            var reopened = new WorkflowTaskService(store, new GuidIdSource(), new SystemClock());
            var task = (await reopened.LoadAsync(DirectPathSetup.TaskId)).Task;

            var events = (await store.LoadAsync(DirectPathSetup.TaskId)).Select(e => e.Kind).ToArray();
            output.WriteLine("events: " + string.Join(" > ", events));

            Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
            Assert.Equal(EventKinds.WorkflowStateChanged, events[^1]);
            Assert.Equal(1, events.Count(k => k == EventKinds.WorkflowStateChanged));

            // each verification directly follows its implementation revision
            var progression = events.Where(k => k is EventKinds.ImplementationRevisionRecorded or EventKinds.VerificationRecorded).ToArray();
            Assert.Equal(
                Enumerable.Range(0, task.Verifications.Length).SelectMany(_ => new[] { EventKinds.ImplementationRevisionRecorded, EventKinds.VerificationRecorded }),
                progression);

            var firstRevision = task.ImplementationRevisions[0];
            var firstVerification = task.Verifications[0];
            var currentRevision = task.CurrentImplementationRevision!;
            var currentVerification = task.CurrentVerification!;

            // the first verification failed on the disturbed state, for the real reasons, and stays history
            Assert.Equal(firstRevision.Id, firstVerification.ImplementationRevisionId);
            Assert.Equal(VerificationOutcome.Failed, firstVerification.Outcome);
            Assert.Equal(
                [DirectPathSetup.DotnetTest.Value, DirectPathSetup.OldSymbolAbsent.Value],
                firstVerification.Checks.Where(c => c.Outcome == VerificationOutcome.Failed).Select(c => c.CheckId.Value).Order().ToArray());
            Assert.Equal(VerificationOutcome.Passed, firstVerification.Checks.Single(c => c.CheckId == DirectPathSetup.NewSymbolPresent).Outcome);

            // the repaired state is a NEW revision, and the current verification belongs to it and passed
            Assert.NotEqual(firstRevision.Id, currentRevision.Id);
            Assert.NotEqual(firstRevision.Fingerprint, currentRevision.Fingerprint);
            Assert.Equal(currentRevision.Id, currentVerification.ImplementationRevisionId);
            Assert.Equal(VerificationOutcome.Passed, currentVerification.Outcome);
            Assert.Equal(task.CurrentSemanticRevisionId, currentRevision.SemanticRevisionId);
            Assert.Equal(VerificationOutcome.Failed, task.LatestVerificationFor(firstRevision.Id)!.Outcome); // the old failure is not rewritten
            Assert.Equal(
                [DirectPathSetup.OldSymbolAbsent.Value, DirectPathSetup.NewSymbolPresent.Value, DirectPathSetup.DotnetTest.Value],
                currentVerification.Checks.Select(c => c.CheckId.Value).ToArray());
            Assert.All(currentVerification.Checks, c => Assert.Equal(VerificationOutcome.Passed, c.Outcome));

            // replay through a fresh service reaches the state the run itself ended with
            var live = result.Task!.Task;
            Assert.Equal(live.CurrentImplementationRevisionId, task.CurrentImplementationRevisionId);
            Assert.Equal(live.CurrentVerification!.Id, currentVerification.Id);

            // ---- Claude really changed the workspace in the repair turn --------------------------------------------------------
            var repaired = disturbance!.Changed(fixture);
            output.WriteLine("files changed by the repair turn: " + string.Join(", ", repaired));
            Assert.Contains(Disturbed, repaired);
            Assert.DoesNotContain("GetMeal", fixture.Read(Disturbed));

            var final = await new GitImplementationStateProvider().CaptureAsync(fixture.Root);
            Assert.Equal(final.Fingerprint, currentRevision.Fingerprint); // the recorded fingerprint is the state on disk

            // ---- nothing forbidden happened ------------------------------------------------------------------------------------
            Assert.Equal(baselineStash, fixture.Git("stash", "list"));
            Assert.Equal(baselineBranch, fixture.Git("branch", "--show-current").Trim());
            Assert.Equal(baselineHead, fixture.Git("rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, fixture.Git("diff", "--cached", "--name-only"));
        }
        finally
        {
            LiveFixtureSupport.RestoreBaseline(guard, output.WriteLine);
            LiveFixtureSupport.DeleteDatabase(database);
        }
    }

    /// <summary>
    /// The controlled disturbance: the renamed test now calls the method by its old name, so the real checks must fail
    /// (<c>git grep</c> finds <c>GetMeal</c>; the test project no longer compiles). Bounded to one occurrence in one file.
    /// </summary>
    private static Disturbance Disturb(DirectFixtureRepo fixture)
    {
        var before = fixture.Read(Disturbed);
        var index = before.IndexOf("GetFood", StringComparison.Ordinal);

        if (index < 0)
            throw new InvalidOperationException($"disturbance precondition not met: turn 1 did not rename the reference in {Disturbed}");

        fixture.Write(Disturbed, before[..index] + "GetMeal" + before[(index + "GetFood".Length)..]);

        return new Disturbance(new[] { "src/Meals/Menu.cs", "src/Meals/Waiter.cs", Disturbed }.ToDictionary(path => path, fixture.Read));
    }

    /// <summary>The state right after the disturbance, to show later which files the repair changed.</summary>
    private sealed record Disturbance(IReadOnlyDictionary<string, string> AfterDisturbance)
    {
        public IReadOnlyList<string> Changed(DirectFixtureRepo fixture)
            => AfterDisturbance.Where(pair => fixture.Read(pair.Key) != pair.Value).Select(pair => pair.Key).Order().ToArray();
    }
}
