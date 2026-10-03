using System.Globalization;
using System.Net;
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

// Independent acceptance assertions; only synthetic data and state doubles, never TUN/firewall changes.
public sealed class IndependentRound5Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Target = new("https://probe.example/generate_204");
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private const string OtherTree = "3333333333333333333333333333333333333333";
    private const string Link = "vless://11111111-1111-4111-8111-111111111111@203.0.113.55:443?security=tls&type=tcp&encryption=none&sni=example.com";

    [Fact]
    public async Task S01_MovingBranchMustNotMixCommitAndUnrelatedTree()
    {
        using var fetch = Fetcher(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/commits/", StringComparison.Ordinal)) return Json(CommitJson());
            if (path.EndsWith("/main", StringComparison.Ordinal)) return Json(TreeJson(OtherTree, "BLACK_VLESS_RUS.txt"));
            return Json(TreeJson(Tree, "BLACK_VLESS_RUS_mobile.txt"));
        });
        var result = await Coordinator(new MemoryCatalogue(), fetch, new SourceLedger()).DiscoverAsync(Registry(), CancellationToken.None);
        Assert.True(!result.Complete || result.Items.All(item => item.ArtifactId != "BLACK_VLESS_RUS.txt"),
            "Resolved commit C was paired with the newer branch tree D and reported complete.");
    }

    [Fact]
    public void S02_BlobShapedMetadataIsNotACommitObject()
    {
        Assert.False(GithubTreeParser.TryReadCommitSha("{\"sha\":\"" + Commit + "\",\"type\":\"blob\"}", out _));
    }

    [Fact]
    public async Task S03_LedgerWithoutCatalogueCannotTurn304IntoUsableSnapshot()
    {
        var catalogue = new MemoryCatalogue(); var ledger = new SourceLedger(); var item = Item("missing");
        ledger.Remember(item.Urls[0].AbsoluteUri, "old-etag", "hash-of-lost-nonempty-body", Now, null);
        var calls = 0;
        using var fetch = Fetcher(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.NotModified) : Json(Link));
        await Coordinator(catalogue, fetch, ledger).RefreshAsync([item], Now.AddHours(3), CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Single(catalogue.Nodes);
    }

    [Fact]
    public async Task S04_InvalidPrimaryBodyMustNotSuppressValidReviewedMirror()
    {
        var c = new MemoryCatalogue(); var mirrorCalled = false;
        using var fetch = Fetcher(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/primary", StringComparison.Ordinal)) return Json("{\"outbounds\":");
            mirrorCalled = true; return Json(Link);
        });
        var item = new RefreshWorkItem { ArtifactId = "feed", FamilyId = "black-vless", Urls =
            [new Uri("https://raw.githubusercontent.com/audit/primary"), new Uri("https://raw.githubusercontent.com/audit/mirror")] };
        await Coordinator(c, fetch, new SourceLedger()).RefreshAsync([item], Now, CancellationToken.None);
        Assert.True(mirrorCalled, "The first HTTP 200 ended mirror selection before parsing had established a usable body.");
        Assert.Single(c.Nodes);
    }

    [Fact]
    public async Task S05_CorruptPersistedCursorMustNotEscapeAsIndexException()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r5-cursor-");
        try
        {
            var path = Path.Combine(directory.FullName, "ledger.json");
            File.WriteAllText(path, "{\"RefreshCursor\":-1,\"Entries\":[]}");
            var ledger = SourceLedger.Load(path); using var fetch = Fetcher(_ => Json(Link));
            var error = await Record.ExceptionAsync(() => Coordinator(new MemoryCatalogue(), fetch, ledger).RefreshAsync([Item("a"), Item("b")], Now, CancellationToken.None));
            Assert.Null(error);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task S06_Discovery304MustAdvanceItsSchedulingCheck()
    {
        var registry = Registry(); var ledger = new SourceLedger(); var old = Now.AddHours(-3);
        ledger.RememberDiscovery(registry.TreeApi.AbsoluteUri, "tree-v1", TreeJson(Tree, "BLACK_VLESS_RUS.txt"), old, Commit);
        using var fetch = Fetcher(request => request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
            ? Json(CommitJson()) : new HttpResponseMessage(HttpStatusCode.NotModified));
        var result = await Coordinator(new MemoryCatalogue(), fetch, ledger).DiscoverAsync(registry, CancellationToken.None);
        Assert.True(result.Complete);
        Assert.True(ledger.Find(registry.TreeApi.AbsoluteUri)!.LastSuccessUtc > old,
            "The discovery ledger entry remains due even though cached tree validation succeeded.");
    }

    [Fact]
    public async Task P01_ConsumedProofFromOldNetworkMustNotBeRestampedAsFresh()
    {
        var c = Catalogue(false, 1); var node = c.Nodes[0]; var proof = Good(node.Semantics, Target);
        var transport = new Scripted((_, _, _) => Task.FromResult(proof));
        Assert.Equal(1, (await ProbeCoordinator.RunAsync(c, transport, Target, Now, CancellationToken.None)).Succeeded);
        c.SetNetworkEpoch(2);
        var replay = await ProbeCoordinator.RunAsync(c, transport, Target, Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(0, replay.Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P02_OlderConcurrentCompletionMustNotOverwriteNewerObservation(bool olderSuccess)
    {
        var c = Catalogue(false, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = ProbeCoordinator.RunAsync(c, new Scripted(async (n, t, _) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); return Observe(n, t, olderSuccess, "old-worker"); }), Target, Now, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ProbeCoordinator.RunAsync(c, new Scripted((n, t, _) => Task.FromResult(Observe(n, t, !olderSuccess, "new-worker"))), Target, Now.AddSeconds(1), CancellationToken.None);
        var newer = c.Nodes[0].Assessment;
        release.TrySetResult(); await older;
        Assert.Equal(newer, c.Nodes[0].Assessment);
    }

    [Fact]
    public async Task P03_UnsupportedObservationCannotPassOnDemandAdmission()
    {
        var c = Catalogue(true, 1); var node = c.Nodes[0];
        c.ApplyAssessment(node.NodeId, node.Assessment! with { LastSuccessUtc = Now.AddSeconds(-65) });
        var transport = new Scripted((n, t, _) => Task.FromResult(Good(n, t) with { Class = ProbeClass.Unsupported }));
        Assert.False(await ProbeCoordinator.AdmitIfStaleAsync(c, transport, Target, node.NodeId, Now, CancellationToken.None));
    }

    [Fact]
    public async Task P04_DeadlineResultMustChargeItsConsumedTraffic()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r5-budget-");
        try
        {
            var day = DateOnly.FromDateTime(Now.UtcDateTime);
            var spent = ProbeByteBudget.Load(Path.Combine(directory.FullName, "budget"), 1000, day);
            var c = Catalogue(false, 1);
            var transport = new Scripted((_, _, _) => Task.FromResult(new ProbeObservation(false, null, false,
                ReasonCodes.Canceled, 100, ProbeClass.Canceled)));
            var result = await ProbeCoordinator.RunAsync(c, transport, Target, Now, CancellationToken.None, spent: spent);
            Assert.Equal(1, result.Failed);
            Assert.True(spent.SpentOn(day) >= 100, "A non-user deadline was classified as failed but its consumed bytes were not charged.");
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task B01_ManualExclusionOverrideMustReachConfirmedConnection(int ageSeconds)
    {
        var c = Catalogue(true, 1); var node = c.Nodes[0]; c.TrySetExcluded(node.NodeId, true);
        c.ApplyAssessment(node.NodeId, node.Assessment! with { LastSuccessUtc = Now.AddSeconds(-ageSeconds) });
        var engine = new BrokerEngine(c, new Guard(), new OwnedCore(), clock: new FakeClock { UtcNow = Now },
            admission: new Scripted((n, t, _) => Task.FromResult(Good(n, t))), admissionTarget: Target);
        var response = await Connect(engine, c);
        Assert.True(response.Ok, response.ErrorCode); Confirm(engine, c);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
    }

    [Fact]
    public async Task B02_CancelAfterSpawnMustSettleConnectingState()
    {
        var c = Catalogue(true, 1); using var stop = new CancellationTokenSource();
        var core = new OwnedCore { OnStart = _ => stop.Cancel() }; var engine = Engine(c, core);
        Assert.False((await Connect(engine, c, stop.Token)).Ok);
        Assert.Empty(core.Active);
        Assert.NotEqual(TunnelPhase.Connecting, engine.State.Phase);
    }

    [Fact]
    public async Task B03_PartialSpawnIOExceptionMustKeepCleanupOwnership()
    {
        var c = Catalogue(true, 1); var core = new OwnedCore { ThrowAfterSpawn = true }; var engine = Engine(c, core);
        Assert.False((await Connect(engine, c)).Ok);
        await engine.HandleAsync(Request(engine, IpcOperations.Disconnect, new DisconnectPayload()), CancellationToken.None);
        Assert.Empty(core.Active);
    }

    [Fact]
    public async Task B04_RejectedStartWithFailedStopMustRemainRecoverablyOwned()
    {
        var c = Catalogue(true, 1);
        var core = new OwnedCore { FailStops = 1, OnStart = _ => c.Settings = c.Settings with { LanAccess = false, Revision = 2 } };
        var engine = Engine(c, core);
        _ = await Record.ExceptionAsync(() => Connect(engine, c));
        await engine.HandleAsync(Request(engine, IpcOperations.Disconnect, new DisconnectPayload()), CancellationToken.None);
        Assert.Empty(core.Active);
    }

    [Fact]
    public async Task B05_CanceledFailoverMustNotCommitLateReplacement()
    {
        var c = Catalogue(true); using var stop = new CancellationTokenSource();
        var core = new OwnedCore { OnStart = attempt => { if (attempt == 2) stop.Cancel(); } };
        var engine = Engine(c, core); await Connect(engine, c); Confirm(engine, c); await Standby(engine, c);
        await Health(engine, c, stop.Token);
        Assert.NotEqual(c.Nodes[1].NodeId, engine.State.ActiveNodeId);
    }

    [Fact]
    public async Task B06_StrictCountryMustRecheckUpdatedStandbyMetadata()
    {
        var c = Catalogue(true); c.Settings = c.Settings with { CountryMode = CountryConstraint.Strict, Country = "DE" };
        var engine = Engine(c, new OwnedCore()); await Connect(engine, c); Confirm(engine, c); await Standby(engine, c);
        c.Nodes[1].AdvertisedCountry = "FI"; // Equivalent to a later source-label update with identical connection semantics.
        await Health(engine, c);
        Assert.NotEqual(c.Nodes[1].NodeId, engine.State.ActiveNodeId);
    }

    [Fact]
    public async Task B07_ExplicitSessionLanPolicyMustSurviveFailover()
    {
        var c = Catalogue(true); var core = new OwnedCore(); var engine = Engine(c, core);
        var selected = c.Nodes[0];
        await engine.HandleAsync(Request(engine, IpcOperations.Connect, new ConnectPayload
        { NodeId = selected.NodeId, Digest = selected.Digest, NetworkEpoch = 1, LanAccess = false }), CancellationToken.None);
        Assert.DoesNotContain("IP-CIDR,10.0.0.0/8,DIRECT", core.Profiles[0], StringComparison.Ordinal);
        Confirm(engine, c); await Standby(engine, c); await Health(engine, c);
        Assert.Equal(2, core.Profiles.Count);
        Assert.DoesNotContain("IP-CIDR,10.0.0.0/8,DIRECT", core.Profiles[1], StringComparison.Ordinal);
    }

    [Fact]
    public void I01_ProbabilisticReplayFilterMustNotDenyFreshSafetyCommand()
    {
        var dispatcher = new IpcDispatcher(); var caller = new CallerIdentity { Sid = "synthetic-owner", SessionId = 1 };
        static IpcResponse Run(IpcRequest r) => new() { RequestId = r.RequestId, Ok = true };
        for (var index = 0; index < 100000; index++)
            dispatcher.Dispatch(new IpcRequest { ProtocolVersion = 1, RequestId = "R5-command-" + index.ToString(CultureInfo.InvariantCulture), Operation = IpcOperations.ReportHealth }, caller, Run);
        var result = dispatcher.Dispatch(new IpcRequest { ProtocolVersion = 1, RequestId = "R5-disconnect-70", Operation = IpcOperations.Disconnect }, caller, Run);
        Assert.True(result.Ok, "A never-issued safety request was rejected: " + result.ErrorCode);
    }

    [Fact]
    public void I02_UnsupportedResponseProtocolCannotReplaceUiState()
    {
        var box = new SessionMailbox();
        box.Apply(new IpcResponse { RequestId = "first", Ok = true, Snapshot = new BrokerSnapshot { BootId = "boot", Sequence = 1, Revision = 1, Phase = "Disconnected" } }, true);
        var accepted = box.Apply(new IpcResponse { RequestId = "second", ProtocolVersion = 999, Ok = true,
            Snapshot = new BrokerSnapshot { BootId = "boot", Sequence = 2, Revision = 2, Phase = "Connected", CoreRunning = true } }, true);
        Assert.False(accepted); Assert.Equal("Disconnected", box.Session.PhaseCode);
    }

    [Fact]
    public void I03_LiveCoreContradictionMustNotProveCleanDisconnect()
    {
        var state = UiSessionReducer.FromSnapshot(UiSessionReducer.Initial(), new BrokerSnapshot
        { BootId = "boot", Phase = "Disconnected", CoreRunning = true, ProtectionArmed = false }, null, true);
        Assert.False(state.ClaimsVerifiedDisconnect);
    }

    [Fact]
    public void D01_NullDisabledFamilyCollectionMustBeRejectedAsSettings()
    {
        Assert.NotNull(new ProductSettings { DisabledFamilyIds = null! }.Validate());
    }

    [Fact]
    public void D02_ClockRollbackMustNotRefundPersistedDailyBudget()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r5-clock-");
        try
        {
            var path = Path.Combine(directory.FullName, "budget"); File.WriteAllText(path, "2026-10-04\n1000\n");
            var counter = ProbeByteBudget.Load(path, 1000, new DateOnly(2026, 10, 3));
            Assert.True(counter.Exhausted(new DateOnly(2026, 10, 3)));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void X01_XraySecondUserMustNotInheritFirstUsersFlow()
    {
        const string body = "{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"203.0.113.44\",\"port\":443,\"users\":[{\"id\":\"11111111-1111-4111-8111-111111111111\",\"flow\":\"xtls-rprx-vision\"},{\"id\":\"22222222-2222-4222-8222-222222222222\"}]}]},\"streamSettings\":{\"network\":\"tcp\",\"security\":\"tls\"}}]}";
        var batch = SubscriptionImporter.Import(body);
        var second = Assert.Single(batch.Records, item => item.Semantics?.UserId == "22222222-2222-4222-8222-222222222222");
        Assert.Null(second.Semantics!.Flow);
    }

    [Fact]
    public void X02_MissingSecondPasswordMustNotBorrowFirstServerCredential()
    {
        const string body = "{\"outbounds\":[{\"protocol\":\"shadowsocks\",\"settings\":{\"servers\":[{\"address\":\"203.0.113.44\",\"port\":443,\"method\":\"aes-256-gcm\",\"password\":\"first-only\"},{\"address\":\"203.0.113.45\",\"port\":443,\"method\":\"aes-256-gcm\"}]}}]}";
        var batch = SubscriptionImporter.Import(body);
        Assert.DoesNotContain(batch.Records, item => item.Disposition == RecordDisposition.Pending && item.Semantics?.Host == "203.0.113.45");
    }

    [Fact]
    public void X03_XrayExplicitCertificatePolicyMustSurviveImport()
    {
        const string body = "{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{\"vnext\":[{\"address\":\"203.0.113.44\",\"port\":443,\"users\":[{\"id\":\"11111111-1111-4111-8111-111111111111\"}]}]},\"streamSettings\":{\"security\":\"tls\",\"tlsSettings\":{\"serverName\":\"example.com\",\"allowInsecure\":true}}}]}";
        var batch = SubscriptionImporter.Import(body, new ImportOptions { AllowInsecureCertificates = true });
        Assert.True(Assert.Single(batch.Records).Semantics!.SkipCertVerify);
    }

    [Fact]
    public async Task C01_ControlStableCommitDiscoveryWorks()
    {
        using var fetch = Fetcher(request => Json(request.RequestUri!.AbsolutePath.Contains("/commits/", StringComparison.Ordinal) ? CommitJson() : TreeJson(Tree, "BLACK_VLESS_RUS.txt")));
        var result = await Coordinator(new MemoryCatalogue(), fetch, new SourceLedger()).DiscoverAsync(Registry(), CancellationToken.None);
        Assert.True(result.Complete); Assert.Equal(Commit, result.CommitSha); Assert.Single(result.Items);
    }

    [Fact]
    public async Task C02_ControlMatchedProofAndOwnedDisconnectWork()
    {
        var c = Catalogue(false, 1);
        Assert.Equal(1, (await ProbeCoordinator.RunAsync(c, new Scripted((n,t,_) => Task.FromResult(Good(n,t))), Target, Now, CancellationToken.None)).Succeeded);
        var core = new OwnedCore(); var engine = Engine(c, core); Assert.True((await Connect(engine, c)).Ok); Confirm(engine, c);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        Assert.True((await engine.HandleAsync(Request(engine, IpcOperations.Disconnect, new DisconnectPayload()), CancellationToken.None)).Ok);
        Assert.Empty(core.Active); Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
    }

    private static CatalogueCoordinator Coordinator(ICatalogue c, PolicyHttpFetcher fetch, SourceLedger ledger) => new(c, fetch, new Scripted((n,t,_) => Task.FromResult(Good(n,t))), ledger);
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static string CommitJson() => "{\"sha\":\"" + Commit + "\",\"commit\":{\"tree\":{\"sha\":\"" + Tree + "\"}}}";
    private static string TreeJson(string tree, string path) => JsonSerializer.Serialize(new { sha = tree, truncated = false, tree = new[] { new { path, type = "blob", mode = "100644", size = 100, sha = new string('4',40) } } });
    private static ReviewedRegistry Registry() => new()
    {
        Owner = "igareck", Repository = "vpn-configs-for-russia", PinnedCommit = new string('a',40),
        TreeApi = new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1"),
        FamilyIds = ["black-vless"], ProbeTargets = [], ApprovedHosts = new HashSet<string> { "raw.githubusercontent.com" }, RejectedHosts = new HashSet<string>(),
        FetchOrigins = [new ApprovedFetchOrigin("raw.githubusercontent.com",443,"/igareck/vpn-configs-for-russia/")]
    };
    private static RefreshWorkItem Item(string id) => new() { ArtifactId = id, FamilyId = "black-vless", Urls = [new Uri("https://raw.githubusercontent.com/audit/" + id)] };
    private static PolicyHttpFetcher Fetcher(Func<HttpRequestMessage,HttpResponseMessage> run) => new(new Handler(run), new HashSet<string> { "api.github.com", "raw.githubusercontent.com" });
    private static ProbeObservation Good(NodeSemantics n, Uri target) => Observe(n,target,true,"synthetic-owned-worker");
    private static ProbeObservation Observe(NodeSemantics n, Uri target, bool success, string worker) => new(success, success ? 10 : null, false, success ? null : ReasonCodes.ProbeFailed, 80,
        success ? ProbeClass.Success : ProbeClass.CandidateFailure, target.AbsoluteUri, CanonicalIdentity.Digest(n), worker);
    private static MemoryCatalogue Catalogue(bool healthy, int count = 2)
    {
        var c = new MemoryCatalogue(); c.Settings = c.Settings with { DisclosureAccepted = true };
        for (var i = 0; i < count; i++)
        {
            var n = new NodeSemantics { Protocol = ProtocolKind.Vless, Host = "203.0.113." + (10+i).ToString(CultureInfo.InvariantCulture), Port = 443,
                UserId = "11111111-1111-4111-8111-111111111111", Security = "tls", Encryption = "none", Transport = "tcp" };
            c.ApplySnapshot(new SnapshotCommit { ArtifactId = "seed-" + i, FamilyId = "black-vless", ContentHash = "synthetic", Complete = true, NowUtc = Now,
                Nodes = [new SnapshotNode { Digest = CanonicalIdentity.Digest(n), Semantics = n, Label = "synthetic-" + i, AdvertisedCountry = "DE", ArtifactId = "seed-" + i, FamilyId = "black-vless" }] });
        }
        if (healthy) foreach (var n in c.Nodes) c.ApplyAssessment(n.NodeId, new AssessmentSnapshot { Digest = n.Digest, NetworkEpoch = 1, Health = HealthState.Healthy, LastSuccessUtc = Now, MedianLatencyMs = 10 });
        return c;
    }
    private static BrokerEngine Engine(ICatalogue c, OwnedCore core) => new(c, new Guard(), core, clock: new FakeClock { UtcNow = Now });
    private static Task<IpcResponse> Connect(BrokerEngine e, ICatalogue c, CancellationToken token = default) => e.HandleAsync(Request(e,IpcOperations.Connect,
        new ConnectPayload { NodeId = c.Nodes[0].NodeId, Digest = c.Nodes[0].Digest, NetworkEpoch = c.NetworkEpoch }), token);
    private static void Confirm(BrokerEngine e, ICatalogue c) { var s = e.Snapshot(); e.ConfirmProduction(s.BootId!,s.Generation,s.OperationId,s.ActiveNodeId,c.NetworkEpoch,true,null); }
    private static Task<IpcResponse> Standby(BrokerEngine e, ICatalogue c) => e.HandleAsync(Request(e,IpcOperations.ApplyRuntimeSet,new { standbys = new[]
        { new StandbyCandidate { NodeId = c.Nodes[1].NodeId, EndpointKey = "synthetic", Country = "DE", SourceFamilyId = "black-vless" } } }), CancellationToken.None);
    private static Task<IpcResponse> Health(BrokerEngine e, ICatalogue c, CancellationToken token = default) => e.HandleAsync(Request(e,IpcOperations.ReportHealth,
        new HealthPayload { FailureKind = nameof(FailureKind.CoreExit), ConsecutiveFailures = 3, NetworkEpoch = c.NetworkEpoch }), token);
    private static IpcRequest Request(BrokerEngine e,string op,object payload) => new() { ProtocolVersion=1,RequestId=Guid.NewGuid().ToString("N"),ExpectedStateRevision=e.Snapshot().Revision,Operation=op,Payload=JsonSerializer.SerializeToElement(payload,IpcJson.Options) };
    private sealed class Scripted(Func<NodeSemantics,Uri,CancellationToken,Task<ProbeObservation>> run) : IProbeTransport
    { public Task<ProbeObservation> ProbeAsync(NodeSemantics n,Uri t,CancellationToken token) => run(n,t,token); }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> run) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(run(request)); }
    private sealed class Guard : INetworkGuard
    {
        public GuardResult Arm(GuardRequest r) => new(true,r.ProtectionRequired,null,[]);
        public GuardResult Disarm(long generation) => new(true,false,null,[]);
        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects) => new(true,false,null,effects.Select(e=>e.Id).ToArray());
    }
    private sealed class OwnedCore : ICoreController
    {
        private int _starts;
        public Action<int>? OnStart { get; init; }
        public bool ThrowAfterSpawn { get; init; }
        public int FailStops { get; set; }
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);
        public List<string> Profiles { get; } = [];
        public Task<CoreStartResult> StartAsync(string yaml,long generation,string operationId,CancellationToken token)
        {
            Profiles.Add(yaml); Active.Clear(); Active.Add(generation + ":" + operationId); OnStart?.Invoke(++_starts);
            if (ThrowAfterSpawn) throw new IOException("synthetic fault after acquiring child handle");
            return Task.FromResult(new CoreStartResult(true,null));
        }
        public Task StopAsync(long generation,string operationId,CancellationToken token)
        {
            if (FailStops-- > 0) throw new IOException("synthetic stop failure");
            Active.Remove(generation + ":" + operationId); return Task.CompletedTask;
        }
    }
}
