using System.Diagnostics;
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
using Microsoft.Data.Sqlite;

namespace AutoVpn.UnitTests;

/// <summary>
/// Regressions named by the 2026-10-02 audit. Each test calls production
/// import, catalogue, probe, or broker code. None of them starts Mihomo
/// or changes this host's network.
/// </summary>
public class AuditRegressionTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private const string Uuid2 = "22222222-2222-4222-8222-222222222222";
    private const string Uuid3 = "33333333-3333-4333-8333-333333333333";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void At07CoreExitAtTheSwitchCapHoldsProtectionInsteadOfStayingConnected()
    {
        var standby = new StandbyCandidate
        {
            NodeId = "other",
            EndpointKey = "203.0.113.21:443",
            Country = "",
            SourceFamilyId = "black-vless",
            Cost = 1,
            FreshOnEpoch = true,
        };
        var held = FailoverPolicy.Decide(Budget(FailureKind.CoreExit, ProductLimits.MaxSwitchesPerMinute, standby));
        Assert.Equal(FailoverAction.HoldProtected, held.Action);
        Assert.Equal("current", held.NodeId);
        Assert.Equal("SWITCH_BUDGET", held.ReasonCode);

        var cooled = FailoverPolicy.Decide(Budget(FailureKind.CoreExit, 0, standby) with { CooldownActive = true });
        Assert.Equal(FailoverAction.HoldProtected, cooled.Action);

        var live = FailoverPolicy.Decide(Budget(FailureKind.HealthTimeout, ProductLimits.MaxSwitchesPerMinute, standby) with
        {
            ConsecutiveHealthFailures = ProductLimits.HealthFailuresBeforeOutage,
        });
        Assert.Equal(FailoverAction.WaitCooldown, live.Action);
        Assert.Equal("current", live.NodeId);

        var early = FailoverPolicy.Decide(Budget(FailureKind.CoreExit, ProductLimits.MaxSwitchesPerMinute - 1, standby));
        Assert.Equal(FailoverAction.Switch, early.Action);
        Assert.Equal("other", early.NodeId);

        var outage = FailoverPolicy.Decide(Budget(FailureKind.TargetOutage, ProductLimits.MaxSwitchesPerMinute, standby));
        Assert.Equal(FailoverAction.DiagnoseTargets, outage.Action);
    }

    [Fact]
    public async Task At13StaleHealthyIsRecheckedAndFreshHealthyIsNot()
    {
        var catalogue = ImportMany(
            Vless(Uuid, "203.0.113.10"),
            Vless(Uuid2, "203.0.113.11"));
        var stale = catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".10", StringComparison.Ordinal));
        var fresh = catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".11", StringComparison.Ordinal));
        catalogue.ApplyAssessment(stale.NodeId, Healthy(stale, Now.AddMinutes(-(ProductLimits.CatalogueFreshnessMinutes + 1))));
        catalogue.ApplyAssessment(fresh.NodeId, Healthy(fresh, Now));
        Assert.True(ProbeCoordinator.NeedsProbe(stale, Now, catalogue.NetworkEpoch));
        Assert.False(ProbeCoordinator.NeedsProbe(fresh, Now, catalogue.NetworkEpoch));

        var seen = new List<string>();
        await ProbeCoordinator.RunAsync(catalogue, Transport(node =>
        {
            seen.Add(node.Host);
            return Proven(node, 40);
        }), Target, Now, CancellationToken.None);

        Assert.Equal([stale.Semantics.Host], seen);
        Assert.Equal(Now, stale.Assessment!.LastSuccessUtc);
        Assert.Equal(Healthy(fresh, Now).LastSuccessUtc, fresh.Assessment!.LastSuccessUtc);
    }

    [Fact]
    public async Task At14EpochChangeRechecksAndDiscardsASuccessFromTheOldEpoch()
    {
        var catalogue = ImportMany(Vless(Uuid, "203.0.113.10"));
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, Healthy(node, Now));
        catalogue.SetNetworkEpoch(2);
        Assert.True(ProbeCoordinator.NeedsProbe(node, Now, catalogue.NetworkEpoch));

        await ProbeCoordinator.RunAsync(catalogue, Transport(node =>
        {
            catalogue.SetNetworkEpoch(3);
            return Proven(node, 15);
        }), Target, Now, CancellationToken.None);

        Assert.Equal(3, catalogue.NetworkEpoch);
        Assert.Equal(HealthState.Healthy, node.Assessment!.Health);
        Assert.Equal(1, node.Assessment.NetworkEpoch);
        Assert.False(MemoryCatalogue.IsEligible(node, new EligibilityContext
        {
            NowUtc = Now,
            NetworkEpoch = catalogue.NetworkEpoch,
        }));
    }

    [Fact]
    public async Task At15ThirdSameEndpointVariantIsNotStarved()
    {
        var catalogue = ImportMany(
            Vless(Uuid, "203.0.113.10", "path=%2Fa"),
            Vless(Uuid2, "203.0.113.10", "path=%2Fb"),
            Vless(Uuid3, "203.0.113.10", "path=%2Fc"));
        Assert.Equal(3, catalogue.Nodes.Select(node => node.Semantics.Host + ":" + node.Semantics.Port).Distinct(StringComparer.Ordinal).Count() == 1
            ? 3
            : 0);
        var seen = new List<string>();
        var report = await ProbeCoordinator.RunAsync(catalogue, Transport(node =>
        {
            seen.Add(node.UserId!);
            return new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed);
        }), Target, Now, CancellationToken.None, TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds));
        Assert.Equal(3, report.Attempted);
        Assert.Equal(3, report.Failed);
        Assert.Equal(3, seen.Distinct(StringComparer.Ordinal).Count());

        var rotated = ImportMany(
            Vless(Uuid, "203.0.113.12"),
            Vless(Uuid2, "203.0.113.12"),
            Vless(Uuid3, "203.0.113.12"));
        var acrossPasses = new List<string>();
        for (var pass = 0; pass < 3; pass++)
        {
            await ProbeCoordinator.RunAsync(rotated, Transport(node =>
            {
                acrossPasses.Add(node.UserId!);
                return new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed);
            }), Target, Now, CancellationToken.None, TimeSpan.Zero);
        }

        Assert.Equal(3, acrossPasses.Count);
        Assert.Equal(3, acrossPasses.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task At16HungProbeIsCancelledAndBudgetsLeaveTheRestPending()
    {
        var hung = ImportMany(
            Vless(Uuid, "203.0.113.21"),
            Vless(Uuid2, "203.0.113.22"),
            Vless(Uuid3, "203.0.113.23"));
        var started = Stopwatch.StartNew();
        var run = ProbeCoordinator.RunAsync(
            hung,
            new AsyncTransport(async (node, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Proven(node, 1);
            }),
            Target,
            Now,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(80));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(run, finished);
        var report = await run;
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(3, report.Attempted);
        Assert.Equal(3, report.Failed);
        Assert.All(hung.Nodes, node => Assert.Equal(HealthState.Failed, node.Assessment!.Health));

        var limited = ImportMany(
            Vless(Uuid, "203.0.113.31"),
            Vless(Uuid2, "203.0.113.32"));
        var calls = 0;
        await ProbeCoordinator.RunAsync(limited, Transport(node =>
        {
            calls++;
            return Proven(node, 20, 25);
        }), Target, Now, CancellationToken.None, byteBudget: 25);
        Assert.Equal(1, calls);
        Assert.Equal(1, limited.Nodes.Count(node => node.Assessment!.Health == HealthState.Healthy));
        Assert.Equal(1, limited.Nodes.Count(node => node.Assessment!.Health == HealthState.Pending));

        var cancelled = ImportMany(
            Vless(Uuid, "203.0.113.41"),
            Vless(Uuid2, "203.0.113.42"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var cancelRun = ProbeCoordinator.RunAsync(cancelled, new AsyncTransport(async (node, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Proven(node, 1);
        }), Target, Now, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        var cancelFinished = await Task.WhenAny(cancelRun, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(cancelRun, cancelFinished);
        await cancelRun;
        Assert.All(cancelled.Nodes, node =>
        {
            Assert.Equal(HealthState.Pending, node.Assessment!.Health);
            Assert.Null(node.Assessment.LastFailureUtc);
        });

        var inflight = 0;
        var maxInflight = 0;
        var gate = new object();
        var overlapped = ImportMany(
            Vless(Uuid, "203.0.113.51"),
            Vless(Uuid2, "203.0.113.52"),
            Vless(Uuid3, "203.0.113.53"));
        await ProbeCoordinator.RunAsync(overlapped, new AsyncTransport(async (node, token) =>
        {
            lock (gate)
            {
                inflight++;
                maxInflight = Math.Max(maxInflight, inflight);
            }

            try
            {
                await Task.Delay(30, token).ConfigureAwait(false);
                return Proven(node, 5);
            }
            finally
            {
                lock (gate)
                {
                    inflight--;
                }
            }
        }), Target, Now, CancellationToken.None);
        Assert.InRange(maxInflight, 1, ProductLimits.MaxProbesPerEndpoint);
    }

    [Fact]
    public async Task PreConnectOlderThanSixtySecondsIsNotAdmission()
    {
        var stale = ImportMany(Vless(Uuid, "203.0.113.10"));
        stale.Settings = stale.Settings with { DisclosureAccepted = true };
        var node = stale.Nodes[0];
        var old = DateTimeOffset.UtcNow.AddSeconds(-(ProductLimits.PreConnectFreshnessSeconds + 5));
        stale.ApplyAssessment(node.NodeId, Healthy(node, old));
        Assert.True(MemoryCatalogue.IsEligible(node, new EligibilityContext
        {
            NowUtc = DateTimeOffset.UtcNow,
            NetworkEpoch = stale.NetworkEpoch,
            AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
        }));
        var engine = new BrokerEngine(stale, new UnavailableNetworkGuard(), new RefusingCoreController());
        var response = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = stale.NetworkEpoch,
        }), CancellationToken.None);
        Assert.Equal(ReasonCodes.NoEligibleServer, response.ErrorCode);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);

        var fresh = ImportMany(Vless(Uuid2, "203.0.113.11"));
        fresh.Settings = fresh.Settings with { DisclosureAccepted = true };
        var candidate = fresh.Nodes[0];
        fresh.ApplyAssessment(candidate.NodeId, Healthy(candidate, DateTimeOffset.UtcNow));
        var admitted = new BrokerEngine(fresh, new UnavailableNetworkGuard(), new RefusingCoreController());
        var refused = await admitted.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = candidate.NodeId,
            Digest = candidate.Digest,
            NetworkEpoch = fresh.NetworkEpoch,
        }), CancellationToken.None);
        Assert.Equal(UnavailableNetworkGuard.PlatformReason(), refused.ErrorCode);
        Assert.NotEqual(TunnelPhase.Connected, admitted.State.Phase);
    }

    [Fact]
    public void At18InvalidSnapshotsKeepLastGoodMembershipAndRecognizedEmptyDoesNot()
    {
        var catalogue = new MemoryCatalogue();
        Ingest(catalogue, "alpha-body", "family-a", Vless(Uuid, "203.0.113.10"));
        Ingest(catalogue, "beta-body", "family-b", Vless(Uuid2, "203.0.113.20"));
        var bad = new[]
        {
            "{",
            "<html><body>login</body></html>",
            """{"outbounds":""",
            "{}",
            """{"outbounds":{}}""",
            "proxies:\n  name: not-a-list\n",
            "vless://not-a-uuid@203.0.113.61:443#x",
        };
        foreach (var text in bad)
        {
            var report = Ingest(catalogue, "alpha-body", "family-a", text);
            Assert.False(report.RefetchRequired);
            Assert.Contains("family-a", catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".10", StringComparison.Ordinal)).CurrentFamilies);
            Assert.Contains("family-b", catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".20", StringComparison.Ordinal)).CurrentFamilies);
        }

        var missingCache = RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "never-stored", FamilyId = "family-a", Enabled = true, NotModified = true },
        ], Now, false);
        Assert.True(missingCache.RefetchRequired);
        Assert.Equal(2, catalogue.Nodes.Count(node => node.CurrentFamilies.Count > 0));

        var cached = RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "alpha-body", FamilyId = "family-a", Enabled = true, NotModified = true, ContentHash = "same" },
        ], Now, false);
        Assert.False(cached.RefetchRequired);
        Assert.Contains("alpha-body", catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".10", StringComparison.Ordinal)).ArtifactFamilies.Keys);

        Ingest(catalogue, "alpha-body", "family-a", "proxies: []\n");
        var removed = catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".10", StringComparison.Ordinal));
        Assert.DoesNotContain("family-a", removed.CurrentFamilies);
        Assert.DoesNotContain("alpha-body", removed.ArtifactFamilies.Keys);
        Assert.Contains("family-b", catalogue.Nodes.Single(node => node.Semantics.Host.EndsWith(".20", StringComparison.Ordinal)).CurrentFamilies);
    }

    [Fact]
    public void At19MalformedRecordDoesNotAbortTheBatchOrLookEmpty()
    {
        var badJson = Convert.ToBase64String("{not-json"u8.ToArray());
        var text = $"vmess://{badJson}\n{Vless(Uuid, "203.0.113.10")}";
        var batch = SubscriptionImporter.Import(text);
        Assert.True(batch.DocumentValid);
        Assert.False(batch.EmptyValidDocument);
        Assert.Equal(2, batch.Total);
        Assert.Contains(batch.Records, record => record.Disposition == RecordDisposition.Invalid && record.ReasonCode == ReasonCodes.InvalidUri);
        Assert.Contains(batch.Records, record => record.Disposition == RecordDisposition.Pending && record.Semantics!.Protocol == ProtocolKind.Vless);

        var objectOutbounds = SubscriptionImporter.Import("""{"outbounds":{"protocol":"vless"}}""");
        Assert.False(objectOutbounds.DocumentValid);
        Assert.False(objectOutbounds.EmptyValidDocument);
        Assert.Equal(ReasonCodes.InvalidUri, Assert.Single(objectOutbounds.Records).ReasonCode);

        var nested = SubscriptionImporter.Import("""
            {"outbounds":[
              {"protocol":"vless","settings":"nope"},
              {"protocol":"trojan","settings":{"address":"203.0.113.62","port":443,"password":"secret"},"streamSettings":{"network":"tcp","security":"tls","tlsSettings":{"serverName":"www.example.com"}}}
            ]}
            """);
        Assert.True(nested.DocumentValid);
        Assert.False(nested.EmptyValidDocument);
        Assert.Contains(nested.Records, record => record.Disposition == RecordDisposition.Invalid);
        Assert.Contains(nested.Records, record => record.Disposition == RecordDisposition.Pending && record.Semantics!.Protocol == ProtocolKind.Trojan);

        var tree = GithubTreeParser.Parse("[]");
        Assert.False(tree.Complete);
        Assert.Empty(tree.Paths);
        var wrongPath = GithubTreeParser.Parse("""{"sha":"abc","tree":[{"path":1,"type":"blob","size":1}]}""");
        Assert.False(wrongPath.Complete);
        Assert.Empty(wrongPath.Paths);
    }

    [Fact]
    public void At20ProtocolFieldsRoundTripIntoTheEmitter()
    {
        var trojan = OnlyYaml("""
            proxies:
              - name: tr
                type: trojan
                server: 203.0.113.31
                port: 443
                password: secret
                sni: www.example.com
            """);
        Assert.Equal("tls", trojan.Security);
        var trojanYaml = Profile(trojan);
        Assert.Contains("tls: true", trojanYaml, StringComparison.Ordinal);
        Assert.Contains("sni: 'www.example.com'", trojanYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("servername:", trojanYaml, StringComparison.Ordinal);

        var vmessJson = """{"v":"2","ps":"vm","add":"203.0.113.30","port":"443","id":"11111111-1111-4111-8111-111111111111","aid":"0","scy":"auto","net":"tcp","type":"none","host":"","path":"","tls":"tls","sni":"www.example.com"}""";
        var vmess = Only("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson)));
        Assert.Equal("auto", vmess.Encryption);
        Assert.Contains("cipher: 'auto'", Profile(vmess), StringComparison.Ordinal);
        Assert.Contains("servername: 'www.example.com'", Profile(vmess), StringComparison.Ordinal);

        var tcpPath = Only(Vless(Uuid, "203.0.113.10", "type=tcp&path=%2Fnot-ws"));
        var rejected = Assert.Throws<InvalidOperationException>(() => Profile(tcpPath));
        Assert.Equal(ReasonCodes.CoreConfigRejected, rejected.Message);

        var ws = Only(Vless(Uuid2, "203.0.113.11", "type=ws&path=%2Fws&host=www.example.com"));
        var wsYaml = Profile(ws);
        Assert.Contains("ws-opts:", wsYaml, StringComparison.Ordinal);
        Assert.Contains("path: '/ws'", wsYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("grpc-opts:", wsYaml, StringComparison.Ordinal);

        var grpc = Only(Vless(Uuid3, "203.0.113.12", "type=grpc&serviceName=Gun"));
        var grpcYaml = Profile(grpc);
        Assert.Contains("grpc-opts:", grpcYaml, StringComparison.Ordinal);
        Assert.Contains("grpc-service-name: 'Gun'", grpcYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ws-opts:", grpcYaml, StringComparison.Ordinal);

        var encoded = "aes-256-gcm:secret"u8.ToArray();
        var ss = Only($"ss://{Convert.ToBase64String(encoded)}@203.0.113.21:8388?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dwww.example.com#name");
        Assert.Equal("obfs", ss.Plugin);
        Assert.Equal("mode=http;host=www.example.com", ss.PluginOpts);
        var ssYaml = Profile(ss);
        Assert.Contains("plugin: 'obfs'", ssYaml, StringComparison.Ordinal);
        Assert.Contains("mode: 'http'", ssYaml, StringComparison.Ordinal);
        Assert.Contains("host: 'www.example.com'", ssYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("obfs-host", ssYaml, StringComparison.Ordinal);

        var hy2 = Only("hysteria2://secret@203.0.113.11:443?sni=www.example.com&up=%2010%20&down=20%20Mbps&mport=20000-20010");
        Assert.Equal(" 10 ", hy2.Up);
        Assert.Equal("20 Mbps", hy2.Down);
        Assert.Equal("20000-20010", hy2.HopPorts);
        var hy2Yaml = Profile(hy2);
        Assert.Contains("sni: 'www.example.com'", hy2Yaml, StringComparison.Ordinal);
        Assert.Contains("up: ' 10 '", hy2Yaml, StringComparison.Ordinal);
        Assert.Contains("down: '20 Mbps'", hy2Yaml, StringComparison.Ordinal);
        Assert.Contains("ports: '20000-20010'", hy2Yaml, StringComparison.Ordinal);

        var packet = Only(Vless(Uuid, "203.0.113.13", "packetEncoding=xudp"));
        Assert.Contains("packet-encoding: 'xudp'", Profile(packet), StringComparison.Ordinal);
    }

    [Fact]
    public void At21UnknownSecurityIsRejectedBeforeAProfileExists()
    {
        const string uri = "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=bogus&type=tcp&sni=www.example.com";
        var batch = SubscriptionImporter.Import(uri);
        var record = Assert.Single(batch.Records);
        Assert.Equal(RecordDisposition.Unsupported, record.Disposition);
        Assert.Equal(ReasonCodes.UnsupportedSecurityOption, record.ReasonCode);
        Assert.False(batch.EmptyValidDocument);
        var wire = new NodeWire
        {
            NodeId = "abc123",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = Uuid,
            Encryption = "none",
            Security = "bogus",
            Sni = "www.example.com",
            Transport = "tcp",
        };
        var rejected = Assert.Throws<InvalidOperationException>(() => MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes = [wire],
            SelectedNodeId = wire.NodeId,
        }));
        Assert.Equal(ReasonCodes.CoreConfigRejected, rejected.Message);

        var upper = Assert.Single(SubscriptionImporter.Import(uri.Replace("bogus", "BOGUS", StringComparison.Ordinal)).Records);
        Assert.Equal(ReasonCodes.UnsupportedSecurityOption, upper.ReasonCode);
        var acceptedCase = Assert.Single(SubscriptionImporter.Import(uri.Replace("bogus", "TLS", StringComparison.Ordinal)).Records);
        Assert.Equal(RecordDisposition.Pending, acceptedCase.Disposition);
        Assert.Equal("tls", acceptedCase.Semantics!.Security);
        var empty = Assert.Single(SubscriptionImporter.Import(uri.Replace("security=bogus&", "", StringComparison.Ordinal)).Records);
        Assert.Equal(RecordDisposition.PolicyBlocked, empty.Disposition);
        Assert.Equal(ReasonCodes.PlaintextTransport, empty.ReasonCode);
    }

    [Fact]
    public void At22OpaqueBytesSurviveAndMigrationDoesNotInventHealth()
    {
        var spaced = Only(Vless(Uuid, "203.0.113.10", "type=ws&path=%20%2Fx%20"));
        Assert.Equal(" /x ", spaced.Path);
        Assert.Contains("path: ' /x '", Profile(spaced), StringComparison.Ordinal);
        Assert.NotEqual(CanonicalIdentity.Digest(spaced, 1), CanonicalIdentity.Digest(spaced));

        var password = Only("hysteria2://secret@203.0.113.14:443?sni=www.example.com&obfs=salamander&obfs-password=%20secret%20");
        Assert.Equal(" secret ", password.ObfsPassword);
        Assert.Contains("obfs-password: ' secret '", Profile(password), StringComparison.Ordinal);
        Assert.NotEqual(CanonicalIdentity.Digest(password, 1), CanonicalIdentity.Digest(password));

        var reality = Only($"vless://{Uuid3}@203.0.113.17:443?encryption=none&security=reality&pbk=PUBLICKEY&sid=abcd&spx=%20%2Fx%20&type=tcp&sni=www.example.com#node");
        Assert.Equal(" /x ", reality.SpiderX);
        Assert.Contains("spider-x: ' /x '", Profile(reality), StringComparison.Ordinal);

        var directory = Directory.CreateTempSubdirectory("autovpn-migrate-");
        try
        {
            var proven = Path.Combine(directory.FullName, "proven.sqlite");
            var dropped = Path.Combine(directory.FullName, "dropped.sqlite");
            SeedOpaque(proven, Vless(Uuid, "203.0.113.15", "type=ws&path=%20%2Fx%20"));
            SeedOpaque(dropped, Vless(Uuid2, "203.0.113.16", "type=ws&path=%20%2Fy%20"));
            RewriteDigest(proven, version: 1);
            RewriteDigest(dropped, version: null);

            using (var kept = SqliteCatalogue.Open(proven, new PassthroughSecretProtector()))
            {
                var node = Assert.Single(kept.Nodes);
                Assert.Equal(" /x ", node.Semantics.Path);
                Assert.True(node.Favorite);
                Assert.Equal(CanonicalIdentity.Digest(node.Semantics), node.Digest);
                Assert.Null(node.Assessment);
                kept.SetNetworkEpoch(2);
            }

            using (var again = SqliteCatalogue.Open(proven, new PassthroughSecretProtector()))
            {
                Assert.True(again.Nodes[0].Favorite);
                Assert.Equal(" /x ", again.Nodes[0].Semantics.Path);
                Assert.Null(again.Nodes[0].Assessment);
                Assert.Equal(CanonicalIdentity.Digest(again.Nodes[0].Semantics), again.Nodes[0].Digest);
            }

            using (var lost = SqliteCatalogue.Open(dropped, new PassthroughSecretProtector()))
            {
                var node = Assert.Single(lost.Nodes);
                Assert.Equal(" /y ", node.Semantics.Path);
                Assert.True(node.Favorite);
                Assert.Null(node.Assessment);
                Assert.Equal(CanonicalIdentity.Digest(node.Semantics), node.Digest);
                lost.SetNetworkEpoch(2);
            }

            using var lostAgain = SqliteCatalogue.Open(dropped, new PassthroughSecretProtector());
            Assert.True(lostAgain.Nodes[0].Favorite);
            Assert.Null(lostAgain.Nodes[0].Assessment);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void At24FailedSqliteCommitDoesNotPublishTheCopy()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-commit-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            string nodeId;
            string digest;
            using (var created = SqliteCatalogue.Open(path, new PassthroughSecretProtector()))
            {
                var record = PendingRecord(Vless(Uuid, "203.0.113.40"));
                created.ApplySnapshot(new SnapshotCommit
                {
                    ArtifactId = "list",
                    FamilyId = "black-vless",
                    ContentHash = "a",
                    Complete = true,
                    NowUtc = Now,
                    Nodes =
                    [
                        new SnapshotNode
                        {
                            Digest = record.Digest!,
                            Semantics = record.Semantics!,
                            Label = record.DisplayName,
                            FamilyId = "black-vless",
                            ArtifactId = "list",
                        },
                    ],
                });
                created.ApplyAssessment(created.Nodes[0].NodeId, Healthy(created.Nodes[0], Now));
                nodeId = created.Nodes[0].NodeId;
                digest = created.Nodes[0].Digest;
            }

            using (var failing = SqliteCatalogue.Open(path, new ThrowingProtector()))
            {
                Assert.False(failing.Nodes[0].Favorite);
                Assert.Equal(HealthState.Healthy, failing.Nodes[0].Assessment!.Health);
                Assert.True(failing.TrySetFavorite(nodeId, true));
                Assert.True(failing.Nodes[0].Favorite);
                failing.ApplyAssessment(nodeId, new AssessmentSnapshot
                {
                    Digest = digest,
                    NetworkEpoch = failing.NetworkEpoch,
                    Health = HealthState.Failed,
                    LastFailureUtc = Now,
                });
                Assert.Equal(HealthState.Failed, failing.Nodes[0].Assessment!.Health);
                var replacement = PendingRecord(Vless(Uuid, "203.0.113.41"));
                Assert.Throws<IOException>(() => failing.ApplySnapshot(new SnapshotCommit
                {
                    ArtifactId = "replacement",
                    FamilyId = "black-vless",
                    ContentHash = "b",
                    Complete = true,
                    NowUtc = Now,
                    Nodes =
                    [
                        new SnapshotNode
                        {
                            Digest = replacement.Digest!,
                            Semantics = replacement.Semantics!,
                            Label = replacement.DisplayName,
                            FamilyId = "black-vless",
                            ArtifactId = "replacement",
                        },
                    ],
                }));
                Assert.Single(failing.Nodes);
                Assert.Equal(nodeId, failing.Nodes[0].NodeId);
                Assert.True(failing.Nodes[0].Favorite);
                Assert.Equal(HealthState.Failed, failing.Nodes[0].Assessment!.Health);
                Assert.Equal("203.0.113.40", failing.Nodes[0].Semantics.Host);
            }

            using var reopened = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            Assert.Single(reopened.Nodes);
            Assert.True(reopened.Nodes[0].Favorite);
            Assert.Equal(HealthState.Failed, reopened.Nodes[0].Assessment!.Health);
            Assert.Equal("203.0.113.40", reopened.Nodes[0].Semantics.Host);
            Assert.Equal(digest, reopened.Nodes[0].Digest);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FailoverContext Budget(FailureKind failure, int switches, StandbyCandidate standby)
    {
        return new FailoverContext
        {
            Generation = 1,
            CommandGeneration = 1,
            Mode = SelectionMode.Automatic,
            Failure = failure,
            ActiveNodeId = "current",
            SwitchesInLastMinute = switches,
            Standbys = [standby],
        };
    }

    private static AssessmentSnapshot Healthy(CatalogueNode node, DateTimeOffset success)
    {
        return new AssessmentSnapshot
        {
            Digest = node.Digest,
            NetworkEpoch = 1,
            Health = HealthState.Healthy,
            LastSuccessUtc = success,
            MedianLatencyMs = 40,
        };
    }

    private static string Vless(string uuid, string host, string extra = "")
    {
        var query = "encryption=none&security=tls&type=tcp&sni=www.example.com";
        if (extra.Length > 0)
        {
            query = "encryption=none&security=tls&sni=www.example.com&" + extra;
        }

        return $"vless://{uuid}@{host}:443?{query}#node";
    }

    private static MemoryCatalogue ImportMany(params string[] lines)
    {
        var catalogue = new MemoryCatalogue();
        Ingest(catalogue, "list", "black-vless", string.Join('\n', lines));
        return catalogue;
    }

    private static IngestReport Ingest(MemoryCatalogue catalogue, string artifactId, string familyId, string text)
    {
        return RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = artifactId, FamilyId = familyId, Enabled = true, Text = text, ContentHash = artifactId },
        ], Now, false);
    }

    private static NodeSemantics Only(string text)
    {
        return PendingRecord(text).Semantics!;
    }

    private static ImportRecord PendingRecord(string text)
    {
        var batch = SubscriptionImporter.Import(text);
        Assert.True(batch.DocumentValid);
        var record = Assert.Single(batch.Records);
        Assert.Equal(RecordDisposition.Pending, record.Disposition);
        return record;
    }

    private static NodeSemantics OnlyYaml(string text)
    {
        return Only(text);
    }

    private static string Profile(NodeSemantics semantics)
    {
        var node = new CatalogueNode
        {
            NodeId = "abc123",
            Digest = CanonicalIdentity.Digest(semantics),
            Semantics = semantics,
            Label = "hidden",
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
        };
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes = [NodeWireFactory.FromCatalogue(node)],
            SelectedNodeId = node.NodeId,
        });
    }

    private static void SeedOpaque(string path, string uri)
    {
        var record = PendingRecord(uri);
        using var store = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
        store.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "list",
            FamilyId = "black-vless",
            ContentHash = "a",
            Complete = true,
            NowUtc = Now,
            Nodes =
            [
                new SnapshotNode
                {
                    Digest = record.Digest!,
                    Semantics = record.Semantics!,
                    Label = record.DisplayName,
                    FamilyId = "black-vless",
                    ArtifactId = "list",
                },
            ],
        });
        store.ApplyAssessment(store.Nodes[0].NodeId, Healthy(store.Nodes[0], Now));
        Assert.True(store.TrySetFavorite(store.Nodes[0].NodeId, true));
    }

    private static void RewriteDigest(string path, int? version)
    {
        string current;
        string replacement;
        using (var store = SqliteCatalogue.Open(path, new PassthroughSecretProtector()))
        {
            var node = store.Nodes[0];
            current = node.Digest;
            replacement = version is int canonical
                ? CanonicalIdentity.Digest(node.Semantics, canonical)
                : new string('0', 64);
            Assert.NotEqual(current, replacement);
        }

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE nodes SET digest=$next, assessment_json=replace(assessment_json, $current, $next);";
        command.Parameters.AddWithValue("$next", replacement);
        command.Parameters.AddWithValue("$current", current);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static ProbeObservation Proven(NodeSemantics node, int latency, int bytes = 0)
    {
        return new ProbeObservation(true, latency, false, null, bytes, ProbeClass.Success, Target.AbsoluteUri, CanonicalIdentity.Digest(node), "unit-proof");
    }

    private static IProbeTransport Transport(Func<NodeSemantics, ProbeObservation> next)
    {
        return new AsyncTransport((node, _) => Task.FromResult(next(node)));
    }

    private static readonly Uri Target = new("https://cp.cloudflare.com/generate_204");

    private static IpcRequest Request(string operation, object payload)
    {
        return new IpcRequest
        {
            ProtocolVersion = ProductLimits.IpcProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
        };
    }

    private sealed class AsyncTransport(Func<NodeSemantics, CancellationToken, Task<ProbeObservation>> next) : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            _ = target;
            return next(node, cancellationToken);
        }
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string ProtectorId => "throwing";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            _ = plaintext;
            throw new IOException("injected commit failure");
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return ciphertext.ToArray();
        }
    }
}
