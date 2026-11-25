using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Runtime.Herdr;

namespace AgenticWorkflow.Cli.Tests;

public sealed class ConfigurationTests
{
    private const string Valid = """
        {
          "version": 1,
          "role": "root",
          "profile": "claude-code-local",
          "toolchain": "flutter",
          "maxTurns": 4,
          "verification": [
            { "id": "analyze", "executable": "flutter", "arguments": ["analyze"] },
            { "id": "tests", "executable": "flutter", "arguments": ["test", "--no-pub"], "timeoutSeconds": 900, "description": "unit tests" }
          ],
          "scope": { "writable": ["lib/**", "test/**"], "protected": [".git/**", "AGENTS.md"] },
          "allowCommands": ["dart format"],
          "operatingNotes": "Prefer small commits of thought."
        }
        """;

    private static IReadOnlyList<string> Problems(string json)
        => Assert.Throws<ConfigurationException>(() => ProjectConfigParser.Parse(json)).Problems;

    [Fact]
    public void A_valid_configuration_loads_with_every_setting()
    {
        var config = ProjectConfigParser.Parse(Valid);

        Assert.Equal("root", config.Role);
        Assert.Equal("claude-code-local", config.Profile);
        Assert.Equal("flutter", config.Toolchain);
        Assert.Equal(4, config.MaxTurns);
        Assert.Equal(["analyze", "tests"], config.Verification.Select(c => c.Id));
        Assert.Equal(["test", "--no-pub"], config.Verification[1].Arguments);
        Assert.Equal(900, config.Verification[1].TimeoutSeconds);
        Assert.Equal(ProjectConfig.DefaultCheckTimeoutSeconds, config.Verification[0].TimeoutSeconds);
        Assert.Equal(["lib/**", "test/**"], config.Writable);
        Assert.Equal(["dart format"], config.AllowCommands);
        Assert.Equal("Prefer small commits of thought.", config.OperatingNotes);
    }

    [Fact]
    public void Max_turns_is_optional_and_defaults_visibly()
        => Assert.Equal(
            ProjectConfig.DefaultMaxTurns,
            ProjectConfigParser.Parse(Valid.Replace("\"maxTurns\": 4,", string.Empty)).MaxTurns);

    [Theory]
    [InlineData("\"version\": 2")]
    [InlineData("\"version\": 0")]
    [InlineData("\"version\": \"1\"")]
    public void An_unsupported_version_is_rejected(string replacement)
    {
        var problems = Problems(Valid.Replace("\"version\": 1", replacement));

        Assert.Contains(problems, p => p.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unknown_member_is_rejected_not_ignored()
        => Assert.Contains("'colour' is not a recognised setting.", Problems(Valid.Replace("\"version\": 1,", "\"version\": 1, \"colour\": \"red\",")));

    [Fact]
    public void An_unknown_member_inside_a_check_or_the_scope_is_rejected_with_its_path()
    {
        var inCheck = Problems(Valid.Replace("\"id\": \"analyze\",", "\"id\": \"analyze\", \"retries\": 3,"));
        var inScope = Problems(Valid.Replace("\"writable\":", "\"readonly\": [], \"writable\":"));

        Assert.Contains("'verification[0].retries' is not a recognised setting.", inCheck);
        Assert.Contains("'scope.readonly' is not a recognised setting.", inScope);
    }

    [Fact]
    public void A_duplicate_member_is_rejected_rather_than_silently_resolved()
        => Assert.Contains(Problems(Valid.Replace("\"role\": \"root\",", "\"role\": \"root\", \"role\": \"other\",")), p => p.Contains("'role' appears more than once"));

    [Theory]
    [InlineData("role")]
    [InlineData("profile")]
    [InlineData("toolchain")]
    [InlineData("verification")]
    [InlineData("scope")]
    [InlineData("version")]
    public void A_missing_required_setting_is_rejected(string name)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Valid)!.AsObject();
        document.Remove(name);

        Assert.Contains($"'{name}' is required.", Problems(document.ToJsonString()));
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var problems = Problems("""{ "version": 1, "extra": 1 }""");

        Assert.Contains("'role' is required.", problems);
        Assert.Contains("'profile' is required.", problems);
        Assert.Contains("'toolchain' is required.", problems);
        Assert.Contains("'verification' is required.", problems);
        Assert.Contains("'scope' is required.", problems);
        Assert.Contains("'extra' is not a recognised setting.", problems);
    }

