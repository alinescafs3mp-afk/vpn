using System.Net;
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

public class BehaviorTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTextArtifactDoesNotRemoveClashNodesFromTheSameFamily()
    {
        var catalogue = new MemoryCatalogue();
        var yaml = """
            proxies:
              - name: Germany
                type: vless
                server: 203.0.113.10
                port: 443
                uuid: 11111111-1111-4111-8111-111111111111
                flow: xtls-rprx-vision
                tls: true
                servername: www.example.com
                client-fingerprint: chrome
                network: tcp
                reality-opts:
                  public-key: PUBLICKEY
                  short-id: abcd
            """;
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "clash", FamilyId = "white-cidr-checked", Enabled = true, Text = yaml, ContentHash = "a" },
            new IngestArtifact { ArtifactId = "txt", FamilyId = "white-cidr-checked", Enabled = true, Text = "# profile-update-interval: 1\n# Количество: 0\n", ContentHash = "b" },
        ], Now, false);

        var node = Assert.Single(catalogue.Nodes);
        Assert.Contains("white-cidr-checked", node.CurrentFamilies);
        Assert.Contains("clash", node.ArtifactFamilies.Keys);
        Assert.DoesNotContain("txt", node.ArtifactFamilies.Keys);
    }

    [Fact]
    public void NotModifiedAndFetchFailureDoNotDropHealthyNodes()
    {
        var catalogue = new MemoryCatalogue();
        var uri = $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#Germany";
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, Text = uri, ContentHash = "a" },
        ], Now, false);
        var node = Assert.Single(catalogue.Nodes);
        catalogue.ApplyAssessment(node.NodeId, Healthy(node, 100));

        var report = RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, NotModified = true, ContentHash = "a" },
            new IngestArtifact { ArtifactId = "other", FamilyId = "black-vless", Enabled = true, FetchFailed = true },
        ], Now.AddMinutes(5), false);

        Assert.True(report.AnyFetchFailed);
        Assert.Equal(HealthState.Healthy, catalogue.Nodes[0].Assessment!.Health);
        Assert.Contains("black-vless", catalogue.Nodes[0].CurrentFamilies);
    }

    [Fact]
    public async Task ProbePublishesOnlySuccessfulSamplesAndUplinkDoesNotFailTheRest()
    {
        var catalogue = new MemoryCatalogue();
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "a", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.21"), ContentHash = "a" },
            new IngestArtifact { ArtifactId = "b", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.22"), ContentHash = "b" },
        ], Now, false);
        var calls = 0;
        var transport = new ScriptedTransport(node =>
        {
            calls++;
            return calls == 1
                ? new ProbeObservation(false, null, true, ReasonCodes.UplinkOffline)
                : Proven(node, 80);
        });
        var report = await ProbeCoordinator.RunAsync(catalogue, transport, new Uri("https://cp.cloudflare.com/generate_204"), Now, CancellationToken.None);
        Assert.True(report.StoppedForUplink);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, calls);
        var stopped = Assert.Single(catalogue.Nodes, node => node.Assessment!.Health == HealthState.EnvironmentUnknown);
        var pending = Assert.Single(catalogue.Nodes, node => node.Assessment!.Health == HealthState.Pending);
        Assert.True(stopped.Assessment!.EnvironmentFailure);
        Assert.Null(pending.Assessment!.LastFailureUtc);
        Assert.Empty(catalogue.Eligible(Context()));
    }

    [Fact]
    public async Task SuccessfulProbeBecomesEligibleAndFailureDoesNot()
    {
        var catalogue = new MemoryCatalogue();
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "a", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.31"), ContentHash = "a" },
            new IngestArtifact { ArtifactId = "b", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.32"), ContentHash = "b" },
        ], Now, false);
        var transport = new ScriptedTransport(node => node.Host.EndsWith(".31", StringComparison.Ordinal)
            ? Proven(node, 90)
            : new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed));
        await ProbeCoordinator.RunAsync(catalogue, transport, new Uri("https://cp.cloudflare.com/generate_204"), Now, CancellationToken.None);
        var eligible = catalogue.Eligible(Context());
        Assert.Single(eligible);
        Assert.Equal("203.0.113.31", eligible[0].Semantics.Host);
        Assert.Equal(90, eligible[0].Assessment!.MedianLatencyMs);
    }

    [Fact]
    public void SqliteRoundTripProtectsSecretsAndRefusesNewerSchema()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-cat-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            var protector = new XorProtector();
            var catalogue = new MemoryCatalogue();
            var uri = $"trojan://super-secret-password@203.0.113.40:443?security=tls&sni=www.example.com#Germany";
            RefreshMerge.Ingest(catalogue, [
                new IngestArtifact { ArtifactId = "list", FamilyId = "black-mixed", Enabled = true, Text = uri, ContentHash = "a" },
            ], Now, false);
            catalogue.ApplyAssessment(catalogue.Nodes[0].NodeId, Healthy(catalogue.Nodes[0], 150));
            catalogue.TrySetFavorite(catalogue.Nodes[0].NodeId, true);
            using (var store = SqliteCatalogue.Open(path, protector))
            {
                store.SetNetworkEpoch(catalogue.NetworkEpoch);
                store.Settings = catalogue.Settings;
                store.ApplySnapshot(new SnapshotCommit
                {
                    ArtifactId = "list",
                    FamilyId = "black-mixed",
                    ContentHash = "a",
                    Complete = true,
                    NowUtc = Now,
                    Nodes =
                    [
                        new SnapshotNode
                        {
                            Digest = catalogue.Nodes[0].Digest,
                            Semantics = catalogue.Nodes[0].Semantics,
                            Label = catalogue.Nodes[0].Label,
                            AdvertisedCountry = catalogue.Nodes[0].AdvertisedCountry,
                            FamilyId = "black-mixed",
                            ArtifactId = "list",
                        },
                    ],
                });
                store.ApplyAssessment(store.Nodes[0].NodeId, Healthy(store.Nodes[0], 150));
                store.TrySetFavorite(store.Nodes[0].NodeId, true);
                var backup = Path.Combine(directory.FullName, "backup.sqlite");
                store.BackupTo(backup);
                Assert.True(File.Exists(backup));
            }

            var raw = File.ReadAllText(path, Encoding.Latin1);
            Assert.DoesNotContain("super-secret-password", raw, StringComparison.Ordinal);
            using (var reopened = SqliteCatalogue.Open(path, protector))
            {
                Assert.Equal("super-secret-password", reopened.Nodes[0].Semantics.Password);
                Assert.True(reopened.Nodes[0].Favorite);
                Assert.Equal(HealthState.Healthy, reopened.Nodes[0].Assessment!.Health);
                Assert.Contains("black-mixed", reopened.Nodes[0].CurrentFamilies);
            }

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value='99' WHERE key='schema_version';";
                command.ExecuteNonQuery();
            }

            var error = Assert.Throws<CatalogueStoreException>(() => SqliteCatalogue.Open(path, protector));
            Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
            using var check = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            check.Open();
            using var version = check.CreateCommand();
            version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            Assert.Equal("99", version.ExecuteScalar() as string);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CorruptCatalogueIsQuarantinedRatherThanParsedAsEmptySuccess()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-bad-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            File.WriteAllText(path, "this is not sqlite");
            using var store = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            Assert.NotNull(store.QuarantinedFrom);
            Assert.True(File.Exists(store.QuarantinedFrom));
            Assert.Equal("this is not sqlite", File.ReadAllText(store.QuarantinedFrom));
            Assert.Empty(store.Nodes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FetcherRejectsHtmlOversizedAndOffRegistryRedirects()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com" && request.RequestUri.AbsolutePath == "/ok")
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/steal") } };
            }

            if (request.RequestUri.AbsolutePath == "/html")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html"),
                };
            }

            if (request.RequestUri.AbsolutePath == "/same")
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 32), Encoding.UTF8, "text/plain"),
            };
        });
        var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var redirect = await fetcher.GetAsync(new Uri("https://raw.githubusercontent.com/ok"), null, 100, CancellationToken.None);
        Assert.Equal(ReasonCodes.OffRegistryRedirect, redirect.ReasonCode);
        Assert.Null(redirect.Body);
        var html = await fetcher.GetAsync(new Uri("https://raw.githubusercontent.com/html"), null, 100, CancellationToken.None);
        Assert.Equal(ReasonCodes.HtmlContent, html.ReasonCode);
        Assert.Null(html.Body);
        var same = await fetcher.GetAsync(new Uri("https://raw.githubusercontent.com/same"), "\"etag\"", 100, CancellationToken.None);
        Assert.True(same.NotModified);
        Assert.Null(same.Body);
        var big = await fetcher.GetAsync(new Uri("https://raw.githubusercontent.com/big"), null, 4, CancellationToken.None);
        Assert.Equal(ReasonCodes.SizeLimit, big.ReasonCode);
        Assert.Null(big.Body);
        var http = await fetcher.GetAsync(new Uri("http://raw.githubusercontent.com/big"), null, 100, CancellationToken.None);
        Assert.Equal(ReasonCodes.OffRegistryRedirect, http.ReasonCode);
        var userInfo = await fetcher.GetAsync(new Uri("https://user:secret@raw.githubusercontent.com/ok"), null, 100, CancellationToken.None);
        Assert.Equal(ReasonCodes.OffRegistryRedirect, userInfo.ReasonCode);
        Assert.Null(userInfo.Body);
    }

    [Fact]
    public void TruncatedTreeIsNotACompleteCatalogue()
    {
        var truncated = GithubTreeParser.Parse("""{"sha":"abc","truncated":true,"tree":[{"path":"BLACK_VLESS_RUS.txt","type":"blob","size":10}]}""");
        Assert.False(truncated.Complete);
        Assert.Empty(truncated.Paths);
        var complete = GithubTreeParser.Parse("""
            {"sha":"abc","truncated":false,"tree":[
              {"path":"BLACK_VLESS_RUS.txt","type":"blob","size":10},
              {"path":"TOR-BRIDGES/bridges.txt","type":"blob","size":4},
              {"path":"README.md","type":"blob","size":4},
              {"path":"submodule","type":"commit","size":0}
            ]}
            """);
        Assert.True(complete.Complete);
        Assert.Equal("black-vless", Assert.Single(complete.Paths, item => item.FamilyId == "black-vless").FamilyId);
        Assert.Equal("black-vless-mobile", SeedFamilies.MatchFamily("BLACK_VLESS_RUS_mobile.txt"));
        Assert.Equal("black-vless", SeedFamilies.MatchFamily("BLACK_VLESS_RUS.txt"));
        Assert.Contains(complete.Paths, item => item.Class == ArtifactClass.TorBridgeList);
        Assert.Contains(complete.Paths, item => item.Class == ArtifactClass.Documentation);
        Assert.DoesNotContain(complete.Paths, item => item.Path == "submodule");
    }

    [Fact]
    public async Task DefaultBrokerDoesNotClaimConnectedAndDisconnectWins()
    {
        var catalogue = PreparedCatalogue();
        var engine = new BrokerEngine(catalogue, new UnavailableNetworkGuard(), new RefusingCoreController());
        var connect = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = "",
            Digest = "",
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Assert.False(connect.Ok);
        Assert.Equal(UnavailableNetworkGuard.PlatformReason(), connect.ErrorCode);
        Assert.NotEqual(nameof(TunnelPhase.Connected), connect.Snapshot!.Phase);
        Assert.False(connect.Snapshot.ProtectionArmed);
        Assert.False(connect.Snapshot.CoreRunning);

        var disconnect = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: connect.Snapshot.Revision), CancellationToken.None);
        Assert.True(disconnect.Ok);
        Assert.Equal(nameof(TunnelPhase.Disconnected), disconnect.Snapshot!.Phase);
        Assert.True(disconnect.Snapshot.Generation > connect.Snapshot.Generation);
    }

    [Fact]
    public async Task ArmedCoreStillNeedsExplicitProductionConfirmation()
    {
        var catalogue = PreparedCatalogue();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), new StartingCore());
        var connect = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = catalogue.Nodes[0].NodeId,
            Digest = catalogue.Nodes[0].Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Assert.True(connect.Ok);
        Assert.Equal(nameof(TunnelPhase.Connecting), connect.Snapshot!.Phase);
        Assert.NotEqual(nameof(TunnelPhase.Connected), connect.Snapshot.Phase);
        Confirm(engine, connect, catalogue.NetworkEpoch);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list-b", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.20"), ContentHash = "b" },
        ], Now, false);
        var standbyNode = catalogue.Nodes.Single(node => node.Semantics.Host == "203.0.113.20");
        catalogue.ApplyAssessment(standbyNode.NodeId, Healthy(standbyNode, 80) with { LastSuccessUtc = DateTimeOffset.UtcNow });
        var update = await engine.HandleAsync(Request(IpcOperations.ApplyRuntimeSet, new Dictionary<string, object>
        {
            ["standbys"] = new[]
            {
                new StandbyCandidate
                {
                    NodeId = standbyNode.NodeId,
                    EndpointKey = "ignored",
                    Country = "US",
                    SourceFamilyId = "ignored",
                    Cost = 0,
                    FreshOnEpoch = false,
                },
                new StandbyCandidate
                {
                    NodeId = "not-a-node",
                    EndpointKey = "203.0.113.99:443",
                    Country = "DE",
                    SourceFamilyId = "black-vless",
                    Cost = 1,
                    FreshOnEpoch = true,
                },
            },
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(update.Ok);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        var health = await engine.HandleAsync(Request(IpcOperations.ReportHealth, new HealthPayload
        {
            FailureKind = nameof(FailureKind.CoreExit),
            ConsecutiveFailures = 3,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(health.Ok);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        Assert.Equal(standbyNode.NodeId, engine.State.ActiveNodeId);
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.Equal(1, update.Snapshot!.StandbyCount);
    }

    [Fact]
    public void PinnedAndStrictCountryDoNotSwitch()
    {
        var standby = new StandbyCandidate
        {
            NodeId = "other",
            EndpointKey = "203.0.113.50:443",
            Country = "US",
            SourceFamilyId = "black-vless",
            Cost = 1,
            FreshOnEpoch = true,
        };
        var pinned = FailoverPolicy.Decide(new FailoverContext
        {
            Generation = 1,
            CommandGeneration = 1,
            Mode = SelectionMode.Pinned,
            Failure = FailureKind.CoreExit,
            ConsecutiveHealthFailures = 3,
            ActiveNodeId = "current",
            Standbys = [standby],
        });
        Assert.Equal(FailoverAction.BlockPinned, pinned.Action);
        var country = FailoverPolicy.Decide(new FailoverContext
        {
            Generation = 1,
            CommandGeneration = 1,
            Mode = SelectionMode.Automatic,
            CountryMode = CountryConstraint.Strict,
            StrictCountry = "DE",
            Failure = FailureKind.CoreExit,
            ConsecutiveHealthFailures = 3,
            ActiveNodeId = "current",
            Standbys = [standby],
        });
        Assert.Equal(FailoverAction.BlockNoServer, country.Action);
        var stale = FailoverPolicy.Decide(new FailoverContext
        {
            Generation = 2,
            CommandGeneration = 1,
            Failure = FailureKind.ExplicitDisconnect,
        });
        Assert.Equal(FailoverAction.IgnoreStale, stale.Action);
    }

    [Fact]
    public void DispatcherRejectsRemoteReplayForbiddenAndUnknown()
    {
        var dispatcher = new ProtocolTestDispatcher();
        var caller = new CallerIdentity { Sid = "S-1", SessionId = 1 };
        var remote = dispatcher.Dispatch(Request(IpcOperations.GetSnapshot, new { }), new CallerIdentity { Sid = "S-1", SessionId = 1, IsRemotePipe = true }, _ => throw new InvalidOperationException());
        Assert.False(remote.Ok);
        var first = dispatcher.Dispatch(Request(IpcOperations.GetSnapshot, new { }, "same"), caller, request => new IpcResponse { RequestId = request.RequestId, Ok = true });
        Assert.True(first.Ok);
        var replay = dispatcher.Dispatch(Request(IpcOperations.GetSnapshot, new { }, "same"), caller, _ => throw new InvalidOperationException());
        Assert.True(replay.Ok);
        Assert.Equal(first.RequestId, replay.RequestId);
        var forbidden = dispatcher.Dispatch(Request(IpcOperations.Connect, new { yaml = "tun: enable" }), caller, _ => throw new InvalidOperationException());
        Assert.Equal("FORBIDDEN_FIELD", forbidden.ErrorCode);
        var nested = dispatcher.Dispatch(Request(IpcOperations.Connect, new { nested = new { profilePath = "C:\\x" } }), caller, _ => throw new InvalidOperationException());
        Assert.Equal("FORBIDDEN_FIELD", nested.ErrorCode);
        var unknown = dispatcher.Dispatch(Request("Shell", new { }), caller, _ => throw new InvalidOperationException());
        Assert.Equal("UNKNOWN_OPERATION", unknown.ErrorCode);
    }

    [Fact]
    public async Task ProfileHidesLabelsAndTunValidationDoesNotRunHere()
    {
        var wire = new NodeWire
        {
            NodeId = "abc123",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = Uuid,
            Encryption = "none",
            Security = "tls",
            Sni = "www.example.com",
            Transport = "tcp",
            DisplayLabel = "LABEL-SHOULD-NOT-LEAK",
            AdvertisedCountry = "Германия",
        };
        var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = true,
            Nodes = [wire],
            SelectedNodeId = wire.NodeId,
        });
        Assert.DoesNotContain("LABEL-SHOULD-NOT-LEAK", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Германия", yaml, StringComparison.Ordinal);
        Assert.Contains("name: 'nabc123'", yaml, StringComparison.Ordinal);
        Assert.Contains("MATCH,AUTO_SELECT", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MATCH,DIRECT", yaml, StringComparison.Ordinal);
        Assert.True(yaml.IndexOf("DST-PORT,53", StringComparison.Ordinal) < yaml.IndexOf("IP-CIDR,10.0.0.0/8,DIRECT", StringComparison.Ordinal));
        Assert.Contains("enhanced-mode: redir-host", yaml, StringComparison.Ordinal);
        var validation = await MihomoProcessController.ValidateAsync("/does/not/exist", "00", yaml, CancellationToken.None);
        Assert.Equal(OperatingSystem.IsWindows() ? "CORE_MISSING" : ReasonCodes.NotWindows, validation.ReasonCode);
    }

    [RequiresMihomoFact]
    public async Task PinnedLinuxCoreValidatesSyntheticNonTunProfileWhenProvided()
    {
        var path = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Fail("AUTOVPN_MIHOMO_PATH is unset. Native mihomo -t is NOT_RUN, not a pass.");
        }

        var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes =
            [
                new NodeWire
                {
                    NodeId = "abc123",
                    Digest = "digest",
                    Protocol = "vless",
                    Host = "203.0.113.10",
                    Port = 443,
                    UserId = Uuid,
                    Encryption = "none",
                    Security = "tls",
                    Sni = "www.example.com",
                    Transport = "tcp",
                },
            ],
            SelectedNodeId = "abc123",
        });
        var result = await MihomoProcessController.ValidateAsync(path!, TestCorePins.ExpectedHash, yaml, CancellationToken.None);
        Assert.True(result.Ok, result.RedactedOutput);
    }

    [Fact]
    public void ConnectedLabelIsUsedOnlyForTheConnectedPhase()
    {
        Assert.Equal(Ru.Connected, SessionText.Phase(nameof(TunnelPhase.Connected)));
        Assert.NotEqual(Ru.Connected, SessionText.Phase(nameof(TunnelPhase.Connecting)));
        Assert.NotEqual(Ru.Connected, SessionText.Phase(nameof(TunnelPhase.Blocked)));
        Assert.False(SessionText.IsConnected(nameof(TunnelPhase.Connecting)));
        Assert.True(SessionText.OffersDisconnect(nameof(TunnelPhase.Connecting)));
        Assert.True(SessionText.OffersDisconnect(nameof(TunnelPhase.Blocked), protectionArmed: true));
        Assert.False(SessionText.OffersDisconnect(nameof(TunnelPhase.Blocked)));
        Assert.False(SessionText.OffersDisconnect(nameof(TunnelPhase.Disconnected)));
    }

    [Fact]
    public void ScheduleDoesNotHonorOneMinuteAdvisoryOrReplayAGap()
    {
        var interval = RefreshSchedule.Interval(1, 0, 0);
        Assert.Equal(TimeSpan.FromMinutes(ProductLimits.MinimumRefreshIntervalMinutes), interval);
        Assert.Equal(1, RefreshSchedule.MissedIntervalsToReplay(TimeSpan.FromHours(10), interval));
        var ranked = Ranker.Order([
            new RankSample { NodeId = "known", EndpointKey = "a", MedianLatencyMs = 100, SampleCount = 20, DownloadMbps = 5 },
            new RankSample { NodeId = "unknown", EndpointKey = "b", MedianLatencyMs = 100, SampleCount = 20 },
        ], RankMode.FasterDownload);
        Assert.Equal("known", ranked[0].NodeId);
    }

    [Fact]
    public async Task LocalPipeRejectsASecondOwnerAndReturnsSnapshot()
    {
        var catalogue = new MemoryCatalogue();
        var engine = new BrokerEngine(catalogue, new UnavailableNetworkGuard(), new RefusingCoreController());
        var dispatcher = new ProtocolTestDispatcher();
        var pipe = "autovpn-test-" + Guid.NewGuid().ToString("N");
        await using var server = LocalIpcServer.Start(pipe, dispatcher, engine, new CallerIdentity { Sid = "owner", SessionId = 1 });
        var response = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal(nameof(TunnelPhase.Disconnected), response.Snapshot!.Phase);
        var second = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
        Assert.NotNull(second);
        Assert.True(second!.Ok);
        Assert.Equal(nameof(TunnelPhase.Disconnected), second.Snapshot!.Phase);
    }

    [Fact]
    public async Task ProbeBudgetLeavesTheRestPendingInsteadOfStoppingAtEight()
    {
        var catalogue = new MemoryCatalogue();
        var lines = string.Join('\n', Enumerable.Range(1, 9).Select(index => NodeUri($"203.0.113.{index}")));
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, Text = lines, ContentHash = "a" },
        ], Now, false);
        var report = await ProbeCoordinator.RunAsync(
            catalogue,
            new ScriptedTransport(node => Proven(node, 40)),
            new Uri("https://cp.cloudflare.com/generate_204"),
            Now,
            CancellationToken.None,
            TimeSpan.Zero);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(8, catalogue.Nodes.Count(node => node.Assessment?.Health == HealthState.Pending));
    }

    [Fact]
    public void PolicyBlockClearsWhenTheSameNodeIsImportedUnderTheOwnerAllow()
    {
        var catalogue = new MemoryCatalogue();
        var uri = $"vless://{Uuid}@203.0.113.40:443?encryption=none&security=tls&allowInsecure=1&type=tcp&sni=www.example.com#Germany";
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, Text = uri, ContentHash = "a" },
        ], Now, false);
        Assert.Equal(ReasonCodes.CertVerificationDisabled, catalogue.Nodes[0].PolicyReason);
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, Text = uri, ContentHash = "b" },
        ], Now, true);
        Assert.Null(catalogue.Nodes[0].PolicyReason);
        Assert.Equal(HealthState.Pending, catalogue.Nodes[0].Assessment!.Health);
    }

    [Fact]
    public void ProfileRejectsControlCharactersInsecureCertsAndEmitsPluginOpts()
    {
        var wire = new NodeWire
        {
            NodeId = "abc123",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = Uuid,
            Password = "line\nproxies: []",
            Encryption = "none",
            Security = "tls",
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
        var insecure = new NodeWire
        {
            NodeId = "abc123",
            Digest = "digest",
            Protocol = "vless",
            Host = "203.0.113.10",
            Port = 443,
            UserId = Uuid,
            Encryption = "none",
            Security = "tls",
            Transport = "tcp",
            SkipCertVerify = true,
        };
        var blocked = Assert.Throws<InvalidOperationException>(() => MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes = [insecure],
            SelectedNodeId = insecure.NodeId,
        }));
        Assert.Equal(ReasonCodes.CertVerificationDisabled, blocked.Message);
        var plugin = new NodeWire
        {
            NodeId = "abc123",
            Digest = "digest",
            Protocol = "shadowsocks",
            Host = "203.0.113.10",
            Port = 443,
            Password = "secret",
            Encryption = "aes-256-gcm",
            Plugin = "obfs",
            PluginOpts = "mode=http;host=www.example.com",
        };
        var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "unit-test-secret-01",
            ControllerPort = 9090,
            Tun = false,
            Nodes = [plugin],
            SelectedNodeId = plugin.NodeId,
        });
        Assert.Contains("plugin: 'obfs'", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: 'http'", yaml, StringComparison.Ordinal);
        Assert.Contains("host: 'www.example.com'", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleRevisionDoesNotDisconnectAndCancelDuringStartStopsTheCore()
    {
        var catalogue = PreparedCatalogue();
        var guard = new RecordingGuard();
        var core = new GateCore();
        var engine = new BrokerEngine(catalogue, guard, core);
        var stale = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: 50), CancellationToken.None);
        Assert.Equal(ReasonCodes.StaleRevision, stale.ErrorCode);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);

        var connect = engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = catalogue.Nodes[0].NodeId,
            Digest = catalogue.Nodes[0].Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            LanAccess = false,
        }), CancellationToken.None);
        await core.Started.Task;
        var disconnect = engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        await core.Stopped.Task;
        core.Release.TrySetResult();
        var connectResult = await connect;
        var disconnectResult = await disconnect;
        Assert.False(connectResult.Ok);
        Assert.Equal(ReasonCodes.Canceled, connectResult.ErrorCode);
        Assert.True(disconnectResult.Ok);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
        Assert.False(engine.State.ProtectionArmed);
        Assert.True(core.Stops >= 1);
        Assert.DoesNotContain("IP-CIDR,10.0.0.0/8,DIRECT", core.Yaml, StringComparison.Ordinal);
        Assert.Equal(guard.ArmedGeneration, guard.DisarmedGeneration);
    }

    [Fact]
    public async Task SwitchBudgetStaysOnTheCurrentSession()
    {
        var catalogue = PreparedCatalogue();
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list-b", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.21"), ContentHash = "b" },
        ], Now, false);
        var other = catalogue.Nodes.Single(node => node.Semantics.Host == "203.0.113.21");
        catalogue.ApplyAssessment(other.NodeId, Healthy(other, 90) with { LastSuccessUtc = DateTimeOffset.UtcNow });
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), new StartingCore());
        var connected = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = catalogue.Nodes[0].NodeId,
            Digest = catalogue.Nodes[0].Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Confirm(engine, connected, catalogue.NetworkEpoch);
        var active = catalogue.Nodes[0].NodeId;
        for (var i = 0; i < 3; i++)
        {
            var target = active == catalogue.Nodes[0].NodeId ? other : catalogue.Nodes[0];
            await engine.HandleAsync(Request(IpcOperations.ApplyRuntimeSet, new Dictionary<string, object>
            {
                ["standbys"] = new[]
                {
                    new StandbyCandidate
                    {
                        NodeId = target.NodeId,
                        EndpointKey = "x",
                        Country = "",
                        SourceFamilyId = "",
                        Cost = 1,
                        FreshOnEpoch = true,
                    },
                },
            }, revision: engine.Snapshot().Revision), CancellationToken.None);
            var switched = await engine.HandleAsync(Request(IpcOperations.ReportHealth, new HealthPayload
            {
                FailureKind = nameof(FailureKind.CoreExit),
                ConsecutiveFailures = 3,
                NetworkEpoch = catalogue.NetworkEpoch,
            }, revision: engine.Snapshot().Revision), CancellationToken.None);
            Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
            Confirm(engine, switched, catalogue.NetworkEpoch);
            active = engine.State.ActiveNodeId!;
        }

        var before = engine.State.ActiveNodeId;
        var held = await engine.HandleAsync(Request(IpcOperations.ReportHealth, new HealthPayload
        {
            FailureKind = nameof(FailureKind.CoreExit),
            ConsecutiveFailures = 3,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(held.Ok);
        Assert.Equal(TunnelPhase.Reconnecting, engine.State.Phase);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal(before, engine.State.ActiveNodeId);
        var outage = await engine.HandleAsync(Request(IpcOperations.ReportHealth, new HealthPayload
        {
            FailureKind = nameof(FailureKind.TargetOutage),
            ConsecutiveFailures = 1,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.Equal(TunnelPhase.Reconnecting, engine.State.Phase);
        Assert.Contains("не переключена", outage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedJournalSchemaIsLeftUntouched()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-journal-");
        try
        {
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            using (var created = EffectJournal.Open(path))
            {
                created.Record(new OwnedEffect("e1", "route", "synthetic"));
            }

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value='0' WHERE key='schema_version';";
                command.ExecuteNonQuery();
            }

            var error = Assert.Throws<CatalogueStoreException>(() => EffectJournal.Open(path));
            Assert.Contains("untouched", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.quarantine-*"));
            using var check = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            check.Open();
            using var version = check.CreateCommand();
            version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            Assert.Equal("0", version.ExecuteScalar() as string);
            using var open = check.CreateCommand();
            open.CommandText = "SELECT count(*) FROM effects WHERE removed_utc IS NULL;";
            Assert.Equal(1L, (long)open.ExecuteScalar()!);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static MemoryCatalogue PreparedCatalogue()
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact { ArtifactId = "list", FamilyId = "black-vless", Enabled = true, Text = NodeUri("203.0.113.10"), ContentHash = "a" },
        ], Now, false);
        var fresh = Healthy(catalogue.Nodes[0], 120) with { LastSuccessUtc = DateTimeOffset.UtcNow };
        catalogue.ApplyAssessment(catalogue.Nodes[0].NodeId, fresh);
        return catalogue;
    }

    private static string NodeUri(string host)
    {
        return $"vless://{Uuid}@{host}:443?encryption=none&security=tls&type=tcp&sni=www.example.com#Germany";
    }

    private static AssessmentSnapshot Healthy(CatalogueNode node, int latency)
    {
        return new AssessmentSnapshot
        {
            Digest = node.Digest,
            NetworkEpoch = 1,
            Health = HealthState.Healthy,
            LastSuccessUtc = Now,
            MedianLatencyMs = latency,
        };
    }

    private static EligibilityContext Context()
    {
        return new EligibilityContext { NowUtc = Now, NetworkEpoch = 1, AllowInsecureCertificates = false };
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

    private static ProbeObservation Proven(NodeSemantics node, int latency)
    {
        return new ProbeObservation(true, latency, false, null, 0, ProbeClass.Success, "https://cp.cloudflare.com/generate_204", CanonicalIdentity.Digest(node), "unit-proof");
    }

    private sealed class ScriptedTransport(Func<NodeSemantics, ProbeObservation> next) : BoundTestProbeTransport
    {
        public override Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        {
            _ = target;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(next(node));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(next(request));
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public string ProtectorId => "test-xor";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var copy = plaintext.ToArray();
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] ^= 0x5A;
            }

            return copy;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return Protect(ciphertext);
        }
    }

    private sealed class ArmingGuard : INetworkGuard
    {
        public GuardResult Arm(GuardRequest request)
        {
            _ = request;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            _ = generation;
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, effects.Select(effect => effect.Id).ToArray());
        }
    }

    private sealed class RecordingGuard : INetworkGuard
    {
        public long ArmedGeneration { get; private set; }
        public long DisarmedGeneration { get; private set; } = -1;

        public GuardResult Arm(GuardRequest request)
        {
            ArmedGeneration = request.Generation;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            DisarmedGeneration = generation;
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, effects.Select(effect => effect.Id).ToArray());
        }
    }

    private sealed class GateCore : ICoreController
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops { get; private set; }
        public string Yaml { get; private set; } = "";

        public long RunningGeneration { get; private set; }
        public string RunningOperation { get; private set; } = "";
        public int IgnoredStops { get; private set; }

        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            Yaml = yaml;
            RunningGeneration = generation;
            RunningOperation = operationId;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CoreStartResult(true, null);
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunningGeneration != generation || !string.Equals(RunningOperation, operationId, StringComparison.Ordinal))
            {
                IgnoredStops++;
                return Task.CompletedTask;
            }

            Stops++;
            RunningGeneration = 0;
            RunningOperation = "";
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class StartingCore : ICoreController
    {
        public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = generation;
            _ = operationId;
            Assert.DoesNotContain("LABEL-SHOULD-NOT-LEAK", yaml, StringComparison.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CoreStartResult(true, null));
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = generation;
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// xUnit 2.9 has no runner support for <c>SkipException.ForSkip</c> (that signal is v3).
/// An absent Mihomo binary is a skip, never a passing fact.
/// </summary>
public sealed class RequiresMihomoFactAttribute : FactAttribute
{
    public RequiresMihomoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH")))
        {
            Skip = "AUTOVPN_MIHOMO_PATH is unset. Native mihomo -t is NOT_RUN, not a pass.";
        }
    }
}
