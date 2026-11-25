using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// Starts the controller as a detached copy of this very program (<c>aw ctl serve --background</c>), so a client and the controller it started are always the same build.
/// <para>
/// Detached means the client can exit and be waited on by whoever ran it (a Herdr <c>exec</c>, a shell, a test) without that waiting also waiting for the controller:
/// the controller's standard streams are private pipes that nobody reads, and on Windows the parent's own standard handles are made non-inheritable for the moment of the
/// launch, because <c>CreateProcess</c> would otherwise hand them to the child and a caller reading this process's output would never see it end. The controller is told
/// the state root explicitly, so it serves the root the client resolved even if the environment differs.
/// </para>
/// </summary>
internal sealed class ProcessControllerLauncher : IControllerLauncher
{
    public IStartedController Launch(string stateRoot)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is unknown."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = stateRoot,
        };

        // Run through the dotnet host (a framework-dependent build, or a test) by naming the assembly; a native apphost needs nothing.
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProcessControllerLauncher).Assembly.Location);

        start.ArgumentList.Add("ctl");
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--background");
        start.Environment[CliEnvironment.HomeVariable] = stateRoot;

        Directory.CreateDirectory(stateRoot);

        using var inheritance = OperatingSystem.IsWindows() ? WindowsStandardHandles.WithholdFromChildren() : null;

        var process = Process.Start(start) ?? throw new InvalidOperationException("The controller process could not be started.");

        // The controller reads nothing and writes nothing to these; closing our ends means it can never block on them.
        process.StandardInput.Close();

        return new StartedProcess(process);
    }

    private sealed class StartedProcess(Process process) : IStartedController
    {
        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public void Dispose() => process.Dispose();
    }

    /// <summary>Makes this process's standard handles non-inheritable until disposed, then restores them.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsStandardHandles : IDisposable
    {
        private const uint HandleFlagInherit = 1;

        private readonly List<(IntPtr Handle, uint Flags)> _saved = [];

        public static WindowsStandardHandles WithholdFromChildren()
        {
            var handles = new WindowsStandardHandles();

            foreach (var id in new[] { -10, -11, -12 })
            {
                var handle = GetStdHandle(id);

                if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetHandleInformation(handle, out var flags))
                    continue;

                if ((flags & HandleFlagInherit) != 0 && SetHandleInformation(handle, HandleFlagInherit, 0))
                    handles._saved.Add((handle, flags));
            }

            return handles;
        }

        public void Dispose()
        {
            foreach (var (handle, flags) in _saved)
                SetHandleInformation(handle, HandleFlagInherit, flags & HandleFlagInherit);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    }
}
