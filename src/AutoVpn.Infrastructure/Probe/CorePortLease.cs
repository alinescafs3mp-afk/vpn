using System.Globalization;
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

    public static CorePortLease Reserve() => Reserve(CorePortRole.Unknown);

    public static CorePortLease Reserve(CorePortRole role)
    {
        ValidateRole(role);
        // Choose dispersed explicit candidates instead of repeatedly relying
        // on one transport's ephemeral allocator. Its admissible range does
        // not imply that the same port is admissible for the other transport.
        // Retry local reservations only, never remote TLS or a candidate probe.
        var first = RandomNumberGenerator.GetInt32(49152, 65536);
        var step = RandomNumberGenerator.GetInt32(1, 8192) * 2 + 1;
        return Reserve(role, first, step, TryReserve);
    }

    internal delegate bool TryReservation(int requestedPort, out CorePortLease? lease,
        out CorePortAttemptFailure failure);

    // A per-call seam for deterministic exhaustion tests; no global allocator state.
    internal static CorePortLease Reserve(CorePortRole role, int first, int step, TryReservation reserve)
    {
        ValidateRole(role);
        if (first is < 49152 or >= 65536) throw new ArgumentOutOfRangeException(nameof(first));
        if (step is < 3 or >= 16384 || (step & 1) == 0) throw new ArgumentOutOfRangeException(nameof(step));
        ArgumentNullException.ThrowIfNull(reserve);
        var failures = new List<CorePortAttemptFailure>(32);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = 49152 + ((first - 49152 + attempt * step) % 16384);
            if (reserve(port, out var lease, out var failure)) return lease!;
            failures.Add(failure);
        }
        throw new CorePortReservationException(new CorePortReservationReport(role, failures));
    }

    public static bool TryReserve(int requestedPort, out CorePortLease? lease) =>
        TryReserve(requestedPort, out lease, out _);

    internal static bool TryReserve(int requestedPort, out CorePortLease? lease,
        out CorePortAttemptFailure failure)
    {
        if (requestedPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(requestedPort));
        lease = null;
        failure = default;
        Socket? udp = null;
        Socket? tcp = null;
        var phase = CorePortPhase.UdpCreateOrOptions;
        try
        {
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
            phase = CorePortPhase.UdpBind;
            udp.Bind(new IPEndPoint(IPAddress.Loopback, requestedPort));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            phase = CorePortPhase.TcpCreateOrOptions;
            tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                { ExclusiveAddressUse = true };
            phase = CorePortPhase.TcpBind;
            tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            phase = CorePortPhase.TcpListen;
            tcp.Listen(1);
            lease = new CorePortLease(udp, tcp, port);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            failure = new CorePortAttemptFailure(phase, ex.SocketErrorCode, ex.NativeErrorCode);
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

    private static void ValidateRole(CorePortRole role)
    {
        if (role is not (CorePortRole.Unknown or CorePortRole.Controller or CorePortRole.Socks))
            throw new ArgumentOutOfRangeException(nameof(role));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _tcp.Dispose(); }
        finally { _udp.Dispose(); }
    }
}

public enum CorePortRole { Unknown, Controller, Socks }
public enum CorePortPhase { UdpCreateOrOptions, UdpBind, TcpCreateOrOptions, TcpBind, TcpListen }

/// <summary>Numeric error data only: no attempted port, address, or native exception text.</summary>
public readonly record struct CorePortAttemptFailure(CorePortPhase Phase, SocketError SocketError, int NativeErrorCode);

public sealed class CorePortReservationReport
{
    public CorePortRole Role { get; }
    public IReadOnlyList<CorePortAttemptFailure> Attempts { get; }
    public string Summary { get; }

    internal CorePortReservationReport(CorePortRole role, IReadOnlyList<CorePortAttemptFailure> attempts)
    {
        if (role is not (CorePortRole.Unknown or CorePortRole.Controller or CorePortRole.Socks))
            throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(attempts);
        var count = attempts.Count;
        if (count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(attempts));
        var copy = new CorePortAttemptFailure[count];
        for (var i = 0; i < copy.Length; i++) copy[i] = attempts[i];
        Role = role;
        Attempts = Array.AsReadOnly(copy);
        var roleName = role switch { CorePortRole.Controller => "CONTROLLER", CorePortRole.Socks => "SOCKS", _ => "UNKNOWN" };
        // Identical failures occupy one entry. At most 32 numeric groups can exist;
        // neither SocketException.Message nor its inner exception is retained.
        var groups = copy.GroupBy(item => item).Select(group =>
            PhaseName(group.Key.Phase) + "/SOCKET=" + Number((int)group.Key.SocketError) +
            "/NATIVE=" + Number(group.Key.NativeErrorCode) + "/COUNT=" + Number(group.Count()));
        Summary = "ROLE=" + roleName + ";ATTEMPTS=" + Number(copy.Length) + ";FAILURES=" + string.Join(",", groups);
    }

    public override string ToString() => Summary;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string PhaseName(CorePortPhase phase) => phase switch
    {
        CorePortPhase.UdpCreateOrOptions => "UDP_CREATE_OR_OPTIONS", CorePortPhase.UdpBind => "UDP_BIND",
        CorePortPhase.TcpCreateOrOptions => "TCP_CREATE_OR_OPTIONS", CorePortPhase.TcpBind => "TCP_BIND",
        CorePortPhase.TcpListen => "TCP_LISTEN", _ => "UNKNOWN",
    };
}

public sealed class CorePortReservationException : IOException
{
    public CorePortReservationReport? Report { get; }
    public CorePortReservationException() : base("CORE_PORT_UNAVAILABLE") { }
    public CorePortReservationException(CorePortReservationReport report) : base("CORE_PORT_UNAVAILABLE")
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }
}

public sealed class ProbeCleanupException : IOException
{
    public ProbeCleanupReport? Report { get; }
    public ProbeCleanupException() : base("CORE_CLEANUP_REQUIRED") { }
    public ProbeCleanupException(ProbeCleanupReport report) : base("CORE_CLEANUP_REQUIRED:" + report.Summary)
    { Report = report; }
}
