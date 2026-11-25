using System.Diagnostics;
using System.Text;

namespace AgenticWorkflow.DirectPath.Tests;

/// <summary>
/// A Git repository the tests may modify. Two kinds exist: a throw-away repository under the system temp directory that this
/// class creates and deletes (offline tests), and an existing, explicitly supplied repository that this class only
/// <see cref="Open"/>s and never deletes (the trusted live fixture, see <see cref="TrustedLiveFixture"/>).
/// </summary>
internal sealed class DirectFixtureRepo : IDisposable
{
    public const string TestProject = "tests/Meals.Tests/Meals.Tests.csproj";

    private readonly bool _owned;

    private DirectFixtureRepo(string root, bool owned)
    {
        Root = root;
        _owned = owned;
    }

    public string Root { get; }

    /// <summary>
    /// The deterministic baseline of the bounded M2B task: <c>Menu.GetMeal()</c> and its two references, with a passing
    /// test project. Excludes <c>.gitignore</c>, which <see cref="WriteBaseline"/> adds.
    /// </summary>
    public static IReadOnlyList<(string Path, string Content)> DotNetMealsFiles { get; } =
    [
        ("src/Meals/Meals.csproj", Csproj(extra: string.Empty)),
        ("src/Meals/Menu.cs", "namespace Meals;\n\npublic sealed class Menu\n{\n    public string GetMeal() => \"pasta\";\n}\n"),
        ("src/Meals/Waiter.cs", "namespace Meals;\n\npublic sealed class Waiter\n{\n    public string Serve(Menu menu) => $\"Today we serve {menu.GetMeal()}.\";\n}\n"),
        ("tests/Meals.Tests/Meals.Tests.csproj", Csproj(extra: TestPackages)),
        (
            "tests/Meals.Tests/MenuTests.cs",
            "using Meals;\n\nnamespace Meals.Tests;\n\npublic sealed class MenuTests\n{\n"
            + "    [Fact]\n    public void The_menu_offers_pasta() => Assert.Equal(\"pasta\", new Menu().GetMeal());\n\n"
            + "    [Fact]\n    public void The_waiter_announces_the_meal() => Assert.Equal(\"Today we serve pasta.\", new Waiter().Serve(new Menu()));\n}\n"),
    ];

    /// <summary>A new temp repository with the given files committed as the baseline. Used by tests that need no .NET build.</summary>
    public static DirectFixtureRepo Create(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenticworkflow-m2b-fixture-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);

        var repo = new DirectFixtureRepo(root, owned: true);
        repo.Git("init", "-q", "-b", "main");
        repo.ConfigureLocally();
        repo.WriteBaseline(files);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "fixture baseline");
        return repo;
    }

    /// <summary>An existing repository root, never deleted by this class. The caller is responsible for having proven it safe.</summary>
    public static DirectFixtureRepo Open(string root) => new(root, owned: false);

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    /// <summary>Writes <c>.gitignore</c> and the given files (LF endings, UTF-8 without BOM).</summary>
    public void WriteBaseline(IEnumerable<(string Path, string Content)> files)
    {
        Write(".gitignore", "bin/\nobj/\n");

        foreach (var (path, content) in files)
            Write(path, content);
    }

    /// <summary>Repository-local settings only; the identity is the throw-away fixture's, never a real user's.</summary>
    public void ConfigureLocally()
    {
        Git("config", "--local", "user.name", "Fixture");
        Git("config", "--local", "user.email", "fixture@example.invalid");
        Git("config", "--local", "commit.gpgsign", "false");
        Git("config", "--local", "core.autocrlf", "false");
    }

    public string Git(params string[] arguments)
    {
        var (exitCode, output, error) = RunGit(Root, arguments);

        return exitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
    }

    public static (int ExitCode, string Output, string Error) RunGit(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, errorTask.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        if (!_owned || Environment.GetEnvironmentVariable("AGENTICWORKFLOW_KEEP_FIXTURE") == "1")
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best effort
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private const string TestPackages =
        """
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
            <Using Include="Xunit" />
            <ProjectReference Include="..\..\src\Meals\Meals.csproj" />
          </ItemGroup>
        """;

    private static string Csproj(string extra)
        => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            {extra}</Project>
            """.Replace("\r\n", "\n") + "\n";
}
