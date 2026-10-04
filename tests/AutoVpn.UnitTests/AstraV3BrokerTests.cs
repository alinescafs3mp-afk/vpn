using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3BrokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string RequiredSet = new('a', 64);

    [Theory]
    [InlineData(null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task LegacyOrDifferentTargetSetCannotReachGuardOrCore(string? priorSet)
    {
        var catalogue = Catalogue(priorSet); var core = new Core(); var guard = new Guard();
        var engine = Engine(catalogue, core, guard);
        var response = await Connect(engine, catalogue);
        Assert.False(response.Ok); Assert.Equal(ReasonCodes.NoEligibleServer, response.ErrorCode);
        Assert.Equal(0, guard.Arms); Assert.Equal(0, core.Starts);
        Assert.Equal(priorSet, catalogue.Nodes[0].Assessment!.VerifiedTargetSetId);
    }

    [Fact]
    public async Task ValidPairStartsAsConnectingNotConnected()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        var response = await Connect(engine, catalogue);
        Assert.True(response.Ok); Assert.Equal(nameof(TunnelPhase.Connecting), engine.Snapshot().Phase);
        Assert.True(engine.Snapshot().CoreRunning); Assert.Equal(1, engine.Snapshot().OwnedResourceCount);
        Assert.True(await engine.ShutdownAsync(default));
    }

    [Fact]
    public async Task CrashObservationRejectsLateVerificationAndRetainsCleanupOwner()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        Assert.True((await Connect(engine, catalogue)).Ok);
        var pending = engine.Snapshot(); core.Running = false;
        engine.ConfirmProduction(pending.BootId!, pending.Generation, pending.OperationId,
            pending.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        var snapshot = engine.Snapshot();
        Assert.False(snapshot.CoreRunning); Assert.NotEqual(nameof(TunnelPhase.Connected), snapshot.Phase);
        Assert.Equal("CORE_EXITED", snapshot.BlockReason); Assert.Equal(1, snapshot.OwnedResourceCount);
        Assert.True(snapshot.ProtectionArmed);
        Assert.True(await engine.ShutdownAsync(default)); Assert.Equal(1, core.Stops);
        Assert.Equal(0, engine.Snapshot().OwnedResourceCount);
    }

    [Fact]
    public async Task ConnectedSessionCannotStayGreenAfterObservedProcessExit()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        await Connect(engine, catalogue); Confirm(engine, catalogue);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        core.Running = false;
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.False(engine.Snapshot().CoreRunning);
        Assert.True(await engine.ShutdownAsync(default));
    }

    [Fact]
    public async Task ConsentRevokedDuringStartPreventsPublicationEvenWithoutRevisionBump()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core { HoldStart = true };
        var engine = Engine(catalogue, core); var start = Connect(engine, catalogue);
        try
        {
            await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            catalogue.Settings = catalogue.Settings with { DisclosureAccepted = false };
        }
        finally { core.Release.TrySetResult(); }
        var response = await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(response.Ok); Assert.Equal(ReasonCodes.PolicyChanged, response.ErrorCode);
        Assert.False(engine.Snapshot().CoreRunning); Assert.Equal(1, core.Stops);
        Assert.Equal(0, engine.Snapshot().OwnedResourceCount); Assert.True(await engine.ShutdownAsync(default));
    }

    [Fact]
    public async Task ConsentRevokedAfterStartBlocksInProcessConfirmation()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        await Connect(engine, catalogue);
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = false };
        Confirm(engine, catalogue);
        Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.Equal(ReasonCodes.PolicyChanged, engine.State.BlockReason);
        Assert.True(await engine.ShutdownAsync(default));
    }

    [Fact]
    public async Task TargetContractChangedAfterStartBlocksConfirmation()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        await Connect(engine, catalogue);
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, node.Assessment! with { VerifiedTargetSetId = new string('b', 64) });
        Confirm(engine, catalogue); Assert.NotEqual(TunnelPhase.Connected, engine.State.Phase);
        Assert.True(await engine.ShutdownAsync(default));
    }

    [Fact]
    public async Task ShutdownStopsOwnedCoreAndPermanentlyClosesNewAdmission()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core(); var engine = Engine(catalogue, core);
        await Connect(engine, catalogue); Confirm(engine, catalogue);
        Assert.True(await engine.ShutdownAsync(default));
        var snapshot = engine.Snapshot();
        Assert.Equal(nameof(TunnelPhase.Disconnected), snapshot.Phase);
        Assert.False(snapshot.ProtectionArmed); Assert.False(snapshot.CoreRunning); Assert.Equal(0, snapshot.OwnedResourceCount);
        var denied = await Connect(engine, catalogue);
        Assert.False(denied.Ok); Assert.Equal("BROKER_CLOSING", denied.ErrorCode); Assert.Equal(1, core.Starts);
    }

    [Fact]
    public async Task ShutdownCannotClaimSuccessAfterFailedStopAndCanRetry()
    {
        var catalogue = Catalogue(RequiredSet); var core = new Core { StopFailures = 1 }; var engine = Engine(catalogue, core);
        await Connect(engine, catalogue);
        Assert.False(await engine.ShutdownAsync(default));
        Assert.Equal(1, engine.Snapshot().OwnedResourceCount);
        Assert.True(await engine.ShutdownAsync(default)); Assert.Equal(2, core.Stops);
    }

    [Fact]
    public async Task UnprotectedFailureMustNotInventProtection()
    {
        var catalogue = Catalogue(RequiredSet);
        catalogue.Settings = catalogue.Settings with { ProtectionOnConnect = false };
        var core = new Core { StartResult = new(false, "TEST_START_FAILED") }; var engine = Engine(catalogue, core);
        var response = await Connect(engine, catalogue);
        Assert.False(response.Ok); Assert.False(engine.Snapshot().ProtectionArmed);
        Assert.DoesNotContain("остаётся включённой", response.Message ?? "", StringComparison.Ordinal);
        Assert.True(await engine.ShutdownAsync(default));
    }

    [Theory]
    [InlineData(TunnelPhase.Connecting, TunnelCommandKind.VerifyFailed, false)]
    [InlineData(TunnelPhase.Connecting, TunnelCommandKind.VerifyFailed, true)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.CoreExited, false)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.CoreExited, true)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.HealthFailed, false)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.HealthFailed, true)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.UplinkLost, false)]
    [InlineData(TunnelPhase.Connected, TunnelCommandKind.UplinkLost, true)]
    public void FailureTransitionsPreserveActualProtection(TunnelPhase phase, TunnelCommandKind command, bool armed)
    {
        var before = new TunnelState { Phase = phase, ProtectionArmed = armed, Generation = 2 };
        var after = TunnelReducer.Apply(before, new TunnelCommand(command, 2));
        Assert.Equal(armed, after.ProtectionArmed); Assert.Equal(before.Revision + 1, after.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void InvalidRequiredTargetIdentityCannotConstructProductionBoundary(string identity)
    {
        Assert.Throws<ArgumentException>(() => new BrokerEngine(Catalogue(RequiredSet), new Guard(), new Core(), requiredTargetSetId: identity));
    }

    [Fact]
    public void SingleTargetAdmissionCannotBeCombinedWithRequiredPair()
    {
        Assert.Throws<ArgumentException>(() => new BrokerEngine(Catalogue(RequiredSet), new Guard(), new Core(),
            admission: new NoProbe(), admissionTarget: new("https://one.example/"), requiredTargetSetId: RequiredSet));
    }

    [Fact]
    public void PairIdentityIsOrderIndependentAndDoesNotConstructWorkers()
    {
        Uri[] targets = [new("https://one.example/204"), new("https://two.example/204")];
        var pair = new TwoTargetProbeTransport(Catalogue(RequiredSet), new NoProbe(), targets);
        Assert.Equal(pair.TargetSetId, TwoTargetProbeTransport.TargetIdentity(targets));
        Assert.Equal(pair.TargetSetId, TwoTargetProbeTransport.TargetIdentity(targets.Reverse().ToArray()));
        Assert.NotEqual(pair.TargetSetId, TwoTargetProbeTransport.TargetIdentity([targets[0], new("https://three.example/204")]));
    }

    private static MemoryCatalogue Catalogue(string? targetSet)
    {
        var catalogue = new MemoryCatalogue { Settings = new ProductSettings { DisclosureAccepted = true } };
        var semantics = new NodeSemantics
        {
            Protocol = ProtocolKind.Vless, Host = "203.0.113.10", Port = 443,
            UserId = "11111111-1111-4111-8111-111111111111", Security = "tls", Encryption = "none", Transport = "tcp",
        };
        catalogue.ApplySnapshot(new SnapshotCommit
        {
            ArtifactId = "synthetic", FamilyId = "black-vless", ContentHash = "synthetic", Complete = true, NowUtc = Now,
            Nodes = [new SnapshotNode { Digest = CanonicalIdentity.Digest(semantics), Semantics = semantics,
                Label = "synthetic", ArtifactId = "synthetic", FamilyId = "black-vless" }],
        });
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot { Digest = node.Digest, NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy, LastSuccessUtc = Now, MedianLatencyMs = 20, VerifiedTargetSetId = targetSet });
        return catalogue;
    }
    private static BrokerEngine Engine(ICatalogue catalogue, Core core, Guard? guard = null)
        => new(catalogue, guard ?? new(), core, clock: new FakeClock { UtcNow = Now }, requiredTargetSetId: RequiredSet);
    private static Task<IpcResponse> Connect(BrokerEngine engine, ICatalogue catalogue)
        => engine.HandleAsync(new IpcRequest { ProtocolVersion = ProductLimits.IpcProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"), ExpectedStateRevision = engine.Snapshot().Revision,
            Operation = IpcOperations.Connect, Payload = JsonSerializer.SerializeToElement(new ConnectPayload
            { NodeId = catalogue.Nodes[0].NodeId, Digest = catalogue.Nodes[0].Digest, NetworkEpoch = catalogue.NetworkEpoch,
                ProtectionRequired = catalogue.Settings.ProtectionOnConnect }, IpcJson.Options) }, default);
    private static void Confirm(BrokerEngine engine, ICatalogue catalogue)
    {
        var snapshot = engine.Snapshot();
        engine.ConfirmProduction(snapshot.BootId!, snapshot.Generation, snapshot.OperationId, snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
    }
    private sealed class Guard : INetworkGuard
    {
        public int Arms;
        public GuardResult Arm(GuardRequest request) { Arms++; return new(true, request.ProtectionRequired, null, []); }
        public GuardResult Disarm(long generation) => new(true, false, null, []);
        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects) => new(true, false, null, []);
    }
    private sealed class Core : ICoreController, ICoreLiveness
    {
        public volatile bool Running;
        public int Starts;
        public int Stops;
        public int StopFailures;
        public bool HoldStart { get; init; }
        public CoreStartResult StartResult { get; init; } = new(true, null);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRunning(long generation, string operationId) => Running;
        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken token)
        {
            Starts++; Entered.TrySetResult(); if (HoldStart) await Release.Task.WaitAsync(token);
            Running = StartResult.Started; return StartResult;
        }
        public Task StopAsync(long generation, string operationId, CancellationToken token)
        {
            Stops++; if (Interlocked.Decrement(ref StopFailures) >= 0) throw new IOException("synthetic cleanup failure");
            Running = false; return Task.CompletedTask;
        }
    }
    private sealed class NoProbe : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken token)
            => throw new InvalidOperationException("This fixture must not dial.");
    }
}
