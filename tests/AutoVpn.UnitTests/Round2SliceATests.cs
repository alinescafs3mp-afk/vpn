using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class Round2SliceATests
{
    private const string FixtureUri =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#fixture";

    private static string CoreHash => TestCorePins.ExpectedHash;
    private const string TreeSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Rt01TlsOverSocksRejectsPlaintextFakeStatusBadCertificatesRedirectAndHtml()
    {
        var trusted = Issue("probe.example");
        var wrongHost = Issue("other.example");
        await using var good = await TlsPeer.StartAsync(trusted, "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        await using var wrong = await TlsPeer.StartAsync(wrongHost, "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        await using var untrusted = await TlsPeer.StartAsync(Issue("probe.example"), "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        await using var redirect = await TlsPeer.StartAsync(trusted, "HTTP/1.1 302 Found\r\nLocation: https://evil.example/x\r\nContent-Length: 0\r\n\r\n");
        await using var html = await TlsPeer.StartAsync(trusted, "HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\n<html>");
        await using var fake = await TlsPeer.StartAsync(trusted, "HTTP/1.1 500 NO\r\nContent-Length: 0\r\n\r\n");
        await using var plain = await PlainSocks.StartAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");

        var anchors = new X509Certificate2Collection { trusted };
        var ok = await ExchangeAsync(await ForwardAsync(good.Port), good.Port, anchors);
        Assert.True(ok.Authenticated);
        Assert.Equal(204, ok.Status);
        Assert.Null(ok.Failure);
        Assert.Contains("probe.example", good.Sni, StringComparison.Ordinal);

        var plainExchange = await Socks5Client.ExchangeAsync(plain.Proxy, Target(1), TimeSpan.FromSeconds(3), anchors, CancellationToken.None);
        Assert.False(plainExchange.Authenticated);
        Assert.Equal("TLS_REJECTED", plainExchange.Failure);
        Assert.Equal(0, await Socks5Client.GetStatusAsync(plain.Proxy, Target(1), TimeSpan.FromSeconds(3), CancellationToken.None));

        var wrongExchange = await ExchangeAsync(await ForwardAsync(wrong.Port), wrong.Port, anchors);
        Assert.Equal("TLS_REJECTED", wrongExchange.Failure);
        var untrustedExchange = await ExchangeAsync(await ForwardAsync(untrusted.Port), untrusted.Port, null);
        Assert.Equal("TLS_REJECTED", untrustedExchange.Failure);
        var redirectExchange = await ExchangeAsync(await ForwardAsync(redirect.Port), redirect.Port, anchors);
        Assert.Equal("REDIRECT", redirectExchange.Failure);
        var htmlExchange = await ExchangeAsync(await ForwardAsync(html.Port), html.Port, anchors);
        Assert.Equal("UNEXPECTED_BODY", htmlExchange.Failure);
        var fakeExchange = await ExchangeAsync(await ForwardAsync(fake.Port), fake.Port, anchors);
        Assert.Equal("STATUS", fakeExchange.Failure);
        Assert.True(good.Accepts >= 1);
    }

    [RequiresMihomoFact]
    public async Task Rt01PinnedCoreAuthenticatesTlsAndBrokenCandidateDoesNot()
    {
        var binary = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
        var trusted = Issue("probe.example");
        await using var tls = await TlsPeer.StartAsync(trusted, "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        const string password = "round2-ss-secret";
        await using var shadowsocks = await ShadowsocksAeadServer.StartAsync(password, tls.Port);
        var good = ShadowsocksNode(shadowsocks.Port, password);
        var fixture = new ProbeEndpointFixture
        {
            LoopbackHosts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["candidate.example"] = "127.0.0.1",
                ["probe.example"] = "127.0.0.1",
            },
            TrustAnchors = new X509Certificate2Collection { trusted },
        };
        var transport = new NonTunCoreProbeTransport(binary, CoreHash, TimeSpan.FromSeconds(20), fixture);
        var target = new Uri("https://probe.example:" + tls.Port.ToString(CultureInfo.InvariantCulture) + "/generate_204");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var observed = await transport.ProbeAsync(good, target, budget.Token);
        Assert.True(observed.Success, transport.LastDiagnostic);
        Assert.Equal(ProbeClass.Success, observed.Class);
        Assert.Equal(target.AbsoluteUri, observed.TargetUri);
        Assert.Equal(CanonicalIdentity.Digest(good), observed.CandidateDigest);
        Assert.Contains("candidate.example", observed.WorkerId, StringComparison.Ordinal);
        Assert.Contains("probe.example", tls.Sni, StringComparison.Ordinal);
        Assert.True(tls.Accepts >= 1);
        Assert.True(shadowsocks.Handshakes >= 1);

        var accepts = tls.Accepts;
        var brokenNode = good with { Password = "wrong-ss-secret" };
        var brokenTransport = new NonTunCoreProbeTransport(binary, CoreHash, TimeSpan.FromSeconds(20), fixture);
        using var brokenBudget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var broken = await brokenTransport.ProbeAsync(brokenNode, target, brokenBudget.Token);
        Assert.False(broken.Success, brokenTransport.LastDiagnostic);
        Assert.Equal(ProbeClass.CandidateFailure, broken.Class);
        Assert.Equal(accepts, tls.Accepts);

        var catalogue = CatalogueWith(brokenNode);
        var report = await ProbeCoordinator.RunAsync(
            catalogue,
            brokenTransport,
            target,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(20));
        Assert.Equal(0, report.Succeeded);
        Assert.NotEqual(HealthState.Healthy, catalogue.Nodes[0].Assessment?.Health);
        Assert.Equal(accepts, tls.Accepts);

        var healthy = CatalogueWith(good);
        var admitted = await ProbeCoordinator.RunAsync(
            healthy,
            transport,
            target,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(20));
        Assert.Equal(1, admitted.Succeeded);
        Assert.Equal(HealthState.Healthy, healthy.Nodes[0].Assessment?.Health);
        Assert.Equal(CanonicalIdentity.Digest(good), healthy.Nodes[0].Assessment?.Digest);
    }

    [LinuxOnlyFact]
    public async Task Rt03WorkerCancelCleansCredentialsAndDoesNotKillTheNextProcess()
    {
        var markerA = "autovpn-rt03-a-" + Guid.NewGuid().ToString("N");
        var markerB = "autovpn-rt03-b-" + Guid.NewGuid().ToString("N");
        var directory = Directory.CreateTempSubdirectory("autovpn-rt03-").FullName;
        File.WriteAllText(Path.Combine(directory, "secret.txt"), "credential");
        try
        {
            var chatty = await ProbeWorker.StartAsync(Shell("while true; do echo hello; done"), null, TimeSpan.FromSeconds(2), CancellationToken.None, directory);
            await Task.Delay(1200);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await chatty.DisposeAsync();
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.True(chatty.OutputBytes >= 10_000);
            Assert.InRange(chatty.OutputTail.Length, 0, 2000);
            Assert.True(chatty.DirectoryRemoved);
            Assert.False(Directory.Exists(directory));

            var hungDirectory = Directory.CreateTempSubdirectory("autovpn-rt03-hung-").FullName;
            var hung = await ProbeWorker.StartAsync(Shell("sleep 30 #" + markerA), null, TimeSpan.FromSeconds(2), CancellationToken.None, hungDirectory);
            var hungWatch = System.Diagnostics.Stopwatch.StartNew();
            await hung.DisposeAsync();
            hungWatch.Stop();
            Assert.True(hungWatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(Alive(markerA));
            Assert.False(Directory.Exists(hungDirectory));

            var cancelDirectory = Directory.CreateTempSubdirectory("autovpn-rt03-cancel-").FullName;
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var cancelled = await ProbeWorker.StartAsync(Shell("sleep 30 #" + markerA), 9, TimeSpan.FromSeconds(3), cancel.Token, cancelDirectory);
            Assert.False(cancelled.Ready);
            Assert.False(Directory.Exists(cancelDirectory));
            Assert.False(Alive(markerA));

            var exitedDirectory = Directory.CreateTempSubdirectory("autovpn-rt03-exit-").FullName;
            File.WriteAllText(Path.Combine(exitedDirectory, "secret.txt"), "credential");
            var exited = await ProbeWorker.StartAsync(Shell("exit 0"), 9, TimeSpan.FromSeconds(2), CancellationToken.None, exitedDirectory);
            Assert.False(exited.Ready);
            Assert.False(File.Exists(Path.Combine(exitedDirectory, "secret.txt")));

            var keepDirectory = Directory.CreateTempSubdirectory("autovpn-rt03-keep-").FullName;
            var kept = await ProbeWorker.StartAsync(Shell("sleep 30 #" + markerB), null, TimeSpan.FromSeconds(2), CancellationToken.None, keepDirectory);
            var other = Process.Start(Shell("sleep 30 #" + markerA));
            Assert.NotNull(other);
            await chattyDisposedSibling(kept, markerA, markerB);
            other.Kill(entireProcessTree: true);
            other.WaitForExit(2000);
        }
        finally
        {
            KillMarker(markerA);
            KillMarker(markerB);
        }
    }

    [Fact]
    public async Task Rt07SixtyFiveSecondCandidateIsAdmittedWithoutWaitingThirtyMinutes()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero) };
        var catalogue = FreshNode(clock.UtcNow.AddSeconds(-65));
        var node = catalogue.Nodes[0];
        Assert.False(ProbeCoordinator.NeedsProbe(node, clock.UtcNow, catalogue.NetworkEpoch));
        Assert.True(ProbeCoordinator.NeedsOnDemandAdmission(node, clock.UtcNow, catalogue.NetworkEpoch));
        Assert.Empty(catalogue.Eligible(new EligibilityContext
        {
            NowUtc = clock.UtcNow,
            NetworkEpoch = catalogue.NetworkEpoch,
            AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
        }));
        var working = CataloguePresentation.Servers(catalogue, "working", clock.UtcNow);
        Assert.Contains("fixture — ", working, StringComparison.Ordinal);
        Assert.DoesNotContain("Рабочих серверов нет", working, StringComparison.Ordinal);
        Assert.Contains(catalogue.Eligible(new EligibilityContext { NowUtc = clock.UtcNow, NetworkEpoch = catalogue.NetworkEpoch }), item => item.NodeId == node.NodeId);

        var calls = 0;
        var transport = new ScriptedTransport(node =>
        {
            calls++;
            return Proven(node, 20, 32);
        });
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), new ImmediateCore(), null, clock, transport, new Uri("https://probe.example/generate_204"));
        var connected = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Assert.True(connected.Ok);
        Assert.Equal(1, calls);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        Assert.Equal(clock.UtcNow, catalogue.Nodes[0].Assessment!.LastSuccessUtc);

        var future = FreshNode(clock.UtcNow.AddHours(2));
        Assert.True(ProbeCoordinator.NeedsProbe(future.Nodes[0], clock.UtcNow, future.NetworkEpoch));
        Assert.Empty(future.Eligible(new EligibilityContext { NowUtc = clock.UtcNow, NetworkEpoch = future.NetworkEpoch }));
        var futureEngine = new BrokerEngine(future, new ArmingGuard(), new ImmediateCore(), null, clock, new ScriptedTransport(_ => new ProbeObservation(true, 5, false, null, 1, ProbeClass.Success)), new Uri("https://probe.example/generate_204"));
        var refused = await futureEngine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = future.Nodes[0].NodeId,
            Digest = future.Nodes[0].Digest,
            NetworkEpoch = future.NetworkEpoch,
        }), CancellationToken.None);
        Assert.Equal(ReasonCodes.NoEligibleServer, refused.ErrorCode);
        Assert.Equal(clock.UtcNow.AddHours(2), future.Nodes[0].Assessment!.LastSuccessUtc);

        var staleEpoch = FreshNode(clock.UtcNow.AddSeconds(-65));
        var epochTransport = new ScriptedTransport(node =>
        {
            staleEpoch.SetNetworkEpoch(staleEpoch.NetworkEpoch + 1);
            return Proven(node, 5, 1);
        });
        var epochEngine = new BrokerEngine(staleEpoch, new ArmingGuard(), new ImmediateCore(), null, clock, epochTransport, new Uri("https://probe.example/generate_204"));
        var epochResult = await epochEngine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = staleEpoch.Nodes[0].NodeId,
            Digest = staleEpoch.Nodes[0].Digest,
            NetworkEpoch = 1,
        }), CancellationToken.None);
        Assert.Equal(ReasonCodes.NoEligibleServer, epochResult.ErrorCode);
        Assert.Equal(HealthState.Healthy, staleEpoch.Nodes[0].Assessment?.Health);
        Assert.Equal(clock.UtcNow.AddSeconds(-65), staleEpoch.Nodes[0].Assessment!.LastSuccessUtc);
    }

    [Fact]
    public void Rt09StaleCatalogueCannotReplaceACommittedSnapshot()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt09-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            var first = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            var second = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            try
            {
                RefreshMerge.Ingest(first, [Body("list", FixtureUri, "a")], DateTimeOffset.UtcNow, false);
                first.Settings = first.Settings with { DisclosureAccepted = true };
                var conflict = Assert.Throws<CatalogueStoreException>(() => second.SetActiveNode(null));
                Assert.Equal("CATALOGUE_CONFLICT", conflict.Message);
                Assert.Equal("203.0.113.10", Assert.Single(first.Nodes).Semantics.Host);
                Assert.True(first.Settings.DisclosureAccepted);
                Assert.Empty(second.Nodes);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            using (var reopened = SqliteCatalogue.Open(path, new PassthroughSecretProtector()))
            {
                Assert.Equal("203.0.113.10", Assert.Single(reopened.Nodes).Semantics.Host);
                Assert.True(reopened.Settings.DisclosureAccepted);
            }

            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                raw.Open();
                using var delete = raw.CreateCommand();
                delete.CommandText = "DELETE FROM meta WHERE key='catalogue_revision';";
                delete.ExecuteNonQuery();
            }

            var older = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            var stale = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            try
            {
                older.SetNetworkEpoch(4);
                var again = Assert.Throws<CatalogueStoreException>(() => stale.SetActiveNode("missing"));
                Assert.Equal("CATALOGUE_CONFLICT", again.Message);
            }
            finally
            {
                older.Dispose();
                stale.Dispose();
            }

            using var survived = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            Assert.Equal(4, survived.NetworkEpoch);
            Assert.Equal("203.0.113.10", Assert.Single(survived.Nodes).Semantics.Host);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rt12TreeNotModifiedReusesTheCachedSnapshotAndRefetchesWhenItIsCorrupt()
    {
        var registry = Registry();
        var body = TreeJson();
        var fetches = new List<string?>();
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"sha\":\"20c38289c29e4dba6b8f01ddd3273ec9ec169b46\",\"commit\":{\"tree\":{\"sha\":\"" + TreeSha + "\"}}}") };
            string? tag = request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null;
            fetches.Add(tag);
            if (tag is "\"tree-1\"")
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tree-1\"");
            return response;
        });
        var ledger = new SourceLedger();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(new MemoryCatalogue(), fetcher, new NonTunCoreProbeTransport(null, null), ledger);
        var first = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(first.Complete);
        const string pin = "20c38289c29e4dba6b8f01ddd3273ec9ec169b46";
        Assert.Equal(pin, first.CommitSha);
        Assert.Contains(pin, Assert.Single(first.Items).Urls[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(TreeSha, first.Items[0].Urls[0].AbsoluteUri, StringComparison.Ordinal);

        var second = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(second.Complete);
        Assert.Single(second.Items);
        Assert.Equal("\"tree-1\"", fetches[^1]);

        var directory = Directory.CreateTempSubdirectory("autovpn-rt12-");
        try
        {
            var path = Path.Combine(directory.FullName, "sources.json");
            ledger.Save(path);
            var restarted = SourceLedger.Load(path);
            using var restartedFetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" });
            var restartedCoordinator = new CatalogueCoordinator(new MemoryCatalogue(), restartedFetcher, new NonTunCoreProbeTransport(null, null), restarted);
            var third = await restartedCoordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
            Assert.True(third.Complete);
            Assert.Single(third.Items);

            restarted.RememberDiscovery(registry.TreeApi.AbsoluteUri, "\"tree-1\"", "{", DateTimeOffset.UtcNow);
            var before = fetches.Count;
            var repaired = await restartedCoordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
            Assert.True(repaired.Complete);
            Assert.Single(repaired.Items);
            // Corrupt cached tree is detected BEFORE sending a conditional validator.
            // Commit lookup is not counted as a tree fetch by this fixture.
            Assert.Equal(before + 1, fetches.Count);
            Assert.Null(fetches[^1]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rt13MalformedBodyDoesNotPublishItsEtag()
    {
        var url = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt");
        var phase = 0;
        string? seen = null;
        var handler = new ScriptedHandler(request =>
        {
            seen = request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (phase == 0)
            {
                response.Content = new StringContent(FixtureUri, Encoding.UTF8, "text/plain");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"good-a\"");
            }
            else if (phase == 1)
            {
                response.Content = new StringContent("this is not a subscription", Encoding.UTF8, "text/plain");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"bad-b\"");
            }
            else if (seen == "\"good-a\"")
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            else
            {
                response.Content = new StringContent("unexpected", Encoding.UTF8, "text/plain");
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"nope\"");
            }

            return response;
        });
        var catalogue = new MemoryCatalogue();
        var ledger = new SourceLedger();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), ledger);
        var item = new RefreshWorkItem { ArtifactId = "BLACK_VLESS_RUS.txt", FamilyId = "black-vless", Urls = [url] };
        var published = await coordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("PUBLISHED", Assert.Single(published.SourceReasons), StringComparison.Ordinal);
        Assert.Equal("\"good-a\"", ledger.EtagFor(url.AbsoluteUri));
        var kept = Assert.Single(catalogue.Nodes).Semantics.Host;

        phase = 1;
        var rejected = await coordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("REJECTED", Assert.Single(rejected.SourceReasons), StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLISHED", rejected.SourceReasons[0], StringComparison.Ordinal);
        Assert.Equal("\"good-a\"", ledger.EtagFor(url.AbsoluteUri));
        Assert.Equal(kept, Assert.Single(catalogue.Nodes).Semantics.Host);

        var directory = Directory.CreateTempSubdirectory("autovpn-rt13-");
        try
        {
            var path = Path.Combine(directory.FullName, "sources.json");
            ledger.Save(path);
            var restarted = SourceLedger.Load(path);
            phase = 2;
            using var restartedFetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
            var restartedCoordinator = new CatalogueCoordinator(catalogue, restartedFetcher, new NonTunCoreProbeTransport(null, null), restarted);
            var unchanged = await restartedCoordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
            Assert.Equal("\"good-a\"", seen);
            Assert.Contains(ReasonCodes.NotModified, Assert.Single(unchanged.SourceReasons), StringComparison.Ordinal);
            Assert.DoesNotContain("PUBLISHED", unchanged.SourceReasons[0], StringComparison.Ordinal);
            Assert.Equal(kept, Assert.Single(catalogue.Nodes).Semantics.Host);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rt17ReadsDoNotExhaustDisconnectAndConflictsStayUncertain()
    {
        var dispatcher = new ProtocolTestDispatcher();
        var caller = new CallerIdentity { Sid = "owner", SessionId = 1 };
        var reads = 0;
        for (var index = 0; index < 1000; index++)
        {
            var response = dispatcher.Dispatch(Request(IpcOperations.GetSnapshot, new { }, "read-" + index.ToString(CultureInfo.InvariantCulture)), caller, _ =>
            {
                reads++;
                return new IpcResponse { RequestId = _.RequestId, Ok = true };
            });
            Assert.True(response.Ok);
        }

        Assert.Equal(1000, reads);
        var disconnects = 0;
        var disconnect = dispatcher.Dispatch(Request(IpcOperations.Disconnect, new DisconnectPayload(), "disconnect-1"), caller, request =>
        {
            disconnects++;
            return new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "stopped" };
        });
        Assert.True(disconnect.Ok);
        Assert.Equal(1, disconnects);

        var uncertainCalls = 0;
        var uncertain = Request(IpcOperations.ApplyRuntimeSet, new { value = 1 }, "effect-1");
        Assert.Throws<InvalidOperationException>(() => dispatcher.Dispatch(uncertain, caller, _ =>
        {
            uncertainCalls++;
            throw new InvalidOperationException("partial");
        }));
        var tombstone = dispatcher.Dispatch(uncertain, caller, _ =>
        {
            uncertainCalls++;
            return new IpcResponse { RequestId = uncertain.RequestId, Ok = true };
        });
        Assert.Equal(1, uncertainCalls);
        Assert.Equal("EFFECT_UNCERTAIN", tombstone.ErrorCode);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Task.Run(() => dispatcher.Dispatch(Request(IpcOperations.Connect, new { late = true }, "late-1"), caller, request =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "late" };
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        dispatcher.ResetOwner();
        release.TrySetResult();
        var lateResponse = await late.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("late", lateResponse.Message);
        var freshCalls = 0;
        var fresh = dispatcher.Dispatch(Request(IpcOperations.Connect, new { late = true }, "late-1"), caller, request =>
        {
            freshCalls++;
            return new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "fresh" };
        });
        Assert.Equal(1, freshCalls);
        Assert.Equal("fresh", fresh.Message);
        dispatcher.ResetOwner();

        var mutations = 0;
        for (var index = 0; index < ProductLimits.IpcIdempotencyEntries; index++)
        {
            dispatcher.Dispatch(Request(IpcOperations.Connect, new { n = index }, "mut-" + index.ToString(CultureInfo.InvariantCulture)), caller, request =>
            {
                mutations++;
                return new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "stored" };
            });
        }

        var replay = dispatcher.Dispatch(Request(IpcOperations.Connect, new { n = 0 }, "mut-0"), caller, _ => throw new InvalidOperationException("replay"));
        Assert.Equal("stored", replay.Message);
        var conflict = dispatcher.Dispatch(Request(IpcOperations.Connect, new { n = 1 }, "mut-0"), caller, _ => throw new InvalidOperationException("conflict"));
        Assert.Equal(ReasonCodes.RequestConflict, conflict.ErrorCode);
        var safety = dispatcher.Dispatch(Request(IpcOperations.RecoverOwned, new { }, "recover-1"), caller, request => new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "recovered" });
        Assert.Equal("recovered", safety.Message);
        var continued = dispatcher.Dispatch(Request(IpcOperations.Connect, new { n = 999 }, "mut-new"), caller, request => new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "continued" });
        Assert.Equal("continued", continued.Message);
        var expiredCalls = 0;
        var expired = dispatcher.Dispatch(Request(IpcOperations.Connect, new { n = 0 }, "mut-0"), caller, _ =>
        {
            expiredCalls++;
            return new IpcResponse { RequestId = "mut-0", Ok = true, Message = "resurrected" };
        });
        Assert.Equal(0, expiredCalls);
        Assert.Equal(ReasonCodes.ReplayExpired, expired.ErrorCode);

        var busyDispatcher = new ProtocolTestDispatcher();
        var blockers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var running = new Task[64];
        for (var index = 0; index < running.Length; index++)
        {
            var id = "busy-" + index.ToString(CultureInfo.InvariantCulture);
            running[index] = busyDispatcher.DispatchAsync(Request(IpcOperations.Connect, new { id }, id), caller, async _ =>
            {
                Interlocked.Increment(ref started);
                await blockers.Task.ConfigureAwait(false);
                return new IpcResponse { RequestId = id, Ok = true };
            });
        }

        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref started) < 64 && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.Equal(64, Volatile.Read(ref started));
        var busy = busyDispatcher.Dispatch(Request(IpcOperations.Connect, new { n = 7 }, "busy-extra"), caller, _ => throw new InvalidOperationException("should-not-run"));
        Assert.Equal("BUSY", busy.ErrorCode);
        var duringBusy = busyDispatcher.Dispatch(Request(IpcOperations.Disconnect, new DisconnectPayload(), "disconnect-busy"), caller, request => new IpcResponse { RequestId = request.RequestId, Ok = true, Message = "still-safe" });
        Assert.Equal("still-safe", duringBusy.Message);
        blockers.TrySetResult();
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rt19LateStartCannotClearTheNewerSession()
    {
        var catalogue = FreshNode(DateTimeOffset.UtcNow);
        var node = catalogue.Nodes[0];
        var core = new LateCore();
        var guard = new ArmingGuard();
        var engine = new BrokerEngine(catalogue, guard, core);
        var connect = engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        await core.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disconnected = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(disconnected.Ok);
        var second = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(second.Ok);
        engine.ConfirmProduction(engine.BootId, second.Snapshot!.Generation, second.Snapshot.OperationId, second.Snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        var active = engine.State.ActiveNodeId;
        var operation = engine.Snapshot().OperationId;
        var generation = engine.Snapshot().Generation;
        var armed = guard.Armed;
        core.ReleaseFirst.TrySetResult();
        var first = await connect.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(first.Ok);
        Assert.Equal(ReasonCodes.Canceled, first.ErrorCode);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        Assert.True(engine.Snapshot().CoreRunning);
        Assert.Equal(active, engine.State.ActiveNodeId);
        Assert.Equal(operation, engine.Snapshot().OperationId);
        Assert.Equal(generation, engine.Snapshot().Generation);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal(armed, guard.Armed);
        Assert.NotEqual(armed, guard.Disarmed);
        Assert.True(catalogue.Nodes[0].ActiveSession);
        Assert.Equal(operation, core.Running);
    }

    [Fact]
    public async Task Rt21DeadCoreCannotBeConfirmedAndCooldownCanStageAnotherAttempt()
    {
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero) };
        var catalogue = TwoNodes(clock.UtcNow);
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), new ImmediateCore(), null, clock);
        var first = catalogue.Nodes[0];
        var second = catalogue.Nodes[1];
        var connected = await engine.HandleAsync(Connect(first, catalogue.NetworkEpoch), CancellationToken.None);
        Confirm(engine, connected, catalogue.NetworkEpoch);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = engine.State.ActiveNodeId == first.NodeId ? second : first;
            await engine.HandleAsync(Standby(target, engine.Snapshot().Revision), CancellationToken.None);
            var switched = await engine.HandleAsync(Health(nameof(FailureKind.CoreExit), catalogue.NetworkEpoch, engine.Snapshot().Revision), CancellationToken.None);
            Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
            Confirm(engine, switched, catalogue.NetworkEpoch);
        }

        var operation = engine.Snapshot().OperationId;
        var generation = engine.Snapshot().Generation;
        var active = engine.State.ActiveNodeId;
        var held = await engine.HandleAsync(Health(nameof(FailureKind.CoreExit), catalogue.NetworkEpoch, engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(held.Ok);
        Assert.Equal(TunnelPhase.Reconnecting, engine.State.Phase);
        Assert.False(engine.Snapshot().CoreRunning);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal(active, engine.State.ActiveNodeId);
        engine.ConfirmProduction(engine.BootId, generation, operation, active, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Reconnecting, engine.State.Phase);
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);

        clock.Advance(TimeSpan.FromSeconds(ProductLimits.SwitchCooldownSeconds + 1));
        foreach (var node in catalogue.Nodes)
        {
            catalogue.ApplyAssessment(node.NodeId, node.Assessment! with { LastSuccessUtc = clock.UtcNow });
        }

        var alternate = engine.State.ActiveNodeId == first.NodeId ? second : first;
        await engine.HandleAsync(Standby(alternate, engine.Snapshot().Revision), CancellationToken.None);
        var staged = await engine.HandleAsync(Health(nameof(FailureKind.CoreExit), catalogue.NetworkEpoch, engine.Snapshot().Revision), CancellationToken.None);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.True(engine.Snapshot().CoreRunning);
        Confirm(engine, staged, catalogue.NetworkEpoch);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);

        var pinned = TwoNodes(DateTimeOffset.UtcNow);
        pinned.Settings = pinned.Settings with { SelectionMode = SelectionMode.Pinned };
        var pinnedEngine = new BrokerEngine(pinned, new ArmingGuard(), new ImmediateCore());
        var pinnedConnect = await pinnedEngine.HandleAsync(Connect(pinned.Nodes[0], pinned.NetworkEpoch), CancellationToken.None);
        Confirm(pinnedEngine, pinnedConnect, pinned.NetworkEpoch);
        await pinnedEngine.HandleAsync(Health(nameof(FailureKind.CoreExit), pinned.NetworkEpoch, pinnedEngine.Snapshot().Revision), CancellationToken.None);
        Assert.NotEqual(TunnelPhase.Connected, pinnedEngine.State.Phase);
        Assert.False(pinnedEngine.Snapshot().CoreRunning);
        pinnedEngine.ConfirmProduction(pinnedEngine.BootId, pinnedConnect.Snapshot!.Generation, pinnedConnect.Snapshot.OperationId, pinned.Nodes[0].NodeId, pinned.NetworkEpoch, true, null);
        Assert.NotEqual(TunnelPhase.Connected, pinnedEngine.State.Phase);
    }

    [Fact]
    public async Task Rt21DuplicateHealthDuringSwitchDoesNotBlockTheAttempt()
    {
        var catalogue = TwoNodes(DateTimeOffset.UtcNow);
        var core = new SwitchGate();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), core);
        var connected = await engine.HandleAsync(Connect(catalogue.Nodes[0], catalogue.NetworkEpoch), CancellationToken.None);
        Confirm(engine, connected, catalogue.NetworkEpoch);
        await engine.HandleAsync(Standby(catalogue.Nodes[1], engine.Snapshot().Revision), CancellationToken.None);
        var switching = engine.HandleAsync(Health(nameof(FailureKind.CoreExit), catalogue.NetworkEpoch, engine.Snapshot().Revision), CancellationToken.None);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var duplicate = await engine.HandleAsync(Health(nameof(FailureKind.CoreExit), catalogue.NetworkEpoch, engine.Snapshot().Revision), CancellationToken.None);
        Assert.NotEqual(TunnelPhase.Blocked, engine.State.Phase);
        core.Release.TrySetResult();
        var switched = await switching.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(switched.Ok);
        Assert.True(duplicate.Ok);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        Assert.Equal(catalogue.Nodes[1].NodeId, engine.State.ActiveNodeId);
        Assert.NotEqual(TunnelPhase.Blocked, engine.State.Phase);
    }

    [Fact]
    public void Rt23SecurityAndH2UseTheCanonicalEmitter()
    {
        var wire = new NodeWire
        {
            NodeId = "node",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = "11111111-1111-4111-8111-111111111111",
            Encryption = "none",
            Security = " TLS ",
            Sni = "www.example.com",
        };
        var tls = Profile(wire);
        Assert.Contains("tls: true", tls, StringComparison.Ordinal);
        var upper = Profile(new NodeWire
        {
            NodeId = wire.NodeId,
            Digest = wire.Digest,
            Protocol = wire.Protocol,
            Host = wire.Host,
            Port = wire.Port,
            UserId = wire.UserId,
            Encryption = wire.Encryption,
            Security = "TLS",
            Sni = wire.Sni,
        });
        Assert.Contains("tls: true", upper, StringComparison.Ordinal);
        var h2 = Profile(new NodeWire
        {
            NodeId = "h2",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = "11111111-1111-4111-8111-111111111111",
            Encryption = "none",
            Security = "tls",
            Sni = "www.example.com",
            Transport = "h2",
            Path = "/custom-h2",
        });
        Assert.Contains("network: 'h2'", h2, StringComparison.Ordinal);
        Assert.Contains("h2-opts:", h2, StringComparison.Ordinal);
        Assert.Contains("/custom-h2", h2, StringComparison.Ordinal);
        Assert.DoesNotContain("http-opts:", h2, StringComparison.Ordinal);
        var socket = Profile(new NodeWire
        {
            NodeId = wire.NodeId,
            Digest = wire.Digest,
            Protocol = wire.Protocol,
            Host = wire.Host,
            Port = wire.Port,
            UserId = wire.UserId,
            Encryption = wire.Encryption,
            Security = "tls",
            Sni = wire.Sni,
            Transport = "websocket",
        });
        Assert.Contains("network: 'ws'", socket, StringComparison.Ordinal);
    }

    [Fact]
    public void Rt25UdpIdentityMatchesEmissionAndDropsUnprovenHealth()
    {
        var imported = Assert.Single(SubscriptionImporter.Import(FixtureUri).Records).Semantics!;
        var absent = imported with { Udp = null };
        var disabled = imported with { Udp = false };
        var enabled = imported with { Udp = true };
        Assert.Equal(CanonicalIdentity.Digest(absent), CanonicalIdentity.Digest(disabled));
        Assert.NotEqual(CanonicalIdentity.Digest(absent), CanonicalIdentity.Digest(enabled));
        Assert.Contains("udp: false", Profile(Wire(absent)), StringComparison.Ordinal);
        Assert.Contains("udp: false", Profile(Wire(disabled)), StringComparison.Ordinal);
        Assert.Contains("udp: true", Profile(Wire(enabled)), StringComparison.Ordinal);

        var catalogue = new MemoryCatalogue();
        catalogue.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "list",
            FamilyId = "black-vless",
            ContentHash = "a",
            Complete = true,
            NowUtc = DateTimeOffset.UtcNow,
            Nodes =
            [
                Snapshot(absent, "absent"),
                Snapshot(enabled, "enabled"),
            ],
        });
        Assert.Equal(2, catalogue.Nodes.Count);
        Assert.Equal(2, catalogue.Nodes.Select(node => node.Digest).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Rt28InsecureProxyFlagDoesNotAuthenticateTheProbeTarget()
    {
        var semantics = new NodeSemantics
        {
            Protocol = ProtocolKind.Vless,
            Host = "candidate.example",
            Port = 443,
            UserId = "11111111-1111-4111-8111-111111111111",
            Encryption = "none",
            Security = "tls",
            Sni = "www.example.com",
            SkipCertVerify = true,
        };
        var node = new CatalogueNode
        {
            NodeId = "insecure",
            Digest = CanonicalIdentity.Digest(semantics),
            Semantics = semantics,
            Label = "insecure",
            FirstSeenUtc = DateTimeOffset.UnixEpoch,
            LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
        var allowed = NonTunCoreProbeTransport.BuildProbeYaml(node, 9, 10, allowInsecureProxyCertificates: true);
        Assert.Contains("skip-cert-verify: true", allowed, StringComparison.Ordinal);
        var denied = Assert.Throws<InvalidOperationException>(() => NonTunCoreProbeTransport.BuildProbeYaml(node, 9, 10));
        Assert.Equal(ReasonCodes.CertVerificationDisabled, denied.Message);

        await using var peer = await TlsPeer.StartAsync(Issue("probe.example"), "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        var exchange = await ExchangeAsync(await ForwardAsync(peer.Port), peer.Port, null);
        Assert.Equal("TLS_REJECTED", exchange.Failure);
        Assert.False(exchange.Authenticated);
    }

    private static async Task chattyDisposedSibling(ProbeWorker kept, string deadMarker, string liveMarker)
    {
        await kept.DisposeAsync();
        Assert.False(Alive(liveMarker));
        Assert.True(Alive(deadMarker));
    }

    private static async Task<TlsProbeExchange> ExchangeAsync(IPEndPoint proxy, int port, X509Certificate2Collection? anchors)
    {
        return await Socks5Client.ExchangeAsync(proxy, Target(port), TimeSpan.FromSeconds(3), anchors, CancellationToken.None);
    }

    private static async Task<IPEndPoint> ForwardAsync(int port)
    {
        var server = await BytePipe.StartAsync(port);
        return server;
    }

    private static Uri Target(int port)
    {
        return new Uri("https://probe.example:" + port.ToString(CultureInfo.InvariantCulture) + "/generate_204");
    }

    private static X509Certificate2 Issue(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var exported = certificate.Export(X509ContentType.Pfx, "round2");
        return X509CertificateLoader.LoadPkcs12(exported, "round2", X509KeyStorageFlags.Exportable);
    }

    private static NodeSemantics ShadowsocksNode(int port, string password)
    {
        return new NodeSemantics
        {
            Protocol = ProtocolKind.Shadowsocks,
            Host = "candidate.example",
            Port = port,
            Password = password,
            Encryption = "aes-256-gcm",
        };
    }

    private static MemoryCatalogue CatalogueWith(NodeSemantics semantics)
    {
        var catalogue = new MemoryCatalogue();
        catalogue.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "ss",
            FamilyId = "black-ss-weak-dpi",
            ContentHash = "ss",
            Complete = true,
            NowUtc = DateTimeOffset.UtcNow,
            Nodes = [Snapshot(semantics, semantics.Host)],
        });
        return catalogue;
    }

    private static MemoryCatalogue FreshNode(DateTimeOffset success)
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        RefreshMerge.Ingest(catalogue, [Body("list", FixtureUri, "a")], success, false);
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
        {
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = success,
            MedianLatencyMs = 40,
        });
        return catalogue;
    }

    private static MemoryCatalogue TwoNodes(DateTimeOffset success)
    {
        var catalogue = FreshNode(success);
        RefreshMerge.Ingest(catalogue, [Body("list-b", FixtureUri.Replace("203.0.113.10", "203.0.113.21", StringComparison.Ordinal).Replace("#fixture", "#other", StringComparison.Ordinal), "b")], success, false);
        var other = catalogue.Nodes.Single(node => node.Semantics.Host == "203.0.113.21");
        catalogue.ApplyAssessment(other.NodeId, new AssessmentSnapshot
        {
            Digest = other.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = success,
            MedianLatencyMs = 50,
        });
        return catalogue;
    }

    private static IngestArtifact Body(string artifact, string text, string hash)
    {
        return new IngestArtifact { ArtifactId = artifact, FamilyId = "black-vless", Enabled = true, Text = text, ContentHash = hash };
    }

    private static SnapshotNode Snapshot(NodeSemantics semantics, string label)
    {
        return new SnapshotNode
        {
            Digest = CanonicalIdentity.Digest(semantics),
            Semantics = semantics,
            Label = label,
            FamilyId = "black-vless",
            ArtifactId = "list",
        };
    }

    private static ReviewedRegistry Registry()
    {
        return new ReviewedRegistry
        {
            Owner = "igareck",
            Repository = "vpn-configs-for-russia",
            PinnedCommit = "20c38289c29e4dba6b8f01ddd3273ec9ec169b46",
            TreeApi = new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1"),
            FamilyIds = ["black-vless"],
            FetchOrigins =
            [
                new ApprovedFetchOrigin("api.github.com", 443, "/repos/igareck/vpn-configs-for-russia/git/trees/"),
                new ApprovedFetchOrigin("raw.githubusercontent.com", 443, "/igareck/vpn-configs-for-russia/"),
            ],
            ApprovedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" },
            RejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ProbeTargets = [],
        };
    }

    private static string TreeJson()
    {
        return "{\"sha\":\"" + TreeSha + "\",\"truncated\":false,\"tree\":[{\"path\":\"BLACK_VLESS_RUS.txt\",\"type\":\"blob\",\"size\":12}]}";
    }

    private static string Profile(NodeWire wire)
    {
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes = [wire],
            SelectedNodeId = wire.NodeId,
        });
    }

    private static NodeWire Wire(NodeSemantics semantics)
    {
        return new NodeWire
        {
            NodeId = "node",
            Digest = CanonicalIdentity.Digest(semantics),
            Protocol = semantics.Protocol.ToString().ToLowerInvariant(),
            Host = semantics.Host,
            Port = semantics.Port,
            UserId = semantics.UserId,
            Password = semantics.Password,
            Encryption = semantics.Encryption,
            Security = semantics.Security,
            Sni = semantics.Sni,
            Transport = semantics.Transport,
            Path = semantics.Path,
            Udp = semantics.Udp,
        };
    }

    private static IpcRequest Connect(CatalogueNode node, long epoch)
    {
        return Request(IpcOperations.Connect, new ConnectPayload { NodeId = node.NodeId, Digest = node.Digest, NetworkEpoch = epoch });
    }

    private static IpcRequest Standby(CatalogueNode node, long revision)
    {
        return Request(IpcOperations.ApplyRuntimeSet, new Dictionary<string, object>
        {
            ["standbys"] = new[]
            {
                new StandbyCandidate
                {
                    NodeId = node.NodeId,
                    EndpointKey = node.Semantics.Host,
                    Country = "",
                    SourceFamilyId = "",
                    Cost = 1,
                    FreshOnEpoch = true,
                },
            },
        }, revision: revision);
    }

    private static IpcRequest Health(string kind, long epoch, long revision)
    {
        return Request(IpcOperations.ReportHealth, new HealthPayload
        {
            FailureKind = kind,
            ConsecutiveFailures = 3,
            NetworkEpoch = epoch,
        }, revision: revision);
    }

    private static void Confirm(BrokerEngine engine, IpcResponse response, long epoch)
    {
        engine.ConfirmProduction(engine.BootId, response.Snapshot!.Generation, response.Snapshot.OperationId, response.Snapshot.ActiveNodeId, epoch, true, null);
    }

    private static IpcRequest Request(string operation, object payload, string? id = null, long revision = 0)
    {
        return new IpcRequest
        {
            ProtocolVersion = ProductLimits.IpcProtocolVersion,
            RequestId = id ?? Guid.NewGuid().ToString("N"),
            ExpectedStateRevision = revision,
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
        };
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }

    private static bool Alive(string marker)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pgrep", "-f " + marker)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        process!.WaitForExit(2000);
        return process.ExitCode == 0;
    }

    private static void KillMarker(string marker)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pkill", "-f " + marker)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        process?.WaitForExit(2000);
    }

    private static ProbeObservation Proven(NodeSemantics node, int latency, int bytes)
    {
        return new ProbeObservation(true, latency, false, null, bytes, ProbeClass.Success, "https://probe.example/generate_204", CanonicalIdentity.Digest(node), "unit-proof");
    }

    private sealed class ScriptedTransport(Func<NodeSemantics, ProbeObservation> next) : BoundTestProbeTransport
    {
        public override Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(next(node));
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(next(request));
        }
    }

    private sealed class ArmingGuard : INetworkGuard
    {
        public long Armed { get; private set; } = -1;
        public long Disarmed { get; private set; } = -1;

        public GuardResult Arm(GuardRequest request)
        {
            Armed = request.Generation;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            Disarmed = generation;
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, effects.Select(effect => effect.Id).ToArray());
        }
    }

    private sealed class ImmediateCore : ICoreController
    {
        public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CoreStartResult(true, null));
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class LateCore : ICoreController
    {
        private int _starts;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Running { get; private set; } = "";

        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _starts) == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new CoreStartResult(true, null);
            }

            Running = operationId;
            return new CoreStartResult(true, null);
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            if (!string.Equals(operationId, Running, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            Running = "";
            return Task.CompletedTask;
        }
    }

    private sealed class SwitchGate : ICoreController
    {
        private int _starts;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _starts) == 1)
            {
                return new CoreStartResult(true, null);
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CoreStartResult(true, null);
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

sealed class TlsPeer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly string _response;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private TlsPeer(TcpListener listener, X509Certificate2 certificate, string response)
    {
        _listener = listener;
        _certificate = certificate;
        _response = response;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; private init; }
    public int Accepts { get; private set; }
    public string? Sni { get; private set; }

    public static Task<TlsPeer> StartAsync(X509Certificate2 certificate, string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var peer = new TlsPeer(listener, certificate, response)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        };
        return Task.FromResult(peer);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        await using (var ssl = new SslStream(stream, leaveInnerStreamOpen: false))
        {
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _stop.Token).ConfigureAwait(false);
                Accepts++;
                Sni = ssl.TargetHostName;
                var buffer = new byte[2048];
                await ssl.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                var bytes = Encoding.ASCII.GetBytes(_response);
                await ssl.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }
}

