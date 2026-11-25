using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>Who is on the other end of a Unix-domain socket, as the kernel states it.</summary>
internal interface IUnixPeerVerifier
{
    /// <summary>True when the peer's effective user is the current user, false when it is anyone else, null when the platform cannot say (the caller then fails closed).</summary>
    bool? IsCurrentUser(Socket connected);
}

/// <summary>
/// Reads the peer's user id from the kernel (<c>SO_PEERCRED</c> on Linux, <c>LOCAL_PEERCRED</c> on macOS) and compares it with this process's effective user. This is the
/// second layer behind the private directory: even a peer that somehow reached the socket is refused unless the kernel says it is this user. On any other platform it cannot
/// tell, and says so (null) rather than guessing.
/// </summary>
internal sealed class UnixPeerCredentials : IUnixPeerVerifier
{
    // Linux: SOL_SOCKET = 1, SO_PEERCRED = 17, struct ucred { pid_t pid; uid_t uid; gid_t gid; } (the user id is the second 32-bit field).
    private const int LinuxSolSocket = 1;
    private const int LinuxSoPeerCred = 17;

    // macOS: SOL_LOCAL = 0, LOCAL_PEERCRED = 1, struct xucred { u_int cr_version; uid_t cr_uid; ... } (the user id is the second 32-bit field).
    private const int MacSolLocal = 0;
    private const int MacLocalPeerCred = 1;

    public bool? IsCurrentUser(Socket connected)
    {
        try
        {
            var buffer = new byte[128];
            int written;

            if (OperatingSystem.IsLinux())
                written = connected.GetRawSocketOption(LinuxSolSocket, LinuxSoPeerCred, buffer);
            else if (OperatingSystem.IsMacOS())
                written = connected.GetRawSocketOption(MacSolLocal, MacLocalPeerCred, buffer);
            else
                return null;

            return written < 8 ? null : BitConverter.ToUInt32(buffer, 4) == geteuid();
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The effective user id of this process. Only meaningful on Linux and macOS.</summary>
    public static uint EffectiveUserId() => geteuid();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}
