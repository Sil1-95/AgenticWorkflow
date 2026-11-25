using System.Text.RegularExpressions;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// a native Claude/Herdr wait is impossible to miss for a person supervising an attended run, and says exactly how to attach. Visibility only: AgenticWorkflow
/// never answers the agent, records no permission decision and offers no command that could. Model-free; the agent runtime is scripted and nothing is shown.
/// </summary>
public sealed class NativeVisibilityCliTests
{
    private const string Request = "approve Bash: dotnet --list-sdks";

    private const string PowerShellLine = "PowerShell:   $env:HC_SESSION = \"scripted-core\"; hc attach scripted-1";
    private const string GitBashLine = "Git Bash:     export HC_SESSION=scripted-core; hc attach scripted-1";

    private static readonly string[] AnswerCommands = ["aw answer", "aw accept", "aw request-changes"];

    private static void WriteFeature(string workspace)
    {
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), $"{CliHarness.Symbol}\n");
    }

    /// <summary>
    /// The agent waits for a person (step 1 of the script is "while the person is at the prompt"), the person acts natively (invisible to aw), the agent resumes
    /// (step 2, "after the resume") and finishes the same turn.
    /// </summary>
    private static Func<int, string, CancellationToken, Task<AgentTurnOutcome>> Script(
        AgentTurnOutcome wait,
        Func<Task>? whileWaiting = null,
        Func<Task>? afterResume = null,
        Func<Task>? beforeWait = null)
    {
        var step = 0;

        return async (_, workspace, _) =>
        {
            switch (step++)
            {
                case 0:
                    if (beforeWait is not null)
                        await beforeWait();

                    return wait;
                case 1:
                    if (whileWaiting is not null)
                        await whileWaiting();

                    return AgentTurnOutcome.Resumed;
                default:
                    if (afterResume is not null)
                        await afterResume();

                    WriteFeature(workspace);
                    return AgentTurnOutcome.Done;
            }
        };
    }

    private static RunStore StoreOf(CliHarness harness) => new(harness.Home);

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await StoreOf(harness).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    /// <summary>The console notice, line by line, without the time stamp and indentation of the progress lines.</summary>
    private static string[] NoticeOf(string output)
    {
        var lines = output.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^\[[^\]]+\]\s+", string.Empty)).ToList();
        var start = lines.FindIndex(l => l == "NATIVE INTERACTION REQUIRED");

        Assert.True(start >= 0, $"no notice in:\n{output}");

        var end = lines.FindIndex(start, l => l.StartsWith("AgenticWorkflow keeps monitoring", StringComparison.Ordinal));

        Assert.True(end >= 0, $"the notice is not complete in:\n{output}");

        return lines.Skip(start).Take(end - start + 1).ToArray();
    }

    private static int Occurrences(string text, string what)
        => Regex.Matches(text, Regex.Escape(what)).Count;

    private static void AssertNoAnswerCommand(string text)
    {
        foreach (var command in AnswerCommands)
            Assert.DoesNotContain(command, text);
    }

    /// <summary>A run whose process died while its agent waited: AWAITING_HUMAN in the stream with no gate, and nothing holds the run.</summary>
    private static async Task<(CliHarness Harness, TaskId RunId)> DeadAwaitingHumanRunAsync()
    {
        var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait(Request));

        Assert.Equal(15, await harness.RunAsync("run", "task"));

        var runId = harness.PrintedRunId();
        var tasks = await StoreOf(harness).OpenExistingAsync(runId, CancellationToken.None);
        await tasks.ChangeWorkflowStateAsync((await tasks.FindAsync(runId, CancellationToken.None))!, WorkflowState.AwaitingHuman, "The agent is waiting for a person in its own interface.", CancellationToken.None);

        return (harness, runId);
    }

    private static NativeInteractionRecord Annotation(TaskId runId, string? host = "scripted-core", string? terminal = "scripted-1")
        => new(NativeInteractionRecord.CurrentVersion, runId.ToString(), "permission", Request, host, terminal, DateTimeOffset.UtcNow);

    // ---- the console notice ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_attended_wait_prints_one_clear_notice_with_the_attach_commands_for_both_shells_and_nothing_that_answers()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request));

        var exit = await harness.RunAsync("run", "--interactive", "task");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Equal(
            [
                "NATIVE INTERACTION REQUIRED",
                "permission: approve Bash: dotnet --list-sdks",
                "The agent is waiting for input in Herdr. Answer it there; AgenticWorkflow never answers it.",
                PowerShellLine,
                GitBashLine,
                "AgenticWorkflow keeps monitoring this run and continues after you answer.",
            ],
            NoticeOf(output));

        // the notice itself never names a command that answers the agent
        AssertNoAnswerCommand(string.Join('\n', NoticeOf(output)));

        // the resumed line stays, after the notice
        Assert.True(output.IndexOf("NATIVE INTERACTION REQUIRED", StringComparison.Ordinal) < output.IndexOf("agent resumed after the native interaction", StringComparison.Ordinal));
        Assert.Equal(string.Empty, harness.Error.ToString());
    }

    [Fact]
    public async Task One_interaction_is_announced_once_and_a_second_interaction_is_announced_again()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var step = 0;
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            switch (step++)
            {
                case 0:
                    return Task.FromResult(ScriptedRuntime.Wait("approve Bash: one"));
                case 1:
                    return Task.FromResult(AgentTurnOutcome.Resumed);
                case 2:
                    return Task.FromResult(ScriptedRuntime.Wait("approve Bash: two"));
                case 3:
                    return Task.FromResult(AgentTurnOutcome.Resumed);
                default:
                    WriteFeature(workspace);
                    return Task.FromResult(AgentTurnOutcome.Done);
            }
        };

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        var output = harness.Out.ToString();
        Assert.Equal(2, Occurrences(output, "NATIVE INTERACTION REQUIRED"));
        Assert.Equal(1, Occurrences(output, "permission: approve Bash: one"));
        Assert.Equal(1, Occurrences(output, "permission: approve Bash: two"));
        Assert.Equal(2, harness.Notifier.Calls.Count);
        Assert.Single(harness.Factory.Runtime.Prompts);   // still one turn
    }

    [Fact]
    public async Task The_description_is_shown_only_when_present_and_is_bounded_single_line_and_free_of_control_characters()
    {
        using var noisy = new CliHarness(CliHarness.ValidConfig());
        noisy.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait("approve Bash: echo \u001b[2Jgone\r\n\u0007bell\n" + new string('x', 5000)));

        Assert.Equal(17, await noisy.RunAsync("run", "--interactive", "task"));

        var noisyOutput = noisy.Out.ToString();
        var request = NoticeOf(noisyOutput)[1];
        Assert.StartsWith("permission: approve Bash: echo ?[2Jgone", request);
        Assert.EndsWith("...", request);
        Assert.True(request.Length <= "permission: ".Length + 300, $"{request.Length} characters");
        Assert.DoesNotContain('\u001b', noisyOutput);
        Assert.DoesNotContain('\u0007', noisyOutput);
        Assert.DoesNotContain(noisyOutput.Split('\n'), line => line.Contains("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", StringComparison.Ordinal));

        foreach (var blank in new[] { string.Empty, "   \n\t " })
        {
            using var harness = new CliHarness(CliHarness.ValidConfig());
            harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(blank));

            Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

            var notice = NoticeOf(harness.Out.ToString());
            Assert.Equal("permission", notice[1]);   // just the kind: nothing is invented where the harness said nothing
        }
    }

    [Fact]
    public async Task Without_a_known_attach_target_the_notice_shows_no_command_at_all()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request, null));

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        var notice = string.Join('\n', NoticeOf(harness.Out.ToString()));
        Assert.DoesNotContain("hc attach", notice);
        Assert.DoesNotContain("HC_SESSION", notice);
        Assert.Contains("No attach command could be shown safely", notice);
    }

    [Fact]
    public async Task The_notice_is_printed_once_the_wait_is_recorded_and_before_the_desktop_notification()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        string? whileWaiting = null;
        string? state = null;
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            async () =>
            {
                await harness.Notifier.Called.WaitAsync(TimeSpan.FromSeconds(10));
                whileWaiting = harness.Out.ToString();
                state = (await harness.RunSeparatelyAsync("status", harness.PrintedRunId().ToString())).Out;
            });

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        Assert.Contains("State: AWAITING_HUMAN", state);

        // the notifier is called after the console notice, once, and the console had the whole notice by then
        var call = Assert.Single(harness.Notifier.Calls);
        Assert.Contains("keeps monitoring this run", call.Console);
        Assert.Contains("NATIVE INTERACTION REQUIRED", whileWaiting);

        // its text: the short run id, the request and the HC_SESSION / hc attach pair
        var runId = harness.PrintedRunId().ToString();
        Assert.Equal("AgenticWorkflow: your agent is waiting for you", call.Notice.Title);
        Assert.Equal($"Run {runId[..8]}: permission: {Request}\n$env:HC_SESSION = \"scripted-core\"; hc attach scripted-1", call.Notice.Body);
    }

    // ---- aw status of a live native wait ----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_of_a_live_native_wait_shows_the_guidance_and_the_attach_block_and_never_a_command_that_answers()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        string status = string.Empty;
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            async () =>
            {
                var result = await harness.RunSeparatelyAsync("status", harness.PrintedRunId().ToString());
                Assert.Equal(0, result.Exit);
                status = result.Out;
            });

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        Assert.Contains("State: AWAITING_HUMAN", status);
        Assert.Contains("Native agent interaction is waiting. Answer it in the live Herdr session.", status);
        Assert.Contains(PowerShellLine, status);
        Assert.Contains(GitBashLine, status);
        Assert.Contains($"Request: permission: {Request}", status);
        Assert.Matches(@"Waiting since: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC", status);
        Assert.DoesNotContain("see that run's output", status);

        // owned by the agent's native interface: no workflow action exists, and nothing here is a gate
        AssertNoAnswerCommand(status);
        Assert.DoesNotContain("CLARIFY", status);
        Assert.DoesNotContain("Pending gate", status);
    }

    [Fact]
    public async Task Status_of_a_live_wait_whose_runtime_gave_no_attach_target_says_so_instead_of_inventing_commands()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        string status = string.Empty;
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request, null),
            async () => status = (await harness.RunSeparatelyAsync("status", harness.PrintedRunId().ToString())).Out);

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        Assert.Contains("Native agent interaction is waiting.", status);
        Assert.Contains("No attach command could be shown safely", status);
        Assert.DoesNotContain("hc attach", status);
        AssertNoAnswerCommand(status);
    }

    [Fact]
    public async Task The_annotation_is_a_small_operational_file_that_exists_only_while_the_wait_is_open()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        NativeInteractionRecord? during = null;
        string? duringText = null;
        bool? existsAfterResume = null;
        string? stateAfterResume = null;
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            () =>
            {
                var runId = harness.PrintedRunId();
                during = StoreOf(harness).ReadNativeInteraction(runId).Record;
                duringText = File.ReadAllText(Path.Combine(StoreOf(harness).RunDirectory(runId), RunStore.NativeInteractionFileName));
                return Task.CompletedTask;
            },
            async () =>
            {
                var runId = harness.PrintedRunId();
                existsAfterResume = File.Exists(Path.Combine(StoreOf(harness).RunDirectory(runId), RunStore.NativeInteractionFileName));
                stateAfterResume = (await harness.RunSeparatelyAsync("status", runId.ToString())).Out;
            });

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));
        var runIdOfRun = harness.PrintedRunId();

        Assert.NotNull(during);
        Assert.Equal(runIdOfRun.ToString(), during.RunId);
        Assert.Equal("permission", during.Kind);
        Assert.Equal(Request, during.Description);
        Assert.Equal("scripted-core", during.HostSession);
        Assert.Equal("scripted-1", during.Terminal);
        Assert.Contains("\"nativeInteractionVersion\": 1", duringText);

        // resumed: the workflow is RUNNING again and the pointer is gone
        Assert.False(existsAfterResume);
        Assert.Contains("State: RUNNING", stateAfterResume);
        Assert.DoesNotContain("Native agent interaction", stateAfterResume);
        Assert.DoesNotContain("hc attach", stateAfterResume);

        // and it is not left behind by the finished run
        Assert.False(File.Exists(Path.Combine(StoreOf(harness).RunDirectory(runIdOfRun), RunStore.NativeInteractionFileName)));
        Assert.False(File.Exists(Path.Combine(StoreOf(harness).RunDirectory(runIdOfRun), RunStore.NativeInteractionFileName + ".tmp")));
    }

    [Fact]
    public async Task Resuming_continues_the_same_turn_of_the_same_conversation_and_records_no_decision_gate_or_revision()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request));

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        // one prompt to one session, never an answer
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Single(harness.Factory.Runtime.StartRequests);
        Assert.Single(harness.Factory.Runtime.Stopped);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Accept, Assert.Single(task.Gates).Type);   // only the ACCEPT gate: no CLARIFY gate was invented
        Assert.Equal(1, task.CurrentSemanticRevision.Ordinal);            // no semantic revision
        Assert.Empty(task.PolicyDecisions);                               // AW decided nothing about the permission

        var states = task.StateReason;
        Assert.DoesNotContain("allow", states, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_of_a_live_run_with_no_usable_annotation_keeps_the_older_wording()
    {
        var (harness, runId) = await DeadAwaitingHumanRunAsync();
        using var _ = harness;
        var store = StoreOf(harness);
        var path = Path.Combine(store.RunDirectory(runId), RunStore.NativeInteractionFileName);
        var results = new List<string>();

        using (store.AcquireLiveLock(runId))
        {
            // no file (a run from before the annotation existed)
            results.Add((await harness.RunSeparatelyAsync("status", runId.ToString())).Out);

            // unreadable, a different version, another run's
            File.WriteAllText(path, "{ not json");
            results.Add((await harness.RunSeparatelyAsync("status", runId.ToString())).Out);

            store.WriteNativeInteraction(runId, Annotation(runId) with { Version = 99 });
            results.Add((await harness.RunSeparatelyAsync("status", runId.ToString())).Out);

            store.WriteNativeInteraction(runId, Annotation(runId) with { RunId = Guid.NewGuid().ToString() });
            results.Add((await harness.RunSeparatelyAsync("status", runId.ToString())).Out);
        }

        Assert.All(results, status =>
        {
            Assert.Contains("Agent session: live in another process, waiting for a person in its own interface (see that run's output for where to answer).", status);
            Assert.DoesNotContain("hc attach", status);
            Assert.DoesNotContain("Native agent interaction is waiting", status);
        });
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("$(evil)")]
    [InlineData("quote\"x")]
    [InlineData("back`tick")]
    [InlineData("-leading-dash")]
    [InlineData("new\nline")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Identifiers_that_are_not_plain_tokens_in_the_annotation_produce_no_attach_commands(string? bad)
    {
        var (harness, runId) = await DeadAwaitingHumanRunAsync();
        using var _ = harness;
        var store = StoreOf(harness);

        foreach (var record in new[] { Annotation(runId, host: bad), Annotation(runId, terminal: bad), Annotation(runId, host: bad, terminal: bad) })
        {
            using (store.AcquireLiveLock(runId))
            {
                store.WriteNativeInteraction(runId, record);

                var status = (await harness.RunSeparatelyAsync("status", runId.ToString())).Out;

                Assert.Contains("Native agent interaction is waiting.", status);
                Assert.Contains("No attach command could be shown safely", status);
                Assert.DoesNotContain("hc attach", status);
                Assert.DoesNotContain("HC_SESSION", status);
                Assert.DoesNotContain("PowerShell:", status);
                AssertNoAnswerCommand(status);
            }
        }
    }

    [Fact]
    public async Task An_over_long_identifier_and_a_tampered_description_are_not_echoed_unsafely()
    {
        var (harness, runId) = await DeadAwaitingHumanRunAsync();
        using var _ = harness;
        var store = StoreOf(harness);

        using (store.AcquireLiveLock(runId))
        {
            store.WriteNativeInteraction(runId, Annotation(runId, host: new string('a', 129)) with { Description = "approve \u001b[2J" + new string('y', 2000) });

            var status = (await harness.RunSeparatelyAsync("status", runId.ToString())).Out;

            Assert.DoesNotContain("hc attach", status);
            Assert.DoesNotContain('\u001b', status);
            Assert.DoesNotContain(new string('y', 301), status);
        }
    }

    [Fact]
    public async Task Status_of_a_wait_whose_process_is_gone_keeps_todays_text_and_never_points_at_a_dead_terminal_even_with_a_stale_annotation()
    {
        var (harness, runId) = await DeadAwaitingHumanRunAsync();
        using var _ = harness;
        StoreOf(harness).WriteNativeInteraction(runId, Annotation(runId));   // what a killed process leaves behind

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: AWAITING_HUMAN", status);
        Assert.Contains("Agent session: not live (the run's process ended without recording what happened); whatever the agent was waiting for can no longer be answered.", status);
        Assert.Contains($"Action: 'aw abort {runId}' cancels the run, or run the task again.", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("Native agent interaction", status);
        Assert.DoesNotContain("PowerShell:", status);
    }

    [Fact]
    public async Task A_blocked_native_interaction_renders_as_it_always_did_with_no_attach_commands()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait(Request));
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        // even a leftover annotation and a held lock must not change what a BLOCKED run says
        StoreOf(harness).WriteNativeInteraction(runId, Annotation(runId));
        using (StoreOf(harness).AcquireLiveLock(runId))
            Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: BLOCKED", status);
        Assert.Contains("Agent session: ended; the native interaction it was waiting for can no longer be answered.", status);
        Assert.Contains($"Action: 'aw abort {runId}' cancels this blocked run, or run the task again (--interactive, and answer the agent's prompt in its Herdr session).", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("Native agent interaction is waiting", status);
        Assert.DoesNotContain("PowerShell:", status);
    }

    // ---- the workflow's own gates are unchanged ------------------------------------------------------------------------

    [Fact]
    public async Task An_accept_gate_status_is_unchanged_names_its_commands_and_never_an_attach_command()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            WriteFeature(workspace);
            return Task.FromResult(AgentTurnOutcome.Done);
        };
        Assert.Equal(17, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        // a held lock and a leftover annotation are irrelevant to a gate
        StoreOf(harness).WriteNativeInteraction(runId, Annotation(runId));
        using (StoreOf(harness).AcquireLiveLock(runId))
            Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("State: AWAITING_HUMAN", status);
        Assert.Contains("Pending gate: ACCEPT (owned by AgenticWorkflow; durable, needs no agent session)", status);
        Assert.Contains($"aw accept {runId} --gate", status);
        Assert.Contains($"aw request-changes {runId} --gate", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("Native agent interaction", status);
        Assert.DoesNotContain("Owner: the agent's native interface", status);
    }

    [Fact]
    public async Task A_clarify_gate_status_is_unchanged_names_aw_answer_and_never_an_attach_command()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Signals.ByTurn[1] = new AgentClarificationSignal("Which symbol?", "Not named anywhere.");
        Assert.Equal(19, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        var questionId = harness.PrintedQuestionId();
        harness.ResetOutput();

        StoreOf(harness).WriteNativeInteraction(runId, Annotation(runId));
        using (StoreOf(harness).AcquireLiveLock(runId))
            Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));

        var status = harness.Out.ToString();
        Assert.Contains("Pending gate: CLARIFY (a question the agent raised; owned by AgenticWorkflow; durable, needs no agent session)", status);
        Assert.Contains($"aw answer {runId} --question {questionId}", status);
        Assert.Contains($"aw abort {runId}", status);
        Assert.DoesNotContain("hc attach", status);
        Assert.DoesNotContain("aw accept", status);
        Assert.DoesNotContain("Native agent interaction", status);
    }

    // ---- aw runs -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_lists_a_native_wait_as_native_from_the_replayed_state_only_and_starts_no_runtime()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        string listed = string.Empty;
        var createdWhileListing = -1;
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            async () =>
            {
                listed = (await harness.RunSeparatelyAsync("runs")).Out;
                createdWhileListing = harness.Factory.Created;
            });

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));
        var runId = harness.PrintedRunId();

        Assert.Contains($"{runId}  AWAITING_HUMAN (native)", listed);
        Assert.Equal(1, createdWhileListing);   // listing started nothing

        // a gate keeps its own annotation, not "native"
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("runs"));
        Assert.Contains($"{runId}  AWAITING_HUMAN (awaits your decision)", harness.Out.ToString());
        Assert.DoesNotContain("(native)", harness.Out.ToString());
    }

    [Fact]
    public async Task Runs_derives_native_from_the_stream_alone_not_from_the_annotation_or_a_live_process()
    {
        var (harness, runId) = await DeadAwaitingHumanRunAsync();
        using var _ = harness;
        var path = Path.Combine(StoreOf(harness).RunDirectory(runId), RunStore.NativeInteractionFileName);
        File.WriteAllText(path, "{ garbage");
        var before = File.ReadAllText(path);
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        Assert.Contains($"{runId}  AWAITING_HUMAN (native)", harness.Out.ToString());
        Assert.Equal(before, File.ReadAllText(path));   // not even touched
        Assert.Equal(1, harness.Factory.Created);

        // without the file the answer is the same
        File.Delete(path);
        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("runs"));
        Assert.Contains($"{runId}  AWAITING_HUMAN (native)", harness.Out.ToString());
    }

    [Fact]
    public async Task Runs_keeps_the_question_annotation_for_a_clarify_gate_and_marks_a_blocked_native_run_as_blocked()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Signals.ByTurn[1] = new AgentClarificationSignal("Which symbol?", "Not named anywhere.");
        Assert.Equal(19, await harness.RunAsync("run", "task"));
        var asked = harness.PrintedRunId();

        harness.Factory.Signals = null;
        harness.Factory.Runtime.Turn = (_, _, _) => Task.FromResult(ScriptedRuntime.Wait(Request));
        harness.ResetOutput();
        Assert.Equal(15, await harness.RunAsync("run", "task"));
        var blocked = harness.PrintedRunId();
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("runs"));

        var runs = harness.Out.ToString();
        Assert.Contains($"{asked}  AWAITING_HUMAN (question awaits your answer)", runs);
        Assert.Contains($"{blocked}  BLOCKED", runs);
        Assert.DoesNotContain("(native)", runs);
    }

    // ---- nobody attending ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_interactive_run_is_unchanged_it_stops_with_exit_15_and_neither_notifies_nor_shows_attach_text()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.IsTerminal = true;   // --non-interactive overrides it
        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request));

        var exit = await harness.RunAsync("run", "--non-interactive", "task");

        var output = harness.Out.ToString();
        Assert.Equal(15, exit);
        Assert.Contains("nobody is attending this run, so it will stop", output);
        Assert.Equal(1, Occurrences(output, "AWAITING HUMAN"));
        Assert.Contains("BLOCKED - NATIVE INTERACTION UNAVAILABLE", output);
        Assert.DoesNotContain("NATIVE INTERACTION REQUIRED", output);
        Assert.DoesNotContain("hc attach", output);
        Assert.DoesNotContain("HC_SESSION", output);
        Assert.Empty(harness.Notifier.Calls);

        var runId = harness.PrintedRunId();
        Assert.False(File.Exists(Path.Combine(StoreOf(harness).RunDirectory(runId), RunStore.NativeInteractionFileName)));

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("status", runId.ToString()));
        Assert.DoesNotContain("hc attach", harness.Out.ToString());
        Assert.Empty(harness.Notifier.Calls);
    }

    // ---- nothing here can change a run -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("throws")]
    [InlineData("reports")]
    public async Task A_failing_notifier_changes_neither_the_state_nor_the_exit_code_and_costs_at_most_one_console_line(string how)
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        if (how == "throws")
            harness.Notifier.Failure = new InvalidOperationException("boom");
        else
            harness.Notifier.Problem = "powershell.exe exited with code 5";

        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request));

        var exit = await harness.RunAsync("run", "--interactive", "task");

        var output = harness.Out.ToString();
        Assert.Equal(17, exit);
        Assert.Equal(1, Occurrences(output, "desktop notification unavailable ("));
        Assert.Equal(1, Occurrences(output, "NATIVE INTERACTION REQUIRED"));

        if (how == "reports")
            Assert.Contains("desktop notification unavailable (powershell.exe exited with code 5)", output);

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal(WorkflowState.AwaitingHuman, task.WorkflowState);
        Assert.Equal(GateType.Accept, Assert.Single(task.Gates).Type);
        Assert.Empty(task.PolicyDecisions);
        Assert.Single(harness.Factory.Runtime.Prompts);
        Assert.Contains("VERIFIED - AWAITING ACCEPT", output);
        Assert.Equal(string.Empty, harness.Error.ToString());
    }

    [Fact]
    public async Task A_healthy_notifier_adds_no_console_line_of_its_own()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(ScriptedRuntime.Wait(Request));

        Assert.Equal(17, await harness.RunAsync("run", "--interactive", "task"));

        Assert.Single(harness.Notifier.Calls);
        Assert.DoesNotContain("desktop notification", harness.Out.ToString());
    }

    [Fact]
    public async Task An_annotation_that_cannot_be_written_does_not_fail_the_run()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            beforeWait: () =>
            {
                // a directory where the file belongs: writing and removing the annotation both fail
                Directory.CreateDirectory(Path.Combine(StoreOf(harness).RunDirectory(harness.PrintedRunId()), RunStore.NativeInteractionFileName));
                return Task.CompletedTask;
            });

        var exit = await harness.RunAsync("run", "--interactive", "task");

        Assert.Equal(17, exit);
        Assert.Contains("NATIVE INTERACTION REQUIRED", harness.Out.ToString());
        Assert.Contains("VERIFIED - AWAITING ACCEPT", harness.Out.ToString());
        Assert.Equal(WorkflowState.AwaitingHuman, (await LoadAsync(harness, harness.PrintedRunId())).WorkflowState);
    }

    [Fact]
    public async Task A_run_that_ends_during_the_wait_leaves_no_annotation_behind()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        harness.Factory.Runtime.Turn = Script(
            ScriptedRuntime.Wait(Request),
            () => throw new AgentRuntimeException("the agent session vanished"));

        var exit = await harness.RunAsync("run", "--interactive", "task");

        var runId = harness.PrintedRunId();
        Assert.Equal(14, exit);   // today's runtime failure, unchanged: RUNNING, then BLOCKED
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
        Assert.False(File.Exists(Path.Combine(StoreOf(harness).RunDirectory(runId), RunStore.NativeInteractionFileName)));
        Assert.Single(harness.Notifier.Calls);   // told once about the wait; nothing more about its disappearance
    }

    [Fact]
    public async Task The_cli_has_no_command_that_answers_a_native_prompt()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(0, await harness.RunAsync("--help", "run"));

        var help = harness.Out.ToString();
        Assert.Contains("NATIVE INTERACTION REQUIRED", help);
        Assert.Contains("AGENTICWORKFLOW_NOTIFY=toast", help);
        Assert.DoesNotContain("aw allow", help);
        Assert.DoesNotContain("aw deny", help);
        // "aw approve" exists since M5B, but only for the workflow's own WHAT gate: it never answers an agent's native prompt.
        Assert.DoesNotContain("aw approve-prompt", help);
        Assert.DoesNotContain("aw answer-prompt", help);
    }
}
