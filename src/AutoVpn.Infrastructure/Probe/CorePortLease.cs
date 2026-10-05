using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// Holds an exclusive IPv4 loopback port in both transport namespaces.
/// Release immediately before the child binds. That handoff is not atomic.
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
        // Choose dispersed explicit candidates instead of repeatedly relying
        // on one transport's ephemeral allocator. Its admissible range does
        // not imply that the same port is admissible for the other transport.
        // Retry local reservations only, never remote TLS or a candidate probe.
        var first = RandomNumberGenerator.GetInt32(49152, 65536);
        var step = RandomNumberGenerator.GetInt32(1, 8192) * 2 + 1;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = 49152 + ((first - 49152 + attempt * step) % 16384);
            if (TryReserve(port, out var lease)) return lease!;
        }
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
    public ProbeCleanupReport? Report { get; }
    public ProbeCleanupException() : base("CORE_CLEANUP_REQUIRED") { }
    public ProbeCleanupException(ProbeCleanupReport report) : base("CORE_CLEANUP_REQUIRED:" + report.Summary)
    { Report = report; }
}
