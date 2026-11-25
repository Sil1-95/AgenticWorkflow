namespace AgenticWorkflow.Execution.Local.Tests;

/// <summary>The pure rules of <see cref="WindowsScripts"/>: what counts as a script, where a bare name is searched, what cmd.exe is shown.</summary>
public sealed class WindowsScriptsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aw-scripts-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private IReadOnlyDictionary<string, string?> OnlyThisPath() => new Dictionary<string, string?> { ["PATH"] = _directory };

    private void Touch(string name) => File.WriteAllText(Path.Combine(_directory, name), "x");

    [Theory]
    [InlineData("flutter.bat", true)]
    [InlineData("flutter.CMD", true)]
    [InlineData(@"C:\tools\flutter\bin\flutter.bat", true)]
    [InlineData("flutter.bat.", true)] // Win32 ignores trailing dots and spaces, so this is still a batch script
    [InlineData("flutter.bat ", true)]
    [InlineData("flutter", false)]
    [InlineData("flutter.exe", false)]
    [InlineData("flutter.ps1", false)]
    [InlineData("flutter.bat.exe", false)]
    public void Only_cmd_and_bat_names_are_batch_scripts(string executable, bool expected)
        => Assert.Equal(expected, WindowsScripts.IsBatchScript(executable));

    [Theory]
    [InlineData("flutter", true)]
    [InlineData("flutter.bat", false)]
    [InlineData(@".\flutter", false)]
    [InlineData(@"bin\flutter", false)]
    [InlineData("C:flutter", false)]
    [InlineData("", false)]
    public void Only_a_name_without_directory_or_extension_is_bare(string executable, bool expected)
        => Assert.Equal(expected, WindowsScripts.IsBareName(executable));

    [Fact]
    public void The_fallback_extensions_are_exactly_com_bat_and_cmd()
        => Assert.Equal([".com", ".bat", ".cmd"], WindowsScripts.FallbackExtensions);

    [Fact]
    public void The_fallback_finds_cmd_and_bat_and_com_and_nothing_else()
    {
        Touch("viacmd.cmd");
        Touch("viabat.bat");
        Touch("viacom.com");
        Touch("viaps.ps1");
        Touch("viajs.js");
        Touch("viavbs.vbs");

        Assert.EndsWith("viacmd.cmd", WindowsScripts.FindFallback("viacmd", OnlyThisPath()));
        Assert.EndsWith("viabat.bat", WindowsScripts.FindFallback("viabat", OnlyThisPath()));
        Assert.EndsWith("viacom.com", WindowsScripts.FindFallback("viacom", OnlyThisPath()));
        Assert.Null(WindowsScripts.FindFallback("viaps", OnlyThisPath()));
        Assert.Null(WindowsScripts.FindFallback("viajs", OnlyThisPath()));
        Assert.Null(WindowsScripts.FindFallback("viavbs", OnlyThisPath()));
    }

    [Fact]
    public void Within_one_directory_bat_wins_over_cmd_like_the_default_pathext()
    {
        Touch("both.cmd");
        Touch("both.bat");

        Assert.EndsWith("both.bat", WindowsScripts.FindFallback("both", OnlyThisPath()));
    }

    [Fact]
    public void An_earlier_path_directory_wins_over_a_later_one()
    {
        var first = Directory.CreateDirectory(Path.Combine(_directory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_directory, "second")).FullName;
        File.WriteAllText(Path.Combine(first, "tool.cmd"), "x");
        File.WriteAllText(Path.Combine(second, "tool.bat"), "x");

        var path = new Dictionary<string, string?> { ["PATH"] = first + Path.PathSeparator + second };

        Assert.Equal(Path.Combine(first, "tool.cmd"), WindowsScripts.FindFallback("tool", path));
    }

    [Fact]
    public void Relative_and_quoted_path_entries_are_handled_and_the_working_directory_is_never_searched()
    {
        Touch("viacmd.cmd");
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_directory);

            // "." and a relative entry are ignored; a quoted absolute entry (legal in PATH) works.
            Assert.Null(WindowsScripts.FindFallback("viacmd", new Dictionary<string, string?> { ["PATH"] = "." + Path.PathSeparator + "relative" }));
            Assert.NotNull(WindowsScripts.FindFallback("viacmd", new Dictionary<string, string?> { ["PATH"] = $"\"{_directory}\"" }));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void The_path_variable_name_is_matched_case_insensitively_and_a_null_path_finds_nothing()
    {
        Touch("viacmd.cmd");

        Assert.NotNull(WindowsScripts.FindFallback("viacmd", new Dictionary<string, string?> { ["Path"] = _directory }));
        Assert.Null(WindowsScripts.FindFallback("viacmd", new Dictionary<string, string?> { ["PATH"] = null }));
    }

    [Fact]
    public void A_script_is_resolved_by_explicit_path_or_by_file_name_on_path_and_a_missing_one_is_null()
    {
        Touch("here.cmd");
        var full = Path.Combine(_directory, "here.cmd");

        Assert.Equal(full, WindowsScripts.ResolveScript(full, null));
        Assert.Equal(full, WindowsScripts.ResolveScript("here.cmd", OnlyThisPath()));
        Assert.Null(WindowsScripts.ResolveScript(Path.Combine(_directory, "missing.cmd"), null));
        Assert.Null(WindowsScripts.ResolveScript("missing.cmd", OnlyThisPath()));
        Assert.Null(WindowsScripts.ResolveScript("bad|name.cmd", OnlyThisPath()));
    }

    [Fact]
    public void The_command_line_runs_the_quoted_script_with_the_outer_quote_pair_cmd_will_strip()
    {
        var line = WindowsScripts.BuildCommandLine(@"C:\Program Files\Tool\tool.cmd", ["analyze", "--no-pub", "test/a b_test.dart", "--name=x", ""]);

        Assert.Equal("/d /v:off /s /c \"\"C:\\Program Files\\Tool\\tool.cmd\" analyze --no-pub \"test/a b_test.dart\" \"--name=x\" \"\"\"", line);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a<b")]
    [InlineData("a^b")]
    [InlineData("%PATH%")]
    [InlineData("100%")]
    [InlineData("!x!")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("&calc")]
    public void An_argument_cmd_could_interpret_is_rejected_not_escaped(string argument)
    {
        var e = Assert.Throws<InvalidOperationException>(() => WindowsScripts.BuildCommandLine(@"C:\t\tool.cmd", ["ok", argument]));

        Assert.Contains("cmd.exe would interpret", e.Message);
    }

    [Theory]
    [InlineData(@"C:\a&b\tool.cmd")]
    [InlineData(@"C:\a%b\tool.cmd")]
    [InlineData(@"C:\a^b\tool.cmd")]
    [InlineData(@"C:\a!b\tool.cmd")]
    public void A_script_path_cmd_could_interpret_is_rejected_too(string path)
        => Assert.Throws<InvalidOperationException>(() => WindowsScripts.BuildCommandLine(path, []));

    [Fact]
    public void A_quoted_argument_ending_in_a_backslash_is_rejected_but_a_bare_one_is_fine()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsScripts.BuildCommandLine(@"C:\t\tool.cmd", [@"C:\with space\"]));

        Assert.EndsWith(@" C:\dir\""", WindowsScripts.BuildCommandLine(@"C:\t\tool.cmd", [@"C:\dir\"]));
    }

    [Fact]
    public void An_unreasonably_long_command_line_is_rejected_instead_of_truncated_by_cmd()
        => Assert.Throws<InvalidOperationException>(() => WindowsScripts.BuildCommandLine(@"C:\t\tool.cmd", [new string('a', 9000)]));

    [Fact]
    public void The_interpreter_is_the_system_cmd_exe_not_whatever_comspec_says()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), WindowsScripts.CommandInterpreter);
        Assert.True(File.Exists(WindowsScripts.CommandInterpreter));
    }
}
