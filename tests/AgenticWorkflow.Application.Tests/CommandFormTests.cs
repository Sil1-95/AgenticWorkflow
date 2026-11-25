using AgenticWorkflow.Application.Evidence;

namespace AgenticWorkflow.Application.Tests;

/// <summary>The conservative command recognition: only plain word lists, nothing a shell would interpret.</summary>
public sealed class CommandFormTests
{
    private static readonly string[] BuildArguments = ["build", "Fixture.sln", "--no-incremental"];

    [Theory]
    [InlineData("dotnet build Fixture.sln --no-incremental")]
    [InlineData("dotnet   build  Fixture.sln --no-incremental")]
    [InlineData("  dotnet build Fixture.sln --no-incremental  ")]
    [InlineData("DOTNET build Fixture.sln --no-incremental")]
    public void A_plain_command_with_the_same_executable_and_arguments_matches(string command)
        => Assert.True(CommandForm.Matches("dotnet", BuildArguments, command));

    [Theory]
    [InlineData("cd src && dotnet build Fixture.sln --no-incremental")]
    [InlineData("dotnet build Fixture.sln --no-incremental | tail -5")]
    [InlineData("dotnet build Fixture.sln --no-incremental ; echo done")]
    [InlineData("dotnet build Fixture.sln --no-incremental 2>&1")]
    [InlineData("dotnet build Fixture.sln --no-incremental > out.txt")]
    [InlineData("dotnet build Fixture.sln --no-incremental || true")]
    [InlineData("dotnet build Fixture.sln --no-incremental &")]
    [InlineData("powershell -Command \"dotnet build Fixture.sln --no-incremental\"")]
    [InlineData("cmd /c dotnet build Fixture.sln --no-incremental")]
    [InlineData("FOO=bar dotnet build Fixture.sln --no-incremental")]
    [InlineData("dotnet build \"Fixture.sln\" --no-incremental")]
    [InlineData("dotnet build 'Fixture.sln' --no-incremental")]
    [InlineData("dotnet build $(echo Fixture.sln) --no-incremental")]
    [InlineData("dotnet build `echo Fixture.sln` --no-incremental")]
    [InlineData("dotnet build Fixture.* --no-incremental")]
    [InlineData("dotnet build src\\Fixture.sln --no-incremental")]
    [InlineData("dotnet build Fixture.sln --no-incremental\ndotnet test")]
    [InlineData("dotnet build Fixture.sln\t--no-incremental")]
    [InlineData("(dotnet build Fixture.sln --no-incremental)")]
    public void Anything_a_shell_would_interpret_is_not_recognised(string command)
        => Assert.False(CommandForm.Matches("dotnet", BuildArguments, command));

