using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3EResourceTests
{
    [Fact]
    public void TcpAndUdpAreReservedTogetherAndReleased()
    {
        var lease = CorePortLease.Reserve();
        try
        {
            Assert.InRange(lease.Port, 1, 65535);
            AssertHeld(lease.Port, ProtocolType.Tcp);
            AssertHeld(lease.Port, ProtocolType.Udp);
        }
        finally { lease.Dispose(); }
        using var tcp = Bind(lease.Port, ProtocolType.Tcp);
        using var udp = Bind(lease.Port, ProtocolType.Udp);
    }

    [Fact]
    public void ConcurrentLeasesNeverShareTheirPort()
    {
        var leases = new List<CorePortLease>();
        try
        {
            for (var i = 0; i < 16; i++) leases.Add(CorePortLease.Reserve());
            Assert.Equal(16, leases.Select(x => x.Port).Distinct().Count());
            foreach (var lease in leases)
            {
                AssertHeld(lease.Port, ProtocolType.Tcp);
                AssertHeld(lease.Port, ProtocolType.Udp);
            }
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public void RepeatedDisposeDoesNotCloseTheNextOwnersSockets()
    {
        var lease = CorePortLease.Reserve();
        lease.Dispose();
        using var tcp = Bind(lease.Port, ProtocolType.Tcp);
        using var udp = Bind(lease.Port, ProtocolType.Udp);
        lease.Dispose();
        AssertHeld(lease.Port, ProtocolType.Tcp);
        AssertHeld(lease.Port, ProtocolType.Udp);
    }

    [Fact]
    public void TcpConflictDoesNotLeaveTheUdpHalfBound()
    {
        var initial = CorePortLease.Reserve(); initial.Dispose();
        using var tcp = Bind(initial.Port, ProtocolType.Tcp);
        var acquired = CorePortLease.TryReserve(initial.Port, out var failed);
        using (failed) Assert.False(acquired);
        Assert.Null(failed);
        using var udp = Bind(initial.Port, ProtocolType.Udp);
    }

    [Fact]
    public void UdpConflictDoesNotOccupyTcp()
    {
        var initial = CorePortLease.Reserve(); initial.Dispose();
        using var udp = Bind(initial.Port, ProtocolType.Udp);
        var acquired = CorePortLease.TryReserve(initial.Port, out var failed);
        using (failed) Assert.False(acquired);
        Assert.Null(failed);
        using var tcp = Bind(initial.Port, ProtocolType.Tcp);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortsAreRejected(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CorePortLease.TryReserve(port, out _));

    [Fact]
    public async Task CancellationBeforeSpawnDoesNotRunTheFixture()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3e-canceled-");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await using var worker = await ProbeWorker.StartAsync(Fixture(directory.FullName, "mark"), null,
            TimeSpan.FromSeconds(2), canceled.Token, null);
        try
        {
            Assert.False(worker.Ready);
            Assert.Equal("", worker.WorkerId);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "spawned.txt")));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Native_Round5_V3E_WorkerStopsBeforeRemovingItsNativeCache()
    {
        var binary = Environment.GetEnvironmentVariable("R5_CORE_PATH");
        Assert.False(string.IsNullOrWhiteSpace(binary));
        var directory = Directory.CreateTempSubdirectory("autovpn-v3e-native-");
        using var lease = CorePortLease.Reserve();
        var config = Path.Combine(directory.FullName, "config.yaml");
        await File.WriteAllTextAsync(config, "socks-port: " + lease.Port.ToString(CultureInfo.InvariantCulture) +
            "\nbind-address: '127.0.0.1'\nallow-lan: false\nmode: rule\nlog-level: warning\ntun:\n  enable: false\ndns:\n  enable: false\nrules:\n  - MATCH,REJECT\n");
        var start = new ProcessStartInfo(binary!) { WorkingDirectory = directory.FullName, CreateNoWindow = true };
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(directory.FullName);
        start.ArgumentList.Add("-f"); start.ArgumentList.Add(config);
        lease.Dispose();
        await using var worker = await ProbeWorker.StartAsync(start, lease.Port, TimeSpan.FromSeconds(10), CancellationToken.None, directory.FullName);
        Assert.True(worker.Ready, worker.OutputTail);
        using var child = Process.GetProcessById(int.Parse(worker.WorkerId.Split(':')[0], CultureInfo.InvariantCulture));
        _ = child.Handle;
        await StopAsync(worker);
        Assert.True(child.HasExited);
        Assert.True(worker.DirectoryRemoved);
        Assert.False(Directory.Exists(directory.FullName));
    }

    [WindowsHandleFact]
    public async Task LockedCleanupIsReportedAndCanBeRetriedAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows fixture was not skipped.");
        var directory = Directory.CreateTempSubdirectory("autovpn-v3e-locked-");
        var held = new FileStream(Path.Combine(directory.FullName, "cache.db"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var worker = await ProbeWorker.StartAsync(Fixture(directory.FullName, "sleep"), null, TimeSpan.FromSeconds(2), CancellationToken.None, directory.FullName);
        try
        {
            Assert.True(worker.Ready);
            using var child = Process.GetProcessById(int.Parse(worker.WorkerId.Split(':')[0], CultureInfo.InvariantCulture));
            _ = child.Handle;
            var nativeFailure = Assert.Throws<IOException>(() => File.Delete(held.Name));
            var error = await Assert.ThrowsAsync<ProbeCleanupException>(() => worker.DisposeAsync().AsTask());
            var report = Assert.IsType<ProbeCleanupReport>(error.Report);
            Assert.Equal("DIRECTORY_CLEANUP_FAILED", report.Phase);
            Assert.Equal("IO", report.ExceptionKind);
            Assert.Equal(nativeFailure.HResult, report.HResult);
            Assert.DoesNotContain(directory.FullName, error.Message, StringComparison.Ordinal);
            Assert.True(child.HasExited);
            Assert.False(worker.DirectoryRemoved);
            held.Dispose();
            await StopAsync(worker);
            Assert.True(worker.DirectoryRemoved);
            Assert.False(Directory.Exists(directory.FullName));
        }
        finally { held.Dispose(); await StopAsync(worker); }
    }

    [Fact]
    public async Task ConcurrentCleanupOfOneWorkerIsIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3e-concurrent-");
        await using var worker = await ProbeWorker.StartAsync(Fixture(directory.FullName, "sleep"), null, TimeSpan.FromSeconds(2), CancellationToken.None, directory.FullName);
        Assert.True(worker.Ready);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StopAsync(worker)));
        Assert.True(worker.DirectoryRemoved);
        Assert.False(Directory.Exists(directory.FullName));
    }

    [Fact]
    public async Task FailedSpawnCleansItsOwnedDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3e-failed-");
        await using var worker = await ProbeWorker.StartAsync(new ProcessStartInfo(Path.Combine(directory.FullName, "missing-executable")),
            null, TimeSpan.FromSeconds(2), CancellationToken.None, directory.FullName);
        Assert.False(worker.Ready); Assert.True(worker.DirectoryRemoved);
        Assert.False(Directory.Exists(directory.FullName));
    }

    [Fact]
    public async Task CleanupDoesNotDeleteAnotherWorkersDirectory()
    {
        var first = Directory.CreateTempSubdirectory("autovpn-v3e-first-");
        var second = Directory.CreateTempSubdirectory("autovpn-v3e-second-");
        await using var a = await ProbeWorker.StartAsync(Fixture(first.FullName, "sleep"), null, TimeSpan.FromSeconds(2), CancellationToken.None, first.FullName);
        await using var b = await ProbeWorker.StartAsync(Fixture(second.FullName, "sleep"), null, TimeSpan.FromSeconds(2), CancellationToken.None, second.FullName);
        Assert.True(a.Ready); Assert.True(b.Ready);
        using var child = Process.GetProcessById(int.Parse(b.WorkerId.Split(':')[0], CultureInfo.InvariantCulture));
        _ = child.Handle;
        await StopAsync(a);
        Assert.False(child.HasExited);
        Assert.True(Directory.Exists(second.FullName));
        await StopAsync(b);
        Assert.False(Directory.Exists(second.FullName));
    }

    private static async Task StopAsync(ProbeWorker worker)
    {
        try { await worker.DisposeAsync(); }
        catch (ProbeCleanupException ex)
        {
            throw new InvalidOperationException("Owned fixture cleanup failed at " + worker.Diagnostic, ex);
        }
    }

    private static ProcessStartInfo Fixture(string directory, string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add(mode);
        return start;
    }

    private static Socket Bind(int port, ProtocolType protocol)
    {
        var socket = new Socket(AddressFamily.InterNetwork, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol)
            { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            // Model a real TCP server, not only an unconnected bound socket.
            // The production lease already calls Listen before claiming success.
            if (protocol == ProtocolType.Tcp) socket.Listen(1);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static void AssertHeld(int port, ProtocolType protocol) =>
        Assert.Throws<SocketException>(() => { using var unexpected = Bind(port, protocol); });
}
