using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>Where a state root's controller keeps its files. All of them live under the state root, next to the runs it serves, never in a repository.</summary>
internal sealed class ControllerPaths(string stateRoot)
{
    public string Directory { get; } = Path.Combine(stateRoot, "controller");

    /// <summary>Held, exclusively, for as long as a controller serves this state root: the single-instance authority.</summary>
    public string InstanceLock => Path.Combine(Directory, "controller.lock");

    /// <summary>Held by the one client that is starting a controller, so simultaneous clients start one rather than several.</summary>
    public string SpawnGate => Path.Combine(Directory, "starting.lock");

    public string Log => Path.Combine(Directory, "controller.log");
}

/// <summary>
/// Chooses the transport for a state root. There is exactly one controller per (operating-system user, state root): the endpoint's name is derived from both, so two
/// users never share one and two state roots (a test's, a second environment) never collide. The name is not a secret and carries no authority; access control is the
/// transport's, by the operating system.
/// </summary>
internal static class ControllerEndpoints
{
    public static IControllerTransport For(string stateRoot, Func<string, string?> environment)
    {
        var name = EndpointName(stateRoot);

        if (OperatingSystem.IsWindows())
            return new NamedPipeControllerTransport($"aw-controller-{name}");

        return new UnixSocketControllerTransport(RuntimeDirectory(environment), $"{name}.sock");
    }

    /// <summary>A short, stable, filesystem- and pipe-safe name for this user and state root.</summary>
    internal static string EndpointName(string stateRoot)
    {
        var identity = $"{UserIdentity()}\n{Path.GetFullPath(stateRoot).TrimEnd('\\', '/')}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)), 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// The per-user private directory for the socket: <c>$XDG_RUNTIME_DIR/agenticworkflow</c> where the platform provides one (a tmpfs the system makes private to the user),
    /// otherwise <c>aw-&lt;uid&gt;</c> under the temporary directory. Either way the transport sets it to 0700 and refuses it if that cannot be done.
    /// </summary>
    private static string RuntimeDirectory(Func<string, string?> environment)
    {
        var runtime = environment("XDG_RUNTIME_DIR");

        if (!string.IsNullOrWhiteSpace(runtime) && Path.IsPathFullyQualified(runtime))
            return Path.Combine(runtime, "agenticworkflow");

        return Path.Combine(Path.GetTempPath(), $"aw-{UnixUserId()}");
    }

    private static string UserIdentity()
        => OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : $"uid:{UnixUserId()}";

    private static uint UnixUserId() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 0 : UnixPeerCredentials.EffectiveUserId();
}

/// <summary>
/// An exclusive lock on a file, held for the life of the object and released by the operating system if the process dies. It is how a controller proves it is the only
/// one (and so that any endpoint left behind is stale), and how a client takes the start-up gate.
/// </summary>
internal sealed class ControllerFileLock : IDisposable
{
    private readonly FileStream _stream;

    private ControllerFileLock(FileStream stream) => _stream = stream;

    /// <summary>The lock, or null when another process (or another holder in this one) has it.</summary>
    public static ControllerFileLock? TryAcquire(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            return new ControllerFileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// The controller's own log: one line per event, never a request body, a task's content, a conversation reference or a secret. It is bounded (it is rotated once, at
/// 512 KiB), private to the user where the platform has file modes, and a failure to write it never affects the controller.
/// </summary>
internal sealed class ControllerLog(string path, TextWriter? echo = null)
{
    private const long MaxBytes = 512 * 1024;

    private readonly object _gate = new();

    public void Write(string line)
    {
        var text = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {line}";

        lock (_gate)
        {
            try
            {
                echo?.WriteLine(text);

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var created = !File.Exists(path);

                if (!created && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                File.AppendAllText(path, text + "\n");

                if (created && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The log is a diagnostic aid; the controller does not depend on it.
            }
        }
    }
}