    [Theory]
    [InlineData("dotnet build Fixture.sln")]
    [InlineData("dotnet build Fixture.sln --no-incremental --verbosity quiet")]
    [InlineData("dotnet test Fixture.sln --no-incremental")]
    [InlineData("dotnet build Other.sln --no-incremental")]
    [InlineData("dotnet build fixture.sln --no-incremental")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_different_executable_or_argument_list_is_a_mismatch(string command)
        => Assert.False(CommandForm.Matches("dotnet", BuildArguments, command));

    // ---- .NET test filters: `!`, `~` and `!=` are data in a --filter value, never anywhere else ----------------------------

    private const string Excluded = "FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script";

    private static readonly string[] FilteredTestArguments = ["test", "--no-build", "--filter", Excluded];

    [Theory]
    [InlineData(Excluded)]
    [InlineData("FullyQualifiedName~Orders")]
    [InlineData("Name!=Slow")]
    [InlineData("Category=Unit")]
    [InlineData("FullyQualifiedName!~A.B_C")]
    public void A_filter_expression_with_the_filters_own_operators_is_one_plain_word(string expression)
    {
        string[] arguments = ["test", "--no-build", "--filter", expression];

        Assert.True(CommandForm.Matches("dotnet", arguments, $"dotnet test --no-build --filter {expression}"));
    }

    [Fact]
    public void The_configured_filtered_test_command_is_recognised_exactly()
    {
        Assert.True(CommandForm.Matches("dotnet", FilteredTestArguments, $"dotnet test --no-build --filter {Excluded}"));
        Assert.Equal($"dotnet test --no-build --filter {Excluded}", CommandForm.Identity("dotnet", FilteredTestArguments));
    }

    [Theory]
    [InlineData("&& echo pwned")]
    [InlineData("; echo pwned")]
    [InlineData("| tee x")]
    [InlineData("|| echo pwned")]
    [InlineData("& echo pwned")]
    [InlineData("> x")]
    [InlineData(">> x")]
    [InlineData("< x")]
    [InlineData("2>&1")]
    [InlineData("$(echo x)")]
    [InlineData("`echo x`")]
    [InlineData("$HOME")]
    [InlineData("\n echo pwned")]
    [InlineData("\r\n echo pwned")]
    [InlineData("\t; echo pwned")]
    [InlineData("-and (echo pwned)")]
    [InlineData("; Remove-Item x")]
    [InlineData("& calc")]
    [InlineData("| Out-File x")]
    public void Shell_composition_after_the_configured_filtered_command_is_never_recognised(string suffix)
        => Assert.False(CommandForm.Matches("dotnet", FilteredTestArguments, $"dotnet test --no-build --filter {Excluded} {suffix}"));

    [Theory]
    [InlineData("&& echo pwned")]
    [InlineData("; echo pwned")]
    [InlineData("| tee x")]
    [InlineData("> x")]
    [InlineData("< x")]
    [InlineData("$(echo x)")]
    [InlineData("`echo x`")]
    public void Shell_composition_glued_to_the_filter_expression_is_never_recognised(string suffix)
        => Assert.False(CommandForm.Matches("dotnet", FilteredTestArguments, $"dotnet test --no-build --filter {Excluded}{suffix}"));

    [Theory]
    [InlineData("cd src && ", "")]
    [InlineData("echo hi; ", "")]
    [InlineData("bash -c ", "")]
    [InlineData("bash -c \"", "\"")]
    [InlineData("cmd /c ", "")]
    [InlineData("powershell -Command \"", "\"")]
    [InlineData("FOO=bar ", "")]
    [InlineData("! ", "")]
    [InlineData("(", ")")]
    [InlineData("", " | tail")]
    [InlineData("", "&&echo")]
    [InlineData("", ";echo")]
    public void The_configured_filtered_command_inside_another_shell_form_is_never_a_match(string prefix, string suffix)
        => Assert.False(CommandForm.Matches("dotnet", FilteredTestArguments, $"{prefix}dotnet test --no-build --filter {Excluded}{suffix}"));

    [Theory]
    [InlineData("dotnet test --no-build --filter \"FullyQualifiedName!~X\"")]
    [InlineData("dotnet test --no-build --filter 'FullyQualifiedName!~X'")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!~X | tail")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!~X&&echo")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!~X;echo")]
    public void A_quoted_or_composed_filter_is_not_a_plain_word_list(string command)
        => Assert.Null(CommandForm.Tokenize(command));

    [Theory]
    [InlineData("dotnet test --no-build --filter !~X")]
    [InlineData("dotnet test --no-build --filter ~X")]
    [InlineData("dotnet test --no-build --filter ~")]
    [InlineData("dotnet test --no-build --filter Name=~/x")]
    [InlineData("dotnet test --no-build --filter Name:~x")]
    [InlineData("dotnet test --no-build --filter Name!")]
    [InlineData("dotnet test --no-build --filter Name!x")]
    [InlineData("dotnet test --no-build --filter Name!!~x")]
    [InlineData("dotnet test --no-build --filter A!~B!C")]
    [InlineData("dotnet test --no-build --filter A&B")]
    [InlineData("dotnet test --no-build --filter A|B")]
    [InlineData("dotnet test --no-build --filter (A)")]
    [InlineData("dotnet test --no-build --filter A\\B")]
    public void The_filter_operators_are_data_only_where_no_shell_would_interpret_them(string command)
        => Assert.Null(CommandForm.Tokenize(command));

    [Theory]
    [InlineData("dotnet test --no-build FullyQualifiedName!~X")]
    [InlineData("dotnet test --no-build --other FullyQualifiedName!~X")]
    [InlineData("dotnet test --no-build --filter A B!~X")]
    [InlineData("dotnet test --no-build --filter=Name!~X")]
    [InlineData("dotnet test --no-build !~X")]
    [InlineData("git status !~x")]
    [InlineData("echo hi!~there")]
    public void The_filter_operators_are_not_data_outside_the_value_of_a_filter_option(string command)
        => Assert.Null(CommandForm.Tokenize(command));

    [Theory]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!~Other")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName~Other")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!=Other")]
    [InlineData("dotnet test --filter FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script")]
    [InlineData("dotnet test --no-build --filter FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script --verbosity quiet")]
    [InlineData("dotnet test --no-build Other.sln --filter FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script")]
    [InlineData("dotnet build --no-build --filter FullyQualifiedName!~WindowsScriptExecutionTests.Environment_variables_are_set_overridden_and_removed_for_the_script")]
    [InlineData("dotnet test --no-build --filter")]
    public void A_recognised_filtered_command_that_differs_from_the_configured_one_is_still_a_mismatch(string command)
        => Assert.False(CommandForm.Matches("dotnet", FilteredTestArguments, command));

