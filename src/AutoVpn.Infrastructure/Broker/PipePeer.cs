using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AutoVpn.Infrastructure.Broker;

public readonly record struct PeerCheck(bool Accepted, bool Verified, string Identity);

/// <summary>
/// On Linux, the connected socket must belong to the same effective uid.
/// Windows pipe ACL remains NOT_RUN; this check does not pretend otherwise.
/// </summary>
public static class PipePeer
{
    public static PeerCheck Inspect(NamedPipeServerStream server)
    {
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

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int fd, int level, int optname, byte[] optval, ref int optlen);

    [DllImport("libc")]
    private static extern uint geteuid();
}
