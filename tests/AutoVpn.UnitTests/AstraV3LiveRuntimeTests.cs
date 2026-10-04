using System.Collections.Concurrent;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Persistence;

namespace AutoVpn.UnitTests;

public sealed class AstraV3LiveRuntimeTests
{
    [Theory]
    [InlineData(FailureKind.HealthTimeout)]
    [InlineData(FailureKind.CoreExit)]
    public async Task ReplacementStopsOldOwnerBeforeCreatingNewOne(FailureKind failure)
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        var old = h.Engine.Snapshot();
        if (failure == FailureKind.CoreExit) h.Processes[0].Crash();
        var result = await h.Health(failure);
        Assert.True(result.Ok); Assert.Equal(2, h.Processes.Count);
        Assert.Equal(new[] { "start:0", "stop:0", "start:1" }, h.Events.ToArray());
        Assert.Equal(1, h.PeakRunning); Assert.Equal(1, h.Engine.Snapshot().OwnedResourceCount);
        Assert.Equal(nameof(TunnelPhase.Connecting), h.Engine.Snapshot().Phase);
        Assert.NotEqual(old.OperationId, h.Engine.Snapshot().OperationId);
        h.Engine.ConfirmProduction(old.BootId!, old.Generation, old.OperationId, old.ActiveNodeId, h.Catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connecting, h.Engine.State.Phase);
        h.Confirm(); Assert.Equal(TunnelPhase.Connected, h.Engine.State.Phase);
    }

    [Fact]
    public async Task FailedOldCleanupNeverStartsReplacementAndCanBeRetried()
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        h.Processes[0].StopFailures = 1;
        await h.Health(FailureKind.HealthTimeout);
        Assert.Single(h.Processes); Assert.Equal(1, h.Engine.Snapshot().OwnedResourceCount);
        Assert.True(h.Engine.Snapshot().ProtectionArmed); Assert.NotEqual(TunnelPhase.Connected, h.Engine.State.Phase);
        Assert.True((await h.Disconnect()).Ok);
        Assert.Equal(2, h.Processes[0].Stops); Assert.Equal(0, h.Engine.Snapshot().OwnedResourceCount);
        Assert.False(h.Engine.Snapshot().ProtectionArmed);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("consent")]
    [InlineData("epoch")]
    [InlineData("policy")]
    [InlineData("cancel")]
    [InlineData("assessment")]
    public async Task ChangedAuthorityDuringOldCleanupCannotLaunchReplacement(string change)
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        var first = h.Processes[0]; first.HoldStop = true;
        using var cancellation = new CancellationTokenSource();
        var work = h.Health(FailureKind.HealthTimeout, cancellation.Token);
        await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IpcResponse>? disconnect = null;
        try
        {
            switch (change)
            {
                case "disconnect": disconnect = h.Disconnect(); break;
                case "consent": h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false }; break;
                case "epoch": h.Catalogue.SetNetworkEpoch(h.Catalogue.NetworkEpoch + 1); break;
                case "policy": h.Catalogue.Settings = h.Catalogue.Settings with { LanAccess = false }; break;
                case "cancel": cancellation.Cancel(); break;
                case "assessment":
                    var second = h.Catalogue.Nodes[1];
                    h.Catalogue.ApplyAssessment(second.NodeId, second.Assessment! with { VerifiedTargetSetId = new string('b', 64) }); break;
            }
        }
        finally { first.StopRelease.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        if (disconnect is not null) Assert.True((await disconnect.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        Assert.Single(h.Processes); Assert.False(h.Engine.Snapshot().CoreRunning);
        Assert.Equal(0, h.Engine.Snapshot().OwnedResourceCount);
        Assert.NotEqual(TunnelPhase.Connected, h.Engine.State.Phase);
        if (change == "disconnect") Assert.Equal(TunnelPhase.Disconnected, h.Engine.State.Phase);
    }

    [Fact]
    public async Task DuplicateFailureDuringReplacementDoesNotCreateAnotherProcess()
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        h.Processes[0].HoldStop = true;
        var first = h.Health(FailureKind.HealthTimeout);
        await h.Processes[0].StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.True((await h.Health(FailureKind.HealthTimeout)).Ok); Assert.Single(h.Processes); }
        finally { h.Processes[0].StopRelease.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, h.Processes.Count); Assert.Equal(1, h.PeakRunning);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("epoch")]
    [InlineData("policy")]
    public async Task SafetyPulseStopsOwnedRuntimeButRetainsProtection(string change)
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        switch (change)
        {
            case "consent": h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false }; break;
            case "epoch": h.Catalogue.SetNetworkEpoch(h.Catalogue.NetworkEpoch + 1); break;
            case "policy": h.Catalogue.Settings = h.Catalogue.Settings with { LanAccess = false }; break;
        }
        Assert.True(await h.Engine.EnforceSafetyAsync(default));
        Assert.Equal(1, h.Processes[0].Stops); Assert.Equal(0, h.Guard.Disarms);
        Assert.Equal(0, h.Engine.Snapshot().OwnedResourceCount);
        Assert.True(h.Engine.Snapshot().ProtectionArmed); Assert.False(h.Engine.Snapshot().CoreRunning);
        Assert.Equal(TunnelPhase.Blocked, h.Engine.State.Phase);
        Assert.True((await h.Disconnect()).Ok); Assert.Equal(1, h.Guard.Disarms);
        Assert.False(h.Engine.Snapshot().ProtectionArmed); Assert.Equal(2, h.Catalogue.Nodes.Count);
    }

    [Fact]
    public async Task SafetyCleanupFailureIsRetainedAndRetriedOnNextPulse()
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        h.Processes[0].StopFailures = 1;
        h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false };
        Assert.False(await h.Engine.EnforceSafetyAsync(default));
        Assert.Equal(1, h.Engine.Snapshot().OwnedResourceCount);
        Assert.True(await h.Engine.EnforceSafetyAsync(default));
        Assert.Equal(2, h.Processes[0].Stops); Assert.Equal(0, h.Engine.Snapshot().OwnedResourceCount);
        Assert.True(h.Engine.Snapshot().ProtectionArmed);
    }

    [Fact]
    public async Task MonitorRunsWithoutSnapshotPollingAndDisposeJoinsOwnedCleanup()
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        h.Processes[0].HoldStop = true;
        h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false };
        var monitor = new BrokerSafetyMonitor(h.Engine, TimeSpan.FromMilliseconds(20));
        await h.Processes[0].StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = monitor.DisposeAsync().AsTask();
        try { Assert.False(disposing.IsCompleted); }
        finally { h.Processes[0].StopRelease.TrySetResult(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(monitor.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, h.Processes[0].Stops); Assert.True(h.Engine.Snapshot().ProtectionArmed);
    }

    [Fact]
    public async Task CanceledSafetyWaitDoesNotDropAnAlreadyInvalidatedCleanup()
    {
        await using var h = new LiveHarness(); await h.StartConnected();
        var first = h.Processes[0]; first.HoldStop = true;
        h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false };
        using var cancellation = new CancellationTokenSource();
        var work = h.Engine.EnforceSafetyAsync(cancellation.Token);
        await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); Assert.False(work.IsCompleted); first.StopRelease.TrySetResult();
        Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(0, h.Engine.Snapshot().OwnedResourceCount);
    }

    [Fact]
    public async Task UnprotectedSessionSafetyFailureDoesNotInventProtection()
    {
        await using var h = new LiveHarness();
        h.Catalogue.Settings = h.Catalogue.Settings with { ProtectionOnConnect = false };
        await h.StartConnected(); h.Catalogue.Settings = h.Catalogue.Settings with { DisclosureAccepted = false };
        Assert.True(await h.Engine.EnforceSafetyAsync(default)); Assert.False(h.Engine.Snapshot().ProtectionArmed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(5001)]
    public void MonitorRejectsUnboundedOrExcessiveCadence(int ms)
    {
        var h = new LiveHarness();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerSafetyMonitor(h.Engine, TimeSpan.FromMilliseconds(ms)));
    }
}

