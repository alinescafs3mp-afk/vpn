using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class AstraV2MaintenanceTests
{
    private static readonly Uri[] Targets = [new("https://one.example/generate_204"), new("https://two.example/generate_204")];
    private static MemoryCatalogue Catalogue(int count = 1, bool consent = true)
    {
        var c = new MemoryCatalogue { Settings = new ProductSettings { DisclosureAccepted = consent } };
        var links = string.Join("\n", Enumerable.Range(1, count).Select(i =>
            $"vless://11111111-1111-4111-8111-111111111111@203.0.113.22:{4000 + i}?security=tls&sni=example.com"));
        RefreshMerge.Ingest(c, [new IngestArtifact { ArtifactId = "source", FamilyId = "family", Enabled = true, Text = links }], new FakeClock().UtcNow, false);
        return c;
    }
    private static ProbeByteBudget Budget(FakeClock clock, long limit = ProductLimits.DailyHealthBudgetBytes)
        => ProbeByteBudget.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()), limit, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
    private static ProbeObservation Good(NodeSemantics n, Uri t, ProbeAdmission a, int latency = 20)
        => new(true, latency, false, null, 100, ProbeClass.Success, t.AbsoluteUri, CanonicalIdentity.Digest(n), "test-worker") { Attempt = a.Attempt };
    private sealed class Transport(Func<NodeSemantics, Uri, ProbeAdmission, CancellationToken, Task<ProbeObservation>> run) : IProbeTransport
    {
        public int Calls;
        public Task<ProbeObservation> ProbeAsync(NodeSemantics n, Uri t, CancellationToken ct) => throw new InvalidOperationException("Unbound calls forbidden");
        public Task<ProbeObservation> ProbeAsync(NodeSemantics n, Uri t, ProbeAdmission a, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return run(n, t, a, ct); }
    }
    private static Transport GoodTransport() => new((n, t, a, _) => Task.FromResult(Good(n, t, a)));
    private static EligibilityContext Context(ICatalogue c, FakeClock clock, string set) => new()
    { NowUtc = clock.UtcNow, NetworkEpoch = c.NetworkEpoch, RequiredTargetSetId = set };

    [Theory]
    [InlineData("http://one.example/", "https://two.example/")]
    [InlineData("https://same.example/a", "https://same.example/b")]
    [InlineData("https://user@one.example/", "https://two.example/")]
    [InlineData("https://one.example/#secret", "https://two.example/")]
    public void InvalidTargetSetsFailBeforeDial(string first, string second)
    {
        var transport = GoodTransport();
        Assert.Throws<ArgumentException>(() => new TwoTargetProbeTransport(Catalogue(), transport, [new(first), new(second)]));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task OnlyBothTargetsPublishAndFirstHalfIsNeverVisible()
    {
        var c = Catalogue(); var clock = new FakeClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Transport(async (n, t, a, ct) =>
        { if (t == Targets[1]) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } return Good(n, t, a, t == Targets[0] ? 20 : 40); });
        var pair = new TwoTargetProbeTransport(c,inner, Targets);
        var run = new CatalogueMaintenance(c, pair, Budget(clock), clock).PulseAsync();
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Empty(c.Eligible(Context(c, clock, pair.TargetSetId))); }
        finally { release.TrySetResult(); }
        var report = await run;
        Assert.Equal(1, report.Succeeded); Assert.Equal(2, inner.Calls);
        Assert.Equal(30, c.Nodes.Single().Assessment!.MedianLatencyMs);
        Assert.Single(c.Eligible(Context(c, clock, pair.TargetSetId)));
    }

    [Fact]
    public async Task MixedTargetResultsRemainInconclusiveNotHealthy()
    {
        var c = Catalogue(); var clock = new FakeClock();
        var inner = new Transport((n,t,a,_) => Task.FromResult(t == Targets[0] ? Good(n,t,a) :
            new ProbeObservation(false, null, false, "TLS_REJECTED", Class: ProbeClass.CandidateFailure) { Attempt = a.Attempt }));
        var pair = new TwoTargetProbeTransport(c,inner, Targets); var service = new CatalogueMaintenance(c,pair,Budget(clock),clock);
        Assert.Equal("BACKOFF", (await service.PulseAsync()).Phase);
        Assert.Equal(HealthState.EnvironmentUnknown, c.Nodes.Single().Assessment!.Health);
        Assert.Empty(c.Eligible(Context(c,clock,pair.TargetSetId)));
        await service.PulseAsync(); Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("target")]
    [InlineData("attempt")]
    [InlineData("worker")]
    [InlineData("classification")]
    public async Task SecondTargetRequiresItsOwnMatchingProof(string fault)
    {
        var c=Catalogue();var clock=new FakeClock();
        var inner=new Transport((n,t,a,_) =>
        {
            var g=Good(n,t,a);
            if(t==Targets[1]) g=fault switch
            {
                "digest"=>g with { CandidateDigest="wrong" },
                "target"=>g with { TargetUri=Targets[0].AbsoluteUri },
                "attempt"=>g with { Attempt=a.Attempt! with { AttemptId="old" } },
                "worker"=>g with { WorkerId=null },
                _=>g with { Class=ProbeClass.Unsupported },
            };
            return Task.FromResult(g);
        });
        var pair=new TwoTargetProbeTransport(c,inner,Targets);
        await new CatalogueMaintenance(c,pair,Budget(clock),clock).PulseAsync();
        Assert.Empty(c.Eligible(Context(c,clock,pair.TargetSetId)));
    }

    [Fact]
    public async Task NoConsentNoDialAndPausedQueueResumes()
    {
        var c=Catalogue(consent:false);var clock=new FakeClock();var inner=GoodTransport();var pair=new TwoTargetProbeTransport(c,inner,Targets);
        var service=new CatalogueMaintenance(c,pair,Budget(clock),clock);
        Assert.Equal("CONSENT_REQUIRED",(await service.PulseAsync()).Phase);
        Assert.False(await service.CheckNowAsync(c.Nodes[0].NodeId,CancellationToken.None));
        c.Settings=c.Settings with { DisclosureAccepted=true,Revision=2 };
        service.Pause();Assert.Equal("PAUSED",(await service.PulseAsync()).Phase);Assert.Equal(0,inner.Calls);
        service.Resume();Assert.Equal(1,(await service.PulseAsync()).Succeeded);
    }

    [Fact]
    public async Task QueueProgressSurvivesNewServiceAndRefreshIsNotRequired()
    {
        var c=Catalogue(9);var clock=new FakeClock();var inner=GoodTransport();var pair=new TwoTargetProbeTransport(c,inner,Targets);var budget=Budget(clock);
        for(var i=0;i<5;i++) await new CatalogueMaintenance(c,pair,budget,clock).PulseAsync(maxNodes:2);
        Assert.Equal(18,inner.Calls);Assert.Equal(9,c.Eligible(Context(c,clock,pair.TargetSetId)).Count);
        clock.Advance(TimeSpan.FromMinutes(26));
        await new CatalogueMaintenance(c,pair,budget,clock).PulseAsync(maxNodes:2);
        Assert.Equal(22,inner.Calls);
    }

    [Fact]
    public async Task FailureBackoffSurvivesRestartAndNewCandidatesProgress()
    {
        var c=Catalogue(3);var clock=new FakeClock();
        var inner=new Transport((n,t,a,_)=>Task.FromResult(n.Port==4001 ? new ProbeObservation(false,null,false,"FAIL") { Attempt=a.Attempt } : Good(n,t,a)));
        var pair=new TwoTargetProbeTransport(c,inner,Targets);var budget=Budget(clock);
        await new CatalogueMaintenance(c,pair,budget,clock).PulseAsync();
        Assert.Equal(6,inner.Calls);Assert.Equal(2,c.Eligible(Context(c,clock,pair.TargetSetId)).Count);
        await new CatalogueMaintenance(c,pair,budget,clock).PulseAsync();Assert.Equal(6,inner.Calls);
        clock.Advance(TimeSpan.FromMinutes(6));await new CatalogueMaintenance(c,pair,budget,clock).PulseAsync();Assert.Equal(8,inner.Calls);
    }

    [Fact]
    public async Task OneTargetV1EvidenceIsNotSilentlyUpgraded()
    {
        var c=Catalogue();var clock=new FakeClock();var inner=GoodTransport();
        await ProbeCoordinator.CheckAsync(c,inner,Targets[0],c.Nodes[0].NodeId,clock.UtcNow,SelectionPurpose.Automatic,CancellationToken.None);
        var pair=new TwoTargetProbeTransport(c,inner,Targets);
        Assert.Empty(c.Eligible(Context(c,clock,pair.TargetSetId)));
        Assert.True(await new CatalogueMaintenance(c,pair,Budget(clock),clock).CheckNowAsync(c.Nodes[0].NodeId,CancellationToken.None));
        Assert.Equal(3,inner.Calls);Assert.Single(c.Eligible(Context(c,clock,pair.TargetSetId)));
    }

    [Fact]
    public async Task PolicyChangeBetweenTargetsPreventsPublication()
    {
        var c=Catalogue();var clock=new FakeClock();
        var inner=new Transport((n,t,a,_)=>
        { if(t==Targets[1]) c.Settings=c.Settings with { DisclosureAccepted=false,Revision=2 };return Task.FromResult(Good(n,t,a)); });
        var pair=new TwoTargetProbeTransport(c,inner,Targets);
        await new CatalogueMaintenance(c,pair,Budget(clock),clock).PulseAsync();
        Assert.Empty(c.Eligible(Context(c,clock,pair.TargetSetId)));
    }

    [Fact]
    public async Task ConcurrentPulsesCoalesceAndPauseWaitsForOwnedCleanup()
    {
        var c=Catalogue(4);var clock=new FakeClock();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner=new Transport(async(n,t,a,ct)=>
        { entered.TrySetResult();try { await Task.Delay(Timeout.Infinite,ct); } finally { await cleanup.Task; } return Good(n,t,a); });
        var pair=new TwoTargetProbeTransport(c,inner,Targets);var service=new CatalogueMaintenance(c,pair,Budget(clock),clock);
        var pulse=service.PulseAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.PulseAsync();Assert.InRange(inner.Calls,1,2);
        service.Pause();await pulse.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(await service.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));
        cleanup.TrySetResult();Assert.True(await service.WaitForIdleAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(c.Eligible(Context(c,clock,pair.TargetSetId)));
    }

    [Fact]
    public async Task PairLimitAppliesToConcurrentRequests()
    {
        var c=Catalogue(5);var clock=new FakeClock();int active=0,maximum=0;
        var inner=new Transport(async(n,t,a,ct)=>
        { var now=Interlocked.Increment(ref active);int old;do {old=maximum;} while(now>old && Interlocked.CompareExchange(ref maximum,now,old)!=old);
          try { await Task.Delay(10,ct);return Good(n,t,a); } finally {Interlocked.Decrement(ref active);} });
        var pair=new TwoTargetProbeTransport(c,inner,Targets);
        await Task.WhenAll(c.Nodes.Select(n=>ProbeCoordinator.CheckAsync(c,pair,pair.PrimaryTarget,n.NodeId,clock.UtcNow,
            SelectionPurpose.Automatic,CancellationToken.None)));
        Assert.InRange(maximum,1,2);Assert.Equal(10,inner.Calls);
    }

    [Fact]
    public async Task BudgetIsReservedBeforeDialAndCannotOverspendConcurrently()
    {
        var c=Catalogue(5);var clock=new FakeClock();var inner=GoodTransport();var budget=Budget(clock,4*ProductLimits.HealthPayloadBytes);
        var pair=new TwoTargetProbeTransport(c,inner,Targets);var service=new CatalogueMaintenance(c,pair,budget,clock);
        await service.PulseAsync();Assert.Equal(4,inner.Calls);
        Assert.False(await service.CheckNowAsync(c.Nodes.First(n=>n.Assessment?.Health!=HealthState.Healthy).NodeId,CancellationToken.None));
        Assert.Equal(budget.Limit,budget.SpentOn(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)));
        clock.Advance(TimeSpan.FromDays(1));await service.PulseAsync();Assert.Equal(8,inner.Calls);
    }

    [Fact]
    public void BudgetReservationIsExactUnderContentionAndRejectsClockRollback()
    {
        var clock=new FakeClock();var day=DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);var budget=Budget(clock,100);int ok=0;
        Parallel.For(0,1000,_=>{if(budget.TryReserve(1,day)) Interlocked.Increment(ref ok);});
        Assert.Equal(100,ok);Assert.False(budget.TryReserve(1,day.AddDays(-1)));
        Assert.True(budget.TryReserve(1,day.AddDays(1)));Assert.Equal(1,budget.SpentOn(day.AddDays(1)));
    }

    [Fact]
    public async Task TwoTargetAssessmentAndBackoffPersistThroughSqliteReopen()
    {
        var directory=Directory.CreateTempSubdirectory("autovpn-v2-state-");
        try
        {
            var clock=new FakeClock();string nodeId;string targetSetId;
            var path=Path.Combine(directory.FullName,"catalogue.sqlite");
            using(var c=SqliteCatalogue.Open(path,new PassthroughSecretProtector()))
            {
                var pair=new TwoTargetProbeTransport(c,GoodTransport(),Targets); targetSetId=pair.TargetSetId;
                c.Settings=c.Settings with { DisclosureAccepted=true };
                RefreshMerge.Ingest(c,[new IngestArtifact{ArtifactId="s",FamilyId="family",Enabled=true,
                    Text="vless://11111111-1111-4111-8111-111111111111@203.0.113.22:443?security=tls&sni=example.com"}],clock.UtcNow,false);
                nodeId=c.Nodes[0].NodeId;Assert.True(await new CatalogueMaintenance(c,pair,Budget(clock),clock).CheckNowAsync(nodeId,CancellationToken.None));
            }
            using var reopened=SqliteCatalogue.Open(path,new PassthroughSecretProtector());
            Assert.Single(reopened.Eligible(Context(reopened,clock,targetSetId)));
            Assert.Equal(clock.UtcNow,reopened.Nodes.Single().Assessment!.LastAttemptUtc);
        }
        finally {directory.Delete(true);}
    }
}
