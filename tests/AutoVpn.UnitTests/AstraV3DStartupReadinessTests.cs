using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3DStartupReadinessTests
{
    [Fact]
    public async Task Native_Round5_V3D_OpenPortPrecedesDispatchAndLocalProofWaitsForDispatch()
    {
        var binary = Environment.GetEnvironmentVariable("R5_CORE_PATH");
        var hash = Environment.GetEnvironmentVariable("R5_CORE_HASH");
        Assert.False(string.IsNullOrWhiteSpace(binary));
        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.Equal(hash!.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary!))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var heldProvider = new HeldProvider();
        await using var readiness = new LoopbackCoreReadiness();
        using var certificate = Certificate();
        await using var tls = await TlsPeer.StartAsync(certificate, "HTTP/1.1 204 No Content\r\n\r\n");
        var target = new Uri("https://127.0.0.1:" + tls.Port.ToString(CultureInfo.InvariantCulture) + "/generate_204");
        var trust = new X509Certificate2Collection(certificate);
        var directory = Directory.CreateTempSubdirectory("autovpn-v3d-startup-");
        using var reserved = CorePortLease.Reserve();
        try
        {
            var socks = reserved.Port;
            // The controlled provider must use DIRECT explicitly. Without that,
            // its own startup download follows MATCH,REJECT and never reaches us.
            var yaml = "mixed-port: 0\nsocks-port: " + socks.ToString(CultureInfo.InvariantCulture) +
                "\nallow-lan: false\nbind-address: 127.0.0.1\nmode: rule\nlog-level: warning\nipv6: false\ndns:\n  enable: false\ntun:\n  enable: false\n" +
                "rule-providers:\n  startup:\n    type: http\n    behavior: classical\n    proxy: DIRECT\n    url: 'http://127.0.0.1:" + heldProvider.Port.ToString(CultureInfo.InvariantCulture) +
                "/held.yaml'\n    path: './held.yaml'\n    interval: 3600\nrules:\n" +
                "  - AND,((NETWORK,tcp),(DST-PORT," + tls.Port.ToString(CultureInfo.InvariantCulture) +
                "),(IP-CIDR,127.0.0.1/32,no-resolve)),DIRECT\n  - RULE-SET,startup,REJECT\n  - MATCH,REJECT\n";
            yaml = readiness.AddToProfile(yaml);
            var config = Path.Combine(directory.FullName, "config.yaml");
            await File.WriteAllTextAsync(config, yaml, deadline.Token);
            var start = new ProcessStartInfo(binary!) { WorkingDirectory = directory.FullName };
            start.ArgumentList.Add("-d"); start.ArgumentList.Add(directory.FullName);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(config);
            // Hold the port through fixture preparation, releasing only for the
            // child's bind. This narrows, but does not eliminate, the bind race.
            reserved.Dispose();
            await using var worker = await ProbeWorker.StartAsync(start, socks, TimeSpan.FromSeconds(10), deadline.Token, directory.FullName);
            Assert.True(worker.Ready, "Controlled core did not acquire its listener: " + worker.Diagnostic + " " + worker.OutputTail);
            await heldProvider.Entered.Task.WaitAsync(deadline.Token);
            var pid = int.Parse(worker.WorkerId.Split(':')[0], CultureInfo.InvariantCulture);
            bool Owns() => ProbeWorker.ProcessOwnsLoopbackPort(pid, socks);
            Assert.True(Owns());
            // The legacy readiness predicate is true, but a valid TLS target
            // still fails while the local provider holds the dispatch state.
            var early = await Socks5Client.ExchangeAsync(new IPEndPoint(IPAddress.Loopback, socks), target,
                TimeSpan.FromSeconds(2), trust, deadline.Token);
            Assert.False(early.Authenticated);
            Assert.Equal("TLS_REJECTED", early.Failure);
            Assert.Equal(0, tls.Accepts);
            Assert.False(await readiness.WaitAsync(socks, Owns, TimeSpan.FromMilliseconds(250), deadline.Token));
            Assert.Equal(0, readiness.Responses);
            heldProvider.Release.TrySetResult();
            Assert.True(await readiness.WaitAsync(socks, Owns, TimeSpan.FromSeconds(5), deadline.Token), "Local dispatch proof failed: " + worker.OutputTail);
            // Same core, same certificate and same target, after dispatch ready.
            var live = await Socks5Client.ExchangeAsync(new IPEndPoint(IPAddress.Loopback, socks), target,
                TimeSpan.FromSeconds(2), trust, deadline.Token);
            Assert.True(live.Authenticated, live.Failure);
            Assert.Null(live.Failure);
            Assert.Equal(204, live.Status);
        }
        finally
        {
            reserved.Dispose();
            heldProvider.Release.TrySetResult();
            if (Directory.Exists(directory.FullName)) Directory.Delete(directory.FullName, true);
        }
    }

    [Theory]
    [InlineData("rules:\n  - MATCH,REJECT\n")]
    [InlineData("mode: rule\r\nrules:\r\n  - MATCH,REJECT\r\n")]
    public async Task RuleIsLimitedToOwnedTcpEndpointAndPreservesReject(string profile)
    {
        await using var ready = new LoopbackCoreReadiness();
        var result = ready.AddToProfile(profile);
        Assert.Contains("(NETWORK,tcp)", result, StringComparison.Ordinal);
        Assert.Contains("(DST-PORT," + ready.Port.ToString(CultureInfo.InvariantCulture) + ")", result, StringComparison.Ordinal);
        Assert.Contains("(IP-CIDR,127.0.0.1/32,no-resolve)", result, StringComparison.Ordinal);
        Assert.Contains("MATCH,REJECT", result, StringComparison.Ordinal);
        Assert.DoesNotContain("MATCH,DIRECT", result, StringComparison.Ordinal);
        Assert.DoesNotContain("external-controller", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mode: rule\n")]
    [InlineData("rules:\n  - MATCH,REJECT\nrules:\n  - MATCH,DIRECT\n")]
    [InlineData("tun:\n  enable: true\nrules:\n  - MATCH,REJECT\n")]
    public async Task UnsupportedProfileIsRejected(string profile)
    {
        await using var ready = new LoopbackCoreReadiness();
        Assert.Throws<InvalidOperationException>(() => ready.AddToProfile(profile));
    }

    [Fact]
    public async Task OwnershipFailureCannotBecomeReady()
    {
        await using var ready = new LoopbackCoreReadiness();
        Assert.False(await ready.WaitAsync(12345, () => false, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(0, ready.Responses);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToSuccess()
    {
        await using var ready = new LoopbackCoreReadiness();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready.WaitAsync(12345, () => true, TimeSpan.FromSeconds(1), canceled.Token));
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "fixture"), "fixture", X509KeyStorageFlags.Exportable);
    }

    private sealed class HeldProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port { get; }
        public HeldProvider()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var header = new byte[4096]; var used = 0;
                while (used < header.Length)
                {
                    var count = await stream.ReadAsync(header.AsMemory(used), _stop.Token);
                    if (count == 0) throw new IOException("Provider request ended early.");
                    used += count;
                    if (Encoding.ASCII.GetString(header, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                Entered.TrySetResult();
                await Release.Task.WaitAsync(_stop.Token);
                const string body = "payload: []\n";
                var response = "HTTP/1.1 200 OK\r\nContent-Type: text/yaml\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested) Entered.TrySetException(error);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); Release.TrySetResult();
            try { await _server; }
            finally { _listener.Stop(); _stop.Dispose(); }
        }
    }
}
