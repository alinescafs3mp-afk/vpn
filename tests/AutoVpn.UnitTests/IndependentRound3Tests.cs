using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;
using Xunit;

namespace AutoVpn.UnitTests;

// Independent acceptance assertions for e7797c7. Failures are audit evidence,
// not assertions to reverse until the suite is green. No TUN, routes, firewall,
// live subscription credentials, or external probe endpoints are used here.
public sealed class IndependentRound3Tests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Target = new("https://example.com/generate_204");

    [Fact]
    public void A01_Control_NormalizedTlsIsActuallyEmitted()
    {
        var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "audit-synthetic-controller", ControllerPort = 19090,
            Tun = false, Nodes = [new NodeWire
            {
                NodeId = "synthetic", Digest = "synthetic", Protocol = "vless",
                Host = "203.0.113.10", Port = 443, UserId = Uuid,
                Security = " TLS ", Encryption = "none", Transport = "tcp",
            }],
        });
        Assert.Contains("tls: true", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("tun:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A02_Control_ReadRequestsDoNotExhaustDisconnect()
    {
        var dispatcher = new IpcDispatcher();
        for (var index = 0; index < 1500; index++)
        {
            var result = dispatcher.Dispatch(Request(IpcOperations.GetSnapshot, "read-" + index), Caller(), Ok);
            Assert.True(result.Ok);
        }
        Assert.True(dispatcher.Dispatch(Request(IpcOperations.Disconnect, "disconnect"), Caller(), Ok).Ok);
    }

    [Fact]
    public void A03_LongLivedMutationTrafficMustRemainUsable()
    {
        var dispatcher = new IpcDispatcher();
        for (var index = 0; index < 300; index++)
        {
            var result = dispatcher.Dispatch(Request(IpcOperations.ReportHealth, "health-" + index), Caller(), Ok);
            Assert.True(result.Ok, $"Mutation {index} rejected: {result.ErrorCode}. A renewable session must not have a lifetime command cap.");
        }
    }

    [Fact]
    public async Task A04_MismatchedCandidateAndTargetProofCannotPublishHealthy()
    {
        var catalogue = Catalogue();
        var report = await ProbeCoordinator.RunAsync(catalogue, new Scripted(_ =>
            new ProbeObservation(true, 10, false, null, 20, ProbeClass.Success,
                "https://different.example/", "not-the-candidate-digest", "different-worker")),
            Target, Now, CancellationToken.None);
        Assert.Empty(catalogue.Eligible(Context(catalogue)));
        Assert.Equal(0, report.Succeeded);
    }

    [Fact]
    public async Task A05_OneUnsupportedCandidateMustNotStarveOtherCandidates()
    {
        var catalogue = Catalogue(2);
        var calls = 0;
        var report = await ProbeCoordinator.RunAsync(catalogue, new Scripted(node =>
        {
            calls++;
            return calls == 1
                ? new ProbeObservation(false, null, false, "UNSUPPORTED_TRANSPORT", 0, ProbeClass.Unsupported)
                : Success(node);
        }), Target, Now, CancellationToken.None);
        Assert.Equal(2, report.Attempted);
        Assert.Equal(1, report.Succeeded);
    }

    [Fact]
    public async Task A06_FailedOnDemandCheckMustInvalidateOldHealthyEvidence()
    {
        var catalogue = Catalogue();
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, Healthy(node, Now.AddSeconds(-65)));
        var admitted = await ProbeCoordinator.AdmitIfStaleAsync(catalogue,
            new Scripted(_ => new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed)),
            Target, node.NodeId, Now, CancellationToken.None);
        Assert.False(admitted);
        Assert.Empty(catalogue.Eligible(Context(catalogue)));
    }

    [Fact]
    public async Task A07_SpeedFromPreviousNetworkEpochMustBeRejected()
    {
        var catalogue = Catalogue();
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, Healthy(node, Now));
        catalogue.SetNetworkEpoch(2);
        using var payload = new MemoryStream(new byte[256]);
        var sample = await SpeedMeasurement.MeasureHealthyDownloadAsync(catalogue, node.NodeId,
            payload, CancellationToken.None, new SpeedMeasurement.MeasurementBinding(node.Digest, 1));
        Assert.Null(sample);
    }

    [Fact]
    public void A08_ByteCounterMustSaturateRatherThanOverflow()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-audit-budget-");
        try
        {
            var path = Path.Combine(directory.FullName, "budget");
            File.WriteAllText(path, "2026-10-03\n" + (long.MaxValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            var day = new DateOnly(2026, 10, 3);
            var budget = ProbeByteBudget.Load(path, 100, day);
            budget.Charge(10, day);
            Assert.True(budget.Exhausted(day), "Overflow must not restore a spent daily allowance.");
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void A09_LatePreviousDayChargeMustNotRefundTodaysBudget()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-audit-day-");
        try
        {
            var day = new DateOnly(2026, 10, 3);
            var budget = ProbeByteBudget.Load(Path.Combine(directory.FullName, "budget"), 10, day);
            budget.Charge(10, day);
            budget.Charge(1, day.AddDays(-1));
            Assert.True(budget.Exhausted(day));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void A10_BenchmarkSpecialUseAddressMustNotBePublic()
    {
        Assert.True(EndpointSafety.IsNonPublicAddress(IPAddress.Parse("198.18.0.1")));
        Assert.True(EndpointSafety.IsNonPublicAddress(IPAddress.Parse("198.19.0.1")));
    }

    [Fact]
    public void A11_QuotedSecretValuesMustBeRedacted()
    {
        const string canary = "AUDIT_SYNTHETIC_SECRET_73";
        var redacted = SecretRedactor.Redact("{\"password\":\"" + canary + "\",\"secret\":\"" + canary + "\"}");
        Assert.DoesNotContain(canary, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A12_NineTinySubscriptionsMustEventuallyAllBeFetched()
    {
        var catalogue = Catalogue();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mutex = new object();
        using var fetcher = new PolicyHttpFetcher(new Handler(request =>
        {
            lock (mutex) { seen.Add(request.RequestUri!.AbsolutePath); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixtureUri()) };
        }), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new Scripted(Success), new SourceLedger());
        var items = Enumerable.Range(0, 9).Select(i => new RefreshWorkItem
        {
            ArtifactId = "item-" + i, FamilyId = "black-vless",
            Urls = [new Uri("https://raw.githubusercontent.com/audit/item-" + i)],
        }).ToArray();
        await coordinator.RefreshAsync(items, Now, CancellationToken.None);
        await coordinator.RefreshAsync(items, Now.AddHours(2), CancellationToken.None);
        Assert.Contains("/audit/item-8", seen);
    }

    [Fact]
    public async Task A13_GitTreeIdentityMustNotBeUsedAsRawCommitRef()
    {
        const string commit = "1111111111111111111111111111111111111111";
        const string tree = "2222222222222222222222222222222222222222";
        var treeApi = new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/" + commit + "?recursive=1");
        var registry = new ReviewedRegistry
        {
            Owner = "igareck", Repository = "vpn-configs-for-russia", PinnedCommit = commit,
            TreeApi = treeApi, FamilyIds = ["black-vless"],
            ApprovedHosts = new HashSet<string> { "raw.githubusercontent.com" },
            RejectedHosts = new HashSet<string>(), ProbeTargets = [Target],
            FetchOrigins = [new("api.github.com", 443, "/repos/igareck/vpn-configs-for-russia/git/trees/"),
                new("raw.githubusercontent.com", 443, "/igareck/vpn-configs-for-russia/")],
        };
        var json = "{\"sha\":\"" + tree + "\",\"truncated\":false,\"tree\":[{\"path\":\"BLACK_VLESS_RUS.txt\",\"type\":\"blob\",\"mode\":\"100644\",\"size\":100}]}";
        using var fetcher = new PolicyHttpFetcher(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json) }), registry.FetchOrigins);
        var result = await new CatalogueCoordinator(Catalogue(), fetcher, new Scripted(Success), new SourceLedger())
            .DiscoverAsync(registry, CancellationToken.None);
        Assert.True(result.Complete);
        var item = Assert.Single(result.Items);
        Assert.All(item.Urls, url => Assert.Contains("/" + commit + "/", url.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public void A14_PortOwnershipCheckMustWorkOnTheSupportedWindowsTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(ProbeWorker.ProcessOwnsLoopbackPort(Environment.ProcessId, port));
    }

    [Fact]
    public async Task A15_CancellationBeforePublicationMustNotPublishHealthy()
    {
        var catalogue = Catalogue();
        using var stop = new CancellationTokenSource();
        var report = await ProbeCoordinator.RunAsync(catalogue, new Scripted(node =>
        {
            stop.Cancel();
            return Success(node);
        }), Target, Now, stop.Token);
        Assert.Empty(catalogue.Eligible(Context(catalogue)));
        Assert.Equal(0, report.Succeeded);
    }

    [Fact]
    public async Task A16_OutputDrainMustContinueAfterItsRetentionCap()
    {
        if (!OperatingSystem.IsLinux()) { return; } // Linux-specific child fixture, not a Windows proof.
        var directory = Directory.CreateTempSubdirectory("autovpn-audit-output-");
        var marker = Path.Combine(directory.FullName, "after-output");
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("head -c 3145728 /dev/zero; printf done > \"$1\"; sleep 10");
        start.ArgumentList.Add("audit");
        start.ArgumentList.Add(marker);
        await using var worker = await ProbeWorker.StartAsync(start, null, TimeSpan.FromSeconds(2), CancellationToken.None, directory.FullName);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(25); }
            Assert.True(File.Exists(marker), "The owned child blocked writing output after CountAsync stopped draining at 1 MiB.");
        }
        finally { await worker.DisposeAsync(); }
    }

    [Fact]
    public void A17_SymlinkBlobMustNotBecomeSubscriptionData()
    {
        var tree = GithubTreeParser.Parse("{\"sha\":\"1111111111111111111111111111111111111111\",\"truncated\":false,\"tree\":[{\"path\":\"BLACK_VLESS_RUS.txt\",\"type\":\"blob\",\"mode\":\"120000\",\"size\":12}]}");
        Assert.DoesNotContain(tree.Paths, path => ArtifactClassifier.IsSubscriptionData(path.Class));
    }

    [Fact]
    public void A18_MalformedLabelMustNotEscapeContainment()
    {
        var error = Record.Exception(() => CountryLabels.FromLabel("\ud800A"));
        Assert.Null(error);
    }

    [Fact]
    public void A19_Control_UnknownSecurityIsRejected()
    {
        var record = Assert.Single(AutoVpn.Infrastructure.Import.SubscriptionImporter.Import(FixtureUri().Replace("security=tls", "security=bogus", StringComparison.Ordinal)).Records);
        Assert.NotEqual(AutoVpn.Infrastructure.Import.RecordDisposition.Pending, record.Disposition);
    }

    [Fact]
    public void A20_Control_PendingCandidatesAreNotEligible()
    {
        Assert.Empty(Catalogue().Eligible(new EligibilityContext { NowUtc = Now, NetworkEpoch = 1 }));
    }

    private static MemoryCatalogue Catalogue(int count = 1)
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        for (var index = 0; index < count; index++)
        {
            var semantics = new NodeSemantics
            {
                Protocol = ProtocolKind.Vless, Host = "203.0.113." + (10 + index), Port = 443,
                UserId = Uuid, Security = "tls", Encryption = "none", Transport = "tcp",
            };
            var digest = CanonicalIdentity.Digest(semantics);
            catalogue.ApplySnapshot(new SnapshotCommit
            {
                ArtifactId = "seed-" + index, FamilyId = "black-vless", ContentHash = "synthetic",
                Complete = true, NowUtc = Now, Nodes = [new SnapshotNode
                {
                    Digest = digest, Semantics = semantics, Label = "synthetic", ArtifactId = "seed-" + index,
                    FamilyId = "black-vless",
                }],
            });
        }
        return catalogue;
    }
    private static string FixtureUri() => $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#synthetic";
    private static EligibilityContext Context(ICatalogue catalogue) => new() { NowUtc = Now, NetworkEpoch = catalogue.NetworkEpoch };
    private static AssessmentSnapshot Healthy(CatalogueNode node, DateTimeOffset time) => new()
    { Digest = node.Digest, NetworkEpoch = 1, Health = HealthState.Healthy, LastSuccessUtc = time, MedianLatencyMs = 20 };
    private static ProbeObservation Success(NodeSemantics node) => new(true, 10, false, null, 20,
        ProbeClass.Success, Target.AbsoluteUri, CanonicalIdentity.Digest(node), "synthetic-owned-worker");
    private static CallerIdentity Caller() => new() { Sid = "audit-synthetic-user", SessionId = 1 };
    private static IpcRequest Request(string operation, string id) => new()
    { ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = id, Operation = operation, Payload = JsonSerializer.SerializeToElement(new { }) };
    private static IpcResponse Ok(IpcRequest request) => new() { RequestId = request.RequestId, Ok = true };
    private sealed class Scripted(Func<NodeSemantics, ProbeObservation> next) : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
            => Task.FromResult(next(node));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(next(request));
    }
}