    [Fact]
    public void A_filtered_command_is_not_a_read_only_command_and_still_shows_its_executable()
    {
        Assert.False(CommandForm.IsKnownReadOnly($"dotnet test --no-build --filter {Excluded}"));
        Assert.Equal("dotnet", CommandForm.Tokenize($"dotnet test --no-build --filter {Excluded}")![0]);
    }

    [Fact]
    public void A_very_long_command_is_not_recognised()
        => Assert.Null(CommandForm.Tokenize("dotnet " + new string('a', CommandForm.MaxCommandLength)));

    [Fact]
    public void Identity_is_the_executable_and_arguments_single_space_separated()
        => Assert.Equal("dotnet build Fixture.sln --no-incremental", CommandForm.Identity("dotnet", BuildArguments));

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff --stat")]
    [InlineData("git grep -n GetFood")]
    [InlineData("git ls-files")]
    [InlineData("ls src")]
    [InlineData("cat src/Menu.cs")]
    public void A_short_list_of_plain_read_only_commands_is_known_read_only(string command)
        => Assert.True(CommandForm.IsKnownReadOnly(command));

    [Theory]
    [InlineData("git checkout .")]
    [InlineData("git add -A")]
    [InlineData("git stash")]
    [InlineData("git diff --output=patch.txt")]
    [InlineData("sed -i s/a/b/ src/Menu.cs")]
    [InlineData("dotnet format")]
    [InlineData("rm src/Menu.cs")]
    [InlineData("cat src/Menu.cs > copy.cs")]
    [InlineData("git status ; rm -rf x")]
    public void Anything_else_may_change_state(string command)
        => Assert.False(CommandForm.IsKnownReadOnly(command));

    [Fact]
    public void Directories_compare_after_normalisation_and_blank_never_matches()
    {
        Assert.True(CommandForm.SameDirectory(@"C:\fixture\", @"c:\FIXTURE"));
        Assert.True(CommandForm.SameDirectory(@"C:\fixture\src", CommandForm.ExpectedDirectory(@"C:\fixture", "src")));
        Assert.False(CommandForm.SameDirectory(@"C:\fixture\src", @"C:\fixture"));
        Assert.False(CommandForm.SameDirectory(null, @"C:\fixture"));
        Assert.False(CommandForm.SameDirectory(@"C:\fixture", " "));
    }
}
