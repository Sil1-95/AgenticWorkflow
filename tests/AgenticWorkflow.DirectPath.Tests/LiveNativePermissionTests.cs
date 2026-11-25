using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Runtime.Herdr;
using Xunit.Abstractions;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>Runs only when <c>AGENTICWORKFLOW_LIVE_NATIVE=1</c>: these tests drive the real <c>aw.exe</c>, Herdr and Claude Code and make real model calls.</summary>
public sealed class LiveNativeFactAttribute : FactAttribute
{
    public const string Variable = "AGENTICWORKFLOW_LIVE_NATIVE";

    public LiveNativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Live native-permission test (real aw.exe, Herdr and Claude Code, real model calls). Set {Variable}=1 and {TrustedLiveFixture.WorkspaceVariable}=<trusted fixture> to run it.";
    }
}

/// <summary>
/// Ordinary tool permissions belong to Claude Code and its host (Herdr); AgenticWorkflow only observes them and keeps the hard limits. These tests
/// prove that against the real thing, on the dedicated, user-trusted fixture only. Nothing is committed or pushed, no trust prompt is answered, no
/// settings are written, and no profile is widened.
/// <list type="bullet">
/// <item>An attended run: Claude Code raises its own permission prompt, <c>aw</c> reports AWAITING HUMAN and where to answer, the test plays the
/// person by pressing the key IN HERDR (never through <c>aw</c>, which has no input path), and the same turn then continues to completion.</item>
/// <item>A hard-denied operation: the agent's own deny rule refuses it with no prompt at all, even though nobody is attending the run.</item>
/// <item>A headless run: the native prompt appears, nobody answers it, and <c>aw</c> records AWAITING_HUMAN while the prompt is live, stops
/// the session and then records BLOCKED (exit 15) without allowing or denying anything; a fresh replay ends BLOCKED, and <c>aw abort</c> cancels it.</item>
/// <item>An unattended automated run on Claude Code's own <c>auto</c> permission mode, chosen by this test and never by a production profile.</item>
/// </list>
/// </summary>
[Trait("Category", "LiveNative")]
public sealed class LiveNativePermissionTests(ITestOutputHelper output)
{
    private const string PermissionTask =
        "Do two things, in this order. First run the single shell command `dotnet --list-sdks` and note its output "
        + "(it needs a permission: if it is refused, simply continue without it). Then rename the public method Menu.GetMeal() to GetFood() "
        + "and update every reference to it (source and tests). Keep behavior identical.";

    private const string HardDenyTask =
        "Do two things, in this order. First try to run the single shell command `git stash` (it may be refused: if it is, "
        + "do not look for another way, just continue). Then rename the public method Menu.GetMeal() to GetFood() and update "
        + "every reference to it (source and tests). Keep behavior identical.";

    // ---- 1. attended: the native prompt is answered in Herdr, aw only observes -------------------------------------------------

    [LiveNativeFact]
    public async Task A_native_permission_prompt_is_answered_in_herdr_and_aw_only_observes_it()
    {
        await using var live = LiveRun.Begin("LIVE NATIVE PERMISSION", output);
        var run = await live.DriveAttendedAsync(PermissionTask, "--interactive");

        Assert.True(run.Prompts >= 1, "the run never reported a native interaction");
        Assert.Contains("NATIVE INTERACTION REQUIRED", run.Output);
        Assert.Contains("dotnet --list-sdks", run.Output);
        Assert.Contains("hc attach", run.Output);
        Assert.Contains("agent resumed after the native interaction", run.Output);

        // aw offered no prompt of its own and recorded no decision
        Assert.DoesNotContain("Decision [d/a/q]", run.Output);
        Assert.DoesNotContain("ALLOW_ONCE", run.Output);

        // the wait came and went inside ONE turn of ONE session, and the run ended VERIFIED, awaiting the human ACCEPT (it is not complete)
        Assert.Equal(17, run.ExitCode);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", run.Output);
        Assert.DoesNotContain("COMPLETED", run.Output);
        Assert.Contains("Turns: 1", run.Output);
        Assert.True(run.Output.IndexOf("NATIVE INTERACTION REQUIRED", StringComparison.Ordinal) < run.Output.IndexOf("agent resumed", StringComparison.Ordinal));
        Assert.Equal(0, (await new LocalVerificationRunner().RunAsync(live.Fixture.Root, DirectPathSetup.SymbolAndTestChecks()[2])).ExitCode);
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());

