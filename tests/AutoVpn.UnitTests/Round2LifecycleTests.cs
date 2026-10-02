using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;
using Microsoft.Data.Sqlite;

namespace AutoVpn.UnitTests;

public sealed class Round2LifecycleTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private const string CoreHash = "3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8";
    private const string TreeSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Rt10RealPipeResyncsAndIgnoresAnOlderReply()
    {
        var catalogue = ReadyCatalogue();
        var core = new HoldFirstStart();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), core);
        var pipe = "autovpn-rt10-" + Guid.NewGuid().ToString("N");
        await using var server = LocalIpcServer.Start(pipe, new IpcDispatcher(), engine, new CallerIdentity { Sid = "owner", SessionId = 1 });
        var restarted = new SessionMailbox();
        var snapshot = await RoundTripAsync(pipe, IpcOperations.GetSnapshot, new Dictionary<string, string>());
        Assert.NotNull(snapshot);
        Assert.True(restarted.Apply(snapshot, true));
        Assert.Equal(nameof(TunnelPhase.Disconnected), restarted.Session.PhaseCode);
        Assert.True(restarted.Session.BrokerReachable);
        Assert.NotEqual("Состояние неизвестно", restarted.Session.PhaseLabel);

        var connectTask = RoundTripAsync(pipe, IpcOperations.Connect, new ConnectPayload
        {
            NodeId = catalogue.Nodes[0].NodeId,
            Digest = catalogue.Nodes[0].Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            ProtectionRequired = true,
        });
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var during = await RoundTripAsync(pipe, IpcOperations.GetSnapshot, new Dictionary<string, string>());
        var disconnect = await RoundTripAsync(pipe, IpcOperations.Disconnect, new DisconnectPayload(), during.StateRevision);
        core.Release.TrySetResult();
        var connect = await connectTask;
        Assert.True(disconnect.Ok);
        Assert.NotNull(disconnect.Snapshot);
        Assert.False(connect.Ok);
        Assert.Equal(ReasonCodes.Canceled, connect.ErrorCode);
        Assert.NotEqual(nameof(TunnelPhase.Connected), disconnect.Snapshot.Phase);
        Assert.False(catalogue.Nodes[0].ActiveSession);

        var mailbox = new SessionMailbox();
        Assert.True(mailbox.Apply(disconnect, true));
        var phase = mailbox.Session.PhaseCode;
        var sequence = mailbox.Sequence;
        Assert.False(mailbox.Apply(snapshot, true));
        Assert.Equal(phase, mailbox.Session.PhaseCode);
        Assert.Equal(sequence, mailbox.Sequence);
        Assert.Equal(nameof(TunnelPhase.Disconnected), mailbox.Session.PhaseCode);

        var lost = new IpcResponse { RequestId = "peer", Ok = false, ErrorCode = "PEER", Message = "канал отклонён" };
        Assert.False(mailbox.Apply(lost, true));
        Assert.Contains("PEER", mailbox.Session.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Брокер недоступен", mailbox.Session.Detail, StringComparison.Ordinal);
        mailbox.OperationPending = true;
        Assert.False(UiSessionReducer.PlanExit(mailbox.Session, mailbox.OperationPending).CanClose);
        var cleared = mailbox.Apply(new IpcResponse
        {
            RequestId = "clear",
            Ok = true,
            StateSequence = disconnect.Snapshot.Sequence + 1,
            Snapshot = new BrokerSnapshot
            {
                BootId = disconnect.Snapshot.BootId,
                Sequence = disconnect.Snapshot.Sequence + 1,
                Revision = disconnect.Snapshot.Revision + 1,
                Phase = nameof(TunnelPhase.Disconnected),
                ProtectionArmed = false,
            },
        }, true);
        Assert.True(cleared);
        Assert.True(UiSessionReducer.PlanExit(mailbox.Session, operationPending: true).CanClose);
    }

    [Fact]
    public async Task Rt28StalePolicyCannotPublishProbeConnectOrSwitch()
    {
        var probeCatalogue = ReadyCatalogue();
        probeCatalogue.Nodes[0].Assessment = null;
        var gate = new ProbeGate();
        var probe = ProbeCoordinator.RunAsync(probeCatalogue, gate, new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        probeCatalogue.Settings = probeCatalogue.Settings with
        {
            CountryMode = CountryConstraint.Strict,
            Country = "FI",
            Revision = probeCatalogue.Settings.Revision + 1,
        };
        gate.Release.TrySetResult();
        var report = await probe;
        Assert.Equal(0, report.Succeeded);
        Assert.Null(probeCatalogue.Nodes[0].Assessment);

        var filtered = ReadyCatalogue();
        filtered.Nodes[0].Assessment = null;
        filtered.Nodes[0].AdvertisedCountry = "DE";
        filtered.Settings = filtered.Settings with { CountryMode = CountryConstraint.Strict, Country = "FI", Revision = 3 };
        var skipped = await ProbeCoordinator.RunAsync(filtered, new RejectTransport(), new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, skipped.Attempted);

        var excluded = ReadyCatalogue();
        excluded.Nodes[0].Assessment = null;
        Assert.True(excluded.TrySetExcluded(excluded.Nodes[0].NodeId, true));
        var excludedReport = await ProbeCoordinator.RunAsync(excluded, new RejectTransport(), new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, excludedReport.Attempted);

        var insecure = ReadyCatalogue();
        insecure.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "skip",
            FamilyId = "black-vless",
            ContentHash = "skip",
            Complete = true,
            NowUtc = DateTimeOffset.UtcNow,
            Nodes =
            [
                new SnapshotNode
                {
                    Digest = "replaced-below",
                    Semantics = insecure.Nodes[0].Semantics with { SkipCertVerify = true },
                    Label = "skip",
                    FamilyId = "black-vless",
                    ArtifactId = "skip",
                },
            ],
        });
        var blocked = await ProbeCoordinator.RunAsync(insecure, new RecordingTransport(), new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, blocked.Attempted);

        var allowed = ReadyCatalogue();
        allowed.Settings = allowed.Settings with { AllowInsecureCertificates = true, Revision = allowed.Settings.Revision + 1 };
        var allowedSemantics = allowed.Nodes[0].Semantics with { SkipCertVerify = true, Port = 8443 };
        allowed.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "skip-allowed",
            FamilyId = "black-vless",
            ContentHash = "skip-allowed",
            Complete = true,
            NowUtc = DateTimeOffset.UtcNow,
            Nodes =
            [
                new SnapshotNode
                {
                    Digest = CanonicalIdentity.Digest(allowedSemantics),
                    Semantics = allowedSemantics,
                    Label = "skip-allowed",
                    FamilyId = "black-vless",
                    ArtifactId = "skip-allowed",
                },
            ],
        });
        var seen = new RecordingTransport();
        var admitted = await ProbeCoordinator.RunAsync(allowed, seen, new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, admitted.Succeeded);
        Assert.True(seen.AllowInsecure);

        var connectCatalogue = ReadyCatalogue();
        var connectGuard = new FlagGuard();
        var connectCore = new HoldFirstStart();
        var connectEngine = new BrokerEngine(connectCatalogue, connectGuard, connectCore);
        var connecting = connectEngine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = connectCatalogue.Nodes[0].NodeId,
            Digest = connectCatalogue.Nodes[0].Digest,
            NetworkEpoch = connectCatalogue.NetworkEpoch,
            ProtectionRequired = false,
        }), CancellationToken.None);
        await connectCore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(connectGuard.ProtectionRequired);
        connectCatalogue.Settings = connectCatalogue.Settings with { LanAccess = false, Revision = connectCatalogue.Settings.Revision + 1 };
        connectCore.Release.TrySetResult();
        var refused = await connecting;
        Assert.Equal(ReasonCodes.PolicyChanged, refused.ErrorCode);
        Assert.Equal(TunnelPhase.Blocked, connectEngine.State.Phase);
        Assert.True(connectEngine.State.ProtectionArmed);
        Assert.False(connectCatalogue.Nodes[0].ActiveSession);

        var switchCatalogue = TwoNodes();
        var switchCore = new HoldSecondStart();
        var switchEngine = new BrokerEngine(switchCatalogue, new ArmingGuard(), switchCore);
        var first = await switchEngine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = switchCatalogue.Nodes[0].NodeId,
            Digest = switchCatalogue.Nodes[0].Digest,
            NetworkEpoch = switchCatalogue.NetworkEpoch,
        }), CancellationToken.None);
        switchEngine.ConfirmProduction(switchEngine.BootId, first.Snapshot!.Generation, first.Snapshot.OperationId, first.Snapshot.ActiveNodeId, switchCatalogue.NetworkEpoch, true, null);
        var current = await switchEngine.HandleAsync(Request(IpcOperations.GetSnapshot, new Dictionary<string, string>()), CancellationToken.None);
        await switchEngine.HandleAsync(Request(IpcOperations.ApplyRuntimeSet, new Dictionary<string, object>
        {
            ["standbys"] = new[]
            {
                new StandbyCandidate
                {
                    NodeId = switchCatalogue.Nodes[1].NodeId,
                    EndpointKey = "203.0.113.21:443",
                    Country = "",
                    SourceFamilyId = "black-vless",
                    Cost = 1,
                    FreshOnEpoch = true,
                },
            },
        }, revision: current.StateRevision), CancellationToken.None);
        current = await switchEngine.HandleAsync(Request(IpcOperations.GetSnapshot, new Dictionary<string, string>()), CancellationToken.None);
        var active = switchEngine.State.ActiveNodeId;
        var health = switchEngine.HandleAsync(Request(IpcOperations.ReportHealth, new HealthPayload
        {
            FailureKind = nameof(FailureKind.CoreExit),
            ConsecutiveFailures = 3,
            NetworkEpoch = switchCatalogue.NetworkEpoch,
        }, revision: current.StateRevision), CancellationToken.None);
        await switchCore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switchCatalogue.Settings = switchCatalogue.Settings with { AllowInsecureCertificates = true, Revision = switchCatalogue.Settings.Revision + 1 };
        switchCore.Release.TrySetResult();
        var held = await health;
        Assert.True(held.Ok);
        Assert.NotEqual(TunnelPhase.Connected, switchEngine.State.Phase);
        Assert.False(switchEngine.Snapshot().CoreRunning);
        Assert.Equal(active, switchEngine.State.ActiveNodeId);
        Assert.True(switchCore.Stops >= 1);
    }

    [Fact]
    public async Task Rt06BudgetAndUnboundSpeedDoNotBecomeHealth()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt06-");
        try
        {
            var day = new DateOnly(2026, 10, 3);
            var path = Path.Combine(directory.FullName, "probe-budget.txt");
            var budget = ProbeByteBudget.Load(path, 5, day);
            var catalogue = ReadyCatalogue();
            catalogue.Nodes[0].Assessment = null;
            var report = await ProbeCoordinator.RunAsync(
                catalogue,
                new FixedTransport(new ProbeObservation(true, 15, false, null, 5, ProbeClass.Success)),
                new Uri("https://probe.example/generate_204"),
                new DateTimeOffset(2026, 10, 3, 1, 0, 0, TimeSpan.Zero),
                CancellationToken.None,
                spent: budget);
            Assert.Equal(1, report.Succeeded);
            Assert.Equal(5, budget.Spent);
            budget.Save(path);
            var reloaded = ProbeByteBudget.Load(path, 5, day);
            Assert.True(reloaded.Exhausted(day));
            var second = ReadyCatalogue();
            second.Nodes[0].Assessment = null;
            var again = await ProbeCoordinator.RunAsync(
                second,
                new RejectTransport(),
                new Uri("https://probe.example/generate_204"),
                new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero),
                CancellationToken.None,
                spent: reloaded);
            Assert.Equal(0, again.Attempted);
            Assert.Null(second.Nodes[0].Assessment);
            var nextDay = ProbeByteBudget.Load(path, 5, day.AddDays(1));
            Assert.False(nextDay.Exhausted(day.AddDays(1)));
            File.WriteAllText(path, "corrupt");
            var corrupt = ProbeByteBudget.Load(path, 5, day);
            Assert.True(corrupt.Exhausted(day));
            Assert.Equal(5, corrupt.Spent);

            var healthy = ReadyCatalogue();
            var latency = healthy.Nodes[0].Assessment!.MedianLatencyMs;
            var unbound = await SpeedMeasurement.MeasureHealthyDownloadAsync(healthy, healthy.Nodes[0].NodeId, new MemoryStream(new byte[1000]), CancellationToken.None);
            Assert.Null(unbound);
            Assert.Equal(latency, healthy.Nodes[0].Assessment!.MedianLatencyMs);
            var bound = await SpeedMeasurement.MeasureHealthyDownloadAsync(
                healthy,
                healthy.Nodes[0].NodeId,
                new MemoryStream(new byte[1000]),
                CancellationToken.None,
                new SpeedMeasurement.MeasurementBinding(healthy.Nodes[0].Digest, healthy.NetworkEpoch));
            Assert.NotNull(bound);
            Assert.Equal(latency, healthy.Nodes[0].Assessment!.MedianLatencyMs);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rt29OneAssessmentDoesNotReprotectTheOtherSecrets()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt29-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            var protector = new CountingProtector();
            using (var catalogue = SqliteCatalogue.Open(path, protector))
            {
                RefreshMerge.Ingest(catalogue, [Body(NodeUri("203.0.113.10") + "\n" + NodeUri("203.0.113.11"))], DateTimeOffset.UtcNow, false);
                Assert.Equal(2, protector.Protects);
                var first = catalogue.Nodes[0];
                var second = catalogue.Nodes[1];
                var beforeFirst = Blob(path, first.NodeId);
                var beforeSecond = Blob(path, second.NodeId);
                catalogue.ApplyAssessment(first.NodeId, new AssessmentSnapshot
                {
                    Digest = first.Digest,
                    NetworkEpoch = catalogue.NetworkEpoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                    MedianLatencyMs = 40,
                });
                Assert.Equal(2, protector.Protects);
                Assert.Equal(beforeFirst, Blob(path, first.NodeId));
                Assert.Equal(beforeSecond, Blob(path, second.NodeId));
                Assert.Equal(40, catalogue.Nodes.Single(node => node.NodeId == first.NodeId).Assessment?.MedianLatencyMs);
                Assert.Equal(HealthState.Pending, catalogue.Nodes.Single(node => node.NodeId == second.NodeId).Assessment?.Health);
                Assert.Null(catalogue.Nodes.Single(node => node.NodeId == second.NodeId).Assessment?.MedianLatencyMs);
            }

            var memory = new MemoryCatalogue();
            for (var i = 0; i < 8; i++)
            {
                RefreshMerge.Ingest(memory, [Body(NodeUri("203.0.113." + (10 + i).ToString(CultureInfo.InvariantCulture)), "row-" + i.ToString(CultureInfo.InvariantCulture))], DateTimeOffset.UtcNow, false);
                var node = memory.Nodes[^1];
                memory.ApplyAssessment(node.NodeId, new AssessmentSnapshot
                {
                    Digest = node.Digest,
                    NetworkEpoch = memory.NetworkEpoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = DateTimeOffset.UtcNow,
                    MedianLatencyMs = 30,
                });
            }

            var counted = new EligibleCounter(memory);
            var text = CataloguePresentation.Servers(counted, "working", DateTimeOffset.UtcNow);
            Assert.Equal(1, counted.Calls);
            Assert.Contains("203.0.113.10", text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [RequiresMihomoFact]
    public async Task Rt02CurrentHeadRefreshPublishesOnlyTheAuthenticatedCandidate()
    {
        var binary = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
        var trusted = Issue("probe.example");
        await using var tls = await TlsPeer.StartAsync(trusted, "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        const string password = "round2-ss-secret";
        await using var shadowsocks = await ShadowsocksAeadServer.StartAsync(password, tls.Port);
        var target = new Uri("https://probe.example:" + tls.Port.ToString(CultureInfo.InvariantCulture) + "/generate_204");
        var body = Ss(shadowsocks.Port, password, "good") + "\n" + Ss(shadowsocks.Port, "wrong-ss-secret", "broken");
        var catalogue = new MemoryCatalogue();
        Assert.False(catalogue.Settings.DisclosureAccepted);
        Consent.AcceptDisclosure(catalogue);
        Assert.True(catalogue.Settings.DisclosureAccepted);
        var registry = Registry(target);
        var fetches = 0;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (request.Headers.IfNoneMatch.Count > 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Tree(TreeSha, "BLACK_VLESS_RUS.txt"), System.Text.Encoding.UTF8, "application/json"),
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"head\"");
                return response;
            }

            fetches++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
            };
        });
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" });
        var ledger = new SourceLedger();
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
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, transport, ledger, [target]);
        var discovered = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(3));
        Assert.True(discovered.Complete);
        Assert.Equal(TreeSha, discovered.CommitSha);
        Assert.Contains(TreeSha, Assert.Single(discovered.Items).Urls[0].AbsoluteUri, StringComparison.Ordinal);
        var refreshed = await coordinator.RefreshAsync(discovered.Items, DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(3));
        Assert.Equal(1, fetches);
        Assert.Contains("PUBLISHED", Assert.Single(refreshed.SourceReasons), StringComparison.Ordinal);
        Assert.Equal(2, catalogue.Nodes.Count);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var probed = await coordinator.ProbeAsync(target, DateTimeOffset.UtcNow, budget.Token, TimeSpan.FromSeconds(35));
        Assert.Equal(1, probed.Succeeded);
        Assert.Equal(1, probed.Failed);
        var working = CataloguePresentation.Servers(catalogue, "working", DateTimeOffset.UtcNow);
        Assert.Contains("good — ", working, StringComparison.Ordinal);
        Assert.DoesNotContain("broken — ", working, StringComparison.Ordinal);
        Assert.Contains("probe.example", tls.Sni, StringComparison.Ordinal);
        Assert.True(shadowsocks.Handshakes >= 1);
        var good = catalogue.Nodes.Single(node => node.Label == "good");
        Assert.Equal(HealthState.Healthy, good.Assessment?.Health);
        Assert.Equal(target.AbsoluteUri, good.Label == "good" ? target.AbsoluteUri : null);
        Assert.NotEqual(HealthState.Healthy, catalogue.Nodes.Single(node => node.Label == "broken").Assessment?.Health);

        var again = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(3));
        Assert.True(again.Complete);
        Assert.Equal(TreeSha, again.CommitSha);
        Assert.Equal(HealthState.Healthy, catalogue.Nodes.Single(node => node.Label == "good").Assessment?.Health);
    }

    private static async Task<IpcResponse> RoundTripAsync(string pipe, string operation, object payload, long revision = 0)
    {
        var response = await LocalIpcServer.RoundTripAsync(pipe, Request(operation, payload, revision: revision), CancellationToken.None);
        Assert.NotNull(response);
        return response;
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

    private static MemoryCatalogue ReadyCatalogue()
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        RefreshMerge.Ingest(catalogue, [Body(NodeUri("203.0.113.10"))], DateTimeOffset.UtcNow, false);
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
        {
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            MedianLatencyMs = 20,
        });
        return catalogue;
    }

    private static MemoryCatalogue TwoNodes()
    {
        var catalogue = ReadyCatalogue();
        RefreshMerge.Ingest(catalogue, [Body(NodeUri("203.0.113.21"), "list-b")], DateTimeOffset.UtcNow, false);
        var other = catalogue.Nodes.Single(node => node.Semantics.Host == "203.0.113.21");
        catalogue.ApplyAssessment(other.NodeId, new AssessmentSnapshot
        {
            Digest = other.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            MedianLatencyMs = 30,
        });
        return catalogue;
    }

    private static string NodeUri(string host)
    {
        return $"vless://{Uuid}@{host}:443?encryption=none&security=tls&type=tcp&sni=www.example.com#{host}";
    }

    private static string Ss(int port, string password, string label)
    {
        return "ss://aes-256-gcm:" + password + "@candidate.example:" + port.ToString(CultureInfo.InvariantCulture) + "#" + label;
    }

    private static IngestArtifact Body(string text, string artifact = "list")
    {
        return new IngestArtifact { ArtifactId = artifact, FamilyId = "black-vless", Enabled = true, Text = text, ContentHash = artifact };
    }

    private static ReviewedRegistry Registry(Uri target)
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
            ApprovedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" },
            RejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ProbeTargets = [target],
        };
    }

    private static string Tree(string sha, params string[] paths)
    {
        var entries = string.Join(',', paths.Select(path => "{\"path\":\"" + path + "\",\"type\":\"blob\",\"size\":80}"));
        return "{\"sha\":\"" + sha + "\",\"truncated\":false,\"tree\":[" + entries + "]}";
    }

    private static byte[] Blob(string path, string nodeId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT semantics_blob FROM nodes WHERE node_id=$id;";
        command.Parameters.AddWithValue("$id", nodeId);
        return (byte[])command.ExecuteScalar()!;
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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(next(request));
        }
    }

    private sealed class ArmingGuard : INetworkGuard
    {
        public GuardResult Arm(GuardRequest request)
        {
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, effects.Select(effect => effect.Id).ToArray());
        }
    }

    private sealed class FlagGuard : INetworkGuard
    {
        public bool ProtectionRequired { get; private set; } = true;

        public GuardResult Arm(GuardRequest request)
        {
            ProtectionRequired = request.ProtectionRequired;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, []);
        }
    }

    private sealed class HoldFirstStart : ICoreController
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CoreStartResult(true, null);
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class HoldSecondStart : ICoreController
    {
        private int _starts;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Stops { get; private set; }

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
            Stops++;
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeGate : IProbeTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeObservation(true, 12, false, null, 4, ProbeClass.Success, target.AbsoluteUri, CanonicalIdentity.Digest(node), "gate");
        }
    }

    private sealed class RejectTransport : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Этот узел не должен проверяться.");
        }
    }

    private sealed class RecordingTransport : IProbeTransport
    {
        public bool AllowInsecure { get; private set; }

        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            return ProbeAsync(node, target, new ProbeAdmission(false), cancellationToken);
        }

        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken cancellationToken)
        {
            AllowInsecure = admission.AllowInsecureProxyCertificates;
            return Task.FromResult(new ProbeObservation(true, 9, false, null, 4, ProbeClass.Success, target.AbsoluteUri, CanonicalIdentity.Digest(node), "recorded"));
        }
    }

    private sealed class FixedTransport(ProbeObservation observation) : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            return Task.FromResult(observation);
        }
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public int Protects { get; private set; }

        public string ProtectorId => "count";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            Protects++;
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return ciphertext.ToArray();
        }
    }

    private sealed class EligibleCounter(ICatalogue inner) : ICatalogue
    {
        public int Calls { get; private set; }

        public long NetworkEpoch => inner.NetworkEpoch;

        public ProductSettings Settings
        {
            get => inner.Settings;
            set => inner.Settings = value;
        }

        public IReadOnlyList<CatalogueNode> Nodes => inner.Nodes;

        public void SetNetworkEpoch(long epoch)
        {
            inner.SetNetworkEpoch(epoch);
        }

        public void ApplySnapshot(SnapshotCommit commit)
        {
            inner.ApplySnapshot(commit);
        }

        public void ApplyAssessment(string nodeId, AssessmentSnapshot assessment)
        {
            inner.ApplyAssessment(nodeId, assessment);
        }

        public int EvictOverflow(DateTimeOffset nowUtc)
        {
            return inner.EvictOverflow(nowUtc);
        }

        public bool TrySetFavorite(string nodeId, bool favorite)
        {
            return inner.TrySetFavorite(nodeId, favorite);
        }

        public bool TrySetExcluded(string nodeId, bool excluded)
        {
            return inner.TrySetExcluded(nodeId, excluded);
        }

        public void SetActiveNode(string? nodeId)
        {
            inner.SetActiveNode(nodeId);
        }

        public IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context)
        {
            Calls++;
            return inner.Eligible(context);
        }
    }
}
