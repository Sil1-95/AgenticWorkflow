using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticWorkflow.Application.Evidence;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;
using Xunit.Abstractions;

namespace AgenticWorkflow.Runtime.Herdr.Tests.Live;

/// <summary>
/// The hook contract against a REAL Claude Code session: the session-only settings make Claude start the AgenticWorkflow hook for
/// each tool event, the records name the right session, tool use ids, working directory and duration, a failing command is a
/// failure and a lone command that is a configured check gets an implementation state captured. Also measures what the hooks cost.
/// <para>
/// The prompt only asks for read-only tool calls (<c>dotnet --version</c>, a failing <c>dotnet build</c>, a <c>git grep</c> that finds nothing, a file read), in the
/// already-trusted repository folder, in an isolated Herdr core started by this test. Nobody answers a dialog. The user's global
/// Claude settings and the project's <c>.claude</c> settings are hashed before and after: they must be byte-identical.
/// Run with <c>AGENTICWORKFLOW_LIVE_CLAUDE_EVIDENCE=1 dotnet test --filter Category=LiveClaudeEvidence</c>.
/// </para>
/// </summary>
[Trait("Category", "LiveClaudeEvidence")]
public sealed class ClaudeEvidenceHookLiveTests(ITestOutputHelper output)
{
    private static readonly ProfileId Profile = new("claude-code-local");

    [LiveFact(LiveFactAttribute.ClaudeEvidenceVariable)]
    public async Task Session_scoped_hooks_record_real_tool_events_without_touching_any_settings_and_at_a_measured_cost()
    {
        var repository = RepositoryRoot();
        var hook = HookExecutable();
        var protectedFiles = SettingsFiles(repository);
        var before = Hashes(protectedFiles);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var token = "AWNOTHERE" + suffix.ToUpperInvariant();
        var versionCheck = new VerificationCheckSpec(new VerificationCheckId("dotnet_version"), "dotnet is available", "dotnet", ["--version"], TimeSpan.FromSeconds(30));

        var options = new ClaudeEvidenceOptions { HookExecutable = hook };

        await using var runtime = new HerdrAgentRuntime(new HerdrAgentRuntimeOptions
        {
            Session = $"agenticworkflow-m2d-{suffix}",
            Profiles = [ClaudeCodeLaunch.Profile(Profile, Policy(), evidence: options)],
            StartupTimeout = TimeSpan.FromSeconds(120),
        });

        var id = await runtime.StartSessionAsync(new AgentSessionRequest(Profile, repository, "m2d hook contract") { ObservableChecks = [versionCheck] });
        var channel = runtime.EvidenceChannelOf(id) ?? throw new InvalidOperationException("the runtime did not create an evidence channel");
        var channelDirectory = channel.Directory;

        output.WriteLine($"evidence channel: {channelDirectory}");
        output.WriteLine($"claude session id: {channel.SessionId}");
        Assert.DoesNotContain(repository, channelDirectory, StringComparison.OrdinalIgnoreCase);

        await runtime.BeginTurnAsync(id);
        var turn = await runtime.SendTurnAsync(
            id,
            "Make exactly these four tool calls, strictly one at a time, each as its own single call, in this order, then reply with the single word done. "
            + "(1) Bash: dotnet --version   "
            + "(2) Bash: dotnet build tests/DoesNotExist/Nope.csproj   (it is expected to fail; that is fine)   "
            + $"(3) Bash: git grep -q -F {token}   (it is expected to find nothing and exit with status 1; that is fine)   "
            + "(4) Read the file AgenticWorkflow.sln.   Do not make any other tool call and do not edit anything.");
        var outcome = await runtime.WaitForTurnAsync(id, turn, TimeSpan.FromMinutes(5));
        Assert.True(outcome.Status == AgentTurnStatus.Done, $"turn ended {outcome.Status}: {outcome.Reason}");

        var evidence = (await runtime.CollectTurnAsync(id))!;

        foreach (var problem in evidence.Problems)
            output.WriteLine($"problem: {problem}");

        foreach (var a in evidence.Activities)
        {
            output.WriteLine(
                $"activity batch={a.Batch}/{a.BatchSize} id={a.ToolUseId} kind={a.Kind} ok={a.Succeeded} cwd={a.WorkingDirectory} ms={a.DurationMilliseconds} state={(a.StateAfterBatch is null ? "-" : "captured")} cmd={a.Command}");
        }

        var handlerTimes = channel.HandlerMilliseconds();
        output.WriteLine($"hook records: {handlerTimes.Count}; in-handler ms: [{string.Join(", ", handlerTimes)}]");

        await runtime.StopSessionAsync(id);

        // ---- the contract ---------------------------------------------------------------------------------------------
        Assert.Empty(evidence.Problems);
        Assert.Equal("claude-code", evidence.Runtime);

        var version = Assert.Single(evidence.Activities, a => a.Command == "dotnet --version");
        Assert.Equal(ObservedActivityKind.Command, version.Kind);
        Assert.True(version.Succeeded, "dotnet exits 0, so it must arrive as a PostToolUse success that the adapter accepts as proof");
        Assert.NotNull(version.StateAfterBatch); // a lone command that is exactly a configured check
        Assert.Equal(1, version.BatchSize);
        Assert.True(CommandForm.SameDirectory(version.WorkingDirectory, repository), $"cwd was {version.WorkingDirectory}");
        Assert.NotNull(version.DurationMilliseconds);

        var failing = Assert.Single(evidence.Activities, a => a.Command == "dotnet build tests/DoesNotExist/Nope.csproj");
        Assert.False(failing.Succeeded, "a non-zero dotnet exit must arrive as PostToolUseFailure");
        Assert.Null(failing.StateAfterBatch); // not a configured check: the repository was not touched for it

        // Discovered live: Claude Code reports `git grep` exiting 1 ("no match") as a PostToolUse success, and the payload carries no
        // exit code. The adapter must therefore NOT treat it as proof of exit 0.
        var grep = Assert.Single(evidence.Activities, a => a.Command == $"git grep -q -F {token}");
        Assert.False(grep.Succeeded, "a PostToolUse for a semantic-exit-code command does not prove exit 0");
        Assert.Null(grep.StateAfterBatch);

        var read = Assert.Single(evidence.Activities, a => a.Kind == ObservedActivityKind.ReadOnly);
        Assert.Equal(1, read.BatchSize);
        Assert.Null(read.StateAfterBatch);

        Assert.All(evidence.Activities, a => Assert.False(string.IsNullOrWhiteSpace(a.ToolUseId)));
        Assert.Equal(evidence.Activities.Count, evidence.Activities.Select(a => a.ToolUseId).Distinct().Count());
        Assert.Equal(evidence.Activities.Select(a => a.Batch).OrderBy(b => b).ToArray(), evidence.Activities.Select(a => a.Batch).ToArray()); // batches are ordered

        // the hooks were session-only: the channel is gone with the session, and no settings file anywhere changed
        Assert.False(Directory.Exists(channelDirectory));
        Assert.Equal(before, Hashes(protectedFiles));
        output.WriteLine("settings files unchanged: " + string.Join("; ", before.Select(p => $"{p.Key} [{p.Value}]")));

        await MeasureHookProcessCostAsync(hook, repository, versionCheck);
    }

