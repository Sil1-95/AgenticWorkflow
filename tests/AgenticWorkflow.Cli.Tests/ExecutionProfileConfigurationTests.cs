using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// The profile definitions of the project configuration (what they may say, what they may never say), the fingerprint that identifies one, and how the Claude Code adapter decides whether it
/// can honour a model and a reasoning level: from what the installed program reports, failing closed when it cannot be asked.
/// </summary>
public sealed class ExecutionProfileConfigurationTests
{
    private const string Base = """
        {
          "version": 1,
          "acceptance": "none",
          "role": "root",
          "profile": "claude-main",
          __PROFILES__
          "toolchain": "dotnet",
          "verification": [ { "id": "t", "executable": "git", "arguments": ["status"] } ],
          "scope": { "writable": ["src/**"] }
        }
        """;

    private const string Defined = """
        "profiles": {
          "claude-main": { "harness": "claude-code", "model": "opus", "reasoning": "high", "description": "main" },
          "pi-later": { "harness": "pi", "model": "some-model" }
        },
        """;

    private static string Config(string profiles, string? profileRef = null)
        => Base.Replace("__PROFILES__", profiles).Replace("\"profile\": \"claude-main\"", $"\"profile\": \"{profileRef ?? "claude-main"}\"");

    private static IReadOnlyList<string> Problems(string json) => Assert.Throws<ConfigurationException>(() => ProjectConfigParser.Parse(json)).Problems;

    // ---- parsing -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Profiles_carry_a_harness_a_model_a_reasoning_and_a_description_as_opaque_text()
    {
        var config = ProjectConfigParser.Parse(Config(Defined));

        var main = config.Catalog.Find("claude-main")!;
        Assert.Equal(("claude-code", "opus", "high", "main"), (main.Harness, main.Model, main.Reasoning, main.Description));
        Assert.False(main.IsBuiltIn);
    }

    [Fact]
    public void A_pi_profile_is_representable_and_its_harness_is_one_this_build_runs()
    {
        var pi = ProjectConfigParser.Parse(Config(Defined)).Catalog.Find("pi-later")!;

        Assert.Equal("pi", pi.Harness);
        Assert.Equal("some-model", pi.Model);
        Assert.True(Harnesses.IsSupported(pi.Harness));
        Assert.False(Harnesses.IsSupported("codex"));
    }

    [Fact]
    public void Model_and_reasoning_are_open_strings_not_a_closed_list()
    {
        var config = ProjectConfigParser.Parse(Config("""
            "profiles": { "claude-main": { "harness": "claude-code", "model": "claude-opus-9-9[1m]", "reasoning": "ultra-deep" } },
            """));

        var entry = config.Catalog.Find("claude-main")!;
        Assert.Equal(("claude-opus-9-9[1m]", "ultra-deep"), (entry.Model, entry.Reasoning));
    }

    [Fact]
    public void A_configuration_with_no_profiles_member_still_has_the_built_in_profile_and_only_that()
    {
        var config = ProjectConfigParser.Parse(Config(string.Empty, profileRef: "claude-code-local"));

        var only = Assert.Single(config.Catalog.All);
        Assert.Equal(("claude-code-local", "claude-code", null, null), (only.Id.Value, only.Harness, only.Model, only.Reasoning));
        Assert.True(only.IsBuiltIn);
    }

    [Fact]
    public void A_role_binding_that_names_an_undefined_profile_is_a_configuration_error_with_no_fallback()
    {
        var problems = Problems(Config(Defined, profileRef: "claude-ghost"));

        Assert.Contains(problems, p => p.Contains("'claude-ghost' is not defined") && p.Contains("no fallback"));
    }