    [Fact]
    public void Duplicate_check_ids_are_rejected()
        => Assert.Contains(
            "Verification check id 'analyze' is used more than once.",
            Problems(Valid.Replace("\"id\": \"tests\"", "\"id\": \"analyze\"")));

    [Theory]
    [InlineData("workspace_scope")]
    [InlineData("workspace_stable")]
    public void A_check_cannot_shadow_the_controllers_own_sensors(string id)
        => Assert.Contains(Problems(Valid.Replace("\"id\": \"tests\"", $"\"id\": \"{id}\"")), p => p.Contains("reserved"));

    [Theory]
    [InlineData("""{ "id": "Bad Id", "executable": "x" }""")]
    [InlineData("""{ "id": "a", "executable": " x" }""")]
    [InlineData("""{ "id": "a", "executable": "" }""")]
    [InlineData("""{ "id": "a" }""")]
    [InlineData("""{ "executable": "x" }""")]
    [InlineData("""{ "id": "a", "executable": "x", "arguments": "analyze" }""")]
    [InlineData("""{ "id": "a", "executable": "x", "arguments": [1] }""")]
    [InlineData("""{ "id": "a", "executable": "x", "arguments": ["a\nb"] }""")]
    [InlineData("""{ "id": "a", "executable": "x", "timeoutSeconds": 0 }""")]
    [InlineData("""{ "id": "a", "executable": "x", "timeoutSeconds": 999999 }""")]
    [InlineData("""{ "id": "a", "executable": "x", "timeoutSeconds": "5" }""")]
    public void A_malformed_check_is_rejected(string check)
        => Assert.NotEmpty(Problems(Valid.Replace("""{ "id": "analyze", "executable": "flutter", "arguments": ["analyze"] }""", check)));

    [Fact]
    public void A_configuration_with_no_checks_is_rejected_because_completion_needs_controller_evidence()
        => Assert.Contains(Problems(WithNoChecks(Valid)), p => p.Contains("at least one check"));

    private static string WithNoChecks(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["verification"] = new System.Text.Json.Nodes.JsonArray();

        return node.ToJsonString();
    }

    [Theory]
    [InlineData("""{ "writable": [] }""")]
    [InlineData("""{ "protected": ["x"] }""")]
    [InlineData("""{ "writable": "lib/**" }""")]
    [InlineData("""{ "writable": [""] }""")]
    public void An_impossible_or_malformed_scope_is_rejected(string scope)
        => Assert.NotEmpty(Problems(Valid.Replace("""{ "writable": ["lib/**", "test/**"], "protected": [".git/**", "AGENTS.md"] }""", scope)));

    [Fact]
    public void An_unsupported_toolchain_is_rejected_and_never_guessed()
    {
        var problems = Problems(Valid.Replace("\"flutter\"", "\"npm\""));

        Assert.Contains(problems, p => p.Contains("'npm' is not supported") && p.Contains("never detected"));
    }

    [Fact]
    public void Role_and_profile_are_explicit_and_an_unavailable_profile_is_an_error_not_a_substitution()
    {
        var unknownProfile = Problems(Valid.Replace("claude-code-local", "codex-local"));
        var otherRole = Problems(Valid.Replace("\"role\": \"root\"", "\"role\": \"reviewer\""));

        Assert.Contains(unknownProfile, p => p.Contains("'codex-local' is not defined") && p.Contains("no fallback"));
        Assert.Contains(otherRole, p => p.Contains("'reviewer' is not supported"));
    }

    [Theory]
    [InlineData("""["bash -c x"]""")]
    [InlineData("""["pwsh"]""")]
    [InlineData("""["git push"]""")]
    [InlineData("""["dotnet format *"]""")]
    [InlineData("""["dart format; rm x"]""")]
    [InlineData("""["dart format", "dart format"]""")]
    public void Extra_permitted_commands_stay_narrow(string commands)
        => Assert.NotEmpty(Problems(Valid.Replace("""["dart format"]""", commands)));