internal sealed class LiveHarness : IAsyncDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    internal static readonly string TargetSet = new('a', 64);
    internal ICatalogue Catalogue { get; }
    internal TestGuard Guard { get; } = new();
    internal List<TestProcess> Processes { get; } = [];
    internal ConcurrentQueue<string> Events { get; } = new();
    internal OwnedCoreSupervisor Supervisor { get; }
    internal BrokerEngine Engine { get; }
    private int _running;
    internal int PeakRunning;
    internal LiveHarness(ICatalogue? catalogue = null)
    {
        Catalogue = catalogue ?? new MemoryCatalogue();
        if (catalogue is null) Populate(Catalogue);
        Supervisor = new OwnedCoreSupervisor(() => { var process = new TestProcess(this, Processes.Count); Processes.Add(process); return process; });
        Engine = new BrokerEngine(Catalogue, Guard, Supervisor, clock: new FakeClock { UtcNow = Now }, requiredTargetSetId: TargetSet);
    }
    internal static void Populate(ICatalogue catalogue)
    {
        catalogue.Settings = new ProductSettings { DisclosureAccepted = true };
        var nodes = Enumerable.Range(10, 2).Select(i => new NodeSemantics
        { Protocol = ProtocolKind.Vless, Host = "203.0.113." + i, Port = 443, UserId = "11111111-1111-4111-8111-111111111111", Security = "tls", Encryption = "none", Transport = "tcp" }).ToArray();
        catalogue.ApplySnapshot(new SnapshotCommit { ArtifactId = "synthetic", FamilyId = "black-vless", ContentHash = "fixture", Complete = true, NowUtc = Now,
            Nodes = nodes.Select(node => new SnapshotNode { Digest = CanonicalIdentity.Digest(node), Semantics = node, Label = "fixture", ArtifactId = "synthetic", FamilyId = "black-vless" }).ToArray() });
        foreach (var node in catalogue.Nodes)
            catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot { Digest = node.Digest, NetworkEpoch = catalogue.NetworkEpoch, Health = HealthState.Healthy, LastSuccessUtc = Now, MedianLatencyMs = 20, VerifiedTargetSetId = TargetSet });
    }
    internal Task<IpcResponse> Call(string op, object payload, CancellationToken token = default)
        => Engine.HandleAsync(new IpcRequest { ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = Guid.NewGuid().ToString("N"), ExpectedStateRevision = Engine.Snapshot().Revision,
            Operation = op, Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options) }, token);
    internal Task<IpcResponse> Connect()
    {
        var first = Catalogue.Nodes[0];
        return Call(IpcOperations.Connect, new ConnectPayload { NodeId = first.NodeId, Digest = first.Digest, NetworkEpoch = Catalogue.NetworkEpoch, ProtectionRequired = Catalogue.Settings.ProtectionOnConnect });
    }
    internal async Task StartConnected()
    {
        var started = await Connect(); Assert.True(started.Ok, started.ErrorCode);
        Confirm(); Assert.Equal(TunnelPhase.Connected, Engine.State.Phase);
        var node = Catalogue.Nodes[1];
        Assert.True((await Call(IpcOperations.ApplyRuntimeSet, new { standbys = new[] { new StandbyCandidate { NodeId = node.NodeId, EndpointKey = "not-trusted", Country = "", SourceFamilyId = "black-vless", FreshOnEpoch = true } } })).Ok);
    }
    internal void Confirm()
    {
        var s = Engine.Snapshot(); Engine.ConfirmProduction(s.BootId!, s.Generation, s.OperationId, s.ActiveNodeId, Catalogue.NetworkEpoch, true, null);
    }
    internal Task<IpcResponse> Health(FailureKind kind, CancellationToken token = default)
        => Call(IpcOperations.ReportHealth, new HealthPayload { FailureKind = kind.ToString(), ConsecutiveFailures = 3, NetworkEpoch = Catalogue.NetworkEpoch }, token);
    internal Task<IpcResponse> Disconnect() => Call(IpcOperations.Disconnect, new DisconnectPayload());
    public async ValueTask DisposeAsync()
    {
        foreach (var p in Processes) p.StopRelease.TrySetResult();
        if (!await Engine.ShutdownAsync(default)) Assert.True(await Engine.ShutdownAsync(default));
        await Supervisor.DisposeAsync();
    }
    internal sealed class TestGuard : INetworkGuard
    {
        internal int Disarms;
        public GuardResult Arm(GuardRequest request) => new(true, request.ProtectionRequired, null, []);
        public GuardResult Disarm(long generation) { Disarms++; return new(true, false, null, []); }
        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects) => new(true, false, null, []);
    }
    internal sealed class TestProcess(LiveHarness harness, int id) : IOwnedCoreProcess
    {
        private int _running;
        public bool IsRunning => Volatile.Read(ref _running) != 0;
        internal int Stops; internal int StopFailures; internal bool HoldStop;
        internal TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CoreStartResult> StartAsync(string yaml, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); harness.Events.Enqueue("start:" + id);
            Interlocked.Exchange(ref _running, 1); var active = Interlocked.Increment(ref harness._running);
            harness.PeakRunning = Math.Max(harness.PeakRunning, active); return Task.FromResult(new CoreStartResult(true, null));
        }
        internal void Crash() { if (Interlocked.Exchange(ref _running, 0) != 0) Interlocked.Decrement(ref harness._running); }
        public async Task StopAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Stops); harness.Events.Enqueue("stop:" + id); StopEntered.TrySetResult();
            if (HoldStop) await StopRelease.Task.WaitAsync(token);
            if (StopFailures-- > 0) throw new IOException("synthetic cleanup failure");
            Crash();
        }
    }
}
