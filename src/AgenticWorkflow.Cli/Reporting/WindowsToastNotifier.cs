using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>Starts one hidden child process and reports its exit code. The seam that keeps tests from ever showing a toast.</summary>
internal interface IToastProcessLauncher
{
    /// <summary>Runs the program to completion, killing it when <paramref name="cancellationToken"/> is cancelled. Throws when it cannot be started.</summary>
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);
}

/// <summary>
/// Shows one Windows toast through a <c>powershell.exe</c> child (Windows PowerShell 5.1, WinRT <c>Windows.UI.Notifications</c> under the PowerShell
/// AppUserModelID): no package, no installer, no service. The script is fixed text; the title and body travel in environment variables and are XML-escaped
/// inside it, never interpolated into it. Hard-bounded (the child is killed after <see cref="DefaultTimeout"/>), and every failure (not Windows, no
/// PowerShell, a non-zero exit, a timeout) is only a short reason: it never reaches the run.
/// </summary>
internal sealed class WindowsToastNotifier(IToastProcessLauncher launcher, TimeSpan? timeout = null, Func<bool>? isWindows = null) : IHumanAttentionNotifier
{
    public const string TitleVariable = "AW_TOAST_TITLE";
    public const string BodyVariable = "AW_TOAST_BODY";
    public const string Executable = "powershell.exe";
    public const int MaxTitleLength = 100;
    public const int MaxBodyLength = 400;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Fixed. Reads the title and body from the environment; the first two non-empty body lines become the toast's text lines.</summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        function ConvertTo-XmlText([string]$Text) { [System.Security.SecurityElement]::Escape($Text) }
        $title = $env:AW_TOAST_TITLE
        $lines = @(($env:AW_TOAST_BODY -split "`n") | Where-Object { $_ -ne '' } | Select-Object -First 2)
        $xml = '<toast><visual><binding template="ToastGeneric"><text>' + (ConvertTo-XmlText $title) + '</text>'
        foreach ($line in $lines) { $xml += '<text>' + (ConvertTo-XmlText $line) + '</text>' }
        $xml += '</binding></visual></toast>'
        $document = New-Object Windows.Data.Xml.Dom.XmlDocument
        $document.LoadXml($xml)
        $toast = New-Object Windows.UI.Notifications.ToastNotification $document
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
        """;

    public async Task<string?> NotifyAsync(HumanAttentionNotice notice, CancellationToken cancellationToken)
    {
        if (!(isWindows is null ? OperatingSystem.IsWindows() : isWindows()))
            return null;

        var limit = timeout ?? DefaultTimeout;

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(limit);

        var environment = new Dictionary<string, string>
        {
            [TitleVariable] = Format.Bounded(notice.Title, MaxTitleLength),
            [BodyVariable] = BoundBody(notice.Body),
        };

        string[] arguments = ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))];

        try
        {
            var exitCode = await launcher.RunAsync(Executable, arguments, environment, bound.Token).ConfigureAwait(false);

            return exitCode == 0 ? null : $"{Executable} exited with code {exitCode}";
        }
        catch (OperationCanceledException)
        {
            // The caller giving up is not a failure to report; the bound running out is.
            return cancellationToken.IsCancellationRequested ? null : $"timed out after {Format.Duration(limit)}";
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"{Executable} could not be run";
        }
        catch (Exception e)
        {
            return $"unexpected {e.GetType().Name}";
        }
    }

    /// <summary>Each line kept single-line and printable; the whole bounded.</summary>
    private static string BoundBody(string body)
    {
        var lines = body.Split('\n').Select(line => Format.Bounded(line, MaxBodyLength)).Where(line => line.Length > 0);
        var joined = string.Join('\n', lines);

        return joined.Length <= MaxBodyLength ? joined : joined[..(MaxBodyLength - 3)] + "...";
    }
}

/// <summary>The real launcher: a hidden window, no shell, nothing inherited from the console, and the whole process tree killed when cancelled.</summary>
internal sealed class SystemToastProcessLauncher : IToastProcessLauncher
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        foreach (var (name, value) in environment)
            info.Environment[name] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{executable} did not start");

        process.StandardInput.Close();

        // Drained so a chatty child can never block on a full pipe; what it prints is not used.
        var drained = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None), process.StandardError.ReadToEndAsync(CancellationToken.None));

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        await drained.ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }
}
