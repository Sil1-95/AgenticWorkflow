using System.Runtime.InteropServices;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// Whether this controller process can outlive whatever started it, which decides whether it may own work. A controller that is killed together with its parent would end the
/// agent work it supervises (recoverable, but never what anyone wants), so it refuses to take ownership instead of silently being fragile.
/// <para>
/// On Windows the danger is a job object that kills its members when it closes (Herdr's <c>hc exec</c> starts its children that way; the Herdr app's own <c>exec</c> does not).
/// The process asks whether it is in such a job. On Unix a controller started in the background makes itself a session leader, so the hang-up of the terminal that started it
/// cannot reach it; nothing else on Unix ties a child to its parent.
/// </para>
/// </summary>
internal static class ControllerDetachment
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>False only when the process is positively known to be in a job that ends its members when it closes. An unknown answer counts as durable: the check must not turn a working controller away.</summary>
    public static bool IsDurable()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();

            if (!IsProcessInJob(self.Handle, IntPtr.Zero, out var inJob) || !inJob)
                return true;

            var information = new ExtendedLimitInformation();
            var size = Marshal.SizeOf<ExtendedLimitInformation>();

            // A null job handle asks about the job the calling process is in.
            if (!QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, ref information, size, out _))
                return true;

            return (information.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>Makes the process the leader of a new session, so a terminal's hang-up does not reach it. A no-op on Windows, and when it already is one.</summary>
    public static void StartNewSession()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            _ = SetSessionId();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not available on this platform; the signal handling still ignores the hang-up.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, int length, out int returned);

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
