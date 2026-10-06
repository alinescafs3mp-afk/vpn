using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class CorePortDiagnosticTests
{
    [Theory]
    [InlineData(CorePortRole.Unknown, "UNKNOWN")]
    [InlineData(CorePortRole.Controller, "CONTROLLER")]
    [InlineData(CorePortRole.Socks, "SOCKS")]
    public void ExhaustionKeepsThe32CandidateBudgetAndAllMixedFailures(CorePortRole role, string roleName)
    {
        var ports = new List<int>();
        bool Reject(int port, out CorePortLease? lease, out CorePortAttemptFailure failure)
        {
            ports.Add(port); lease = null;
            failure = ports.Count <= 16 ? new(CorePortPhase.UdpBind, SocketError.AddressAlreadyInUse, 98) :
                ports.Count <= 24 ? new(CorePortPhase.TcpBind, SocketError.AccessDenied, 10013) :
                new(CorePortPhase.TcpListen, SocketError.AddressAlreadyInUse, 10048);
            return false;
        }

        var error = Assert.Throws<CorePortReservationException>(() =>
            CorePortLease.Reserve(role, 65535, 16383, Reject));
        Assert.Equal("CORE_PORT_UNAVAILABLE", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(Enumerable.Range(65504, 32).Reverse(), ports);
        Assert.Equal(32, ports.Distinct().Count());
        Assert.All(ports, port => Assert.InRange(port, 49152, 65535));
        var report = Assert.IsType<CorePortReservationReport>(error.Report);
        Assert.Equal(role, report.Role);
        Assert.Equal(32, report.Attempts.Count);
        Assert.Equal(CorePortPhase.UdpBind, report.Attempts[0].Phase);
        Assert.Equal(CorePortPhase.TcpBind, report.Attempts[16].Phase);
        Assert.Equal(CorePortPhase.TcpListen, report.Attempts[31].Phase);
        Assert.Equal("ROLE=" + roleName + ";ATTEMPTS=32;FAILURES=" +
            "UDP_BIND/SOCKET=10048/NATIVE=98/COUNT=16," +
            "TCP_BIND/SOCKET=10013/NATIVE=10013/COUNT=8," +
            "TCP_LISTEN/SOCKET=10048/NATIVE=10048/COUNT=8", report.Summary);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UndefinedRolesAreRejectedBeforeAnyAttempt(int value)
    {
        var calls = 0;
        bool Reject(int port, out CorePortLease? lease, out CorePortAttemptFailure failure)
        { calls++; lease = null; failure = default; return false; }
        var role = (CorePortRole)value;
        Assert.Throws<ArgumentOutOfRangeException>(() => CorePortLease.Reserve(role));
        Assert.Throws<ArgumentOutOfRangeException>(() => CorePortLease.Reserve(role, 49152, 3, Reject));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ReportRetainsAnImmutableAttemptSnapshot()
    {
        var original = new CorePortAttemptFailure(CorePortPhase.UdpBind, SocketError.AccessDenied, 13);
        var source = new[] { original };
        var report = new CorePortReservationReport(CorePortRole.Controller, source);
        var summary = report.Summary;
        source[0] = new(CorePortPhase.TcpListen, SocketError.AddressAlreadyInUse, 10048);
        Assert.Equal(original, Assert.Single(report.Attempts));
        Assert.Equal(summary, report.Summary);
        var list = Assert.IsAssignableFrom<IList<CorePortAttemptFailure>>(report.Attempts);
        Assert.Throws<NotSupportedException>(() => list[0] = default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void UnboundedOrEmptyReportsAreRejectedBeforeReadingAnyItems(int count)
    {
        var source = new IndexedFailures(count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorePortReservationReport(CorePortRole.Socks, source));
        Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData(CorePortPhase.UdpCreateOrOptions, "UDP_CREATE_OR_OPTIONS")]
    [InlineData(CorePortPhase.UdpBind, "UDP_BIND")]
    [InlineData(CorePortPhase.TcpCreateOrOptions, "TCP_CREATE_OR_OPTIONS")]
    [InlineData(CorePortPhase.TcpBind, "TCP_BIND")]
    [InlineData(CorePortPhase.TcpListen, "TCP_LISTEN")]
    public void EachStageHasAClosedDiagnosticLabel(CorePortPhase phase, string label)
    {
        var report = new CorePortReservationReport(CorePortRole.Socks,
            new[] { new CorePortAttemptFailure(phase, SocketError.AccessDenied, 10013) });
        Assert.Equal("ROLE=SOCKS;ATTEMPTS=1;FAILURES=" + label +
            "/SOCKET=10013/NATIVE=10013/COUNT=1", report.Summary);
    }

    [Fact]
    public void ExtremeNumericValuesRemainBoundedAndUseOnlyClosedText()
    {
        var attempts = Enumerable.Range(0, 32).Select(index => new CorePortAttemptFailure(
            (CorePortPhase)(1000 + index), (SocketError)int.MinValue, int.MinValue + index)).ToArray();
        var report = new CorePortReservationReport(CorePortRole.Unknown, attempts);
        Assert.InRange(report.Summary.Length, 1, 4096);
        Assert.Matches("^[A-Z0-9;=,/_-]+$", report.Summary);
        Assert.Equal(report.Summary, report.ToString());
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Address", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Message", json, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticNumbersCannotImportTextFromTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        custom.NumberFormat.NegativeSign = "SYNTHETIC_PRIVATE";
        try
        {
            CultureInfo.CurrentCulture = custom;
            var report = new CorePortReservationReport(CorePortRole.Socks,
                new[] { new CorePortAttemptFailure(CorePortPhase.TcpBind, (SocketError)(-1), -123) });
            Assert.Contains("/SOCKET=-1/NATIVE=-123/COUNT=1", report.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE", report.Summary, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void HeldUdpSocketRecordsTheActualNativeBindError()
    {
        using var held = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ExclusiveAddressUse = true };
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = Assert.IsType<IPEndPoint>(held.LocalEndPoint);
        using var conflicting = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            { ExclusiveAddressUse = true };
        var native = Assert.Throws<SocketException>(() => conflicting.Bind(endpoint));
        var acquired = CorePortLease.TryReserve(endpoint.Port, out var lease, out var failure);
        using (lease) Assert.False(acquired);
        Assert.Null(lease);
        Assert.Equal(CorePortPhase.UdpBind, failure.Phase);
        Assert.Equal(native.SocketErrorCode, failure.SocketError);
        Assert.Equal(native.NativeErrorCode, failure.NativeErrorCode);
    }

    [Fact]
    public void LegacyExceptionKeepsItsExactReasonAndHasNoFabricatedReport()
    {
        var error = new CorePortReservationException();
        Assert.Equal("CORE_PORT_UNAVAILABLE", error.Message);
        Assert.Null(error.Report);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void DistinctNativeErrorsAreNotMergedIntoOneGroup()
    {
        var report = new CorePortReservationReport(CorePortRole.Controller, new[]
        {
            new CorePortAttemptFailure(CorePortPhase.TcpBind, SocketError.AddressAlreadyInUse, 98),
            new CorePortAttemptFailure(CorePortPhase.TcpBind, SocketError.AddressAlreadyInUse, 10048),
            new CorePortAttemptFailure(CorePortPhase.TcpBind, SocketError.AddressAlreadyInUse, 98),
        });
        Assert.Equal("ROLE=CONTROLLER;ATTEMPTS=3;FAILURES=" +
            "TCP_BIND/SOCKET=10048/NATIVE=98/COUNT=2," +
            "TCP_BIND/SOCKET=10048/NATIVE=10048/COUNT=1", report.Summary);
    }

    [Fact]
    public void AttemptCopyUsesBoundedIndexesAndNeverRunsAnUntrustedEnumerator()
    {
        var source = new IndexedFailures(32);
        var report = new CorePortReservationReport(CorePortRole.Socks, source);
        Assert.Equal(32, source.Reads);
        Assert.Equal(32, report.Attempts.Count);
    }

    private sealed class IndexedFailures(int count) : IReadOnlyList<CorePortAttemptFailure>
    {
        public int Count => count;
        public int Reads { get; private set; }
        public CorePortAttemptFailure this[int index]
        {
            get { Reads++; return new(CorePortPhase.UdpBind, SocketError.AddressAlreadyInUse, index); }
        }
        public IEnumerator<CorePortAttemptFailure> GetEnumerator() => throw new InvalidOperationException("SYNTHETIC_PRIVATE");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