file sealed class PlainSocks : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _payload;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private PlainSocks(TcpListener listener, string payload)
    {
        _listener = listener;
        _payload = payload;
        _loop = Task.Run(AcceptLoop);
    }

    public IPEndPoint Proxy { get; private init; } = new(IPAddress.Loopback, 0);

    public static Task<PlainSocks> StartAsync(string payload)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new PlainSocks(listener, payload)
        {
            Proxy = (IPEndPoint)listener.LocalEndpoint,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    await ReadSocksConnectAsync(stream).ConfigureAwait(false);
                    await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }).ConfigureAwait(false);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_payload)).ConfigureAwait(false);
                }
            });
        }
    }

    internal static async Task ReadSocksConnectAsync(NetworkStream stream)
    {
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }).ConfigureAwait(false);
        var head = new byte[4];
        await stream.ReadExactlyAsync(head).ConfigureAwait(false);
        var rest = head[3] switch
        {
            0x01 => 4 + 2,
            0x04 => 16 + 2,
            0x03 => (await ReadByteAsync(stream).ConfigureAwait(false)) + 2,
            _ => 0,
        };
        if (rest > 0)
        {
            await stream.ReadExactlyAsync(new byte[rest]).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one).ConfigureAwait(false);
        return one[0];
    }
}

