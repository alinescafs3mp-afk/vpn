using System.Globalization;
using System.Net;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;
using Xunit;

namespace AutoVpn.UnitTests;

// Controlled acceptance contracts. No public proxy, TUN, OS firewall or production credential is used.
public sealed class IndependentRound6Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Target = new("https://probe.example/generate_204");
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Tree = "2222222222222222222222222222222222222222";
    private const string Pin = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Link = "vless://11111111-1111-4111-8111-111111111111@203.0.113.55:443?security=tls&type=tcp&encryption=none&sni=example.com";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R601_OnDemandOlderCompletionCannotOverwriteNewer(bool oldSuccess)
    {
        var c = Catalogue(true, 1); var id = c.Nodes[0].NodeId;
        var entered = NewGate(); var release = NewGate();
        var older = ProbeCoordinator.AdmitIfStaleAsync(c, new Scripted(async (n,t,_) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); return Observe(n,t,oldSuccess,"older"); }), Target,id,Now.AddSeconds(65),CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await ProbeCoordinator.AdmitIfStaleAsync(c,new Scripted((n,t,_)=>Task.FromResult(Observe(n,t,!oldSuccess,"newer"))),Target,id,Now.AddSeconds(66),CancellationToken.None);
            var newer = c.Nodes[0].Assessment;
            release.TrySetResult(); await older;
            Assert.Equal(newer,c.Nodes[0].Assessment);
        }
        finally { release.TrySetResult(); await older; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R602_SqliteFavoriteCopyMustNotReuseAnInFlightAttemptNumber(bool oldSuccess)
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r6-order-");
        try
        {
            using var c = SqliteCatalogue.Open(Path.Combine(directory.FullName,"catalogue.sqlite"),new Plain()); Seed(c,1,false);
            var id = c.Nodes[0].NodeId; var entered=NewGate(); var release=NewGate();
            var older=ProbeCoordinator.RunAsync(c,new Scripted(async(n,t,_)=>
            { entered.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(10));return Observe(n,t,oldSuccess,"older"); }),Target,Now,CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                Assert.True(c.TrySetFavorite(id,true));
                await ProbeCoordinator.RunAsync(c,new Scripted((n,t,_)=>Task.FromResult(Observe(n,t,!oldSuccess,"newer"))),Target,Now.AddSeconds(1),CancellationToken.None);
                var newer=c.Nodes[0].Assessment;
                release.TrySetResult();await older;
                Assert.Equal(newer,c.Nodes[0].Assessment);
            }
            finally {release.TrySetResult();await older;}
        }
        finally {directory.Delete(true);}
    }

    [Fact]
    public async Task R603_OldEpochProofMustStayRejectedAfterItsFirstRefusal()
    {
        var c=Catalogue(false,1);var proof=Observe(c.Nodes[0].Semantics,Target,true,"one-attempt");
        var transport=new Scripted((_,_,_)=>Task.FromResult(proof));
        Assert.Equal(1,(await ProbeCoordinator.RunAsync(c,transport,Target,Now,CancellationToken.None)).Succeeded);
        c.SetNetworkEpoch(2);
        Assert.Equal(0,(await ProbeCoordinator.RunAsync(c,transport,Target,Now.AddMinutes(1),CancellationToken.None)).Succeeded);
        Assert.Equal(0,(await ProbeCoordinator.RunAsync(c,transport,Target,Now.AddMinutes(2),CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task R604_ConsumedSameEpochProofCannotRefreshItsTimestamp()
    {
        var c=Catalogue(false,1);var proof=Observe(c.Nodes[0].Semantics,Target,true,"single-completed-worker");
        var transport=new Scripted((_,_,_)=>Task.FromResult(proof));
        Assert.Equal(1,(await ProbeCoordinator.RunAsync(c,transport,Target,Now,CancellationToken.None)).Succeeded);
        Assert.Equal(0,(await ProbeCoordinator.RunAsync(c,transport,Target,Now.AddMinutes(31),CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task S601_MetadataFailureCannotPairTheFallbackPinWithCurrentBranchTree()
    {
        using var f=Fetcher(r=>r.RequestUri!.AbsolutePath.Contains("/commits/",StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(TreeJson(Tree,"BLACK_VLESS_RUS.txt")));
        var result=await Coordinator(new MemoryCatalogue(),f,new SourceLedger()).DiscoverAsync(Registry(),CancellationToken.None);
        Assert.False(result.Complete,"Current mutable branch tree was labeled with an unresolved fallback pin.");
    }

    [Fact]
    public async Task S602_ValidEmptySnapshotCanReuse304WithoutRepeatedDownload()
    {
        var c=new MemoryCatalogue();var ledger=new SourceLedger();var calls=0;
        using var f=Fetcher(_=>
        {
            calls++;if(calls==2)return new HttpResponseMessage(HttpStatusCode.NotModified);
            var r=Json("[]");r.Headers.TryAddWithoutValidation("ETag","\"empty-v1\"");return r;
        });
        var coordinator=Coordinator(c,f,ledger);var item=Item("empty");
        await coordinator.RefreshAsync([item],Now,CancellationToken.None);
        await coordinator.RefreshAsync([item],Now.AddHours(3),CancellationToken.None);
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task S603_DisabledFamilyMustNotBeDownloadedByBackgroundRefresh()
    {
        var c=Catalogue(false,1);c.Settings=c.Settings with {DisabledFamilyIds=["black-vless"],Revision=2};var calls=0;
        using var f=Fetcher(_=>{calls++;return Json(Link);});
        await Coordinator(c,f,new SourceLedger()).RefreshAsync([Item("disabled")],Now,CancellationToken.None);
        Assert.Equal(0,calls);
    }

    [Fact]
    public void S604_HistoricalRawRevisionMustNotMakeFreshLiveSourcesDue()
    {
        var ledger=new SourceLedger();
        ledger.Remember("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/"+Pin+"/BLACK_VLESS_RUS.txt","old","old",Now.AddDays(-1),null);
        ledger.Remember("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/"+Commit+"/BLACK_VLESS_RUS.txt","new","new",Now,null);
        ledger.RememberDiscovery(Registry().TreeApi.AbsoluteUri,"tree",TreeJson(Tree,"BLACK_VLESS_RUS.txt"),Now,Commit);
        Assert.DoesNotContain(ledger.LiveSuccessStamps(Now),stamp=>RefreshSchedule.IsDue(stamp,Now,TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task B601_FailoverPartialSpawnMustRetainTheReplacementCleanupHandle()
    {
        var c=Catalogue(true,2);var core=new OwnedCore { ThrowOnStart=2 };var e=Engine(c,core);
        await Connect(e,c);Confirm(e,c);await Standby(e,c);
        _=await Record.ExceptionAsync(()=>Health(e,c));
        var disconnected=await e.HandleAsync(Request(e,IpcOperations.Disconnect,new DisconnectPayload()),CancellationToken.None);
        Assert.True(disconnected.Ok);Assert.Empty(core.Active);
    }

    [Fact]
    public async Task B602_ExpiredStandbyMustBeRecheckedAtSwitchCompletion()
    {
        var c=Catalogue(true,2);var clock=new FakeClock {UtcNow=Now};
        var core=new OwnedCore {OnStart=i=>{if(i==2)clock.UtcNow=Now.AddSeconds(65);}};
        var e=Engine(c,core,clock);await Connect(e,c);Confirm(e,c);await Standby(e,c);await Health(e,c);
        Assert.NotEqual(c.Nodes[1].NodeId,e.State.ActiveNodeId);
    }

    [Fact]
    public async Task B603_AutomaticFailoverMustNotInheritThePreviousManualOverride()
    {
        var c=Catalogue(true,2);var core=new OwnedCore();var e=Engine(c,core);
        await Connect(e,c);Confirm(e,c);await Standby(e,c);await Health(e,c);
        Assert.Equal(c.Nodes[1].NodeId,e.State.ActiveNodeId);
        c.TrySetExcluded(c.Nodes[1].NodeId,true);Confirm(e,c);
        Assert.NotEqual(TunnelPhase.Connected,e.State.Phase);
    }

    [Fact]
    public async Task B604_StrictCountryChangeBeforeConfirmationMustBeRevalidated()
    {
        var c=Catalogue(true,1);c.Settings=c.Settings with {CountryMode=CountryConstraint.Strict,Country="DE",Revision=2};
        var e=Engine(c,new OwnedCore());Assert.True((await Connect(e,c)).Ok);
        c.Nodes[0].AdvertisedCountry="FI";Confirm(e,c);
        Assert.NotEqual(TunnelPhase.Connected,e.State.Phase);
    }

    [Fact]
    public async Task B605_PolicyTighteningDuringArmCannotConfirmTheOldLanProfile()
    {
        var c=Catalogue(true,1);c.Settings=c.Settings with {LanAccess=true,Revision=2};
        var core=new OwnedCore();var guard=new Guard {OnArm=()=>c.Settings=c.Settings with {LanAccess=false,Revision=3}};
        var e=new BrokerEngine(c,guard,core,clock:new FakeClock {UtcNow=Now});
        await e.HandleAsync(Request(e,IpcOperations.Connect,new {nodeId=c.Nodes[0].NodeId,digest=c.Nodes[0].Digest,networkEpoch=c.NetworkEpoch}),CancellationToken.None);Confirm(e,c);
        Assert.True(e.State.Phase!=TunnelPhase.Connected || !core.Profiles[0].Contains("192.168.0.0/16,DIRECT",StringComparison.Ordinal),"Confirmed the LAN-enabled profile after the owner policy had become LAN-disabled.");
    }

    [Fact]
    public void I601_AgedOutSafetyRequestMustNotExecuteAgain()
    {
        var dispatcher=new IpcDispatcher();var caller=new CallerIdentity {Sid="owner",SessionId=1};var executions=0;
        IpcRequest Make(string id)=>new() {ProtocolVersion=1,RequestId=id,Operation=IpcOperations.Disconnect,ExpectedStateRevision=0,Payload=JsonSerializer.SerializeToElement(new DisconnectPayload(),IpcJson.Options)};
        IpcResponse Run(IpcRequest r){if(r.RequestId=="retired-safety")executions++;return new IpcResponse {ProtocolVersion=1,RequestId=r.RequestId,Ok=true};}
        dispatcher.Dispatch(Make("retired-safety"),caller,Run);
        for(var i=0;i<9000;i++)dispatcher.Dispatch(Make("safety-"+i.ToString(CultureInfo.InvariantCulture)),caller,Run);
        dispatcher.Dispatch(Make("retired-safety"),caller,Run);
        Assert.Equal(1,executions);
    }

    [Fact]
    public void I602_CoreStillRunningMustKeepExitBlocked()
    {
        var mailbox=new SessionMailbox();
        mailbox.Apply(new IpcResponse {ProtocolVersion=1,RequestId="state",Ok=true,Snapshot=new BrokerSnapshot {BootId="boot",Sequence=1,Revision=1,Phase=nameof(TunnelPhase.Disconnected),CoreRunning=true,ProtectionArmed=false}},true);
        Assert.False(UiSessionReducer.PlanExit(mailbox.Session).CanClose);
    }

    [Fact]
    public void C601_ControlSqliteAssessmentPersistsAndReopensNormally()
    {
        var directory=Directory.CreateTempSubdirectory("autovpn-r6-reopen-");
        try
        {
            var path=Path.Combine(directory.FullName,"catalogue.sqlite");
            using(var c=SqliteCatalogue.Open(path,new Plain())){Seed(c,1,true);}
            using(var reopened=SqliteCatalogue.Open(path,new Plain())){Assert.Equal(HealthState.Healthy,Assert.Single(reopened.Nodes).Assessment!.Health);}
        }
        finally{directory.Delete(true);}
    }

    [Fact]
    public async Task C602_ControlStableCommitDiscoveryWorks()
    {
        using var f=Fetcher(r=>Json(r.RequestUri!.AbsolutePath.Contains("/commits/",StringComparison.Ordinal)?CommitJson():TreeJson(Tree,"BLACK_VLESS_RUS.txt")));
        var result=await Coordinator(new MemoryCatalogue(),f,new SourceLedger()).DiscoverAsync(Registry(),CancellationToken.None);
        Assert.True(result.Complete);Assert.Equal(Commit,result.CommitSha);Assert.Single(result.Items);
    }

    [Fact]
    public async Task C603_ControlOwnedConnectConfirmDisconnectWorks()
    {
        var c=Catalogue(true,1);var core=new OwnedCore();var e=Engine(c,core);
        Assert.True((await Connect(e,c)).Ok);Confirm(e,c);Assert.Equal(TunnelPhase.Connected,e.State.Phase);
        Assert.True((await e.HandleAsync(Request(e,IpcOperations.Disconnect,new DisconnectPayload()),CancellationToken.None)).Ok);Assert.Empty(core.Active);
    }

    [Fact]
    public async Task C604_ControlFreshManualExcludedSelectionRemainsAllowed()
    {
        var c=Catalogue(true,1);c.TrySetExcluded(c.Nodes[0].NodeId,true);var core=new OwnedCore();var e=Engine(c,core);
        Assert.True((await Connect(e,c)).Ok);Confirm(e,c);Assert.Equal(TunnelPhase.Connected,e.State.Phase);
        await e.HandleAsync(Request(e,IpcOperations.Disconnect,new DisconnectPayload()),CancellationToken.None);Assert.Empty(core.Active);
    }

    private static TaskCompletionSource NewGate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProbeObservation Observe(NodeSemantics n,Uri t,bool ok,string worker)=>new(ok,ok?10:null,false,ok?null:ReasonCodes.ProbeFailed,80,ok?ProbeClass.Success:ProbeClass.CandidateFailure,t.AbsoluteUri,CanonicalIdentity.Digest(n),worker);
    private static MemoryCatalogue Catalogue(bool healthy,int count){var c=new MemoryCatalogue();Seed(c,count,healthy);return c;}
    private static void Seed(ICatalogue c,int count,bool healthy)
    {
        c.Settings=c.Settings with {DisclosureAccepted=true};
        for(var i=0;i<count;i++)
        {
            var n=new NodeSemantics {Protocol=ProtocolKind.Vless,Host="203.0.113."+(10+i).ToString(CultureInfo.InvariantCulture),Port=443,UserId="11111111-1111-4111-8111-111111111111",Security="tls",Encryption="none",Transport="tcp"};
            c.ApplySnapshot(new SnapshotCommit {ArtifactId="seed-"+i,FamilyId="black-vless",ContentHash="synthetic",Complete=true,NowUtc=Now,Nodes=[new SnapshotNode {Digest=CanonicalIdentity.Digest(n),Semantics=n,Label="synthetic",AdvertisedCountry="DE",ArtifactId="seed-"+i,FamilyId="black-vless"}]});
        }
        if(healthy)foreach(var n in c.Nodes)c.ApplyAssessment(n.NodeId,new AssessmentSnapshot {Digest=n.Digest,NetworkEpoch=c.NetworkEpoch,Health=HealthState.Healthy,LastSuccessUtc=Now,MedianLatencyMs=10});
    }
    private static BrokerEngine Engine(ICatalogue c,OwnedCore core,FakeClock? clock=null)=>new(c,new Guard(),core,clock:clock??new FakeClock {UtcNow=Now});
    private static Task<IpcResponse> Connect(BrokerEngine e,ICatalogue c)=>e.HandleAsync(Request(e,IpcOperations.Connect,new ConnectPayload {NodeId=c.Nodes[0].NodeId,Digest=c.Nodes[0].Digest,NetworkEpoch=c.NetworkEpoch}),CancellationToken.None);
    private static void Confirm(BrokerEngine e,ICatalogue c){var s=e.Snapshot();e.ConfirmProduction(s.BootId!,s.Generation,s.OperationId,s.ActiveNodeId,c.NetworkEpoch,true,null);}
    private static Task<IpcResponse> Standby(BrokerEngine e,ICatalogue c)=>e.HandleAsync(Request(e,IpcOperations.ApplyRuntimeSet,new {standbys=new[]{new StandbyCandidate {NodeId=c.Nodes[1].NodeId,EndpointKey="synthetic",Country="DE",SourceFamilyId="black-vless"}}}),CancellationToken.None);
    private static Task<IpcResponse> Health(BrokerEngine e,ICatalogue c)=>e.HandleAsync(Request(e,IpcOperations.ReportHealth,new HealthPayload {FailureKind=nameof(FailureKind.CoreExit),ConsecutiveFailures=3,NetworkEpoch=c.NetworkEpoch}),CancellationToken.None);
    private static IpcRequest Request(BrokerEngine e,string op,object body)=>new() {ProtocolVersion=1,RequestId=Guid.NewGuid().ToString("N"),ExpectedStateRevision=e.Snapshot().Revision,Operation=op,Payload=JsonSerializer.SerializeToElement(body,IpcJson.Options)};
    private static CatalogueCoordinator Coordinator(ICatalogue c,PolicyHttpFetcher f,SourceLedger l)=>new(c,f,new Scripted((n,t,_)=>Task.FromResult(Observe(n,t,true,Guid.NewGuid().ToString("N")))),l);
    private static HttpResponseMessage Json(string s)=>new(HttpStatusCode.OK){Content=new StringContent(s)};
    private static string CommitJson()=>"{\"sha\":\""+Commit+"\",\"commit\":{\"tree\":{\"sha\":\""+Tree+"\"}}}";
    private static string TreeJson(string sha,string path)=>JsonSerializer.Serialize(new {sha,truncated=false,tree=new[]{new {path,type="blob",mode="100644",size=100,sha=new string('4',40)}}});
    private static ReviewedRegistry Registry()=>new(){Owner="igareck",Repository="vpn-configs-for-russia",PinnedCommit=Pin,TreeApi=new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1"),FamilyIds=["black-vless"],ProbeTargets=[],ApprovedHosts=new HashSet<string>{"raw.githubusercontent.com"},RejectedHosts=new HashSet<string>(),FetchOrigins=[new ApprovedFetchOrigin("raw.githubusercontent.com",443,"/igareck/vpn-configs-for-russia/")]};
    private static RefreshWorkItem Item(string id)=>new(){ArtifactId=id,FamilyId="black-vless",Urls=[new Uri("https://raw.githubusercontent.com/audit/"+id)]};
    private static PolicyHttpFetcher Fetcher(Func<HttpRequestMessage,HttpResponseMessage> f)=>new(new Handler(f),new HashSet<string>{"api.github.com","raw.githubusercontent.com"});
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> f):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>Task.FromResult(f(r));}
    private sealed class Scripted(Func<NodeSemantics,Uri,CancellationToken,Task<ProbeObservation>> f):IProbeTransport
    {public Task<ProbeObservation> ProbeAsync(NodeSemantics n,Uri u,CancellationToken t)=>f(n,u,t);}
    private sealed class Plain:ISecretProtector
    {public string ProtectorId=>"test-round6-only";public byte[] Protect(ReadOnlySpan<byte> b)=>b.ToArray();public byte[] Unprotect(ReadOnlySpan<byte> b)=>b.ToArray();}
    private sealed class Guard:INetworkGuard
    {public Action? OnArm {get;init;}public GuardResult Arm(GuardRequest r){OnArm?.Invoke();return new(true,r.ProtectionRequired,null,[]);}public GuardResult Disarm(long g)=>new(true,false,null,[]);public GuardResult Recover(IReadOnlyList<OwnedEffect> e)=>new(true,false,null,e.Select(x=>x.Id).ToArray());}
    private sealed class OwnedCore:ICoreController
    {
        private int _starts;public int ThrowOnStart {get;init;}public Action<int>? OnStart {get;init;}
        public HashSet<string> Active {get;}=new(StringComparer.Ordinal);public List<string> Profiles {get;}=[];
        public Task<CoreStartResult> StartAsync(string yaml,long generation,string id,CancellationToken token)
        {Profiles.Add(yaml);Active.Clear();Active.Add(generation+":"+id);_starts++;OnStart?.Invoke(_starts);if(_starts==ThrowOnStart)throw new IOException("controlled partial replacement spawn");return Task.FromResult(new CoreStartResult(true,null));}
        public Task StopAsync(long generation,string id,CancellationToken token){Active.Remove(generation+":"+id);return Task.CompletedTask;}
    }
}
