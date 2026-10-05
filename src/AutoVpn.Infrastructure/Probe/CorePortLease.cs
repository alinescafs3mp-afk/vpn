using System.Net;
using System.Net.Sockets;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// Holds one IPv4 loopback port in both transport namespaces. A free TCP port
/// alone does not establish UDP bindability on Windows. The lease must be
/// released immediately before the child binds; that handoff is not atomic.
/// </summary>
public sealed class CorePortLease : IDisposable
{
    private readonly Socket _udp;
    private readonly Socket _tcp;
    private int _disposed;

    private CorePortLease(Socket udp, Socket tcp, int port)
    {
        _udp = udp;
        _tcp = tcp;
        Port = port;
    }

    public int Port { get; }

    public static CorePortLease Reserve()
    {
        // Retry local bind collisions only, never a remote handshake or probe.
        for (var attempt = 0; attempt < 32; attempt++)
            if (TryReserve(0, out var lease)) return lease!;
        throw new CorePortReservationException();
    }

    public static bool TryReserve(int requestedPort, out CorePortLease? lease)
    {
        if (requestedPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(requestedPort));
        lease = null;
        Socket? udp = null;
        Socket? tcp = null;
        try
        {
            // UDP chooses its own admissible ephemeral port first. Reusing a
            // TCP-selected ephemeral port can hit a Windows UDP exclusion range.
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
            udp.Bind(new IPEndPoint(IPAddress.Loopback, requestedPort));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                { ExclusiveAddressUse = true };
            tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            tcp.Listen(1);
            lease = new CorePortLease(udp, tcp, port);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return false;
        }
        finally
        {
            if (lease is null)
            {
                tcp?.Dispose();
                udp?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _tcp.Dispose(); }
        finally { _udp.Dispose(); }
    }
}

public sealed class CorePortReservationException : IOException
{
    public CorePortReservationException() : base("CORE_PORT_UNAVAILABLE") { }
}

public sealed class ProbeCleanupException : IOException
{
    public ProbeCleanupException() : base("CORE_CLEANUP_REQUIRED") { }
}
