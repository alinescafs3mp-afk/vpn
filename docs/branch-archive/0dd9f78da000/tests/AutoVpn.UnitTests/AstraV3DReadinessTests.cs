using System.Net;
using System.Net.Sockets;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3DReadinessTests
{
    [Fact]
    public async Task ListenerAloneIsNotReadyButOwnedRoutingEventuallyIs()
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await using var proxy = new Proxy(challenge.Port, failures: 3);
        Assert.True(await challenge.WaitAsync(proxy.Port, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(4, proxy.Attempts);
        Assert.Equal(1, proxy.Routed);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("forged")]
    [InlineData("wrong-version")]
    [InlineData("wrong-reserved")]
    [InlineData("stalled")]
    public async Task NonRoutingProxyDoesNotProveReadiness(string mode)
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await using var proxy = new Proxy(challenge.Port, mode: mode);
        Assert.False(await challenge.WaitAsync(proxy.Port, TimeSpan.FromMilliseconds(180), CancellationToken.None));
        Assert.Equal(0, proxy.Routed);
    }

    [Fact]
    public async Task CancellationIsNotAReadinessTimeoutOrSuccess()
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await using var proxy = new Proxy(challenge.Port, mode: "stalled");
        using var stop = new CancellationTokenSource();
        var pending = challenge.WaitAsync(proxy.Port, TimeSpan.FromSeconds(5), stop.Token);
        await proxy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, proxy.Routed);
    }

    [Fact]
    public async Task PreCancelledReadinessDoesNotOpenAConnection()
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await using var proxy = new Proxy(challenge.Port);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => challenge.WaitAsync(proxy.Port, TimeSpan.FromSeconds(1), stop.Token));
        Assert.Equal(0, proxy.Attempts);
    }

    [Fact]
    public async Task DisposalJoinsTheStalledChallengeConnectionAndReleasesItsPort()
    {
        var challenge = new ProbeLoopbackReadiness();
        var port = challenge.Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await challenge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await challenge.DisposeAsync(); // Idempotent cleanup retains the original join.
        Assert.False(await challenge.WaitAsync(port, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        var replacement = new TcpListener(IPAddress.Loopback, port);
        try { replacement.Start(); }
        finally { replacement.Stop(); }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(65536)]
    public async Task ReadinessRejectsInvalidSocksPort(int port)
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => challenge.WaitAsync(port, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(61000)]
    public async Task ReadinessRejectsInvalidDeadline(int milliseconds)
    {
        await using var challenge = new ProbeLoopbackReadiness();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => challenge.WaitAsync(10080, TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));
    }

    [Fact]
    public async Task TlsTransportCloseIsNotLabelledCertificateRejection()
    {
        await using var proxy = new Proxy(0, mode: "tls-close");
        var result = await Socks5Client.ExchangeAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port),
            new Uri("https://probe.example/generate_204"), TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.False(result.Authenticated);
        Assert.Equal("TLS_TRANSPORT_CLOSED", result.Failure);
    }

    [Theory]
    [InlineData("wrong-version")] [InlineData("wrong-reserved")]
    public async Task CandidateSocksProtocolReplyIsValidatedBeforeTls(string mode)
    {
        await using var proxy = new Proxy(0, mode: mode);
        var result = await Socks5Client.ExchangeAsync(new IPEndPoint(IPAddress.Loopback, proxy.Port),
            new Uri("https://probe.example/generate_204"), TimeSpan.FromSeconds(2), null, CancellationToken.None);
        Assert.False(result.Authenticated); Assert.Equal("SOCKS_PROTOCOL", result.Failure);
    }

    private static ProfileBuildRequest Profile(int? readiness = null) => new()
    {
        Secret = "readiness-fixture-only", ControllerPort = 11111, SocksPort = 11112,
        Tun = false, LanAccess = false, ExternalController = false, Nodes = [], ProbeReadinessPort = readiness,
    };

    [Fact]
    public void ReadinessExceptionIsOnlyTheExactLoopbackEndpoint()
    {
        var yaml = MihomoProfileGenerator.Build(Profile(11113));
        Assert.Contains("AND,((NETWORK,tcp),(DST-PORT,11113),(IP-CIDR,127.0.0.1/32,no-resolve)),DIRECT", yaml, StringComparison.Ordinal);
        Assert.Single(yaml.Split('\n'), line => line.Contains(",DIRECT", StringComparison.Ordinal));
        Assert.Contains("MATCH,REJECT", yaml, StringComparison.Ordinal);
        Assert.False(MihomoProfileGenerator.EnablesTun(yaml));
        Assert.DoesNotContain(",DIRECT", MihomoProfileGenerator.Build(Profile()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] [InlineData(53)] [InlineData(1023)] [InlineData(65536)] [InlineData(11112)]
    public void ReadinessPortMustBeDistinctAndUnprivileged(int port) =>
        Assert.Throws<InvalidOperationException>(() => MihomoProfileGenerator.Build(Profile(port)));

    [Fact]
    public void ReadinessExceptionIsForbiddenForTunLanAndMissingSocks()
    {
        foreach (var request in new[] { Profile(11113) with { Tun = true }, Profile(11113) with { LanAccess = true },
            Profile(11113) with { SocksPort = null }, Profile(11113) with { ExternalController = true },
            Profile(11113) with { SocksPort = 0 }, Profile(11113) with { SocksPort = 65536 } })
            Assert.Throws<InvalidOperationException>(() => MihomoProfileGenerator.Build(request));
    }

    private sealed class Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly int _targetPort, _failures;
        private readonly string _mode;
        private int _attempts, _routed;
        public int Port { get; }
        public int Attempts => Volatile.Read(ref _attempts);
        public int Routed => Volatile.Read(ref _routed);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Proxy(int targetPort, int failures = 0, string mode = "route")
        {
            _targetPort = targetPort; _failures = failures; _mode = mode;
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _serve = Serve();
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var attempt = Interlocked.Increment(ref _attempts); Entered.TrySetResult();
                    await using var stream = client.GetStream();
                    if (_mode == "stalled") { await Task.Delay(Timeout.Infinite, _stop.Token); continue; }
                    var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, _stop.Token);
                    await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                    var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, _stop.Token);
                    var length = prefix[3] == 1 ? 6 : -1;
                    if (length == -1)
                    {
                        var count = new byte[1]; await stream.ReadExactlyAsync(count, _stop.Token); length = count[0] + 2;
                    }
                    var address = new byte[length]; await stream.ReadExactlyAsync(address, _stop.Token);
                    await stream.WriteAsync(new byte[] { (byte)(_mode == "wrong-version" ? 4 : 5), 0,
                        (byte)(_mode == "wrong-reserved" ? 1 : 0), 1, 127, 0, 0, 1, 0, 0 }, _stop.Token);
                    if (attempt <= _failures || _mode is "closed" or "tls-close" or "wrong-version" or "wrong-reserved") continue;
                    var challenge = new byte[32]; await stream.ReadExactlyAsync(challenge, _stop.Token);
                    if (_mode == "forged") { await stream.WriteAsync(new byte[32], _stop.Token); continue; }
                    Assert.Equal(new byte[] { 127, 0, 0, 1 }, address[..4]);
                    Assert.Equal(_targetPort, (address[^2] << 8) | address[^1]);
                    using var target = new TcpClient(); await target.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token);
                    await using var output = target.GetStream();
                    await output.WriteAsync(challenge, _stop.Token);
                    var answer = new byte[32]; await output.ReadExactlyAsync(answer, _stop.Token);
                    Interlocked.Increment(ref _routed);
                    await stream.WriteAsync(answer, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                { if (_stop.IsCancellationRequested) break; }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop();
            await _serve.WaitAsync(TimeSpan.FromSeconds(2)); _stop.Dispose();
        }
    }
}
