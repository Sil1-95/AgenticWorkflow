using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgenticWorkflow.Execution.Local;

/// <summary>One process to run: an executable and an argument list (never a shell string).</summary>
internal sealed record ProcessSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null,
    int MaxCapturedChars = ProcessSpec.DefaultMaxCapturedChars)
{
    public const int DefaultMaxCapturedChars = 16_000;
}

/// <summary>How a process ended. <see cref="ExitCode"/> is null when it never started or was killed on timeout.</summary>
internal sealed record ProcessResult(
    int? ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    string StdOut,
    string StdErr,
    bool OutputTruncated,
    string? StartError);

/// <summary>
/// Runs one child process with a hard timeout and bounded output. On timeout the whole process tree is killed.
/// Output streams are read by us (not by <c>Process</c>), and after the process exits we wait only a short grace for
/// EOF: a build server or test host that outlived its parent can keep an inherited pipe open indefinitely, and a
/// finished check must not hang on that.
/// </summary>
internal static class ProcessExecutor
{
    private static readonly TimeSpan StreamGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        Process launched;

        try
        {
            launched = Start(spec);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
        {
            return new ProcessResult(null, false, started.Elapsed, string.Empty, string.Empty, false, $"{e.GetType().Name}: {e.Message}");
        }

        using var process = launched;

        process.StandardInput.Close();

        var stdOut = new BoundedTail(spec.MaxCapturedChars);
        var stdErr = new BoundedTail(spec.MaxCapturedChars);

        using var readCancel = new CancellationTokenSource();
        var readers = Task.WhenAll(
            Drain(process.StandardOutput, stdOut, readCancel.Token),
            Drain(process.StandardError, stdErr, readCancel.Token));

        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillTreeAsync(process).ConfigureAwait(false);
            await FinishReadingAsync(readers, readCancel).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                throw;

            timedOut = true;
        }

        if (!timedOut)
            await FinishReadingAsync(readers, readCancel).ConfigureAwait(false);

        started.Stop();

        return new ProcessResult(
            timedOut ? null : process.ExitCode,
            timedOut,
            started.Elapsed,
            stdOut.ToString(),
            stdErr.ToString(),
            stdOut.Truncated || stdErr.Truncated,
            null);
    }

    private const int ErrorFileNotFound = 2;

    /// <summary>
    /// Starts the process. The native start is always the first and, for everything that is not a batch script, the only path, so a
    /// normal executable pays nothing for script support. See <see cref="WindowsScripts"/> for when and how a script is run instead.
    /// </summary>
    private static Process Start(ProcessSpec spec)
    {
        if (OperatingSystem.IsWindows() && WindowsScripts.IsBatchScript(spec.Executable))
        {
            var script = WindowsScripts.ResolveScript(spec.Executable, spec.Environment)
                         ?? throw new FileNotFoundException($"The batch script '{spec.Executable}' was not found.");

            return StartBatchScript(spec, script);
        }

        try
        {
            return StartProcess(CreateStartInfo(spec, spec.Executable, null));
        }
        catch (Win32Exception e) when (OperatingSystem.IsWindows()
                                       && e.NativeErrorCode == ErrorFileNotFound
                                       && WindowsScripts.IsBareName(spec.Executable)
                                       && WindowsScripts.FindFallback(spec.Executable, spec.Environment) is { } fallback)
        {
            // A bare toolchain name backed by .com/.bat/.cmd on PATH (flutter, npm...). Only reached when nothing started.
            return WindowsScripts.IsBatchScript(fallback)
                ? StartBatchScript(spec, fallback)
                : StartProcess(CreateStartInfo(spec, fallback, null));
        }
    }

    private static Process StartBatchScript(ProcessSpec spec, string scriptPath)
        => StartProcess(CreateStartInfo(spec, WindowsScripts.CommandInterpreter, WindowsScripts.BuildCommandLine(scriptPath, spec.Arguments)));

    private static Process StartProcess(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };

        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <param name="rawArguments">When set, the exact argument string (already validated) used instead of the argument list.</param>
    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec, string executable, string? rawArguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory,
        };

        if (rawArguments is null)
        {
            foreach (var argument in spec.Arguments)
                info.ArgumentList.Add(argument);
        }
        else
        {
            info.Arguments = rawArguments;
        }

        if (spec.Environment is not null)
        {
            foreach (var (name, value) in spec.Environment)
            {
                if (value is null)
                    info.Environment.Remove(name);
                else
                    info.Environment[name] = value;
            }
        }

        return info;
    }

    private static async Task Drain(StreamReader reader, BoundedTail sink, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return;

                sink.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // abandoned or the pipe broke: keep what was captured
        }
    }

    private static async Task FinishReadingAsync(Task readers, CancellationTokenSource readCancel)
    {
        var finished = await Task.WhenAny(readers, Task.Delay(StreamGrace)).ConfigureAwait(false);
        if (finished != readers)
            await readCancel.CancelAsync().ConfigureAwait(false);
    }

    private static async Task KillTreeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return; // already gone
        }

        using var grace = new CancellationTokenSource(KillGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // best effort
        }
    }

    /// <summary>Keeps the last <c>capacity</c> characters appended and remembers whether anything was dropped.</summary>
    private sealed class BoundedTail(int capacity)
    {
        private readonly StringBuilder _text = new();

        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<char> chunk)
        {
            lock (_text)
            {
                _text.Append(chunk);

                if (_text.Length > capacity)
                {
                    _text.Remove(0, _text.Length - capacity);
                    Truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_text)
                return _text.ToString();
        }
    }
}
