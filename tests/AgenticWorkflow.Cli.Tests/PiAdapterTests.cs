using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// How the Pi adapter decides whether the installed Pi can honour a profile (from Pi's own help and model list, failing closed), and how Pi is found and launched without a shell. Pi
/// itself would only warn about a model or level it cannot use and carry on with another, so every refusal here is the only thing standing between a profile and a silent substitution.
/// </summary>
public sealed class PiAdapterTests : IDisposable
{
    private const string Help = """
        Options:
          --model <pattern>              Model pattern or ID (supports "provider/id" and optional ":<thinking>")
          --thinking <level>             Set thinking level: off, minimal, low, medium, high, xhigh, max
          --session <path|id>            Use a specific session file or partial UUID
          --session-id <id>              Use a specific session id
          --extension, -e <path>         Load an extension file
          --tools <allowlist>            Comma-separated allowlist of tool names
        """;

    private const string Models = """
        provider  model            context  max-out  thinking  images
        llama     Qwen3.8-27B      262144   32768    yes       no
        llama     Plain-NoReason   32768    4096     no        no
        anthropic claude-opus-4    200000   32000    yes       yes
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-pi-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public PiAdapterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ExecutionConfiguration Execution(string? model, string? reasoning) => new("pi", model, reasoning, "fp");

    private static HerdrAgentRuntimeFactory Factory(PiCliCapabilities? installed, bool program = true)
        => new(AppContext.BaseDirectory, new ExecutableLocator(_ => null), () => null, () => installed, () => program ? ["node", "cli.js"] : null);

    private static IReadOnlyList<string> Validate(PiCliCapabilities? installed, string? model, string? reasoning, bool program = true)
        => Factory(installed, program).ValidateExecution(new ProfileId("pi-main"), Execution(model, reasoning));

    // ---- what Pi says it accepts ------------------------------------------------------------------------------------------------

    [Fact]
    public void Pis_help_and_model_list_are_read_for_its_options_levels_and_models()
    {
        var installed = PiCliCapabilities.Parse(Help, Models);

        Assert.Empty(installed.MissingOptions);
        Assert.Equal(["off", "minimal", "low", "medium", "high", "xhigh", "max"], installed.ThinkingLevels);
        Assert.True(installed.Models["llama/Qwen3.8-27B"]);
        Assert.False(installed.Models["llama/Plain-NoReason"]);
        Assert.Equal(3, installed.Models.Count);
    }

    [Fact]
    public void A_pi_that_lacks_an_option_the_launch_depends_on_is_reported_by_name()
    {
        var installed = PiCliCapabilities.Parse(Help.Replace("--session-id <id>", "--sessionid <id>", StringComparison.Ordinal), Models);

        Assert.Equal(["--session-id"], installed.MissingOptions);
    }

    [Fact]
    public void A_help_text_with_no_thinking_line_lists_no_levels()
        => Assert.Null(PiCliCapabilities.Parse("Options:\n  --model <pattern>  Model\n", Models).ThinkingLevels);

    // ---- validation --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_listed_model_and_a_listed_level_are_accepted_and_so_is_a_profile_that_chooses_neither()
    {
        var installed = PiCliCapabilities.Parse(Help, Models);

        Assert.Empty(Validate(installed, "llama/Qwen3.8-27B", "low"));
        Assert.Empty(Validate(installed, "anthropic/claude-opus-4", "xhigh"));
        Assert.Empty(Validate(installed, null, null));
        Assert.Empty(Validate(installed, "llama/Qwen3.8-27B", null));
        Assert.Empty(Validate(installed, null, "high"));
    }

    [Fact]
    public void A_model_that_is_not_provider_qualified_is_refused_with_the_form_it_needs()
        => Assert.Contains("provider/id", Assert.Single(Validate(PiCliCapabilities.Parse(Help, Models), "Qwen3.8-27B", null)));

    [Theory]
    [InlineData("llama/Nope")]
    [InlineData("other/Qwen3.8-27B")]
    [InlineData("llama/Qwen3.8-27B:high")]    // Pi's own shorthand would hide a reasoning choice inside the model; it is set separately
    public void A_model_the_installed_pi_does_not_list_is_refused_never_replaced(string model)
        => Assert.Contains(model, Assert.Single(Validate(PiCliCapabilities.Parse(Help, Models), model, null)));

    [Fact]
    public void A_level_the_installed_pi_does_not_list_is_refused_naming_it_and_the_levels_it_does()
    {
        var problem = Assert.Single(Validate(PiCliCapabilities.Parse(Help, Models), "llama/Qwen3.8-27B", "ultra"));

        Assert.Contains("'ultra'", problem);
        Assert.Contains("off, minimal, low", problem);
    }

    [Fact]
    public void A_model_without_reasoning_accepts_only_off()
    {
        var installed = PiCliCapabilities.Parse(Help, Models);

        Assert.Empty(Validate(installed, "llama/Plain-NoReason", "off"));
        Assert.Contains("does not support reasoning", Assert.Single(Validate(installed, "llama/Plain-NoReason", "high")));
    }

    [Fact]
    public void A_pi_that_cannot_be_asked_or_found_refuses_everything_even_a_profile_that_chooses_nothing()
    {
        Assert.NotEmpty(Validate(null, "llama/Qwen3.8-27B", "low"));
        Assert.NotEmpty(Validate(null, null, null));                                       // Pi must be launchable at all
        Assert.NotEmpty(Validate(PiCliCapabilities.Parse(Help, Models), null, null, program: false));
    }

    [Fact]
    public void A_pi_missing_a_required_option_is_refused_as_a_whole()
    {
        var installed = PiCliCapabilities.Parse("Options:\n  --model <pattern>  Model\n  --thinking <level>  Set thinking level: off, low\n", Models);

        Assert.Contains("--session", Assert.Single(Validate(installed, null, null)));
    }

    [Fact]
    public void A_pi_profile_is_never_judged_by_the_claude_program_and_a_claude_profile_never_by_pi()
    {
        var factory = new HerdrAgentRuntimeFactory(AppContext.BaseDirectory, new ExecutableLocator(_ => null), () => null, () => PiCliCapabilities.Parse(Help, Models), () => ["node", "cli.js"]);

        Assert.Empty(factory.ValidateExecution(new ProfileId("p"), Execution("llama/Qwen3.8-27B", "low")));
        Assert.NotEmpty(factory.ValidateExecution(new ProfileId("p"), new ExecutionConfiguration("claude-code", "opus", null, "fp")));   // Claude could not be asked: fails closed, not answered by Pi
    }

    // ---- finding Pi without a shell ----------------------------------------------------------------------------------------------

    private ExecutableLocator Path_(params string[] directories)
        => new(name => name == "PATH" ? string.Join(Path.PathSeparator, directories) : null);

    private string WriteShim(string directory, string lastLine, bool script = true)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pi.cmd"), "@ECHO off\r\nSETLOCAL\r\nSET dp0=%~dp0\r\n" + lastLine + "\r\n");

        if (script)
        {
            var path = Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// pi");

            return path;
        }

        return string.Empty;
    }

    private const string ShimLine = "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & set PATHEXT=%PATHEXT:;.JS;=;% & \"%_prog%\"  \"%dp0%\\node_modules\\@earendil-works\\pi-coding-agent\\dist\\bundle\\cli.js\" %*";

    [Fact]
    public void A_missing_pi_is_a_stated_problem_not_a_guess()
    {
        Assert.Null(PiProgram.Resolve(Path_(_root), out var problem));
        Assert.Contains("not found", problem);
    }

    [Fact]
    public void A_command_shim_is_replaced_by_node_and_the_script_it_runs_so_no_argument_passes_through_a_shell()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shims = Path.Combine(_root, "npm");
        var cli = WriteShim(shims, ShimLine);
        File.WriteAllText(Path.Combine(shims, "node.exe"), string.Empty);

        var program = PiProgram.Resolve(Path_(shims), out var problem);

        Assert.Null(problem);
        Assert.Equal([Path.Combine(shims, "node.exe"), cli], program);
    }

    [Fact]
    public void A_shim_whose_script_is_missing_or_unrecognisable_is_never_launched()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var missing = Path.Combine(_root, "missing");
        WriteShim(missing, ShimLine, script: false);
        Assert.Null(PiProgram.Resolve(Path_(missing), out var missingProblem));
        Assert.Contains("does not exist", missingProblem);

        var odd = Path.Combine(_root, "odd");
        WriteShim(odd, "pi-something-else %*", script: false);
        Assert.Null(PiProgram.Resolve(Path_(odd), out var oddProblem));
        Assert.Contains("without a shell", oddProblem);
    }

    [Fact]
    public void A_shim_with_no_node_anywhere_is_refused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shims = Path.Combine(_root, "nonode");
        WriteShim(shims, ShimLine);

        Assert.Null(PiProgram.Resolve(Path_(shims), out var problem));
        Assert.Contains("node", problem);
    }
}
