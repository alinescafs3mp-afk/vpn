using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AutoVpn.Infrastructure.Broker;

public readonly record struct PeerCheck(bool Accepted, bool Verified, string Identity, int SessionId = 0);

/// <summary>
/// On Linux, the connected socket must belong to the same effective uid.
/// On Windows, impersonation identifies the actual SID and the OS pipe session.
/// A network logon is rejected. Installation supplies the authorized owner SID.
/// </summary>
public static class PipePeer
{
    public static PeerCheck Inspect(NamedPipeServerStream server, string? allowedWindowsSid = null)
    {
        if (OperatingSystem.IsWindows()) return InspectWindows(server, allowedWindowsSid);
        if (!OperatingSystem.IsLinux())
        {
            return new PeerCheck(false, false, "");
        }

        try
        {
            var handle = server.SafePipeHandle;
            if (handle.IsInvalid)
            {
                return new PeerCheck(false, false, "");
            }

            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var fd = (int)handle.DangerousGetHandle();
                var buffer = new byte[12];
                var length = buffer.Length;
                if (getsockopt(fd, 1, 17, buffer, ref length) != 0 || length < 8)
                {
                    return new PeerCheck(false, false, "");
                }

                var uid = BitConverter.ToUInt32(buffer, 4);
                if (uid != geteuid())
                {
                    return new PeerCheck(false, true, "uid:" + uid.ToString(CultureInfo.InvariantCulture));
                }

                return new PeerCheck(true, true, "uid:" + uid.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or EntryPointNotFoundException)
        {
            return new PeerCheck(false, false, "");
        }
    }

    [SupportedOSPlatform("windows")]
    private static PeerCheck InspectWindows(NamedPipeServerStream server, string? allowedSid)
    {
        try
        {
            var identity = "";
            var remote = false;
            server.RunAsClient(() =>
            {
                using var token = WindowsIdentity.GetCurrent(ifImpersonating: true);
                identity = token?.User?.Value ?? "";
                remote = token?.Groups?.Any(g => g.Value == "S-1-5-2") == true;
            });
            if (remote || string.IsNullOrEmpty(identity) ||
                !GetNamedPipeClientSessionId(server.SafePipeHandle, out var session)) return new(false, false, "");
            using var self = WindowsIdentity.GetCurrent();
            var owner = allowedSid ?? self.User?.Value;
            return new(identity == owner, true, identity, checked((int)session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return new(false, false, "");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint session);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int fd, int level, int optname, byte[] optval, ref int optlen);

    [DllImport("libc")]
    private static extern uint geteuid();
}
