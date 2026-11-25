using System.Diagnostics;

namespace AgenticWorkflow.Execution.Local.Tests;

/// <summary>A throw-away Git repository under the system temp directory (never inside the project tree).</summary>
internal sealed class TempRepo : IDisposable
{
    public string Root { get; }

    private TempRepo(string root) => Root = root;

    public static TempRepo Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "agenticworkflow-exec-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        var repo = new TempRepo(root);
        repo.Git("init", "-q", "-b", "main");
        repo.Git("config", "user.name", "Test");
        repo.Git("config", "user.email", "test@example.invalid");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "core.autocrlf", "false");

        repo.Write(".gitignore", "bin/\nobj/\n");
        repo.Write("src/Greeting.cs", "public string GetMeal() => \"hello\";\n");
        repo.Write("README.md", "fixture\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "initial");

        return repo;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    public string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");

        return output;
    }

    public void Dispose()
    {
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
}
