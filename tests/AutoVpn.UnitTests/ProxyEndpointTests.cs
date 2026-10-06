using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class ProxyEndpointTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    private static NodeSemantics Node(string host = "vpn.example.net") => new()
    {
        Protocol = ProtocolKind.Vless, Host = host, Port = 443,
        UserId = "11111111-1111-4111-8111-111111111111", Security = "tls",
        Encryption = "none", Transport = "tcp",
    };
    private static ProxyEndpointResolver Resolver(params string[] addresses) =>
        new((_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    [InlineData("223.255.255.255")]
    [InlineData("2001:200::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public async Task AllowedLiteralNeverQueriesDns(string address)
    {
        var resolver = new ProxyEndpointResolver((_, _) => throw new Exception("NO_DNS"));
        var result = await resolver.ResolveAsync(Node(address), Budget, default);
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(IPAddress.TryParse(result.ExecutionNode!.Host, out _));
        Assert.Equal(0, resolver.InFlight);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.2")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.0")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:1ff:ffff::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("3fff::1")]
    [InlineData("3fff:fff:ffff::1")]
    [InlineData("5f00::1")]
    [InlineData("fc00::1")]
    [InlineData("fdff::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    public async Task ForbiddenLiteralNeverQueriesDns(string address)
    {
        var calls = 0;
        var resolver = new ProxyEndpointResolver((_, _) => { calls++; throw new Exception("NO_DNS"); });
        var result = await resolver.ResolveAsync(Node(address), Budget, default);
        Assert.False(result.Succeeded); Assert.Null(result.ExecutionNode);
        Assert.Equal(ReasonCodes.NonPublicEndpoint, result.ReasonCode); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("vpn.example.net\n")]
    [InlineData("https://vpn.example.net")]
    [InlineData("user@vpn.example.net")]
    [InlineData("vpn.example.net:443")]
    [InlineData("vpn..example.net")]
    [InlineData("vpn.example.net..")]
    [InlineData("-vpn.example.net")]
    [InlineData("vpn-.example.net")]
    [InlineData("vpn_name.example.net")]
    [InlineData("[8.8.8.8]")]
    [InlineData("[2606:4700::1111")]
    [InlineData("2606:4700::1111]")]
    [InlineData("fe80::1%3")]
    [InlineData("[2606:4700::1111%3]")]
    [InlineData("localhost")]
    [InlineData("a.localhost")]
    [InlineData("router.local")]
    [InlineData("service.internal")]
    [InlineData("router.home.arpa")]
    [InlineData("metadata")]
    [InlineData("metadata.google.com.")]
    [InlineData("metadata.google.internal")]
    public async Task InvalidOrLocalNameIsRefusedWithoutDns(string? host)
    {
        var calls = 0;
        var resolver = new ProxyEndpointResolver((_, _) => { calls++; throw new Exception("NO_DNS"); });
        var result = await resolver.ResolveAsync(Node() with { Host = host! }, Budget, default);
        Assert.Equal("ENDPOINT_NAME_INVALID", result.ReasonCode); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LongNameAndLabelAreRefused()
    {
        Assert.Equal("ENDPOINT_NAME_INVALID", (await Resolver("8.8.8.8").ResolveAsync(
            Node(new string('a', 254)), Budget, default)).ReasonCode);
        Assert.Equal("ENDPOINT_NAME_INVALID", (await Resolver("8.8.8.8").ResolveAsync(
            Node(new string('a', 64) + ".net"), Budget, default)).ReasonCode);
    }

    [Theory]
    [InlineData("vpn.EXAMPLE.net.", "vpn.example.net.")]
    [InlineData("bücher.example.net", "xn--bcher-kva.example.net.")]
    public async Task DnsLookupUsesAbsoluteCanonicalName(string host, string expected)
    {
        string? seen = null; var calls = 0;
        var resolver = new ProxyEndpointResolver((h, _) =>
        { seen = h; calls++; return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }); });
        var original = Node(host);
        var digest = CanonicalIdentity.Digest(original);
        var result = await resolver.ResolveAsync(original, Budget, default);
        Assert.True(result.Succeeded); Assert.Equal(expected, seen); Assert.Equal(1, calls);
        Assert.Equal("8.8.8.8", result.ExecutionNode!.Host);
        Assert.Equal(expected.TrimEnd('.'), result.ExecutionNode.Sni);
        Assert.Equal(host, original.Host); Assert.Equal(digest, CanonicalIdentity.Digest(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAnswerSetIsRejectedRegardlessOfOrder(bool reverse)
    {
        var answers = new[] { "8.8.8.8", "127.0.0.1" };
        if (reverse) Array.Reverse(answers);
        var result = await Resolver(answers).ResolveAsync(Node(), Budget, default);
        Assert.False(result.Succeeded); Assert.Null(result.ExecutionNode);
        Assert.Equal(ReasonCodes.NonPublicEndpoint, result.ReasonCode);
    }

    [Fact]
    public async Task ScopedDnsAnswerIsNotAnAllowedGlobalAddress()
    {
        var address = IPAddress.Parse("2606:4700::1111"); address.ScopeId = 7;
        var resolver = new ProxyEndpointResolver((_, _) => Task.FromResult(new[] { address }));
        Assert.Equal(ReasonCodes.NonPublicEndpoint, (await resolver.ResolveAsync(Node(), Budget, default)).ReasonCode);
    }

    [Fact]
    public async Task EmptyOversizedAndNullAnswersFailClosed()
    {
        Assert.Equal("ENDPOINT_DNS_EMPTY", (await Resolver().ResolveAsync(Node(), Budget, default)).ReasonCode);
        Assert.Equal("ENDPOINT_DNS_LIMIT", (await Resolver(Enumerable.Repeat("8.8.8.8", 33).ToArray())
            .ResolveAsync(Node(), Budget, default)).ReasonCode);
        var resolver = new ProxyEndpointResolver((_, _) => Task.FromResult(new IPAddress[] { null! }));
        Assert.Equal(ReasonCodes.NonPublicEndpoint, (await resolver.ResolveAsync(Node(), Budget, default)).ReasonCode);
    }

    [Fact]
    public async Task ResultPinsFirstAnswerWithoutRetryOrCache()
    {
        var calls = 0;
        var resolver = new ProxyEndpointResolver((_, _) => Task.FromResult(new[]
            { IPAddress.Parse(++calls == 1 ? "8.8.8.8" : "1.1.1.1"), IPAddress.Parse("9.9.9.9") }));
        var first = await resolver.ResolveAsync(Node(), Budget, default);
        var second = await resolver.ResolveAsync(Node(), Budget, default);
        Assert.Equal("8.8.8.8", first.ExecutionNode!.Host);
        Assert.Equal("1.1.1.1", second.ExecutionNode!.Host); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    [InlineData(ProtocolKind.Trojan)]
    [InlineData(ProtocolKind.Shadowsocks)]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    public async Task NumericBindingPreservesTlsNameAndCredentials(ProtocolKind protocol)
    {
        var original = Node() with { Protocol = protocol, Password = "SYNTHETIC_PRIVATE", Alpn = new[] { "h2" },
            Security = protocol == ProtocolKind.Shadowsocks ? "aead" : "tls" };
        var result = await Resolver("8.8.8.8").ResolveAsync(original, Budget, default);
        Assert.True(result.Succeeded);
        var execution = result.ExecutionNode!;
        Assert.Equal("8.8.8.8", execution.Host);
        Assert.Equal(protocol == ProtocolKind.Shadowsocks ? null : "vpn.example.net", execution.Sni);
        Assert.Equal(original.Password, execution.Password); Assert.Equal(original.UserId, execution.UserId);
        Assert.Equal(original.Port, execution.Port); Assert.False(execution.SkipCertVerify);
        Assert.Equal(original.Alpn, execution.Alpn); Assert.NotSame(original.Alpn, execution.Alpn);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    public async Task WebSocketRetainsHostAndSniPrecedence(ProtocolKind protocol)
    {
        var original = Node() with { Protocol = protocol, Transport = "ws", HostHeader = "front.example.net" };
        var first = (await Resolver("8.8.8.8").ResolveAsync(original, Budget, default)).ExecutionNode!;
        Assert.Equal("front.example.net", first.HostHeader); Assert.Equal(first.HostHeader, first.Sni);
        var second = (await Resolver("8.8.8.8").ResolveAsync(original with { Sni = "tls.example.net" }, Budget, default)).ExecutionNode!;
        Assert.Equal("tls.example.net", second.Sni); Assert.Equal("front.example.net", second.HostHeader);
        var third = (await Resolver("8.8.8.8").ResolveAsync(original with { HostHeader = null }, Budget, default)).ExecutionNode!;
        Assert.Equal("vpn.example.net", third.Sni); Assert.Equal("vpn.example.net", third.HostHeader);
    }

    [Fact]
    public async Task TrojanWebSocketUsesSniAsDefaultHttpHost()
    {
        var original = Node() with { Protocol = ProtocolKind.Trojan, Transport = "ws", Sni = "tls.example.net" };
        var execution = (await Resolver("8.8.8.8").ResolveAsync(original, Budget, default)).ExecutionNode!;
        Assert.Equal("tls.example.net", execution.Sni); Assert.Equal(execution.Sni, execution.HostHeader);
        var explicitHost = (await Resolver("8.8.8.8").ResolveAsync(
            original with { HostHeader = "front.example.net" }, Budget, default)).ExecutionNode!;
        Assert.Equal("tls.example.net", explicitHost.Sni); Assert.Equal("front.example.net", explicitHost.HostHeader);
    }

    [Theory]
    [InlineData("http", "vpn.example.net")]
    [InlineData("h2", null)]
    [InlineData("grpc", null)]
    public async Task OtherTransportDefaultsAreNotConfusedWithTls(string transport, string? expected)
    {
        var result = await Resolver("8.8.8.8").ResolveAsync(Node() with { Transport = transport }, Budget, default);
        Assert.Equal(expected, result.ExecutionNode!.HostHeader);
        Assert.Equal("vpn.example.net", result.ExecutionNode.Sni);
    }

    [Fact]
    public async Task ExplicitSecuritySettingsAreNotRelaxed()
    {
        var original = Node() with { Sni = "tls.example.net", Security = "reality",
            PublicKey = "SYNTHETIC_PUBLIC", SkipCertVerify = false, HostHeader = "front.example.net" };
        var execution = (await Resolver("8.8.8.8").ResolveAsync(original, Budget, default)).ExecutionNode!;
        Assert.Equal(original with { Host = execution.Host }, execution);
    }

    [Fact]
    public async Task UnreviewedPluginRebindingRefusedButLiteralIsUnchanged()
    {
        var calls = 0;
        var resolver = new ProxyEndpointResolver((_, _) => { calls++; throw new Exception("NO_DNS"); });
        var source = Node() with { Plugin = "obfs", PluginOpts = "obfs=http" };
        Assert.Equal("ENDPOINT_BINDING_UNSUPPORTED", (await resolver.ResolveAsync(source, Budget, default)).ReasonCode);
        source = source with { Host = "8.8.8.8" };
        Assert.Equal(source, (await resolver.ResolveAsync(source, Budget, default)).ExecutionNode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AlreadyCanceledCallsDoNotQueryOrReturnAPin()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var resolver = new ProxyEndpointResolver((_, _) => throw new Exception("NO_DNS"));
        Assert.Equal(ReasonCodes.Canceled, (await resolver.ResolveAsync(Node(), Budget, stop.Token)).ReasonCode);
        Assert.Equal(ReasonCodes.Canceled, (await resolver.ResolveAsync(Node("8.8.8.8"), Budget, stop.Token)).ReasonCode);
        Assert.Equal(0, resolver.InFlight);
    }

    [Fact]
    public async Task CancellationDoesNotFreeAnUnfinishedOsLookupSlot()
    {
        var release = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ProxyEndpointResolver((_, _) => release.Task, 1);
        using var stop = new CancellationTokenSource();
        var work = resolver.ResolveAsync(Node(), Budget, stop.Token);
        Assert.Equal(1, resolver.InFlight);
        stop.Cancel();
        try
        {
            Assert.Equal(ReasonCodes.Canceled, (await work.WaitAsync(Budget)).ReasonCode);
            Assert.Equal(1, resolver.InFlight);
            Assert.Equal("ENDPOINT_DNS_BUSY", (await resolver.ResolveAsync(Node(), Budget, default)).ReasonCode);
            // Numeric inputs need no resolver worker.
            Assert.True((await resolver.ResolveAsync(Node("8.8.8.8"), Budget, default)).Succeeded);
        }
        finally { release.TrySetResult([IPAddress.Parse("8.8.8.8")]); }
        await WaitForRelease(resolver);
    }

    [Fact]
    public async Task TimeoutIsBoundedAndDelayedFaultIsObserved()
    {
        var release = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ProxyEndpointResolver((_, _) => release.Task, 1);
        try
        {
            var result = await resolver.ResolveAsync(Node(), TimeSpan.FromMilliseconds(30), default).WaitAsync(Budget);
            Assert.Equal("ENDPOINT_DNS_TIMEOUT", result.ReasonCode); Assert.Null(result.ExecutionNode);
            Assert.Equal(1, resolver.InFlight);
        }
        finally { release.TrySetException(new IOException("SYNTHETIC_PRIVATE_DNS_ERROR")); }
        await WaitForRelease(resolver);
    }

    [Fact]
    public async Task LookupExceptionsAreSanitizedAndCapacityIsReturned()
    {
        var resolver = new ProxyEndpointResolver((_, _) => throw new IOException("SYNTHETIC_PRIVATE_DNS_ERROR"), 1);
        for (var i = 0; i < 2; i++)
        {
            var result = await resolver.ResolveAsync(Node(), Budget, default);
            Assert.Equal("ENDPOINT_DNS_FAILED", result.ReasonCode);
            Assert.DoesNotContain("SYNTHETIC_PRIVATE", result.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, resolver.InFlight);
        }
    }

    [Fact]
    public async Task CollectionSnapshotIsTakenBeforeAsyncLookup()
    {
        var alpn = new[] { "h2" };
        var release = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ProxyEndpointResolver((_, _) => release.Task);
        var result = resolver.ResolveAsync(Node() with { Alpn = alpn }, Budget, default);
        alpn[0] = "changed";
        release.SetResult([IPAddress.Parse("8.8.8.8")]);
        Assert.Equal("h2", (await result).ExecutionNode!.Alpn![0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61000)]
    public async Task InvalidBudgetCreatesNoLookup(int milliseconds)
    {
        var resolver = new ProxyEndpointResolver((_, _) => throw new Exception("NO_DNS"));
        Assert.Equal("ENDPOINT_DNS_TIMEOUT", (await resolver.ResolveAsync(Node(),
            TimeSpan.FromMilliseconds(milliseconds), default)).ReasonCode);
        Assert.Equal(0, resolver.InFlight);
    }

    [Fact]
    public async Task InvalidPortsAreRejectedBeforeLookup()
    {
        foreach (var port in new[] { -1, 0, 65536 })
            Assert.Equal("ENDPOINT_NAME_INVALID", (await Resolver("8.8.8.8").ResolveAsync(
                Node() with { Port = port }, Budget, default)).ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionProbeRejectsNonPublicDnsBeforeCreatingAWorker(bool unrelatedFixture)
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-endpoint-test-");
        try
        {
            var binary = Path.Combine(directory.FullName, "must-not-run");
            await File.WriteAllTextAsync(binary, "SYNTHETIC_NOT_EXECUTABLE");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary)));
            var fixture = unrelatedFixture ? new ProbeEndpointFixture
            { LoopbackHosts = new Dictionary<string, string> { ["other.example.net"] = "127.0.0.1" } } : null;
            var transport = new NonTunCoreProbeTransport(binary, hash, Budget, fixture, Resolver("127.0.0.1"));
            Assert.True(transport.CanRun);
            var result = await transport.ProbeAsync(Node(), new Uri("https://probe.example.net/"), default);
            Assert.False(result.Success); Assert.Equal(ProbeClass.Unsupported, result.Class);
            Assert.Equal(ReasonCodes.NonPublicEndpoint, result.ReasonCode); Assert.Null(result.WorkerId);
            Assert.Equal(CanonicalIdentity.Digest(Node()), result.CandidateDigest);
        }
        finally { directory.Delete(true); }
    }


    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    [InlineData(ProtocolKind.Trojan)]
    [InlineData(ProtocolKind.Shadowsocks)]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    public async Task GeneratedProbeProfileUsesNumericServerAndOriginalTlsName(ProtocolKind protocol)
    {
        var original = Node() with { Protocol = protocol, Password = "synthetic-test-password",
            Security = protocol == ProtocolKind.Shadowsocks ? "aead" : "tls",
            Encryption = protocol == ProtocolKind.Shadowsocks ? "aes-128-gcm" :
                protocol == ProtocolKind.Vmess ? "auto" : "none" };
        var result = await Resolver("8.8.8.8").ResolveAsync(original, Budget, default);
        var catalogue = new AutoVpn.Application.CatalogueNode
        {
            NodeId = "probe", Digest = CanonicalIdentity.Digest(original), Semantics = result.ExecutionNode!,
            Label = "synthetic", FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
        var yaml = NonTunCoreProbeTransport.BuildProbeYaml(catalogue, 18080, 18081);
        Assert.Contains("server: '8.8.8.8'", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("server: 'vpn.example.net'", yaml, StringComparison.Ordinal);
        if (protocol != ProtocolKind.Shadowsocks) Assert.Contains(": 'vpn.example.net'", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("skip-cert-verify: true", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("tun:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DIRECT", yaml, StringComparison.Ordinal);
        Assert.Equal("vpn.example.net", original.Host);
    }

    [Fact]
    public async Task CooperativeDnsCancellationDoesNotPublishLateSuccess()
    {
        using var stop = new CancellationTokenSource();
        var resolver = new ProxyEndpointResolver((_, token) =>
        {
            stop.Cancel();
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        });
        var result = await resolver.ResolveAsync(Node(), Budget, stop.Token);
        Assert.False(result.Succeeded); Assert.Equal(ReasonCodes.Canceled, result.ReasonCode);
        Assert.Equal(0, resolver.InFlight);
    }


    [RequiresMihomoFact]
    public async Task PinnedCoreParsesAllSixBasicEndpointBindingsWithoutConnecting()
    {
        var binary = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH")!;
        var expected = OperatingSystem.IsWindows()
            ? "beb9878924d7bd38c67176b441ab5e668cc378bfe751c8dc8fef3eb5aaf566d5"
            : "3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8";
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary))).ToLowerInvariant());
        foreach (var protocol in Enum.GetValues<ProtocolKind>().Where(p => p != ProtocolKind.Unknown))
        {
            var source = Node() with { Protocol = protocol, Password = "synthetic-test-password",
                Security = protocol == ProtocolKind.Shadowsocks ? "aead" : "tls",
                Encryption = protocol == ProtocolKind.Shadowsocks ? "aes-128-gcm" :
                    protocol == ProtocolKind.Vmess ? "auto" : "none" };
            var pinned = (await Resolver("8.8.8.8").ResolveAsync(source, Budget, default)).ExecutionNode!;
            var directory = Directory.CreateTempSubdirectory("autovpn-endpoint-parse-");
            try
            {
                var node = new AutoVpn.Application.CatalogueNode
                {
                    NodeId = "probe", Digest = CanonicalIdentity.Digest(source), Semantics = pinned,
                    Label = "synthetic", FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
                };
                var config = Path.Combine(directory.FullName, "config.yaml");
                await File.WriteAllTextAsync(config, NonTunCoreProbeTransport.BuildProbeYaml(node, 18080, 18081));
                var start = new System.Diagnostics.ProcessStartInfo(binary)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.FullName };
                foreach (var arg in new[] { "-t", "-f", config, "-d", directory.FullName }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!;
                var stdout = ProbeOutputDrain.ReadAsync(process.StandardOutput, default);
                var stderr = ProbeOutputDrain.ReadAsync(process.StandardError, default);
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, process.ExitCode);
                    Assert.Equal(ProbeOutputEnd.Eof, (await stdout.WaitAsync(Budget)).End);
                    Assert.Equal(ProbeOutputEnd.Eof, (await stderr.WaitAsync(Budget)).End);
                }
                finally
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(Budget);
                    await Task.WhenAll(stdout, stderr).WaitAsync(Budget);
                }
            }
            finally { directory.Delete(true); }
        }
    }

    private static async Task WaitForRelease(ProxyEndpointResolver resolver)
    {
        using var stop = new CancellationTokenSource(Budget);
        while (resolver.InFlight != 0) await Task.Delay(5, stop.Token);
    }
}
