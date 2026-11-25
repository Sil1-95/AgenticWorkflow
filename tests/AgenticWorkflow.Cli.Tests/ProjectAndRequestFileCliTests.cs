using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Lifecycle;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The typed operations a Herdr mod uses instead of touching files itself: a request written to a file (<c>ctl send --request-file</c>), the project inspection and the project initialisation.
/// Each is strict about what it accepts and none of them runs, defaults or invents anything on a person's behalf.
/// </summary>
public sealed class ProjectAndRequestFileCliTests
{
    private const string ValidInit = """
        {
          "toolchain": "dotnet",
          "profile": "claude-code-local",
          "verification": [ { "id": "build", "executable": "dotnet", "arguments": ["build", "App.sln"], "timeoutSeconds": 600 } ],
          "writable": ["src/**"],
          "protected": ["agenticworkflow.json"],
          "acceptance": "human",
          "maxTurns": 5
        }
        """;

    private static string RequestFile(CliHarness harness, string text)
    {
        var path = Path.Combine(harness.Home, $"request-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);

        return path;
    }

    private static JsonDocument Document(string output) => JsonDocument.Parse(output);

    private static string ConfigPath(CliHarness harness) => Path.Combine(harness.Workspace, ProjectResolver.DefaultConfigFileName);

    // ---- ctl send --request-file ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_file_is_read_once_and_deleted_whatever_it_holds()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var file = RequestFile(harness, """{"operation":"exec","command":"rm -rf /"}""");

        var result = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", file);

        Assert.Equal(2, result.Exit);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_request_file_that_does_not_exist_or_is_too_large_or_is_not_json_is_refused_as_malformed()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        var missing = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", Path.Combine(harness.Home, "nope.json"));
        Assert.Equal(2, missing.Exit);
        Assert.Contains("malformed_request", missing.Out);

        var big = RequestFile(harness, new string(' ', 70 * 1024));
        var oversize = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", big);
        Assert.Equal(2, oversize.Exit);
        Assert.Contains("malformed_request", oversize.Out);
        Assert.False(File.Exists(big));

        var garbage = RequestFile(harness, "not json");
        var malformed = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", garbage);
        Assert.Equal(2, malformed.Exit);
        Assert.False(File.Exists(garbage));
    }

    [Fact]
    public async Task A_request_file_must_be_an_absolute_path()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        var result = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", "relative.json");

