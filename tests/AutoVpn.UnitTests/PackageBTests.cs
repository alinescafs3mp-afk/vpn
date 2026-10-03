using System.Security.Authentication;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class PackageBTests
{
    private const string FixtureUri =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#fixture";

    [Fact]
    public async Task ProductionHandlerCancelsAStalledBodyAndDoesNotFollowAnOffRegistryRedirect()
    {
        using var handler = PolicyHttpFetcher.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        using var redirector = new SocketsHttpHandler { AllowAutoRedirect = true, UseProxy = false };
        Assert.Throws<InvalidOperationException>(() => new PolicyHttpFetcher(redirector, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" }));

        await using var first = await LoopbackHttps.StartAsync();
        await using var second = await LoopbackHttps.StartAsync();
        first.Responder = request => Task.FromResult<byte[]?>(LoopbackHttps.Redirect("https://127.0.0.1:" + second.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/secret"));
        second.Responder = request => Task.FromResult<byte[]?>(LoopbackHttps.Text("secret"));
        Pin(handler, first, second);
        using var fetcher = new PolicyHttpFetcher(handler, [new ApprovedFetchOrigin("127.0.0.1", first.Port, "/ok")]);
        var blocked = await fetcher.GetAsync(new Uri("https://127.0.0.1:" + first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/nope"), null, 1000, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Equal(ReasonCodes.OffRegistryRedirect, blocked.ReasonCode);
        Assert.Equal(0, first.Requests);
        var redirect = await fetcher.GetAsync(new Uri("https://127.0.0.1:" + first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/ok"), null, 1000, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Equal(ReasonCodes.OffRegistryRedirect, redirect.ReasonCode);
        Assert.Null(redirect.Body);
        Assert.Equal(1, first.Requests);
        Assert.Equal(0, second.Requests);

        Pin(redirector, first, second);
        using var bypass = new HttpClient(redirector, disposeHandler: false);
        var followed = await bypass.GetAsync(new Uri("https://127.0.0.1:" + first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/ok"));
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        Assert.True(second.Requests >= 1);

        first.Responder = async request =>
        {
            await request.Stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n"));
            await request.Stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), request.Cancelled);
            return null;
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stalled = await fetcher.GetAsync(
            new Uri("https://127.0.0.1:" + first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/ok"),
            null,
            100_000,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(500));
        watch.Stop();
        Assert.Equal(ReasonCodes.FetchTimeout, stalled.ReasonCode);
        Assert.Null(stalled.Body);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task FreshImportStaysPendingUntilARealCoreProbeAndConsentSurvivesRestart()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-b-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            var ledgerPath = Path.Combine(directory.FullName, "sources.json");
            var calls = 0;
            var handler = new AsyncHandler(async (request, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                if (request.Headers.TryGetValues("If-None-Match", out _))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                if (request.RequestUri!.Host == "raw.githubusercontent.com")
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FixtureUri, Encoding.UTF8, "text/plain"),
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");
                await Task.Yield();
                return response;
            });
            using (var store = SqliteCatalogue.Open(path, new PassthroughSecretProtector()))
            {
                Assert.False(store.Settings.DisclosureAccepted);
                var ledger = new SourceLedger();
                using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gitlab.com", "raw.githubusercontent.com" });
                var coordinator = new CatalogueCoordinator(store, fetcher, new NonTunCoreProbeTransport(null, null), ledger);
                var outcome = await coordinator.RefreshAsync(
                [
                    new RefreshWorkItem
                    {
                        ArtifactId = "BLACK_VLESS_RUS.txt",
                        FamilyId = "black-vless",
                        Urls =
                        [
                            new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt"),
                            new Uri("https://gitlab.com/igareck/vpn-configs-for-russia/-/raw/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt"),
                        ],
                    },
                ], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
                Assert.False(outcome.Cancelled);
                var node = Assert.Single(store.Nodes);
                Assert.Equal(HealthState.Pending, node.Assessment?.Health);
                Assert.Empty(store.Eligible(new EligibilityContext { NowUtc = DateTimeOffset.UtcNow, NetworkEpoch = store.NetworkEpoch }));
                var probe = await coordinator.ProbeAsync(new Uri("https://127.0.0.1/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.Equal(0, probe.Attempted);
                Assert.Equal(HealthState.Pending, Assert.Single(store.Nodes).Assessment?.Health);
                Assert.Contains("не измерялась", CataloguePresentation.Format(node));
                Assert.Contains("Страна по подписке", CataloguePresentation.Format(node));
                Assert.Contains("Рабочих серверов нет", CataloguePresentation.Servers(store, "working", DateTimeOffset.UtcNow));
                Consent.AcceptDisclosure(store);
                ledger.Save(ledgerPath);
                var yaml = NonTunCoreProbeTransport.BuildProbeYaml(node, 19001, 19002);
                Assert.False(MihomoProfileGenerator.EnablesTun(yaml));
                Assert.Contains("listen: 127.0.0.1", yaml, StringComparison.Ordinal);
                Assert.DoesNotContain("tun:", yaml, StringComparison.Ordinal);
            }

            using var again = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            Assert.True(again.Settings.DisclosureAccepted);
            Assert.Equal(HealthState.Pending, Assert.Single(again.Nodes).Assessment?.Health);
            Assert.False(UiSessionReducer.ConnectAllowed(false, true));
            Assert.True(calls >= 2);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NotModifiedWithoutMembershipRefetchesAndCancellationDoesNotPublish()
    {
        var bodyHandler = new AsyncHandler((request, _) =>
        {
            var modified = request.Headers.Contains("If-None-Match");
            if (modified && request.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains("\"known\""))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            if (modified)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixtureUri, Encoding.UTF8, "text/plain"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"known\"");
            return Task.FromResult(response);
        });
        var catalogue = new MemoryCatalogue();
        var ledger = new SourceLedger();
        var url = new Uri("https://raw.githubusercontent.com/igareck/repo/commit/BLACK_VLESS_RUS.txt");
        ledger.Remember(url.AbsoluteUri, "\"known\"", null, DateTimeOffset.UtcNow, null);
        using var fetcher = new PolicyHttpFetcher(bodyHandler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), ledger);
        var item = new RefreshWorkItem { ArtifactId = "BLACK_VLESS_RUS.txt", FamilyId = "black-vless", Urls = [url] };
        var fetched = await coordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(fetched.RefetchPerformed);
        Assert.Equal(HealthState.Pending, Assert.Single(catalogue.Nodes).Assessment?.Health);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new AsyncHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        });
        var empty = new MemoryCatalogue();
        using var blockingFetcher = new PolicyHttpFetcher(blocking, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var blockingCoordinator = new CatalogueCoordinator(empty, blockingFetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger());
        using var cancel = new CancellationTokenSource();
        var pending = blockingCoordinator.RefreshAsync([item], DateTimeOffset.UtcNow, cancel.Token, TimeSpan.FromSeconds(5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancel.CancelAsync();
        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancelled.Cancelled);
        Assert.Empty(empty.Nodes);
    }

    [Fact]
    public async Task RefreshDownloadConcurrencyStaysBounded()
    {
        var inflight = 0;
        var max = 0;
        var calls = 0;
        var gate = new object();
        var handler = new AsyncHandler(async (_, _) =>
        {
            var now = Interlocked.Increment(ref inflight);
            Interlocked.Increment(ref calls);
            lock (gate)
            {
                max = Math.Max(max, now);
            }

            await Task.Delay(80);
            Interlocked.Decrement(ref inflight);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixtureUri, Encoding.UTF8, "text/plain"),
            };
        });
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var catalogue = new MemoryCatalogue();
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger());
        var items = Enumerable.Range(0, 6).Select(index => new RefreshWorkItem
        {
            ArtifactId = "artifact-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FamilyId = "black-vless",
            Urls = [new Uri("https://raw.githubusercontent.com/file-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        }).ToArray();
        await coordinator.RefreshAsync(items, DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.Equal(6, calls);
        Assert.InRange(max, 1, ProductLimits.SourceDownloadConcurrency);
        Assert.Single(catalogue.Nodes);
    }

    [Fact]
    public async Task RateLimitIsNotTreatedAsADocument()
    {
        var calls = 0;
        var handler = new AsyncHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return Task.FromResult(response);
        });
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var result = await fetcher.GetAsync(new Uri("https://raw.githubusercontent.com/limited"), null, 100, CancellationToken.None, TimeSpan.FromSeconds(2), maxRetries: 3);
        Assert.Equal(ReasonCodes.RateLimited, result.ReasonCode);
        Assert.Null(result.Body);
        Assert.Equal(9, result.RetryAfterSeconds);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BranchRefreshTargetUsesACommitObjectRatherThanTheTreeShaOrThePin()
    {
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
        Assert.Equal("20c38289c29e4dba6b8f01ddd3273ec9ec169b46", registry.PinnedCommit);
        Assert.EndsWith("/main", registry.TreeApi.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains(registry.FetchOrigins, origin => origin.Host == "api.github.com" && origin.PathPrefix.EndsWith("/commits/", StringComparison.Ordinal));
        var handler = new AsyncHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/commits/", StringComparison.Ordinal)
                ? "{\"sha\":\"" + commit + "\",\"commit\":{\"tree\":{\"sha\":\"" + tree + "\"}}}"
                : "{\"sha\":\"" + tree + "\",\"truncated\":false,\"tree\":[{\"path\":\"BLACK_VLESS_RUS.txt\",\"type\":\"blob\",\"mode\":\"100644\",\"size\":30}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
        using var fetcher = new PolicyHttpFetcher(handler, registry.FetchOrigins);
        var discovery = await new CatalogueCoordinator(new MemoryCatalogue(), fetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger())
            .DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(discovery.Complete);
        Assert.Equal(commit, discovery.CommitSha);
        var item = Assert.Single(discovery.Items);
        Assert.All(item.Urls, url =>
        {
            Assert.Contains("/" + commit + "/", url.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain("/" + tree + "/", url.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain("/" + registry.PinnedCommit + "/", url.AbsoluteUri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReviewedRegistryRejectsUnsafePathsAndRejectedHosts()
    {
        var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
        Assert.Contains("raw.githubusercontent.com", registry.ApprovedHosts);
        Assert.Contains("bitbucket.org", registry.RejectedHosts);
        Assert.Contains("translate.yandex.ru", registry.RejectedHosts);
        Assert.Equal(2, registry.ProbeTargets.Count);
        Assert.All(registry.ProbeTargets, target => Assert.Equal("https", target.Scheme));
        Assert.Empty(ReviewedRegistryLoader.ContentUrls(registry, "../etc/passwd"));
        Assert.Empty(ReviewedRegistryLoader.ContentUrls(registry, "/etc/passwd"));
        var urls = ReviewedRegistryLoader.ContentUrls(registry, "BLACK_VLESS_RUS.txt");
        Assert.NotEmpty(urls);
        Assert.All(urls, url =>
        {
            Assert.DoesNotContain(url.IdnHost, registry.RejectedHosts, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(registry.PinnedCommit, url.AbsoluteUri, StringComparison.Ordinal);
        });
        Assert.Equal("raw.githubusercontent.com", urls[0].IdnHost);
    }

    [Fact]
    public async Task IncompleteDiscoveryDoesNotBecomeASubscriptionRefresh()
    {
        var handler = new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sha":"abc","truncated":true,"tree":[]}""", Encoding.UTF8, "application/json"),
        }));
        var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
        using var fetcher = new PolicyHttpFetcher(handler, registry.FetchOrigins);
        var catalogue = new MemoryCatalogue();
        catalogue.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "kept",
            FamilyId = "black-vless",
            ContentHash = "abc",
            Complete = true,
            NowUtc = DateTimeOffset.UtcNow,
            Nodes =
            [
                new SnapshotNode
                {
                    Digest = "digest",
                    Semantics = new NodeSemantics
                    {
                        Protocol = ProtocolKind.Vless,
                        Host = "203.0.113.10",
                        Port = 443,
                        UserId = "11111111-1111-4111-8111-111111111111",
                        Encryption = "none",
                        Security = "tls",
                        Transport = "tcp",
                    },
                    Label = "kept",
                    FamilyId = "black-vless",
                    ArtifactId = "kept",
                },
            ],
        });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger(), registry.ProbeTargets);
        var discovery = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.False(discovery.Complete);
        Assert.Empty(discovery.Items);
        Assert.Single(catalogue.Nodes);
        var offTarget = await coordinator.ProbeAsync(new Uri("https://evil.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, offTarget.Attempted);
    }

    [Fact]
    public async Task MissingCoreDoesNotReportASuccessfulProbe()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-core-");
        try
        {
            var binary = Path.Combine(directory.FullName, "not-mihomo");
            await File.WriteAllTextAsync(binary, "this is not a core");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary))).ToLowerInvariant();
            var transport = new NonTunCoreProbeTransport(binary, hash, TimeSpan.FromMilliseconds(300));
            Assert.True(transport.CanRun);
            var observation = await transport.ProbeAsync(FixtureNode().Semantics, new Uri("https://127.0.0.1/generate_204"), CancellationToken.None);
            Assert.False(observation.Success);
            Assert.Equal("CORE_START_FAILED", observation.ReasonCode);
            var missing = new NonTunCoreProbeTransport(null, hash);
            Assert.False(missing.CanRun);
            var absent = await missing.ProbeAsync(FixtureNode().Semantics, new Uri("https://127.0.0.1/generate_204"), CancellationToken.None);
            Assert.False(absent.Success);
            Assert.Equal("CORE_MISSING", absent.ReasonCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SocksClientReadsALocalStatusAndSpeedStaysUnknownWithoutHealth()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var served = ServeHttp204(target);
        var relayed = ServeSocks(proxy, targetPort);
        var status = await Socks5Client.GetStatusAsync(
            new IPEndPoint(IPAddress.Loopback, proxyPort),
            new Uri("https://127.0.0.1:" + targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/generate_204"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(0, status);
        await Task.WhenAll(served, relayed).WaitAsync(TimeSpan.FromSeconds(2));
        target.Stop();
        proxy.Stop();

        var catalogue = new MemoryCatalogue();
        var unread = await SpeedMeasurement.MeasureHealthyDownloadAsync(catalogue, "missing", new MemoryStream(new byte[1000]), CancellationToken.None);
        Assert.Null(unread);
        var read = await BoundedTransfer.ReadAsync(new BurstThenBlock(1500), 1_000_000, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal(1500, read.PayloadBytes);
        Assert.True(read.Truncated);
        var sample = SpeedSample.From(read);
        Assert.NotNull(sample);
        Assert.Equal(1500, sample.Value.PayloadBytes);
        Assert.True(sample.Value.MegabitsPerSecond > 0);
    }

    [Fact]
    public void ScheduleClampAndUnknownSessionDoNotClaimDisconnect()
    {
        foreach (var seed in new[] { 0, 1, -1, -100, int.MinValue, int.MaxValue })
        {
            var interval = RefreshSchedule.Interval(120, 10, seed);
            Assert.InRange(interval.TotalMinutes, 110, 130);
        }

        var floor = RefreshSchedule.Interval(15, 7, int.MinValue);
        Assert.InRange(floor.TotalMinutes, 15, 22);
        var catalogue = new MemoryCatalogue();
        Assert.Throws<InvalidOperationException>(() => catalogue.Settings = new ProductSettings { RefreshIntervalMinutes = 8 * 24 * 60 });
        var initial = UiSessionReducer.Initial();
        Assert.Equal("Unknown", initial.PhaseCode);
        Assert.False(initial.ClaimsVerifiedDisconnect);
        Assert.True(UiSessionReducer.PlanExit(initial).CanClose);
        var connected = UiSessionReducer.FromSnapshot(
            initial,
            new BrokerSnapshot { Phase = nameof(TunnelPhase.Connected), ProtectionArmed = true },
            "сессия",
            disclosureAccepted: false);
        Assert.True(connected.SafetyDisconnectAvailable);
        Assert.Equal(Ru.Disconnect, connected.PrimaryAction);
        var lost = UiSessionReducer.BrokerUnreachable(connected);
        Assert.Equal("Unknown", lost.PhaseCode);
        Assert.False(lost.ClaimsVerifiedDisconnect);
        Assert.True(lost.LastKnownProtectionArmed);
        Assert.False(UiSessionReducer.PlanExit(lost).CanClose);
        var cleared = UiSessionReducer.FromSnapshot(
            connected,
            new BrokerSnapshot { Phase = nameof(TunnelPhase.Disconnected), ProtectionArmed = false },
            "отключено",
            disclosureAccepted: false);
        Assert.True(cleared.ClaimsVerifiedDisconnect);
        Assert.True(UiSessionReducer.PlanExit(cleared).CanClose);
    }

    private static CatalogueNode FixtureNode()
    {
        return new CatalogueNode
        {
            NodeId = "fixture",
            Digest = "digest",
            Semantics = new NodeSemantics
            {
                Protocol = ProtocolKind.Vless,
                Host = "203.0.113.10",
                Port = 443,
                UserId = "11111111-1111-4111-8111-111111111111",
                Encryption = "none",
                Security = "tls",
                Transport = "tcp",
                Sni = "www.example.com",
            },
            Label = "fixture",
            FirstSeenUtc = DateTimeOffset.UnixEpoch,
            LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
    }

    private static string ConfigDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "source-manifest.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "config");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("config");
    }

    private static void Pin(SocketsHttpHandler handler, params LoopbackHttps[] servers)
    {
        var pins = servers.Select(server => server.Certificate.GetCertHashString(HashAlgorithmName.SHA256)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    private static async Task ServeHttp204(TcpListener listener)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[1024];
            while (await stream.ReadAsync(buffer) > 0)
            {
                break;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private static async Task ServeSocks(TcpListener listener, int targetPort)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var greeting = new byte[3];
            await ReadExact(stream, greeting);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 });
            var head = new byte[4];
            await ReadExact(stream, head);
            var length = new byte[1];
            await ReadExact(stream, length);
            var rest = new byte[length[0] + 2];
            await ReadExact(stream, rest);
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            using var target = new TcpClient();
            await target.ConnectAsync(IPAddress.Loopback, targetPort);
            await using var remote = target.GetStream();
            var left = Copy(stream, remote);
            var right = Copy(remote, stream);
            await Task.WhenAny(left, right);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private static async Task Copy(Stream from, Stream to)
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var count = await from.ReadAsync(buffer);
                if (count == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, count));
            }
        }
        catch (IOException)
        {
        }
    }

    private static async Task ReadExact(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (count == 0)
            {
                throw new IOException("short read");
            }

            read += count;
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return next(request, cancellationToken);
        }
    }

    private sealed class BurstThenBlock(int bytes) : Stream
    {
        private int _left = bytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left > 0)
            {
                var count = Math.Min(_left, buffer.Length);
                _left -= count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class LoopbackHttps : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private LoopbackHttps(TcpListener listener, X509Certificate2 certificate)
        {
            _listener = listener;
            Certificate = certificate;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public X509Certificate2 Certificate { get; }
        public int Port { get; }
        public int Requests { get; private set; }
        public string? LastError { get; private set; }
        public Func<LoopRequest, Task<byte[]?>> Responder { get; set; } = _ => Task.FromResult<byte[]?>(Text("ok"));

        public static Task<LoopbackHttps> StartAsync()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackHttps(listener, certificate));
        }

        public static byte[] Text(string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
            return Encoding.ASCII.GetBytes(head).Concat(payload).ToArray();
        }

        public static byte[] Redirect(string location)
        {
            var head = "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            return Encoding.ASCII.GetBytes(head);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }

            Certificate.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    break;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = Certificate,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    }, _stop.Token);
                    var header = await ReadHeaderAsync(ssl);
                    Requests++;
                    var response = await Responder(new LoopRequest(header, ssl, _stop.Token));
                    if (response is not null)
                    {
                        await ssl.WriteAsync(response, _stop.Token);
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private static async Task<string> ReadHeaderAsync(Stream stream)
        {
            var buffer = new byte[2048];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
                if (count == 0)
                {
                    break;
                }

                read += count;
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, read);
        }
    }

    private sealed record LoopRequest(string Header, Stream Stream, CancellationToken Cancelled);
}