    [Theory]
    [InlineData("\"args\": [\"--dangerously-skip-permissions\"]")]
    [InlineData("\"env\": { \"KEY\": \"value\" }")]
    [InlineData("\"command\": \"claude\"")]
    [InlineData("\"extraArguments\": \"--foo\"")]
    [InlineData("\"apiKey\": \"sk-secret\"")]
    public void A_profile_has_no_place_for_commands_arguments_environment_or_secrets(string member)
    {
        var problems = Problems(Config($$"""
            "profiles": { "claude-main": { "harness": "claude-code", {{member}} } },
            """));

        Assert.Contains(problems, p => p.Contains("is not a recognised setting"));
    }

    [Theory]
    [InlineData("opus --dangerously-skip-permissions")]
    [InlineData("--model")]
    [InlineData("-x")]
    [InlineData("a;b")]
    [InlineData("a b")]
    [InlineData("$(whoami)")]
    [InlineData("a`b")]
    [InlineData("")]
    public void A_model_or_reasoning_that_could_be_more_than_one_argument_or_a_flag_is_rejected(string value)
    {
        var model = Problems(Config($$"""
            "profiles": { "claude-main": { "harness": "claude-code", "model": "{{value}}" } },
            """));
        var reasoning = Problems(Config($$"""
            "profiles": { "claude-main": { "harness": "claude-code", "reasoning": "{{value}}" } },
            """));

        Assert.NotEmpty(model);
        Assert.NotEmpty(reasoning);
    }

    [Fact]
    public void An_overlong_model_is_rejected()
        => Assert.NotEmpty(Problems(Config($$"""
            "profiles": { "claude-main": { "harness": "claude-code", "model": "{{new string('a', LaunchValues.MaxLength + 1)}}" } },
            """)));

    [Fact]
    public void An_unknown_harness_is_a_configuration_error_naming_the_known_ones()
    {
        var problems = Problems(Config("""
            "profiles": { "claude-main": { "harness": "codex" } },
            """));

        Assert.Contains(problems, p => p.Contains("'claude-code'") && p.Contains("'pi'") && p.Contains("'codex'"));
    }

    [Fact]
    public void The_built_in_profile_cannot_be_redefined_so_the_old_name_always_means_the_same_launch()
    {
        var problems = Problems(Config("""
            "profiles": { "claude-code-local": { "harness": "claude-code", "model": "opus" } },
            """, profileRef: "claude-code-local"));

        Assert.Contains(problems, p => p.Contains("redefines a built-in profile"));
    }

    [Fact]
    public void A_profile_without_a_harness_a_duplicate_id_and_a_malformed_id_are_each_rejected()
    {
        Assert.NotEmpty(Problems(Config("\"profiles\": { \"claude-main\": { \"model\": \"opus\" } },")));
        Assert.NotEmpty(Problems(Config("\"profiles\": { \"claude-main\": { \"harness\": \"claude-code\" }, \"claude-main\": { \"harness\": \"claude-code\" } },")));
        Assert.NotEmpty(Problems(Config("\"profiles\": { \"Claude Main\": { \"harness\": \"claude-code\" } },")));
    }

    // ---- the fingerprint -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_fingerprint_follows_what_is_launched_and_not_the_description()
    {
        AgentProfileEntry Entry(string? model, string? reasoning, string? description) => new(new ProfileId("p"), "claude-code", model, reasoning, description);

        Assert.Equal(Entry("opus", "high", "a").Fingerprint, Entry("opus", "high", "b").Fingerprint);
        Assert.NotEqual(Entry("opus", "high", null).Fingerprint, Entry("sonnet", "high", null).Fingerprint);
        Assert.NotEqual(Entry("opus", "high", null).Fingerprint, Entry("opus", "low", null).Fingerprint);
        Assert.NotEqual(Entry("opus", null, null).Fingerprint, Entry(null, "opus", null).Fingerprint);   // the fields are not interchangeable
    }

    [Fact]
    public void The_recorded_execution_is_the_profiles_values_exactly_with_nothing_filled_in()
    {
        var execution = new AgentProfileEntry(new ProfileId("p"), "claude-code", null, "low", null).ToExecution();

        Assert.Equal(("claude-code", null, "low"), (execution.Harness, execution.Model, execution.Reasoning));
    }