        Assert.Equal(2, result.Exit);
    }

    [Fact]
    public async Task A_task_text_sent_in_a_request_file_reaches_the_controller_exactly_as_written()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(acceptance: "none"));
        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");

            return Task.FromResult(AgentTurnOutcome.Done);
        };

        await using var controller = await SupervisedController.StartAsync(harness);
        var objective = "Expose AW_SCRIPTED_SYMBOL from src/Feature.txt. \"quoted\" & $(not run) `either`";
        var file = RequestFile(harness, JsonSerializer.Serialize(new { operation = "start_task", objective, workingDirectory = harness.Workspace }));

        var result = await harness.RunSeparatelyAsync("ctl", "send", "--request-file", file);

        Assert.Equal(0, result.Exit);
        Assert.Contains("\"kind\": \"accepted\"", result.Out);
        Assert.False(File.Exists(file));

        var runId = Assert.Single(new RunStore(harness.Home).ListRunIds());
        var done = await controller.WaitForAsync(runId, t => t.State == WorkflowState.Completed, "completion");
        Assert.Equal(objective, done.Intent);
    }

    // ---- aw project init -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Initialising_a_project_writes_exactly_the_configuration_the_strict_parser_reads_back()
    {
        using var harness = new CliHarness(config: null);
        var file = RequestFile(harness, ValidInit);

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", file);

        Assert.Equal(0, result.Exit);
        Assert.False(File.Exists(file));
        using var reply = Document(result.Out);
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ConfigPath(harness), reply.RootElement.GetProperty("configPath").GetString());

        var config = ProjectConfigParser.Parse(File.ReadAllText(ConfigPath(harness)));
        Assert.Equal("dotnet", config.Toolchain);
        Assert.Equal(5, config.MaxTurns);
        Assert.Equal("build", Assert.Single(config.Verification).Id);
        Assert.Equal(["src/**"], config.Writable);

        // Nothing the person did not give was added: no allowed commands, no operating notes.
        Assert.Empty(config.AllowCommands);
        Assert.Null(config.OperatingNotes);
    }

    [Fact]
    public async Task A_custom_project_is_initialised_with_exactly_the_checks_the_person_gave_and_nothing_else()
    {
        using var harness = new CliHarness(config: null);
        var request = ValidInit.Replace("\"toolchain\": \"dotnet\"", "\"toolchain\": \"custom\"").Replace("\"executable\": \"dotnet\", \"arguments\": [\"build\", \"App.sln\"]", "\"executable\": \"make\", \"arguments\": [\"verify\"]");

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, request));

        Assert.Equal(0, result.Exit);
        var config = ProjectConfigParser.Parse(File.ReadAllText(ConfigPath(harness)));
        Assert.Equal("custom", config.Toolchain);
        var check = Assert.Single(config.Verification);
        Assert.Equal("make", check.Executable);
        Assert.Equal(["verify"], check.Arguments);
        Assert.Empty(config.AllowCommands);
        Assert.Null(config.OperatingNotes);
    }

    [Fact]
    public async Task An_unknown_toolchain_is_refused_when_initialising_and_names_the_ones_that_exist()
    {
        using var harness = new CliHarness(config: null);

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, ValidInit.Replace("\"dotnet\",", "\"unity\",")));

        Assert.Equal(2, result.Exit);
        using var reply = Document(result.Out);
        var problems = string.Join("\n", reply.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("'unity' is not supported", problems);
        Assert.Contains("'custom'", problems);
        Assert.False(File.Exists(ConfigPath(harness)));
    }

    [Fact]
    public async Task Initialising_never_replaces_a_configuration_that_exists()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());
        var before = File.ReadAllText(ConfigPath(harness));

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, ValidInit));

        Assert.Equal(2, result.Exit);
        Assert.Contains("already exists", result.Out);
        Assert.Equal(before, File.ReadAllText(ConfigPath(harness)));
    }

    [Theory]
    [InlineData("""{ "toolchain": "dotnet", "profile": "claude-code-local", "run": "rm -rf /" }""", "run")]
    [InlineData("""{ "toolchain": "dotnet", "profile": "claude-code-local", "env": { "A": "b" } }""", "env")]
    [InlineData("""{ "toolchain": "dotnet", "profile": "claude-code-local", "verification": [ { "id": "x", "executable": "dotnet", "extraArgs": "--x" } ] }""", "extraArgs")]
    [InlineData("""{ "toolchain": "dotnet", "profile": "claude-code-local", "profiles": [ { "id": "p", "harness": "pi", "args": ["--yolo"] } ] }""", "args")]
    public async Task Initialising_refuses_any_member_the_configuration_format_does_not_have(string request, string member)
    {
        using var harness = new CliHarness(config: null);

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, request));

        Assert.Equal(2, result.Exit);
        Assert.Contains(member, result.Out);
        Assert.False(File.Exists(ConfigPath(harness)));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("bash")]
    public async Task A_verification_check_cannot_be_a_command_interpreter(string executable)
    {
        using var harness = new CliHarness(config: null);
        var request = JsonSerializer.Serialize(new { toolchain = "dotnet", profile = "claude-code-local", verification = new[] { new { id = "x", executable, arguments = new[] { "-c", "anything" } } } });

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, request));

        Assert.Equal(2, result.Exit);
        Assert.Contains("starts other programs from text", result.Out);
        Assert.False(File.Exists(ConfigPath(harness)));
    }

    [Fact]
    public async Task Nothing_is_chosen_for_the_person_a_missing_toolchain_or_profile_is_refused()
    {
        using var harness = new CliHarness(config: null);

        var result = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, "{}"));

        Assert.Equal(2, result.Exit);
        Assert.Contains("Choose a toolchain", result.Out);
        Assert.Contains("Choose the profile", result.Out);
    }

    [Fact]
    public async Task Duplicate_profile_ids_and_values_the_parser_refuses_are_reported_without_writing_anything()
    {
        using var harness = new CliHarness(config: null);
        const string duplicate = """{ "toolchain": "dotnet", "profile": "a", "profiles": [ { "id": "a", "harness": "claude-code" }, { "id": "a", "harness": "claude-code" } ] }""";
        const string unknownHarness = """{ "toolchain": "dotnet", "profile": "a", "profiles": [ { "id": "a", "harness": "not-a-harness" } ] }""";

        var first = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, duplicate));
        var second = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", RequestFile(harness, unknownHarness));

        Assert.Equal(2, first.Exit);
        Assert.Contains("used more than once", first.Out);
        Assert.Equal(2, second.Exit);
        Assert.False(File.Exists(ConfigPath(harness)));
    }

    [Fact]
    public async Task Initialising_needs_an_existing_workspace_and_an_absolute_request_file()
    {
        using var harness = new CliHarness(config: null);

        var missing = await harness.RunSeparatelyAsync("project", "init", "--workspace", Path.Combine(harness.Home, "nowhere"), "--request-file", RequestFile(harness, ValidInit));
        Assert.Equal(2, missing.Exit);
        Assert.Contains("does not exist", missing.Out);

        var relative = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace, "--request-file", "request.json");
        Assert.Equal(2, relative.Exit);

        var noFile = await harness.RunSeparatelyAsync("project", "init", "--workspace", harness.Workspace);
        Assert.Equal(2, noFile.Exit);
    }

    // ---- aw project inspect --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inspecting_a_workspace_without_a_configuration_reports_it_missing_and_only_suggests_what_its_files_say()
    {
        using var harness = new CliHarness(config: null);
        File.WriteAllText(Path.Combine(harness.Workspace, "App.sln"), string.Empty);

        var result = await harness.RunSeparatelyAsync("project", "inspect", "--workspace", harness.Workspace);

        Assert.Equal(0, result.Exit);
        using var reply = Document(result.Out);
        var root = reply.RootElement;
        Assert.Equal("missing", root.GetProperty("configState").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("project").ValueKind);

        // The only profile that exists without a configuration is the built-in one.
        Assert.Equal("claude-code-local", Assert.Single(root.GetProperty("profiles").EnumerateArray()).GetProperty("id").GetString());

        var suggestions = root.GetProperty("suggestions");
        Assert.Equal("dotnet", suggestions.GetProperty("toolchain").GetString());
        Assert.Equal(["App.sln"], suggestions.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["build", "test"], suggestions.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public void Nothing_is_suggested_when_the_workspace_does_not_say_which_toolchain_it_is()
    {
        using var harness = new CliHarness(config: null);
        var sln = Path.Combine(harness.Workspace, "App.sln");
        var pubspec = Path.Combine(harness.Workspace, "pubspec.yaml");
        var other = Path.Combine(harness.Workspace, "Other.sln");
        File.WriteAllText(sln, string.Empty);
        File.WriteAllText(pubspec, string.Empty);

        var both = ProjectInspector.Suggest(harness.Workspace);
        Assert.Null(both.Toolchain);
        Assert.Empty(both.Checks);
        Assert.Contains("for you to choose", Assert.Single(both.Evidence));

        File.Delete(pubspec);
        File.WriteAllText(other, string.Empty);

        // Two solutions: the toolchain is clear, but which one a check builds is the person's choice.
        var several = ProjectInspector.Suggest(harness.Workspace);
        Assert.Equal("dotnet", several.Toolchain);
        Assert.Empty(several.Checks);

        File.Delete(sln);
        File.Delete(other);

        var none = ProjectInspector.Suggest(harness.Workspace);
        Assert.Null(none.Toolchain);
        Assert.Empty(none.Evidence);
    }

    [Fact]
    public async Task Inspecting_reports_an_invalid_configuration_with_its_problems_and_a_valid_one_with_its_profiles_and_checks()
    {
        using var invalid = new CliHarness("""{ "version": 1, "nonsense": true }""");
        var bad = await invalid.RunSeparatelyAsync("project", "inspect", "--workspace", invalid.Workspace);

        Assert.Equal(0, bad.Exit);
        using (var reply = Document(bad.Out))
        {
            Assert.Equal("invalid", reply.RootElement.GetProperty("configState").GetString());
            Assert.NotEmpty(reply.RootElement.GetProperty("problems").EnumerateArray());
        }

        using var valid = new CliHarness(CliHarness.ValidConfig());
        var good = await valid.RunSeparatelyAsync("project", "inspect", "--workspace", valid.Workspace);

        using var ok = Document(good.Out);
        var root = ok.RootElement;
        Assert.Equal("ready", root.GetProperty("configState").GetString());
        Assert.Equal("dotnet", root.GetProperty("project").GetProperty("toolchain").GetString());
        Assert.Equal("symbol_present", Assert.Single(root.GetProperty("project").GetProperty("checks").EnumerateArray()).GetProperty("id").GetString());
        Assert.Contains(root.GetProperty("profiles").EnumerateArray(), p => p.GetProperty("id").GetString() == "claude-code-local");
        Assert.Contains("pi", root.GetProperty("harnesses").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(["dotnet", "flutter", "custom"], root.GetProperty("toolchains").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    [Fact]
    public async Task Inspecting_a_custom_project_reports_it_as_custom_with_its_own_checks()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: "custom"));

        var result = await harness.RunSeparatelyAsync("project", "inspect", "--workspace", harness.Workspace);

        using var reply = Document(result.Out);
        Assert.Equal("ready", reply.RootElement.GetProperty("configState").GetString());
        Assert.Equal("custom", reply.RootElement.GetProperty("project").GetProperty("toolchain").GetString());
        Assert.Equal("symbol_present", Assert.Single(reply.RootElement.GetProperty("project").GetProperty("checks").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public void A_workspace_is_never_suggested_to_be_custom()
    {
        using var harness = new CliHarness(config: null);
        Directory.CreateDirectory(Path.Combine(harness.Workspace, "ProjectSettings"));
        File.WriteAllText(Path.Combine(harness.Workspace, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1\n");

        var suggestions = ProjectInspector.Suggest(harness.Workspace);

        Assert.Null(suggestions.Toolchain);
        Assert.Empty(suggestions.Checks);
    }

    [Fact]
    public async Task Inspecting_a_workspace_that_does_not_exist_says_so_and_changes_nothing()
    {
        using var harness = new CliHarness(config: null);
        var nowhere = Path.Combine(harness.Home, "nowhere");

        var result = await harness.RunSeparatelyAsync("project", "inspect", "--workspace", nowhere);

        Assert.Equal(0, result.Exit);
        using var reply = Document(result.Out);
        Assert.False(reply.RootElement.GetProperty("workspaceExists").GetBoolean());
        Assert.False(Directory.Exists(nowhere));
    }

    // ---- aw attach -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Attach_needs_exactly_one_valid_run_id()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        Assert.Equal(2, (await harness.RunSeparatelyAsync("attach")).Exit);
        Assert.Equal(2, (await harness.RunSeparatelyAsync("attach", "not-a-run")).Exit);
        Assert.Equal(2, (await harness.RunSeparatelyAsync("attach", Guid.NewGuid().ToString(), "extra")).Exit);
    }

    [Fact]
    public async Task Describing_a_run_that_does_not_exist_is_a_successful_answer_that_there_is_no_session()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig());

        var result = await harness.RunSeparatelyAsync("attach", Guid.NewGuid().ToString(), "--describe");

        Assert.Equal(0, result.Exit);
        using var reply = Document(result.Out);
        Assert.False(reply.RootElement.GetProperty("available").GetBoolean());
        Assert.Contains("There is no run", reply.RootElement.GetProperty("reason").GetString());

        var attach = await harness.RunSeparatelyAsync("attach", Guid.NewGuid().ToString());
        Assert.NotEqual(0, attach.Exit);
        Assert.Contains("There is no run", attach.Error);
    }
}
