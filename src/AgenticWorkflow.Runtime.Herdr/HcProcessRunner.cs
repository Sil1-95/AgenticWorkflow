using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgenticWorkflow.Runtime.Herdr;

/// <summary>Captured outcome of one finished process.</summary>
internal sealed record HcProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Launches an executable with an explicit argument list (never a shell string) and captures
/// exit code, stdout and stderr with timeout and cancellation support.
/// </summary>
internal sealed class HcProcessRunner
{
    private readonly string _executable;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyList<string> _scrubbedEnvironmentPrefixes;

    public HcProcessRunner(
        string executable,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        IReadOnlyList<string>? scrubbedEnvironmentPrefixes = null)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string>();
        _workingDirectory = workingDirectory;
        _scrubbedEnvironmentPrefixes = scrubbedEnvironmentPrefixes ?? [];
    }

    public async Task<HcProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new HcNotFoundException(_executable, exception);
        }

        // hc never reads stdin for the commands we use; close it so nothing can block on it.
        process.StandardInput.Close();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);

            if (cancellationToken.IsCancellationRequested)
                throw;

            throw new HcTimeoutException(_executable, arguments, timeout);
        }

        // WaitForExitAsync returning means the process ended; drain the redirected streams.
        return new HcProcessResult(process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (_workingDirectory is not null)
            startInfo.WorkingDirectory = _workingDirectory;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Applied before overrides so an explicit override always wins.
        foreach (var key in startInfo.Environment.Keys.ToList())
        {
            if (_scrubbedEnvironmentPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                startInfo.Environment.Remove(key);
        }

        foreach (var (key, value) in _environment)
            startInfo.Environment[key] = value;

        return startInfo;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
