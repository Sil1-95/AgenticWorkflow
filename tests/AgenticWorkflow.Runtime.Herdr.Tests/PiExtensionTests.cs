using System.Diagnostics;
using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;
using AgenticWorkflow.Runtime.Herdr.Pi;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>A test that runs the real Pi extension under Node against a stand-in for Pi's extension API. Skipped where Node or the built hook executable is not available.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (PiExtensionTests.Node() is null)
            Skip = "Needs Node.js on PATH.";
        else if (PiExtensionTests.Hook() is null)
            Skip = "Needs the built hook executable (build the solution first).";
    }
}

/// <summary>
/// The extension AgenticWorkflow gives a Pi session, run for real under Node with a stand-in for Pi's extension API and the real hook executable. It proves what the extension
/// does with the session id Pi's own session manager reports, how often it asks for a record, when it stops the turn, what it refuses, and what it reports about the launch. Pi itself
/// is proven live.
/// </summary>
public sealed class PiExtensionTests : IDisposable
{
    private const string Model = "llama/Qwen3.8-27B";
    private const string HowTool = "mcp__aw__propose_how";

    private static readonly string Driver = """
        import { pathToFileURL } from "node:url";
        const [dir, scenarioJson] = [process.argv[2], process.argv[3]];
        const sc = JSON.parse(scenarioJson);
        const handlers = {};
        const servers = [];
        let aborts = 0;
        const pi = {
          registerMcpServer: (name, config) => servers.push({ name, config }),
          on: (event, handler) => { (handlers[event] ??= []).push(handler); },
          getThinkingLevel: () => sc.thinking,
        };
        const context = (session) => ({ sessionManager: { getSessionId: () => session }, model: sc.model, cwd: dir, abort: () => { aborts++; } });
        const module = await import(pathToFileURL(dir + "/aw-pi-extension.mjs").href);
        module.default(pi);
        await handlers.session_start[0]({}, context(sc.session));
        const blocked = [];
        for (const command of sc.commands) {
          const result = await handlers.tool_call[0]({ toolName: "bash", input: { command } });
          blocked.push(!!(result && result.block));
        }
        const readResult = await handlers.tool_call[0]({ toolName: "read", input: { command: "git commit" } });
        for (const r of sc.results) {
          await handlers.tool_result[0]({ toolName: r.tool, isError: !!r.isError, toolCallId: r.id, input: r.input }, context(r.session ?? sc.session));
        }
        console.log(JSON.stringify({ servers, blocked, readBlocked: !!(readResult && readResult.block), aborts, handlers: Object.keys(handlers).sort() }));
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-pi-ext-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PiExtensionTests() => Directory.CreateDirectory(Path.Combine(_root, "workspace"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string? Node()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "node.exe" } : ["node"])
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name);

                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry cannot hold node
                }
            }
        }

        return null;
    }

    internal static string? Hook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(directory.FullName, "src", "AgenticWorkflow.ClaudeEvidenceHook", "bin", configuration, "net10.0", "AgenticWorkflow.ClaudeEvidenceHook.exe");

            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static Dictionary<string, object?> How(string strategy)
        => new()
        {
            ["strategy"] = strategy,
            ["components"] = "One new file.",
            ["data_and_control_flow"] = "A pure function.",
            ["compatibility"] = "Nothing existing changes.",
            ["risks_and_tradeoffs"] = "A static method over an instance: simpler.",
            ["verification_approach"] = "One test.",
            ["rationale"] = "A new public API shape is a person's call.",
        };

    private ClaudeEvidenceChannel Channel()
    {
        var options = new ClaudeEvidenceOptions { HookExecutable = Hook()!, EvidenceRoot = Path.Combine(_root, "evidence"), OfferHow = true, OfferReviewRequest = true };
        var harness = new PiChannelHarness(1, Model, "low", PiLaunch.DeniedCommandsOf(ClaudeCodeToolPolicy.BaselineDeniedTools));

        return ClaudeEvidenceChannel.TryCreate(options with { Harness = harness }, Path.Combine(_root, "workspace"), [], sessionId: null, clarification: false, what: false, how: true, reclassification: false, reviewRequest: true, review: false, out var failure)
               ?? throw new InvalidOperationException(failure);
    }

    private static async Task<JsonDocument> RunAsync(ClaudeEvidenceChannel channel, object scenario)
    {
        var driver = Path.Combine(channel.Directory, "driver.mjs");
        await File.WriteAllTextAsync(driver, Driver);

        var start = new ProcessStartInfo(Node()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = channel.Directory };
        start.ArgumentList.Add(driver);
        start.ArgumentList.Add(channel.Directory);
        start.ArgumentList.Add(JsonSerializer.Serialize(scenario));

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, await error);

        return JsonDocument.Parse(await output);
    }

    private static object Scenario(ClaudeEvidenceChannel channel, string[]? commands = null, object[]? results = null)
        => new
        {
            session = channel.SessionId,
            thinking = "low",
            model = new { provider = "llama", id = "Qwen3.8-27B" },
            commands = commands ?? [],
            results = results ?? [],
        };

    [NodeFact]
    public async Task The_extension_hosts_the_workflow_mcp_server_for_this_session_only_and_listens_to_nothing_else()
    {
        using var channel = Channel();

        using var result = await RunAsync(channel, Scenario(channel));
        var server = Assert.Single(result.RootElement.GetProperty("servers").EnumerateArray());
        var config = server.GetProperty("config");

        Assert.Equal("aw", server.GetProperty("name").GetString());
        Assert.Equal(Path.GetFullPath(Hook()!), config.GetProperty("command").GetString());
        Assert.Equal(["--mcp", channel.Directory], config.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Equal(["session_start", "tool_call", "tool_result"], result.RootElement.GetProperty("handlers").EnumerateArray().Select(h => h.GetString()!).ToArray());
    }

    [NodeFact]
    public async Task The_extension_reports_the_session_model_and_thinking_level_pi_actually_started_with_and_the_controller_checks_them()
    {
        using var channel = Channel();

        using var _ = await RunAsync(channel, Scenario(channel));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(channel.Directory, PiChannelHarness.LaunchReportFile)));

        Assert.Equal(channel.SessionId, report.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(Model, report.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", report.RootElement.GetProperty("thinking").GetString());
        Assert.Null(await channel.VerifyStartedAsync(CancellationToken.None));
    }

    [NodeFact]
    public async Task A_how_call_is_recorded_once_against_pis_own_session_stops_the_turn_once_and_never_twice_for_the_same_call()
    {
        using var channel = Channel();

        using var result = await RunAsync(channel, Scenario(channel, results:
        [
            new { tool = HowTool, id = "call-1", input = How("First.") },
            new { tool = HowTool, id = "call-1", input = How("First.") },   // the same call observed again
        ]));

        Assert.Equal("First.", Assert.Single(channel.CollectHowProposals(0, null)).Strategy);
        Assert.Equal(1, result.RootElement.GetProperty("aborts").GetInt32());
        Assert.Single(Directory.EnumerateFiles(channel.Directory, "e-*.json"));
    }

    [NodeFact]
    public async Task Two_distinct_calls_are_two_records_and_a_failed_or_foreign_tool_is_never_recorded()
    {
        using var channel = Channel();

        using var result = await RunAsync(channel, Scenario(channel, results:
        [
            new { tool = HowTool, id = "a", input = How("A.") },
            new { tool = HowTool, id = "b", input = How("B."), isError = true },        // the server refused it: not a signal
            new { tool = "bash", id = "c", input = new { command = "echo mcp__aw__propose_how" } },   // not a workflow tool
            new { tool = "mcp__other__propose_how", id = "d", input = How("D.") },     // another server's tool of the same name
        ]));

        Assert.Equal(["A."], channel.CollectHowProposals(0, null).Select(s => s.Strategy).ToArray());
        Assert.Equal(1, result.RootElement.GetProperty("aborts").GetInt32());
    }

    [NodeFact]
    public async Task A_call_attributed_to_another_pi_session_is_refused_by_the_channel_and_does_not_stop_the_turn()
    {
        using var channel = Channel();

        using var result = await RunAsync(channel, Scenario(channel, results:
        [
            new { tool = HowTool, id = "x", input = How("Foreign."), session = Guid.NewGuid().ToString() },
        ]));

        Assert.Empty(channel.CollectHowProposals(0, null));
        Assert.Equal(0, result.RootElement.GetProperty("aborts").GetInt32());
        Assert.Contains("foreign_session", string.Join('\n', Directory.EnumerateFiles(channel.Directory, "e-*.json").Select(File.ReadAllText)));
    }

    [NodeFact]
    public async Task A_model_supplied_session_or_run_id_in_the_tool_input_does_not_change_who_the_call_is_attributed_to()
    {
        using var channel = Channel();
        var forged = How("Forged.");
        forged["session_id"] = channel.SessionId;
        forged["run_id"] = "another-run";

        using var result = await RunAsync(channel, Scenario(channel, results:
        [
            new { tool = HowTool, id = "x", input = (object)forged, session = Guid.NewGuid().ToString() },
        ]));

        Assert.Empty(channel.CollectHowProposals(0, null));
        Assert.Equal(0, result.RootElement.GetProperty("aborts").GetInt32());
    }

    [NodeFact]
    public async Task A_review_request_is_recorded_without_stopping_the_turn()
    {
        using var channel = Channel();

        using var result = await RunAsync(channel, Scenario(channel, results:
        [
            new { tool = "mcp__aw__request_review", id = "r", input = new { reason = "A public API changed shape." } },
        ]));

        Assert.Equal("A public API changed shape.", Assert.Single(channel.CollectReviewRequests(0, null)).Reason);
        Assert.Equal(0, result.RootElement.GetProperty("aborts").GetInt32());
    }

    [NodeFact]
    public async Task History_branch_remote_and_network_commands_are_refused_before_they_run_and_ordinary_work_is_not()
    {
        using var channel = Channel();
        string[] refused =
        [
            "git commit -m x",
            "cd src && git push",
            "git -C . add -A",
            "FOO=1 git reset --hard",
            "echo ok; git checkout main",
            "dotnet nuget push x.nupkg",
            "curl http://example.invalid",
            "echo hi | wget http://example.invalid",
            "(git stash)",
            "git --no-pager -c user.name=x commit",
        ];
        string[] allowed = ["git status", "git diff --stat", "git log --oneline", "dotnet test", "dotnet build -c Release", "ls -la", "echo 'git commit is only text here'"];

        using var result = await RunAsync(channel, Scenario(channel, commands: [.. refused, .. allowed]));
        var blocked = result.RootElement.GetProperty("blocked").EnumerateArray().Select(b => b.GetBoolean()).ToArray();

        Assert.Equal(refused.Select(_ => true), blocked.Take(refused.Length));
        Assert.Equal(allowed.Select(_ => false), blocked.Skip(refused.Length));
        Assert.False(result.RootElement.GetProperty("readBlocked").GetBoolean());   // only the shell tool is looked at
    }
}
