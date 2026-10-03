using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;
using Xunit;

namespace AutoVpn.UnitTests;

// Independent acceptance tests. Synthetic credentials only; no TUN, firewall changes or public-node dials.
public sealed class IndependentRound4Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Target = new("https://probe.example/generate_204");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Q01_MissingEvidenceCannotPublishHealthy(bool supplyDigestAndTarget)
    {
        var c = Catalogue(false);
        var transport = new Scripted((node, target, _) => Task.FromResult(new ProbeObservation(true, 12, false, null,
            Class: ProbeClass.Success, CandidateDigest: supplyDigestAndTarget ? CanonicalIdentity.Digest(node) : null,
            TargetUri: supplyDigestAndTarget ? target.AbsoluteUri : null, WorkerId: null)));
        await ProbeCoordinator.RunAsync(c, transport, Target, Now, CancellationToken.None);
        Assert.Empty(c.Eligible(new EligibilityContext { NowUtc = Now, NetworkEpoch = 1 }));
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("disabled-family")]
    [InlineData("country")]
    public async Task Q02_OnDemandDeniedPolicyMustNotDial(string policy)
    {
        var c = Catalogue(true);
        var node = c.Nodes[0];
        c.ApplyAssessment(node.NodeId, node.Assessment! with { LastSuccessUtc = Now.AddSeconds(-65) });
        if (policy == "excluded") c.TrySetExcluded(node.NodeId, true);
        if (policy == "disabled-family") c.Settings = c.Settings with { DisabledFamilyIds = ["black-vless"] };
        if (policy == "country") c.Settings = c.Settings with { CountryMode = CountryConstraint.Strict, Country = "FI" };
        var calls = 0;
        var transport = new Scripted((n, t, _) => { calls++; return Task.FromResult(Success(n, t)); });
        var accepted = await ProbeCoordinator.AdmitIfStaleAsync(c, transport, Target, node.NodeId, Now, CancellationToken.None);
        Assert.False(accepted);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Q03_RequestDeadlineReturnedAsCanceledIsNotUserCancellation()
    {
        var c = Catalogue(false);
        var transport = new Scripted(async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new ProbeObservation(false, null, false, ReasonCodes.Canceled, Class: ProbeClass.Canceled);
        });
        var report = await ProbeCoordinator.RunAsync(c, transport, Target, Now, CancellationToken.None,
            attemptTimeout: TimeSpan.FromMilliseconds(30));
        Assert.True(report.Failed > 0, "The caller was not canceled; a candidate deadline must not silently become a canceled cycle.");
    }

    [Fact]
    public async Task Q04_RetryAfterMustPaceTheProbeQueue()
    {
        var c = Catalogue(true);
        foreach (var n in c.Nodes) c.ApplyAssessment(n.NodeId, n.Assessment! with
        { Health = HealthState.Failed, LastFailureUtc = Now, RetryAfterUtc = Now.AddHours(1) });
        var calls = 0;
        await ProbeCoordinator.RunAsync(c, new Scripted((n, t, _) => { calls++; return Task.FromResult(Success(n, t)); }), Target, Now, CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Q05_FreshCoordinatorPerRefreshMustNotRestartSourceQueue()
    {
        var c = Catalogue(false); var ledger = new SourceLedger(); var fence = new RefreshFence();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        using var fetcher = Fetcher(request =>
        { seen.TryAdd(request.RequestUri!.AbsolutePath, 0); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Link()) }; });
        var items = Enumerable.Range(0, 9).Select(i => Item("item-" + i)).ToArray();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            fence.Begin();
            var coordinator = new CatalogueCoordinator(c, fetcher, new Scripted((n, t, _) => Task.FromResult(Success(n, t))), ledger, fence: fence);
            await coordinator.RefreshAsync(items, Now.AddHours(cycle * 2), CancellationToken.None);
        }
        Assert.Contains("/audit/item-8", seen.Keys);
    }

    [Fact]
    public async Task Q06_Valid304MustAdvanceSourceCheckFreshness()
    {
        var c = Catalogue(false); var ledger = new SourceLedger(); var item = Item("seed-0");
        ledger.Remember(item.Urls[0].AbsoluteUri, "v1", "body", Now, null);
        using var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var coordinator = new CatalogueCoordinator(c, fetcher, new Scripted((n, t, _) => Task.FromResult(Success(n, t))), ledger);
        await coordinator.RefreshAsync([item], Now.AddHours(3), CancellationToken.None);
        Assert.False(RefreshScheduleGate.AnyDue(ledger.Entries.Select(e => e.LastSuccessUtc), Now.AddHours(3), c.Settings, 0),
            "An unchanged successful response leaves the source perpetually due on each one-minute pulse.");
    }

    [Fact]
    public async Task Q07_BranchTreeObjectMustNotBecomeRawCommitReference()
    {
        const string tree = "2222222222222222222222222222222222222222";
        var registry = new ReviewedRegistry
        {
            Owner = "igareck", Repository = "vpn-configs-for-russia",
            PinnedCommit = "1111111111111111111111111111111111111111",
            TreeApi = new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1"),
            FamilyIds = ["black-vless"], FetchOrigins = [new ApprovedFetchOrigin("raw.githubusercontent.com", 443, "/igareck/vpn-configs-for-russia/")], ProbeTargets = [],
            ApprovedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" },
            RejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        using var fetcher = new PolicyHttpFetcher(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"sha\":\"" + tree + "\",\"truncated\":false,\"tree\":[{\"path\":\"BLACK_VLESS_RUS.txt\",\"type\":\"blob\",\"mode\":\"100644\",\"size\":30}]}") }),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com" });
        var discovery = await new CatalogueCoordinator(Catalogue(false), fetcher, new Scripted((n, t, _) => Task.FromResult(Success(n, t))), new SourceLedger()).DiscoverAsync(registry, CancellationToken.None);
        foreach (var item in discovery.Items)
            foreach (var uri in item.Urls) Assert.DoesNotContain("/" + tree + "/", uri.AbsoluteUri);
        Assert.True(discovery.Items.Count > 0, "The fixture must actually yield a subscription.");
    }

    [Fact]
    public void Q08_MissingPreviouslySeenJournalMustNotReopenAsClean()
    {
        var dir = Directory.CreateTempSubdirectory("autovpn-r4-journal-");
        var path = Path.Combine(dir.FullName, "effects.sqlite");
        try
        {
            using (var journal = EffectJournal.Open(path)) journal.Record(new OwnedEffect("owned-before-loss", "synthetic", "no OS effect"));
            File.Delete(path);
            Assert.True(EffectJournal.RequiresReconciliation(path));
            using var reopened = EffectJournal.Open(path);
            var result = reopened.Recover(new Guard());
            Assert.False(result.Completed, "Service-style Open erased the missing-journal condition before recovery.");
        }
        finally { if (dir.Exists) dir.Delete(true); }
    }

    [Fact]
    public void Q09_RetiredRequestCannotExecuteAgainAfterTombstoneEviction()
    {
        var dispatcher = new IpcDispatcher();
        var caller = new CallerIdentity { Sid = "synthetic-owner", SessionId = 1, IsRemotePipe = false };
        var executions = 0;
        IpcResponse Handle(IpcRequest r) { executions++; return new IpcResponse { RequestId = r.RequestId, Ok = true }; }
        var old = new IpcRequest { ProtocolVersion = 1, RequestId = "old", Operation = IpcOperations.ReportHealth };
        dispatcher.Dispatch(old, caller, Handle);
        for (var i = 0; i < ProductLimits.IpcRetiredEntries + ProductLimits.IpcIdempotencyEntries + 5; i++)
            dispatcher.Dispatch(new IpcRequest { ProtocolVersion = 1, RequestId = "new-" + i, Operation = IpcOperations.ReportHealth }, caller, Handle);
        var before = executions;
        dispatcher.Dispatch(old, caller, Handle);
        Assert.Equal(before, executions);
    }

    [Fact]
    public void Q10_PreviousBrokerBootCannotOverwriteNewBootSnapshot()
    {
        var box = new SessionMailbox();
        box.Apply(Response("boot-a", 100, "Connected"), true);
        box.Apply(Response("boot-b", 1, "Disconnected"), true);
        box.Apply(Response("boot-a", 101, "Connected"), true);
        Assert.Equal("boot-b", box.BootId);
        Assert.Equal("Disconnected", box.Session.PhaseCode);
    }

    [Fact]
    public void Q11_UnknownSettingsEnumsMustBeRejected()
    {
        Assert.NotNull(new ProductSettings { CountryMode = (CountryConstraint)999, SelectionMode = (SelectionMode)999, Theme = (ThemePreference)999 }.Validate());
    }

    [Fact]
    public void Q12_OneXrayShadowsocksServerMustImportLikeTheTwoServerCase()
    {
        const string json = "{\"outbounds\":[{\"protocol\":\"shadowsocks\",\"settings\":{\"servers\":[{\"address\":\"203.0.113.44\",\"port\":443,\"method\":\"aes-128-gcm\",\"password\":\"synthetic-only\"}]}}]}";
        var result = SubscriptionImporter.Import(json);
        Assert.Equal(1, result.Pending);
        Assert.Equal("203.0.113.44", Assert.Single(result.Records).Semantics!.Host);
    }

    [Fact]
    public void Q13_XrayWebSocketPathMustSurviveImport()
    {
        const string json = "{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"203.0.113.44\",\"port\":443,\"users\":[{\"id\":\"11111111-1111-4111-8111-111111111111\",\"encryption\":\"none\"}]}]},\"streamSettings\":{\"network\":\"ws\",\"security\":\"tls\",\"tlsSettings\":{\"serverName\":\"example.com\"},\"wsSettings\":{\"path\":\"/opaque-path\",\"headers\":{\"Host\":\"edge.example\"}}}}]}";
        var result = SubscriptionImporter.Import(json);
        Assert.Equal(1, result.Pending);
        Assert.Equal("/opaque-path", Assert.Single(result.Records).Semantics!.Path);
    }

    [Fact]
    public void Q14_ConflictingCaseVariantYamlSecurityKeysMustBeRejected()
    {
        var result = SubscriptionImporter.Import("proxies:\n  - name: synthetic\n    type: trojan\n    server: 203.0.113.44\n    port: 443\n    password: synthetic-only\n    skip-cert-verify: false\n    Skip-Cert-Verify: true\n");
        Assert.Equal(0, result.Pending);
    }

    [Fact]
    public void Q15_AllowingInsecureCertificatesMustReconsiderPolicyBlockedNodes()
    {
        var c = Catalogue(false);
        RefreshMerge.Ingest(c, [new IngestArtifact { ArtifactId = "insecure", FamilyId = "black-vless", Enabled = true,
            Text = Link() + "&allowInsecure=1" }], Now, false);
        var node = Assert.Single(c.Nodes, n => n.Semantics.SkipCertVerify);
        c.Settings = c.Settings with { AllowInsecureCertificates = true, Revision = 2 };
        Assert.True(ProbeCoordinator.NeedsProbe(node, Now, c.NetworkEpoch), "Import-time policy classification must not permanently suppress a now-authorized candidate.");
    }

    [Fact]
    public async Task Q16_CancelDuringStartMustRejectLateSuccess()
    {
        var c = Catalogue(true); var core = new OwnedCore { GateAt = 1 }; var engine = Engine(c, core);
        using var stop = new CancellationTokenSource();
        var pending = Connect(engine, c, stop.Token);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel(); core.Release.TrySetResult();
        IpcResponse? result = null;
        try { result = await pending; } catch (OperationCanceledException) { }
        Assert.False(result?.Ok == true, "A canceled request committed a late successful Start.");
        Assert.Empty(core.Active);
    }

    [Fact]
    public async Task Q17_PolicyRejectionMustNotLoseOwnedCoreCleanupIdentity()
    {
        var c = Catalogue(true); var core = new OwnedCore(); var engine = Engine(c, core);
        Assert.True((await Connect(engine, c)).Ok);
        c.Settings = c.Settings with { LanAccess = false, Revision = 2 };
        Confirm(engine, c);
        await engine.HandleAsync(Request(engine, IpcOperations.Disconnect, new DisconnectPayload()), CancellationToken.None);
        Assert.Empty(core.Active);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("excluded")]
    public async Task Q18_FailoverMustRevalidateCandidateAfterAwait(string change)
    {
        var c = Catalogue(true); var core = new OwnedCore { GateAt = 2 }; var engine = Engine(c, core);
        Assert.True((await Connect(engine, c)).Ok); Confirm(engine, c);
        var target = c.Nodes[1];
        await engine.HandleAsync(Request(engine, IpcOperations.ApplyRuntimeSet, new { standbys = new[] { new StandbyCandidate { NodeId = target.NodeId, EndpointKey = "synthetic", Country = "DE", SourceFamilyId = "black-vless" } } }), CancellationToken.None);
        var switching = engine.HandleAsync(Request(engine, IpcOperations.ReportHealth, new HealthPayload { FailureKind = nameof(FailureKind.CoreExit), ConsecutiveFailures = 3, NetworkEpoch = 1 }), CancellationToken.None);
        await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (change == "epoch") c.SetNetworkEpoch(2); else c.TrySetExcluded(target.NodeId, true);
        core.Release.TrySetResult(); await switching;
        Assert.NotEqual(target.NodeId, engine.Snapshot().ActiveNodeId);
    }

    [Fact]
    public async Task Q19_ExplicitManualSelectionRetainsExclusionOverride()
    {
        var c = Catalogue(true); c.TrySetExcluded(c.Nodes[0].NodeId, true);
        var engine = Engine(c, new OwnedCore());
        var result = await Connect(engine, c);
        Assert.True(result.Ok, result.ErrorCode);
    }

    [Fact]
    public async Task Q20_RuntimeStandbysMustBeDeduplicatedAndBounded()
    {
        var c = Catalogue(true); var engine = Engine(c, new OwnedCore());
        var standby = new StandbyCandidate { NodeId = c.Nodes[1].NodeId, EndpointKey = "synthetic", Country = "DE", SourceFamilyId = "black-vless" };
        await engine.HandleAsync(Request(engine, IpcOperations.ApplyRuntimeSet, new { standbys = Enumerable.Repeat(standby, 100).ToArray() }), CancellationToken.None);
        Assert.InRange(engine.Snapshot().StandbyCount, 0, ProductLimits.WarmStandbys);
    }

    [Fact]
    public async Task Q21_StartExceptionMustNotLeaveConnectingForever()
    {
        var c = Catalogue(true); var engine = Engine(c, new OwnedCore { ThrowStart = true });
        try { await Connect(engine, c); } catch (IOException) { }
        Assert.NotEqual(TunnelPhase.Connecting, engine.State.Phase);
    }

    [Fact]
    public async Task Q22_Control_HealthyConnectAndOwnedDisconnectWorkInStateModel()
    {
        var c = Catalogue(true); var core = new OwnedCore(); var engine = Engine(c, core);
        Assert.True((await Connect(engine, c)).Ok); Confirm(engine, c);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        Assert.True((await engine.HandleAsync(Request(engine, IpcOperations.Disconnect, new DisconnectPayload()), CancellationToken.None)).Ok);
        Assert.Empty(core.Active);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
    }

    [Fact]
    public async Task Q23_Control_MatchingProofStillPublishes()
    {
        var c = Catalogue(false);
        var result = await ProbeCoordinator.RunAsync(c, new Scripted((n, t, _) => Task.FromResult(Success(n, t))), Target, Now, CancellationToken.None);
        Assert.Equal(2, result.Succeeded);
    }

    private static ProbeObservation Success(NodeSemantics node, Uri target) => new(true, 10, false, null, 80,
        ProbeClass.Success, target.AbsoluteUri, CanonicalIdentity.Digest(node), "synthetic-owned-attempt");
    private static string Link() => "vless://11111111-1111-4111-8111-111111111111@203.0.113.55:443?security=tls&type=tcp&encryption=none&sni=example.com";
    private static RefreshWorkItem Item(string id) => new() { ArtifactId = id, FamilyId = "black-vless", Urls = [new Uri("https://raw.githubusercontent.com/audit/" + id)] };
    private static PolicyHttpFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new Handler(handler), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
    private static MemoryCatalogue Catalogue(bool healthy)
    {
        var c = new MemoryCatalogue(); c.Settings = c.Settings with { DisclosureAccepted = true };
        for (var i = 0; i < 2; i++)
        {
            var n = new NodeSemantics { Protocol = ProtocolKind.Vless, Host = "203.0.113." + (10 + i), Port = 443,
                UserId = "11111111-1111-4111-8111-111111111111", Security = "tls", Encryption = "none", Transport = "tcp" };
            c.ApplySnapshot(new SnapshotCommit { ArtifactId = "seed-" + i, FamilyId = "black-vless", ContentHash = "synthetic", Complete = true, NowUtc = Now,
                Nodes = [new SnapshotNode { Digest = CanonicalIdentity.Digest(n), Semantics = n, Label = "synthetic-" + i, AdvertisedCountry = "DE", ArtifactId = "seed-" + i, FamilyId = "black-vless" }] });
        }
        if (healthy) foreach (var n in c.Nodes) c.ApplyAssessment(n.NodeId, new AssessmentSnapshot { Digest = n.Digest, NetworkEpoch = 1, Health = HealthState.Healthy, LastSuccessUtc = Now, MedianLatencyMs = 10 });
        return c;
    }
    private static BrokerEngine Engine(ICatalogue c, OwnedCore core) => new(c, new Guard(), core, clock: new FakeClock { UtcNow = Now });
    private static Task<IpcResponse> Connect(BrokerEngine e, ICatalogue c, CancellationToken token = default) => e.HandleAsync(Request(e, IpcOperations.Connect,
        new ConnectPayload { NodeId = c.Nodes[0].NodeId, Digest = c.Nodes[0].Digest, NetworkEpoch = c.NetworkEpoch, ProtectionRequired = c.Settings.ProtectionOnConnect }), token);
    private static void Confirm(BrokerEngine e, ICatalogue c) { var s = e.Snapshot(); e.ConfirmProduction(s.BootId!, s.Generation, s.OperationId, s.ActiveNodeId, c.NetworkEpoch, true, null); }
    private static IpcRequest Request(BrokerEngine e, string op, object payload) => new() { ProtocolVersion = 1, RequestId = Guid.NewGuid().ToString("N"), ExpectedStateRevision = e.Snapshot().Revision, Operation = op, Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options) };
    private static IpcResponse Response(string boot, long sequence, string phase) => new() { RequestId = Guid.NewGuid().ToString("N"), Ok = true, Snapshot = new BrokerSnapshot { BootId = boot, Sequence = sequence, Revision = sequence, Phase = phase } };
    private sealed class Scripted(Func<NodeSemantics, Uri, CancellationToken, Task<ProbeObservation>> run) : IProbeTransport
    { public Task<ProbeObservation> ProbeAsync(NodeSemantics n, Uri t, CancellationToken token) => run(n, t, token); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> run) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(run(request)); }
    private sealed class Guard : INetworkGuard
    {
        public GuardResult Arm(GuardRequest r) => new(true, r.ProtectionRequired, null, []);
        public GuardResult Disarm(long generation) => new(true, false, null, []);
        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects) => new(true, false, null, effects.Select(e => e.Id).ToArray());
    }
    private sealed class OwnedCore : ICoreController
    {
        private int _starts;
        public int GateAt { get; init; }
        public bool ThrowStart { get; init; }
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken token)
        {
            if (ThrowStart) throw new IOException("synthetic start fault");
            if (++_starts == GateAt) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(4)); }
            Active.Clear(); Active.Add(generation + ":" + operationId);
            return new CoreStartResult(true, null);
        }
        public Task StopAsync(long generation, string operationId, CancellationToken token) { Active.Remove(generation + ":" + operationId); return Task.CompletedTask; }
    }
}