    /// <summary>
    /// What one hook invocation costs the agent: the wall time of starting the real executable, parsing a payload and writing the
    /// record, for the cheap case (a tool event) and the costly one (a batch that captures the implementation state of this repository).
    /// </summary>
    private async Task MeasureHookProcessCostAsync(string hook, string repository, VerificationCheckSpec check)
    {
        using var channel = ClaudeEvidenceChannel.TryCreate(new ClaudeEvidenceOptions { HookExecutable = hook }, repository, [check], out var failure)
                            ?? throw new InvalidOperationException(failure);

        var toolEvent = JsonSerializer.Serialize(new
        {
            session_id = channel.SessionId,
            hook_event_name = "PostToolUse",
            cwd = repository,
            tool_name = "Bash",
            tool_use_id = "toolu_measure",
            tool_input = new { command = "dotnet --version" },
            duration_ms = 10,
        });

        var batchEvent = JsonSerializer.Serialize(new
        {
            session_id = channel.SessionId,
            hook_event_name = "PostToolBatch",
            cwd = repository,
            tool_calls = new[] { new { tool_name = "Bash", tool_use_id = "toolu_measure", tool_input = new { command = "dotnet --version" } } },
        });

        var readBatch = JsonSerializer.Serialize(new
        {
            session_id = channel.SessionId,
            hook_event_name = "PostToolBatch",
            cwd = repository,
            tool_calls = new[] { new { tool_name = "Read", tool_use_id = "toolu_measure_read", tool_input = new { file_path = "x" } } },
        });

        foreach (var (name, payload) in new[] { ("PostToolUse (command outcome)", toolEvent), ("PostToolBatch (read, no state)", readBatch), ("PostToolBatch (lone check, state captured)", batchEvent) })
        {
            await RunHookAsync(hook, channel.Directory, payload); // warm-up (disk cache, JIT image)

            var samples = new List<double>();

            for (var i = 0; i < 8; i++)
                samples.Add(await RunHookAsync(hook, channel.Directory, payload));

            samples.Sort();
            output.WriteLine($"hook process wall time, {name}: median {samples[samples.Count / 2]:F0} ms, min {samples[0]:F0} ms, max {samples[^1]:F0} ms (n={samples.Count})");

            Assert.True(samples[samples.Count / 2] < 5000, "a hook invocation must stay far below any tool's own cost");
        }
    }

    private static async Task<double> RunHookAsync(string hook, string channelDirectory, string payload)
    {
        var info = new ProcessStartInfo(hook)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(channelDirectory);

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        clock.Stop();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stdout); // never prints: nothing for Claude to interpret as a decision

        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>The default profile policy plus the one extra read-only command this test asks for, so nothing needs approval.</summary>
    private static ClaudeCodeToolPolicy Policy()
        => new() { AllowedTools = [.. new ClaudeCodeToolPolicy().AllowedTools, "Bash(dotnet --version)"] };

    private static string[] SettingsFiles(string repository)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new[]
            {
                Path.Combine(home, ".claude", "settings.json"),
                Path.Combine(home, ".claude", "settings.local.json"),
                Path.Combine(repository, ".claude", "settings.json"),
                Path.Combine(repository, ".claude", "settings.local.json"),
            }
            .ToArray();
    }

    private static Dictionary<string, string> Hashes(IEnumerable<string> files)
        => files.ToDictionary(
            f => f,
            f => File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))[..16] : "(absent)");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string HookExecutable()
    {
        var root = RepositoryRoot();

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(root, "src", "AgenticWorkflow.ClaudeEvidenceHook", "bin", configuration, "net10.0", "AgenticWorkflow.ClaudeEvidenceHook.exe");

            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("The hook executable has not been built (build the solution first).");
    }
}