    [Theory]
    [InlineData("{ \"version\": 1, }")]
    [InlineData("{ \"version\": 1 /* no comments */ }")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void Anything_but_plain_strict_json_object_is_rejected(string json)
        => Assert.NotEmpty(Problems(json));

    // ---- resolution against a workspace -------------------------------------------------------------------------------------

    [Fact]
    public void Resolution_builds_the_existing_scope_and_check_objects_and_keeps_the_explicit_binding()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: "flutter"));

        var project = ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);

        Assert.Equal("claude-code-local", project.ProfileId.Value);
        Assert.Equal("root", project.RoleId.Value);
        Assert.Equal(["src/"], project.Scope.AllowedPaths);
        Assert.Equal(["src/locked.txt"], project.Scope.ProtectedPaths);
        Assert.Equal("symbol_present", Assert.Single(project.Checks).Id.Value);
        Assert.Equal("git", project.Checks[0].Executable);
        Assert.Equal(64, project.ConfigSha256.Length);

        var definition = project.ToDefinition("do it");
        Assert.Equal(project.ProfileId, definition.ExecutionProfile);
        Assert.Equal(project.RoleId, definition.RootRole);
    }

    [Fact]
    public void The_toolchain_never_chooses_the_profile()
    {
        foreach (var toolchain in new[] { "dotnet", "flutter" })
        {
            using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: toolchain));

            Assert.Equal("claude-code-local", ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace).ProfileId.Value);
        }
    }

    [Fact]
    public void Tool_policy_comes_from_the_toolchain_plus_narrow_extras_and_the_baseline_denials_always_apply()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: "flutter", extra: "\"allowCommands\": [\"dart format\"],"));

        var policy = ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace).ToolPolicy;

        Assert.Contains("Bash(flutter analyze:*)", policy.AllowedTools);
        Assert.Contains("Bash(dart format:*)", policy.AllowedTools);
        Assert.DoesNotContain("Bash(dotnet test:*)", policy.AllowedTools);
        Assert.All(ClaudeCodeToolPolicy.BaselineDeniedTools, denied => Assert.Contains(denied, policy.DeniedTools));
        Assert.Contains("Bash(git commit:*)", policy.DeniedTools);
        Assert.DoesNotContain(policy.AllowedTools, tool => tool.Contains("git commit") || tool.Contains("git push"));
    }

    [Fact]
    public void The_shipped_example_is_valid_and_states_explicit_claude_settings_and_no_machine_specific_model()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var config = ProjectConfigParser.Parse(File.ReadAllText(Path.Combine(directory.FullName, "examples", "agenticworkflow.json")));

        var claude = Assert.Single(config.DefinedProfiles, p => p.Id == config.Profile);
        Assert.Equal("claude-code", claude.Harness);
        Assert.NotNull(claude.Model);
        Assert.NotNull(claude.Reasoning);

        // Pi's model ids are the machine's own; the example uses Pi's native defaults and says so.
        var pi = Assert.Single(config.DefinedProfiles, p => p.Harness == "pi");
        Assert.Null(pi.Model);
        Assert.Null(pi.Reasoning);
        Assert.Contains("native default", pi.Description);
        Assert.Contains(config.Toolchain, Toolchain.All.Select(t => t.Id));
    }

    [Fact]
    public void A_custom_toolchain_has_no_built_in_commands_only_what_the_project_lists_and_read_only_git()
    {
        using var harness = new CliHarness(CliHarness.ValidConfig(toolchain: "custom", extra: "\"allowCommands\": [\"make verify\"], \"operatingNotes\": \"Run make verify once.\","));

        var project = ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);
        var policy = project.ToolPolicy;

        Assert.Equal("custom", project.Toolchain.Id);
        Assert.Equal([.. ClaudeCodeToolPolicy.ReadOnlyGitTools, "Bash(make verify:*)"], policy.AllowedTools);
        Assert.DoesNotContain(policy.AllowedTools, tool => tool.Contains("dotnet") || tool.Contains("flutter") || tool.Contains("dart"));
        Assert.Empty(project.Toolchain.AdditionalDeniedTools);
        Assert.All(ClaudeCodeToolPolicy.BaselineDeniedTools, denied => Assert.Contains(denied, policy.DeniedTools));
        Assert.DoesNotContain("dotnet", project.Toolchain.OperatingNotes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flutter", project.Toolchain.OperatingNotes, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Run make verify once.", project.OperatingNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_toolchain_still_needs_explicit_verification_and_a_bounded_scope()
    {
        var custom = Valid.Replace("\"flutter\"", "\"custom\"");
        var noChecks = Problems(System.Text.RegularExpressions.Regex.Replace(custom, @"""verification"": \[.*?\],\s*""scope""", "\"verification\": [], \"scope\"", System.Text.RegularExpressions.RegexOptions.Singleline));
        var interpreter = Problems(custom.Replace("""["dart format"]""", """["bash -c x"]"""));

        Assert.Contains(noChecks, p => p.Contains("'verification' must list at least one check"));
        Assert.Contains(interpreter, p => p.Contains("starts other programs") || p.Contains("must be words"));
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("CUSTOM")]
    [InlineData("customs")]
    [InlineData("unity")]
    [InlineData("")]
    public void The_toolchain_is_an_exact_known_word_never_free_text(string word)
    {
        var problems = Problems(Valid.Replace("\"flutter\"", $"\"{word}\""));

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("'custom'") || p.Contains("toolchain"));
    }

    [Fact]
    public void A_valid_custom_configuration_parses()
    {
        var config = ProjectConfigParser.Parse(Valid.Replace("\"flutter\"", "\"custom\""));

        Assert.Equal("custom", config.Toolchain);
    }

    [Fact]
    public void The_default_claude_tool_policy_is_exactly_what_it_was_before_the_cli()
    {
        var policy = new ClaudeCodeToolPolicy();

        Assert.Equal(
            ["Bash(dotnet test:*)", "Bash(dotnet build:*)", "Bash(git grep:*)", "Bash(git diff:*)", "Bash(git status:*)", "Bash(git ls-files:*)", "Bash(git log:*)", "Bash(git show:*)"],
            policy.AllowedTools);
        Assert.Equal(25, policy.DeniedTools.Count);
        Assert.Equal("Bash(git add:*)", policy.DeniedTools[0]);
        Assert.Equal("WebSearch", policy.DeniedTools[^1]);
    }

    [Theory]
    [InlineData("""["src/**", "src/**"]""", """["src/"]""", true)]
    [InlineData("""["src/a.txt"]""", """["src/**"]""", true)]
    [InlineData("""["src/**"]""", """["src/**"]""", true)]
    [InlineData("""["src/*.cs"]""", """[]""", true)]
    [InlineData("""["../outside"]""", """[]""", true)]
    [InlineData("""["/abs"]""", """[]""", true)]
    [InlineData("""["C:/abs"]""", """[]""", true)]
    [InlineData("""["src/**"]""", """["**"]""", true)]
    [InlineData("""["src/**"]""", """["src/locked.txt"]""", false)]
    [InlineData("""["**"]""", """[".git/**"]""", false)]
    public void Scope_patterns_are_translated_onto_the_existing_matcher_and_impossible_ones_fail(string writable, string protectedPaths, bool fails)
    {
        var json = CliHarness.ValidConfig().Replace("""{ "writable": ["src/**"], "protected": ["src/locked.txt"] }""", $$"""{ "writable": {{writable}}, "protected": {{protectedPaths}} }""");
        using var harness = new CliHarness(json);

        if (fails)
            Assert.Throws<ConfigurationException>(() => ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace));
        else
            ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);
    }

    [Fact]
    public void Everywhere_means_no_allow_list_and_a_directory_pattern_means_a_prefix()
    {
        using var everywhere = new CliHarness(CliHarness.ValidConfig().Replace("\"src/**\"", "\"**\""));

        Assert.Empty(ProjectResolver.Resolve(everywhere.Workspace, null, everywhere.Workspace).Scope.AllowedPaths);
    }

    [Fact]
    public void A_missing_configuration_fails_clearly_and_is_never_created()
    {
        using var harness = new CliHarness();

        var e = Assert.Throws<ConfigurationException>(() => ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace));

        Assert.Contains("No project configuration", e.Problems[0]);
        Assert.False(File.Exists(Path.Combine(harness.Workspace, ProjectResolver.DefaultConfigFileName)));
    }

    [Fact]
    public void A_missing_workspace_fails_clearly()
        => Assert.Contains("does not exist", Assert.Throws<ConfigurationException>(() => ProjectResolver.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null, Path.GetTempPath())).Problems[0]);

    [Fact]
    public void An_explicit_config_path_may_live_outside_the_workspace()
    {
        using var harness = new CliHarness();
        var elsewhere = Path.Combine(harness.Home, "elsewhere.json");
        File.WriteAllText(elsewhere, CliHarness.ValidConfig());

        var project = ProjectResolver.Resolve(harness.Workspace, elsewhere, harness.Workspace);

        Assert.Equal(elsewhere, project.ConfigPath);
    }
}