        // Claude Code's own record: the person's native "yes" let exactly that one command run
        live.AssertSingleSessionRanCommand("list-sdks", expectError: false);

        var status = await live.RunAwAsync("status", run.RunId);
        output.WriteLine(status.Output);
        Assert.Contains("Pending gate: ACCEPT", status.Output);
        Assert.Contains("COMPLETED", status.Output);
        Assert.DoesNotContain("Pending human request", status.Output);
    }

    // ---- 2. hard deny: refused natively, no prompt, nobody attending ---------------------------------------------------------

    [LiveNativeFact]
    public async Task A_hard_denied_operation_is_refused_by_the_agent_itself_without_any_prompt()
    {
        await using var live = LiveRun.Begin("LIVE NATIVE HARD DENY", output);
        var stashBefore = live.Fixture.Git("stash", "list");

        // --non-interactive: if the denied command had produced a prompt, this run would end in exit 15
        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", HardDenyTask);
        output.WriteLine(run.Output);

        Assert.DoesNotContain("AWAITING HUMAN", run.Output);
        Assert.Equal(17, run.ExitCode);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", run.Output);

        // the denial is the agent's own deny rule: the transcript shows the command asked for and refused, and nothing changed
        live.AssertSingleSessionRanCommand("git stash", expectError: true);
        Assert.Equal(stashBefore, live.Fixture.Git("stash", "list"));
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, live.Fixture.Git("diff", "--cached", "--name-only"));
    }

    // ---- 3. headless: stop truthfully; no auto-approve, no auto-deny -------------------------------------------------------

    [LiveNativeFact]
    public async Task A_headless_run_stops_at_the_native_prompt_without_allowing_or_denying_it()
    {
        await using var live = LiveRun.Begin("LIVE NATIVE HEADLESS", output);

        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", PermissionTask);
        output.WriteLine(run.Output);

        Assert.Equal(15, run.ExitCode);
        Assert.Contains("AWAITING HUMAN", run.Output);
        Assert.Contains("dotnet --list-sdks", run.Output);
        Assert.Contains("BLOCKED - NATIVE INTERACTION UNAVAILABLE", run.Output);
        Assert.DoesNotContain("COMPLETED", run.Output);

        // nothing was allowed: the command the agent asked for never ran
        live.AssertNoCompletedCommand("list-sdks");
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());

        var runId = LiveRun.RunIdOf(run.Output);

        var status = await live.RunAwAsync("status", runId);
        output.WriteLine(status.Output);
        Assert.Contains("State: BLOCKED", status.Output);
        Assert.DoesNotContain("AWAITING_HUMAN", status.Output);
        Assert.Contains("Native agent interaction was required", status.Output);
        Assert.Contains("native session has ended", status.Output);
        Assert.Contains("can no longer be answered", status.Output);
        Assert.DoesNotContain("hc attach", status.Output);   // never point at a session that is gone

        var abort = await live.RunAwAsync("abort", runId);
        output.WriteLine(abort.Output);
        Assert.Equal(16, abort.ExitCode);

        var after = await live.RunAwAsync("status", runId);
        Assert.Contains("CANCELLED", after.Output);
    }

    // ---- 4. automated tests: Claude Code's own auto mode, no brokering by aw ---------------------------------------------------

    /// <summary>
    /// How the automated, model-backed tests run unattended: the agent's own <c>auto</c> permission mode decides what a person would otherwise be asked,
    /// and its deny list still wins. This is a choice of THIS test through the launch policy; production profiles keep <c>acceptEdits</c>.
    /// </summary>
    [LiveNativeFact]
    public async Task An_unattended_run_in_the_agents_own_auto_mode_completes_with_no_brokering_by_aw()
    {
        var guard = LiveFixtureSupport.RequireTrustedFixture("LIVE NATIVE AUTO MODE");
        var database = Path.Combine(Path.GetTempPath(), "aw-live-auto-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var herdrSession = $"agenticworkflow-auto-{Guid.NewGuid().ToString("N")[..8]}";

        using var fixture = TrustedLiveFixture.ResetToBaseline(guard);
        var baselineHead = fixture.Git("rev-parse", "HEAD").Trim();

        try
        {
            await LiveFixtureSupport.AssertBaselineIsKnownGoodAsync(fixture);

            var service = await DirectPathSetup.OpenServiceAsync(database);
            await DirectPathSetup.CreateTaskAsync(service, "Rename GetMeal to GetFood (native auto mode)");

            await using var herdr = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
            {
                Session = herdrSession,
                Profiles = [ClaudeCodeLaunch.Profile(DirectPathSetup.ClaudeProfile, new ClaudeCodeToolPolicy { PermissionMode = "auto" })],
                StartupTimeout = TimeSpan.FromSeconds(120),
            });

            var runtime = new ObservingAgentRuntime(herdr);

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
            output.WriteLine("profile argv: " + string.Join(' ', ClaudeCodeLaunch.Profile(DirectPathSetup.ClaudeProfile, new ClaudeCodeToolPolicy { PermissionMode = "auto" }).Argv));

            Assert.True(result.Status == DirectRunStatus.AwaitingAcceptance, $"direct run ended {result.Status}: {result.Reason}");

            // nothing waited for a person, so nothing needed to be brokered: the only things the runtime ever reported were turn endings
            Assert.DoesNotContain(runtime.Outcomes, o => o.Outcome.Status is AgentTurnStatus.AwaitingHuman or AgentTurnStatus.Resumed);

            var task = (await (await DirectPathSetup.OpenServiceAsync(database)).LoadAsync(DirectPathSetup.TaskId)).Task;
            Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
            Assert.Empty(task.PolicyDecisions);
            Assert.Equal(baselineHead, fixture.Git("rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, fixture.Git("stash", "list").Trim());
        }
        finally
        {
            LiveFixtureSupport.RestoreBaseline(guard, output.WriteLine);
            LiveFixtureSupport.DeleteDatabase(database);
        }
    }

    // ---- 5. M4A: the final ACCEPT gate, real aw.exe, real Herdr, real Claude Code ------------------------------------------------

    private const string RenameTask =
        "Rename the public method Menu.GetMeal() to GetFood() and update every reference to it (source and tests). Keep behavior identical.";

    [LiveNativeFact]
    public async Task A_verified_run_awaits_the_human_and_aw_accept_from_a_fresh_process_completes_it_with_no_agent()
    {
        await using var live = LiveRun.Begin("LIVE ACCEPT GATE", output);

        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", RenameTask);
        output.WriteLine(run.Output);

        Assert.Equal(17, run.ExitCode);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", run.Output);
        Assert.DoesNotContain("COMPLETED", run.Output);
        Assert.Contains("Verification: PASSED", run.Output);
        var runId = run.RunId;
        var sessionsAfterRun = live.NewTranscripts().Count;
        Assert.Equal(1, sessionsAfterRun);
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());

        // a fresh process sees the durable gate
        var clock = Stopwatch.StartNew();
        var status = await live.RunAwAsync("status", runId);
        output.WriteLine($"---- aw status (fresh process, {clock.ElapsedMilliseconds} ms) ----");
        output.WriteLine(status.Output);
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("State: AWAITING_HUMAN", status.Output);
        Assert.Contains("Pending gate: ACCEPT (owned by AgenticWorkflow", status.Output);
        Assert.Contains("binding: current", status.Output);
        Assert.Contains("working tree: unchanged since verification", status.Output);
        Assert.DoesNotContain("Owner: the agent's native interface", status.Output);
        var gate = Regex.Match(status.Output, @"gate:\s+([0-9a-fA-F-]{36})").Groups[1].Value;
        Assert.NotEmpty(gate);

        // the human decides, from another process, with no agent and no model
        clock.Restart();
        var accept = await live.RunAwAsync("accept", runId, "--gate", gate);
        output.WriteLine($"---- aw accept (fresh process, {clock.ElapsedMilliseconds} ms) ----");
        output.WriteLine(accept.Output);
        Assert.Equal(0, accept.ExitCode);
        Assert.StartsWith("COMPLETED", accept.Output.TrimStart());
        Assert.Equal(sessionsAfterRun, live.NewTranscripts().Count);   // accept started no Claude session

        var completed = await live.RunAwAsync("status", runId);
        output.WriteLine(completed.Output);
        Assert.Contains("State: COMPLETED", completed.Output);
        Assert.Contains($"ACCEPT {gate}: ACCEPTED", completed.Output);

        // a second accept is refused and the repository was never committed to
        var again = await live.RunAwAsync("accept", runId, "--gate", gate);
        Assert.Equal(18, again.ExitCode);
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, live.Fixture.Git("stash", "list").Trim());
    }

    [LiveNativeFact]
    public async Task With_no_acceptance_required_a_verified_run_is_completed_by_the_controller_with_no_gate_and_no_human_decision()
    {
        await using var live = LiveRun.Begin("LIVE NO ACCEPTANCE", output);

        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", "--acceptance", "none", RenameTask);
        output.WriteLine(run.Output);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("COMPLETED", run.Output);
        Assert.Contains("Verification: PASSED", run.Output);
        Assert.Contains("Acceptance: not required", run.Output);
        Assert.DoesNotContain("AWAITING ACCEPT", run.Output);
        Assert.Single(live.NewTranscripts());
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());

        var clock = Stopwatch.StartNew();
        var status = await live.RunAwAsync("status", run.RunId);
        output.WriteLine($"---- aw status (fresh process, {clock.ElapsedMilliseconds} ms) ----");
        output.WriteLine(status.Output);
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("State: COMPLETED", status.Output);
        Assert.Contains("Acceptance: not required", status.Output);
        Assert.DoesNotContain("Pending gate", status.Output);

        // there is no gate to decide
        var accept = await live.RunAwAsync("accept", run.RunId);
        Assert.Equal(18, accept.ExitCode);
    }

    // ---- 6. M4C: native session continuity, real aw.exe, real Herdr, real Claude Code ----------------------------------------------

    /// <summary>The first task plants a code word the agent is only told once. The feedback never repeats it: only the same conversation can know it.</summary>
    private const string ContinuityTask =
        RenameTask + " Also remember this code word for later, but do not write it anywhere yet: PELICAN-7.";

    private const string ContinuityFeedback =
        "Also add a one-line XML doc comment directly above the GetFood method that contains the code word I gave you earlier, and nothing else about it.";

    [LiveNativeFact]
    public async Task Request_changes_from_a_fresh_process_resumes_the_same_native_conversation_and_a_new_gate_is_accepted()
    {
        await using var live = LiveRun.Begin("LIVE RESUME", output);

        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", ContinuityTask);
        output.WriteLine(run.Output);
        Assert.Equal(17, run.ExitCode);
        var runId = run.RunId;
        var firstGate = Regex.Match(run.Output, @"aw accept \S+ --gate ([0-9a-fA-F-]{36})").Groups[1].Value;
        Assert.NotEmpty(firstGate);
        var firstImplementation = Regex.Match(run.Output, @"Implementation:\s+(\S+)").Groups[1].Value;
        Assert.NotEmpty(firstImplementation);

        // S1: one Claude conversation, whose native identity aw recorded durably in the run directory
        var s1 = Assert.Single(live.NewTranscripts());
        var reference = live.ConversationReference(runId);
        output.WriteLine($"S1 native conversation: {reference}");
        Assert.Equal(reference, Path.GetFileNameWithoutExtension(s1));
        Assert.DoesNotContain(reference, run.Output);   // the opaque id is not dumped by default
        var s1Size = new FileInfo(s1).Length;

        // aw has exited and with it the agent process: the conversation is stopped, not lost
        Assert.Empty(await LiveRun.LiveClaudeProcessesAsync(reference));

        // a fresh aw process continues it
        var changes = await live.RunAwAsync("request-changes", runId, "--gate", firstGate, "--feedback", ContinuityFeedback, "--non-interactive");
        output.WriteLine(changes.Output);

        Assert.Equal(17, changes.ExitCode);
        Assert.Contains("changes requested (not approved", changes.Output);
        Assert.Contains("Agent continuation: resumed native conversation", changes.Output);
        Assert.DoesNotContain("Agent continuation: fresh session", changes.Output);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", changes.Output);
        Assert.DoesNotContain(reference, changes.Output);

        // technical identity: still exactly ONE Claude session (no S2), its id unchanged, and the same transcript grew with the feedback turn
        var after = Assert.Single(live.NewTranscripts());
        Assert.Equal(s1, after);
        Assert.True(new FileInfo(s1).Length > s1Size, "the resumed conversation did not append to the same transcript");
        Assert.Contains("code word I gave you earlier", File.ReadAllText(s1), StringComparison.Ordinal);
        Assert.Equal(reference, live.ConversationReference(runId));
        Assert.Empty(await LiveRun.LiveClaudeProcessesAsync(reference));

        // useful continuity: the feedback never said the word, the agent knew it from its own earlier conversation
        Assert.Contains("PELICAN-7", live.Fixture.Git("diff", "HEAD"));
        Assert.Contains("/// ", live.Fixture.Git("diff", "HEAD"));

        // semantic currentness is the controller's, independent of the conversation
        var secondGate = Regex.Match(changes.Output, @"aw accept \S+ --gate ([0-9a-fA-F-]{36})").Groups[1].Value;
        Assert.NotEmpty(secondGate);
        Assert.NotEqual(firstGate, secondGate);
        var secondImplementation = Regex.Match(changes.Output, @"Implementation:\s+(\S+)").Groups[1].Value;
        Assert.NotEqual(firstImplementation, secondImplementation);

        var stale = await live.RunAwAsync("accept", runId, "--gate", firstGate);
        Assert.Equal(18, stale.ExitCode);

        var status = await live.RunAwAsync("status", runId);
        output.WriteLine(status.Output);
        Assert.Contains($"ACCEPT {firstGate}: CHANGES REQUESTED", status.Output);
        Assert.Contains("binding: current", status.Output);
        Assert.Contains("feedback: Also add a one-line XML doc comment", status.Output);

        var accept = await live.RunAwAsync("accept", runId, "--gate", secondGate);
        output.WriteLine(accept.Output);
        Assert.Equal(0, accept.ExitCode);
        Assert.Equal(s1, Assert.Single(live.NewTranscripts()));   // accept started and resumed nothing

        var done = await live.RunAwAsync("status", runId);
        Assert.Contains("State: COMPLETED", done.Output);
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());
    }

    [LiveNativeFact]
    public async Task The_hard_authority_ceiling_holds_on_a_resumed_conversation()
    {
        await using var live = LiveRun.Begin("LIVE RESUME HARD DENY", output);
        var stashBefore = live.Fixture.Git("stash", "list");

        var run = await live.RunAwAsync("run", "--workspace", live.Fixture.Root, "--config", live.Config, "--non-interactive", RenameTask);
        output.WriteLine(run.Output);
        Assert.Equal(17, run.ExitCode);
        var runId = run.RunId;
        var firstGate = Regex.Match(run.Output, @"aw accept \S+ --gate ([0-9a-fA-F-]{36})").Groups[1].Value;
        var s1 = Assert.Single(live.NewTranscripts());
        live.AssertNoCompletedCommand("git stash");

        // the resumed conversation is told, in its own turn, to do something the profile hard-denies; nobody is attending
        const string feedback =
            "Before anything else, try to run the single shell command `git stash` (it may be refused: if it is, do not look for another way, just continue). "
            + "Then add a one-line XML doc comment directly above the GetFood method that says it returns the meal of the day.";

        var changes = await live.RunAwAsync("request-changes", runId, "--gate", firstGate, "--feedback", feedback, "--non-interactive");
        output.WriteLine(changes.Output);

        Assert.Equal(17, changes.ExitCode);
        Assert.Contains("Agent continuation: resumed native conversation", changes.Output);
        Assert.DoesNotContain("AWAITING HUMAN", changes.Output);   // refused by the agent's own deny rule: no prompt at all

        // the same session asked for it, and the agent's own rule refused it on the resumed launch
        Assert.Equal(s1, Assert.Single(live.NewTranscripts()));
        live.AssertSingleSessionRanCommand("git stash", expectError: true);
        Assert.Equal(stashBefore, live.Fixture.Git("stash", "list"));
        Assert.Equal(live.BaselineHead, live.Fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, live.Fixture.Git("diff", "--cached", "--name-only"));
        Assert.Contains("/// ", live.Fixture.Git("diff", "HEAD"));
    }

    // ---- machinery ---------------------------------------------------------------------------------------------------------

    private sealed record AwResult(int ExitCode, string Output, int Prompts = 0)
    {
        public string RunId => LiveRun.RunIdOf(Output);
    }

    /// <summary>One live scenario: the trusted fixture reset to its baseline, a scratch home and config, and the real aw.exe.</summary>
    private sealed class LiveRun : IAsyncDisposable
    {
        private readonly FixtureGuard _guard;
        private readonly string _scratch;
        private readonly ITestOutputHelper _output;
        private readonly IReadOnlyList<string> _transcriptsBefore;

        private LiveRun(FixtureGuard guard, DirectFixtureRepo fixture, string scratch, string aw, ITestOutputHelper output)
        {
            _guard = guard;
            Fixture = fixture;
            _scratch = scratch;
            Aw = aw;
            _output = output;
            BaselineHead = fixture.Git("rev-parse", "HEAD").Trim();
            _transcriptsBefore = ClaudeTranscripts(fixture.Root);
        }

        public DirectFixtureRepo Fixture { get; }

        public string Aw { get; }

        public string BaselineHead { get; }

        public string Config => Path.Combine(_scratch, "agenticworkflow.json");

        /// <summary>The opaque native conversation reference aw recorded for the run, read from its run directory (what a later aw process resumes).</summary>
        public string ConversationReference(string runId)
        {
            var path = Path.Combine(Home, "runs", runId, "conversation.json");
            Assert.True(File.Exists(path), $"no conversation was recorded for the run: {path}");
            using var record = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(runId, record.RootElement.GetProperty("runId").GetString(), ignoreCase: true);

            return record.RootElement.GetProperty("reference").GetString()!;
        }

        /// <summary>Claude Code processes whose command line names this native conversation (Windows process table, read only).</summary>
        public static async Task<IReadOnlyList<string>> LiveClaudeProcessesAsync(string reference)
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var script = $"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{reference}*' -and $_.Name -like 'claude*' }} | ForEach-Object {{ $_.ProcessId }}";

            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info) ?? throw new InvalidOperationException("powershell did not start");
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        private string Home => Path.Combine(_scratch, "home");

        public static LiveRun Begin(string title, ITestOutputHelper output)
        {
            var guard = LiveFixtureSupport.RequireTrustedFixture(title);
            var aw = LocateAw();
            var scratch = Path.Combine(Path.GetTempPath(), "aw-live-native-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(scratch);

            var fixture = TrustedLiveFixture.ResetToBaseline(guard);
            File.WriteAllText(Path.Combine(scratch, "agenticworkflow.json"), ConfigJson);

            output.WriteLine($"fixture (trusted, reused): {fixture.Root}");
            output.WriteLine($"aw: {aw}");

            var live = new LiveRun(guard, fixture, scratch, aw, output);
            LiveFixtureSupport.AssertBaselineIsKnownGoodAsync(fixture).GetAwaiter().GetResult();
            return live;
        }

        public static string RunIdOf(string output)
        {
            var id = Regex.Match(output, @"Run:\s+([0-9a-fA-F-]{36})").Groups[1].Value;
            Assert.NotEmpty(id);
            return id;
        }

        public async Task<AwResult> RunAwAsync(params string[] args)
        {
            using var process = Start(args);
            var text = new StringBuilder();
            await Task.WhenAll(ReadAsync(process.StandardOutput, text), ReadAsync(process.StandardError, text), process.WaitForExitAsync());
            return new AwResult(process.ExitCode, text.ToString());
        }

        /// <summary>
        /// Plays the person, IN HERDR: whenever aw reports AWAITING HUMAN with where to answer, presses Enter in that native session (Claude Code's
        /// default choice, "Yes", allow once). aw is never given input; its standard input is closed.
        /// </summary>
        public async Task<AwResult> DriveAttendedAsync(string task, string presenceFlag)
        {
            using var process = Start(["run", "--workspace", Fixture.Root, "--config", Config, presenceFlag, task]);
            process.StandardInput.Close();

            var text = new StringBuilder();
            var reader = ReadAsync(process.StandardOutput, text);
            var errors = ReadAsync(process.StandardError, text);
            var answered = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(12);

            while (!process.HasExited)
            {
                if (DateTime.UtcNow > deadline)
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException("aw did not finish in time. Output so far:" + Environment.NewLine + text);
                }

                string snapshot;
                lock (text)
                    snapshot = text.ToString();

                var waits = Regex.Matches(snapshot, @"Git Bash:\s+export HC_SESSION=(?<session>[A-Za-z0-9._-]+); hc attach (?<terminal>[A-Za-z0-9._-]+)");

                if (waits.Count > answered)
                {
                    var wait = waits[answered];
                    await PressInHerdrAsync(wait.Groups["session"].Value, wait.Groups["terminal"].Value, "enter");
                    answered++;
                }

                await Task.Delay(100);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(reader, errors);

            _output.WriteLine("---- aw run output ----");
            _output.WriteLine(text.ToString());
            _output.WriteLine($"---- end (exit {process.ExitCode}; native prompts answered in herdr: {answered}) ----");

            return new AwResult(process.ExitCode, text.ToString(), answered);
        }

        private static async Task PressInHerdrAsync(string session, string terminal, string key)
        {
            var info = new ProcessStartInfo("hc")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in new[] { "keys", terminal, key })
                info.ArgumentList.Add(arg);

            info.Environment["HC_SESSION"] = session;

            using var hc = Process.Start(info) ?? throw new InvalidOperationException("hc did not start");
            var stderr = hc.StandardError.ReadToEndAsync();
            await hc.WaitForExitAsync();

            if (hc.ExitCode != 0)
                throw new InvalidOperationException($"hc keys failed ({hc.ExitCode}): {await stderr}");
        }

        /// <summary>The one Claude Code session this scenario created; its transcript is the agent's own record of what it asked for and what it was told.</summary>
        private string Transcript()
        {
            var created = ClaudeTranscripts(Fixture.Root).Except(_transcriptsBefore).ToList();
            _output.WriteLine("new Claude session transcripts: " + string.Join(", ", created.Select(Path.GetFileName)));
            return Assert.Single(created);
        }

        /// <summary>The command was asked for exactly once, and its tool result was an error (refused) or a normal result (it ran).</summary>
        public void AssertSingleSessionRanCommand(string commandFragment, bool expectError)
        {
            var (uses, results) = ReadToolCalls(Transcript(), commandFragment);
            var id = Assert.Single(uses);
            var (isError, text) = results[id];
            _output.WriteLine($"{commandFragment}: is_error={isError}: {text}");

            Assert.Equal(expectError, isError);

            if (!expectError)
                Assert.Matches(@"\d+\.\d+\.\d+", text);
        }

        /// <summary>The Claude Code sessions this scenario created (their transcripts), oldest first.</summary>
        public IReadOnlyList<string> NewTranscripts()
            => [.. ClaudeTranscripts(Fixture.Root).Except(_transcriptsBefore).OrderBy(File.GetCreationTimeUtc)];

        /// <summary>The agent may have asked for the command, but it never produced a successful result.</summary>
        public void AssertNoCompletedCommand(string commandFragment)
        {
            var created = ClaudeTranscripts(Fixture.Root).Except(_transcriptsBefore).ToList();

            foreach (var transcript in created)
            {
                var (uses, results) = ReadToolCalls(transcript, commandFragment);
                Assert.DoesNotContain(uses, id => results.TryGetValue(id, out var r) && !r.IsError);
            }
        }

        private static (List<string> Uses, Dictionary<string, (bool IsError, string Content)> Results) ReadToolCalls(string transcript, string commandFragment)
        {
            var uses = new List<string>();
            var results = new Dictionary<string, (bool IsError, string Content)>();

            foreach (var line in File.ReadLines(transcript))
            {
                using var entry = JsonDocument.Parse(line);

                if (!entry.RootElement.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var block in content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "tool_use" && block.GetProperty("input").ToString().Contains(commandFragment, StringComparison.Ordinal) && !uses.Contains(block.GetProperty("id").GetString()!))
                        uses.Add(block.GetProperty("id").GetString()!);

                    if (type == "tool_result")
                    {
                        results[block.GetProperty("tool_use_id").GetString()!] =
                            (block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True, block.GetProperty("content").ToString());
                    }
                }
            }

            return (uses, results);
        }

        private Process Start(IReadOnlyList<string> args)
        {
            var info = new ProcessStartInfo(Aw)
            {
                WorkingDirectory = Fixture.Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            info.Environment["AGENTICWORKFLOW_HOME"] = Home;

            return Process.Start(info) ?? throw new InvalidOperationException("aw did not start");
        }

        public ValueTask DisposeAsync()
        {
            Fixture.Dispose();
            LiveFixtureSupport.RestoreBaseline(_guard, _output.WriteLine);

            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private static string ConfigJson => JsonSerializer.Serialize(new
    {
        version = 1,
        role = "root",
        profile = "claude-code-local",
        toolchain = "dotnet",
        maxTurns = 2,
        acceptance = "human",   // explicit: the M4A tests exercise the gate; the no-acceptance test passes --acceptance none
        verification = new[]
        {
            new { id = "tests", executable = "dotnet", arguments = new[] { "test", DirectFixtureRepo.TestProject, "--nologo", "--verbosity", "minimal" }, timeoutSeconds = 600 },
        },
        scope = new
        {
            writable = new[] { "src/**", "tests/**" },
            @protected = new[] { ".git/**", "src/Meals/Meals.csproj", "tests/Meals.Tests/Meals.Tests.csproj", ".gitignore" },
        },
    });

    private static Task ReadAsync(StreamReader reader, StringBuilder into)
        => Task.Run(async () =>
        {
            var buffer = new char[256];
            int read;

            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (into)
                    into.Append(buffer, 0, read);
            }
        });

    private static string LocateAw()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug";

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        var aw = directory is null
            ? null
            : Path.Combine(directory.FullName, "src", "AgenticWorkflow.Cli", "bin", configuration, "net10.0", "aw.exe");

        return aw is not null && File.Exists(aw) ? aw : throw new FileNotFoundException("aw.exe was not built; build src/AgenticWorkflow.Cli first.", aw);
    }

    /// <summary>Claude Code's own record of its sessions for a directory (read only).</summary>
    private static IReadOnlyList<string> ClaudeTranscripts(string workspace)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(home, ".claude", "projects", Regex.Replace(Path.GetFullPath(workspace).TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-"));

        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.jsonl") : [];
    }
}
