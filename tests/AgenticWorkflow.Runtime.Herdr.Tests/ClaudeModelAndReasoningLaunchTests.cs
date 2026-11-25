using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// How a profile's model and reasoning reach the Claude Code command line: as the CLI's own <c>--model</c> and <c>--effort</c> options, each one separate argument, placed before the
/// variadic tool lists, and nothing at all when the profile chooses none. A value that could be more than one argument or a flag is refused, not sanitised.
/// </summary>
public sealed class ClaudeModelAndReasoningLaunchTests
{
    private static readonly ProfileId Profile = new("claude-main");

    [Fact]
    public void A_profile_that_chooses_neither_launches_exactly_as_it_always_did()
    {
        var plain = ClaudeCodeLaunch.Profile(Profile).Argv;

        Assert.DoesNotContain("--model", plain);
        Assert.DoesNotContain("--effort", plain);
        Assert.Equal(plain, ClaudeCodeLaunch.Profile(Profile, model: null, reasoning: null).Argv);
    }

    [Fact]
    public void A_model_and_a_reasoning_level_are_separate_arguments_placed_before_the_variadic_tool_lists()
    {
        var argv = ClaudeCodeLaunch.Profile(Profile, model: "opus", reasoning: "high").Argv.ToList();

        Assert.Equal(["claude", "--permission-mode", "acceptEdits", "--model", "opus", "--effort", "high"], argv.Take(7));
        Assert.True(argv.IndexOf("--effort") < argv.IndexOf("--allowedTools"));
        Assert.True(argv.IndexOf("--effort") < argv.IndexOf("--disallowedTools"));
    }

    [Fact]
    public void Only_the_value_that_was_chosen_is_passed()
    {
        var modelOnly = ClaudeCodeLaunch.Profile(Profile, model: "claude-opus-4-1").Argv;
        var effortOnly = ClaudeCodeLaunch.Profile(Profile, reasoning: "low").Argv;

        Assert.Contains("claude-opus-4-1", modelOnly);
        Assert.DoesNotContain("--effort", modelOnly);
        Assert.Contains("low", effortOnly);
        Assert.DoesNotContain("--model", effortOnly);
    }

    [Fact]
    public void The_permission_policy_and_the_denials_are_unchanged_by_a_model_or_reasoning()
    {
        var plain = ClaudeCodeLaunch.Profile(Profile).Argv;
        var chosen = ClaudeCodeLaunch.Profile(Profile, model: "opus", reasoning: "max").Argv;

        Assert.Equal(plain, chosen.Where(a => a is not ("--model" or "opus" or "--effort" or "max")));
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("-x")]
    [InlineData("opus --dangerously-skip-permissions")]
    [InlineData("opus high")]
    [InlineData("opus\nhigh")]
    [InlineData("")]
    [InlineData("  ")]
    public void A_value_that_is_a_flag_or_more_than_one_argument_is_refused_not_cleaned(string value)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeLaunch.Profile(Profile, model: value));
        Assert.Throws<ArgumentException>(() => ClaudeCodeLaunch.Profile(Profile, reasoning: value));
    }

    [Fact]
    public void The_reviewer_and_prover_launch_with_their_own_recorded_values_under_the_read_only_policy()
    {
        var policy = new ClaudeCodeToolPolicy().ForReviewer();

        var reviewer = ClaudeCodeLaunch.Profile(new ProfileId("claude-review"), policy, model: "sonnet", reasoning: "low", purpose: AgentSessionPurpose.Review).Argv;

        Assert.Contains("sonnet", reviewer);
        Assert.Contains("low", reviewer);
        Assert.Contains("Edit", reviewer);   // still denied
        Assert.Equal("default", reviewer[reviewer.ToList().IndexOf("--permission-mode") + 1]);
    }
}