file static class BytePipe
{
    public static async Task<IPEndPoint> StartAsync(int targetPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                await using var inbound = client.GetStream();
                await PlainSocks.ReadSocksConnectAsync(inbound).ConfigureAwait(false);
                await inbound.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }).ConfigureAwait(false);
                using var target = new TcpClient();
                await target.ConnectAsync(IPAddress.Loopback, targetPort, stop.Token).ConfigureAwait(false);
                await using var outbound = target.GetStream();
                var left = inbound.CopyToAsync(outbound, stop.Token);
                var right = outbound.CopyToAsync(inbound, stop.Token);
                await Task.WhenAny(left, right).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
            finally
            {
                listener.Stop();
            }
        });
        return (IPEndPoint)listener.LocalEndpoint;
    }
}

sealed class ShadowsocksAeadServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _key;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private ShadowsocksAeadServer(TcpListener listener, byte[] key, int targetPort)
    {
        _listener = listener;
        _key = key;
        _targetPort = targetPort;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; private init; }
    public int Handshakes { get; private set; }

    public static Task<ShadowsocksAeadServer> StartAsync(string password, int targetPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new ShadowsocksAeadServer(listener, Derive(password, 32), targetPort)
        {
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var salt = await ReadExactAsync(stream, 32).ConfigureAwait(false);
                using var decrypt = new AeadDirection(_key, salt);
                var serverSalt = RandomNumberGenerator.GetBytes(32);
                await stream.WriteAsync(serverSalt).ConfigureAwait(false);
                using var encrypt = new AeadDirection(_key, serverSalt);
                var plain = new MemoryStream();
                while (AddressSize(plain) is null)
                {
                    var chunk = await ReadChunkAsync(stream, decrypt).ConfigureAwait(false);
                    plain.Write(chunk);
                }

                Handshakes++;
                var address = AddressSize(plain) ?? 0;
                using var target = new TcpClient();
                await target.ConnectAsync(IPAddress.Loopback, _targetPort).ConfigureAwait(false);
                await using var outbound = target.GetStream();
                if (plain.Length > address)
                {
                    await outbound.WriteAsync(plain.ToArray().AsMemory(address)).ConfigureAwait(false);
                }

                var up = PumpAsync(stream, outbound, decrypt, encrypt: null);
                var down = PumpAsync(outbound, stream, decrypt: null, encrypt);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or AuthenticationTagMismatchException or IOException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    private static async Task PumpAsync(Stream source, Stream destination, AeadDirection? decrypt, AeadDirection? encrypt)
    {
        if (decrypt is not null)
        {
            while (true)
            {
                var chunk = await ReadChunkAsync(source, decrypt).ConfigureAwait(false);
                await destination.WriteAsync(chunk).ConfigureAwait(false);
            }
        }

        var buffer = new byte[4096];
        while (true)
        {
            var count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(encrypt!.Pack(buffer.AsSpan(0, count))).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadChunkAsync(Stream stream, AeadDirection direction)
    {
        var lengthBytes = await ReadExactAsync(stream, 18).ConfigureAwait(false);
        var lengthPlain = direction.Open(lengthBytes);
        var length = (lengthPlain[0] << 8) | lengthPlain[1];
        length &= 0x3FFF;
        return direction.Open(await ReadExactAsync(stream, length + 16).ConfigureAwait(false));
    }

    private static int? AddressSize(MemoryStream plain)
    {
        var data = plain.ToArray();
        if (data.Length == 0)
        {
            return null;
        }

        var size = data[0] switch
        {
            0x01 => 7,
            0x04 => 19,
            0x03 when data.Length >= 2 => 2 + data[1] + 2,
            _ => 0,
        };
        return size > 0 && data.Length >= size ? size : null;
    }

    private static byte[] Derive(string password, int length)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var material = new byte[length];
        var previous = Array.Empty<byte>();
        var filled = 0;
        while (filled < length)
        {
            var block = MD5.HashData(previous.Concat(passwordBytes).ToArray());
            var copy = Math.Min(block.Length, length - filled);
            block.AsSpan(0, copy).CopyTo(material.AsSpan(filled));
            filled += copy;
            previous = block;
        }

        return material;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).ConfigureAwait(false);
        return buffer;
    }

    private sealed class AeadDirection : IDisposable
    {
        private readonly AesGcm _cipher;
        private readonly byte[] _nonce = new byte[12];

        public AeadDirection(byte[] key, byte[] salt)
        {
            var subkey = HKDF.DeriveKey(HashAlgorithmName.SHA1, key, key.Length, salt, "ss-subkey"u8.ToArray());
            _cipher = new AesGcm(subkey, 16);
        }

        public byte[] Open(byte[] packet)
        {
            var cipherLength = packet.Length - 16;
            var plain = new byte[cipherLength];
            _cipher.Decrypt(_nonce, packet.AsSpan(0, cipherLength), packet.AsSpan(cipherLength), plain);
            Increment();
            return plain;
        }

        public byte[] Pack(ReadOnlySpan<byte> plain)
        {
            var length = new byte[2];
            length[0] = (byte)(plain.Length >> 8);
            length[1] = (byte)plain.Length;
            var output = new byte[2 + 16 + plain.Length + 16];
            _cipher.Encrypt(_nonce, length, output.AsSpan(0, 2), output.AsSpan(2, 16));
            Increment();
            _cipher.Encrypt(_nonce, plain, output.AsSpan(18, plain.Length), output.AsSpan(18 + plain.Length, 16));
            Increment();
            return output;
        }

        public void Dispose()
        {
            _cipher.Dispose();
        }

        private void Increment()
        {
            for (var index = 0; index < _nonce.Length; index++)
            {
                _nonce[index]++;
                if (_nonce[index] != 0)
                {
                    break;
                }
            }
        }
    }
}
