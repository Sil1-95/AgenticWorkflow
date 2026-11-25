using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AgenticWorkflow.Packaging.Tests;

/// <summary>The outcome of running a program: its exit code and everything it printed.</summary>
public sealed record Outcome(int ExitCode, string Output)
{
    public override string ToString() => $"exit {ExitCode}:\n{Output}";
}

/// <summary>
/// The real release, built once for all tests by the real <c>scripts/package.ps1</c> and extracted once into a pristine folder. Tests never install from that folder directly when they
/// change anything: they take a copy (<see cref="CopyPackage"/>).
/// </summary>
public sealed class ReleaseFixture : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("aw-release-").FullName;

    public ReleaseFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
        Version = ReadVersion(RepositoryRoot);

        OutputDirectory = Path.Combine(_work, "out");
        var build = Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepositoryRoot, "scripts", "package.ps1"), "-OutputDirectory", OutputDirectory]);

        if (build.ExitCode != 0)
            throw new InvalidOperationException($"scripts/package.ps1 failed:\n{build.Output}");

        ZipPath = Path.Combine(OutputDirectory, $"AgenticWorkflow-{Version}-win-x64.zip");
        ExtractedRoot = Path.Combine(_work, "extracted");
        ZipFile.ExtractToDirectory(ZipPath, ExtractedRoot);
        PackageRoot = Directory.GetDirectories(ExtractedRoot).Single();

        FakeAw = Path.Combine(_work, "fake-aw", "aw.exe");
        BuildFakeAw(FakeAw);
    }

    public string RepositoryRoot { get; }

    public string Version { get; }

    public string OutputDirectory { get; }

    public string ZipPath { get; }

    public string ExtractedRoot { get; }

    /// <summary>The extracted release, as a person would find it after unzipping. Read only: copy it before changing anything.</summary>
    public string PackageRoot { get; }

    /// <summary>A stand-in for <c>aw.exe</c> that reports version 0.0.1 and answers <c>ctl stop</c> as a controller that supervises two running tasks.</summary>
    public string FakeAw { get; }

    public string CopyPackage(string destinationParent)
    {
        var target = Path.Combine(destinationParent, Path.GetFileName(PackageRoot));
        CopyDirectory(PackageRoot, target);

        return target;
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    public void Dispose()
    {
        // The per-test keys are gone by now; the shared parent is removed too when nothing else is using it.
        using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\AgenticWorkflowTests"))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\AgenticWorkflowTests", throwOnMissingSubKey: false);
        }

        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs a program without a shell; the environment is the test process's, minus the variables that would point at the real machine's state, plus what the caller sets.</summary>
    public static Outcome Run(string program, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (workingDirectory is not null)
            start.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        start.Environment.Remove("HERDR_APP_HOME");
        start.Environment.Remove("AGENTICWORKFLOW_HOME");

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                start.Environment.Remove(name);
            else
                start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{program} did not finish: {string.Join(' ', arguments)}");
        }

        process.WaitForExit();

        return new Outcome(process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgenticWorkflow.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root (AgenticWorkflow.sln) was not found above the test output.");
    }

    private static string ReadVersion(string repositoryRoot)
        => XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props")).Descendants("Version").Single().Value;

    private static void BuildFakeAw(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string source = """
            using System;
            public static class Fake
            {
                public static int Main(string[] args)
                {
                    var line = string.Join(" ", args);
                    if (line == "--version") { Console.WriteLine("aw 0.0.1"); return 0; }
                    if (line == "ctl stop") { Console.WriteLine("{ \"kind\": \"shutdown\", \"accepted\": false, \"activeRuns\": 2 }"); return 0; }
                    return 1;
                }
            }
            """;

        var script = Path.Combine(Path.GetDirectoryName(path)!, "build.ps1");
        File.WriteAllText(script, $"$ErrorActionPreference='Stop'\nAdd-Type -TypeDefinition @'\n{source}\n'@ -OutputType ConsoleApplication -OutputAssembly '{path}'\n");

        var build = Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]);

        if (build.ExitCode != 0 || !File.Exists(path))
            throw new InvalidOperationException($"The stand-in aw.exe could not be built:\n{build.Output}");
    }
}

/// <summary>One release build shared by every test class, and the classes run one after another: they all install into isolated roots, but they share the build and the machine's single registry.</summary>
[CollectionDefinition(Name)]
public sealed class ReleaseCollection : ICollectionFixture<ReleaseFixture>
{
    public const string Name = "Release";
}