    // ---- what the installed Claude Code can honour ---------------------------------------------------------------------------------

    private const string HelpWithBoth = """
        Options:
          --effort <level>                                  Effort level for the current session (low, medium, high, xhigh, max)
          --model <model>                                   Model for the current session. Provide an alias for the latest model (e.g. 'sonnet' or 'opus') or a model's full name.
          --permission-mode <mode>                          Permission mode
        """;

    private static HerdrAgentRuntimeFactory FactoryWith(ClaudeCliCapabilities? installed)
        => new(AppContext.BaseDirectory, new ExecutableLocator(_ => null), () => installed);

    private static ExecutionConfiguration Execution(string? model, string? reasoning, string harness = "claude-code") => new(harness, model, reasoning, "fp");

    [Fact]
    public void Help_text_is_read_for_the_model_option_and_the_listed_effort_levels()
    {
        var capabilities = ClaudeCliCapabilities.Parse(HelpWithBoth);

        Assert.True(capabilities.SupportsModel);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], capabilities.EffortLevels);
    }

    [Fact]
    public void A_help_text_without_the_options_reports_neither()
    {
        var capabilities = ClaudeCliCapabilities.Parse("Options:\n  --permission-mode <mode>   Permission mode\n");

        Assert.False(capabilities.SupportsModel);
        Assert.Null(capabilities.EffortLevels);
    }

    [Fact]
    public void A_listed_model_and_reasoning_are_accepted_and_any_model_name_is_passed_on_for_the_program_to_judge()
    {
        var factory = FactoryWith(ClaudeCliCapabilities.Parse(HelpWithBoth));

        Assert.Empty(factory.ValidateExecution(new ProfileId("p"), Execution("opus", "high")));
        Assert.Empty(factory.ValidateExecution(new ProfileId("p"), Execution("a-model-from-the-future", "max")));
    }

    [Fact]
    public void A_reasoning_level_the_program_does_not_list_is_refused_naming_it_and_the_levels_it_does()
    {
        var problems = FactoryWith(ClaudeCliCapabilities.Parse(HelpWithBoth)).ValidateExecution(new ProfileId("p"), Execution("opus", "ultra"));

        var problem = Assert.Single(problems);
        Assert.Contains("'ultra'", problem);
        Assert.Contains("low, medium, high, xhigh, max", problem);
    }

    [Fact]
    public void A_program_that_lacks_the_option_refuses_the_value_that_needs_it_instead_of_dropping_it()
    {
        var factory = FactoryWith(ClaudeCliCapabilities.Parse("Options:\n  --permission-mode <mode>   Permission mode\n"));

        Assert.Contains("model", Assert.Single(factory.ValidateExecution(new ProfileId("p"), Execution("opus", null))));
        Assert.Contains("reasoning", Assert.Single(factory.ValidateExecution(new ProfileId("p"), Execution(null, "high"))));
        Assert.Empty(factory.ValidateExecution(new ProfileId("p"), Execution(null, null)));
    }

    [Fact]
    public void When_the_program_cannot_be_asked_a_model_or_reasoning_fails_closed_and_a_profile_with_neither_still_works()
    {
        var factory = FactoryWith(null);

        Assert.NotEmpty(factory.ValidateExecution(new ProfileId("p"), Execution("opus", null)));
        Assert.NotEmpty(factory.ValidateExecution(new ProfileId("p"), Execution(null, "high")));
        Assert.Empty(factory.ValidateExecution(new ProfileId("p"), Execution(null, null)));
    }

    [Fact]
    public void An_unknown_harness_is_refused_by_the_adapters_and_never_launched_as_claude()
        => Assert.NotEmpty(FactoryWith(ClaudeCliCapabilities.Parse(HelpWithBoth)).ValidateExecution(new ProfileId("p"), Execution("m", null, harness: "codex")));
}
