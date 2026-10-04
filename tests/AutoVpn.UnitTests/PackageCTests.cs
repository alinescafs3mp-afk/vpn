using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class PackageCTests
{
    [Fact]
    public async Task PartialOversizedAndHeldPipeClientsDoNotBlockTheNextCommand()
    {
        var engine = new BrokerEngine(new MemoryCatalogue(), new UnavailableNetworkGuard(), new RefusingCoreController());
        var pipe = "autovpn-c-" + Guid.NewGuid().ToString("N");
        await using var server = LocalIpcServer.Start(pipe, new ProtocolTestDispatcher(), engine, Caller());
        await using var partial = await HoldAsync(pipe, [1, 2]);
        var partialWatch = Stopwatch.StartNew();
        var duringPartial = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
        partialWatch.Stop();
        Assert.NotNull(duringPartial);
        Assert.True(duringPartial!.Ok);
        Assert.True(partialWatch.Elapsed < TimeSpan.FromMilliseconds(1500));

        var huge = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(huge, ProductLimits.MaxIpcFrameBytes + 1);
        await using var oversized = await HoldAsync(pipe, huge);
        var oversizedWatch = Stopwatch.StartNew();
        var duringOversized = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
        oversizedWatch.Stop();
        Assert.NotNull(duringOversized);
        Assert.True(duringOversized!.Ok);
        Assert.True(oversizedWatch.Elapsed < TimeSpan.FromMilliseconds(1500));

        await using var held = await HoldFrameAsync(pipe, Request(IpcOperations.GetSnapshot, new { }, "held"));
        var heldWatch = Stopwatch.StartNew();
        var duringHeld = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
        heldWatch.Stop();
        Assert.NotNull(duringHeld);
        Assert.True(duringHeld!.Ok);
        Assert.True(heldWatch.Elapsed < TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task SecondPipeDisconnectsAStartBlockedOnTheCore()
    {
        var catalogue = FreshCatalogue();
        var core = new GateCore();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), core);
        var pipe = "autovpn-c-" + Guid.NewGuid().ToString("N");
        await using var server = LocalIpcServer.Start(pipe, new ProtocolTestDispatcher(), engine, Caller());
        var node = catalogue.Nodes[0];
        var connectTask = LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        await core.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disconnect = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.NotNull(disconnect);
        Assert.True(disconnect!.Ok);
        core.Release.TrySetResult();
        var connect = await connectTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(connect);
        Assert.False(connect!.Ok);
        Assert.Equal(ReasonCodes.Canceled, connect.ErrorCode);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
        Assert.False(engine.State.ProtectionArmed);
        Assert.True(core.Stops >= 1);
    }

    [Fact]
    public async Task ReplayReturnsTheOriginalResultAndAStaleConnectDoesNotStartAgain()
    {
        var catalogue = FreshCatalogue();
        var core = new CountingCore();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), core);
        var dispatcher = new ProtocolTestDispatcher();
        var node = catalogue.Nodes[0];
        var connect = Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, "connect-1");
        var first = dispatcher.Dispatch(connect, Caller(), request => engine.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        var second = dispatcher.Dispatch(connect, Caller(), _ => throw new InvalidOperationException("повтор не должен исполняться"));
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Snapshot!.OperationId, second.Snapshot!.OperationId);
        Assert.Equal(1, core.Starts);
        engine.ConfirmProduction(engine.BootId, first.Snapshot.Generation, first.Snapshot.OperationId, first.Snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);

        var recover = dispatcher.Dispatch(Request(IpcOperations.RecoverOwned, new { }, revision: engine.Snapshot().Revision), Caller(), request => engine.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal("RECOVERY_BLOCKED", recover.ErrorCode);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);

        var disconnect = dispatcher.Dispatch(
            Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision),
            Caller(),
            request => engine.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        Assert.True(disconnect.Ok);
        var replay = dispatcher.Dispatch(connect, Caller(), _ => throw new InvalidOperationException("старый connect не должен ожить"));
        Assert.Equal(first.Snapshot.OperationId, replay.Snapshot!.OperationId);
        Assert.Equal(1, core.Starts);
        var stale = dispatcher.Dispatch(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: 0), Caller(), request => engine.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(ReasonCodes.StaleRevision, stale.ErrorCode);
        Assert.Equal(1, core.Starts);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
    }

    [Fact]
    public async Task OldAttemptCannotConfirmOrStopTheNextCore()
    {
        var catalogue = FreshCatalogue();
        var core = new CountingCore();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), core);
        var node = catalogue.Nodes[0];
        var first = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        var oldGeneration = first.Snapshot!.Generation;
        var oldOperation = first.Snapshot.OperationId;
        engine.ConfirmProduction(engine.BootId, oldGeneration, oldOperation, first.Snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
        var disconnected = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(disconnected.Ok);
        var second = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }, revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(second.Ok);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        engine.ConfirmProduction("other-boot", second.Snapshot!.Generation, second.Snapshot.OperationId, second.Snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        engine.ConfirmProduction(engine.BootId, oldGeneration, oldOperation, node.NodeId, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connecting, engine.State.Phase);
        await core.StopAsync(oldGeneration, oldOperation!, CancellationToken.None);
        Assert.Equal(second.Snapshot.OperationId, core.RunningOperation);
        Assert.Equal(1, core.IgnoredStops);
        engine.ConfirmProduction(engine.BootId, second.Snapshot.Generation, second.Snapshot.OperationId, second.Snapshot.ActiveNodeId, catalogue.NetworkEpoch, true, null);
        Assert.Equal(TunnelPhase.Connected, engine.State.Phase);
    }

    [Fact]
    public async Task FailedStartKeepsProtectionArmed()
    {
        var catalogue = FreshCatalogue();
        var engine = new BrokerEngine(catalogue, new ArmingGuard(), new FailedCore());
        var node = catalogue.Nodes[0];
        var response = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Equal(ReasonCodes.CoreConfigRejected, response.ErrorCode);
        Assert.Equal(TunnelPhase.Blocked, engine.State.Phase);
        Assert.True(engine.State.ProtectionArmed);
    }

    [Fact]
    public async Task PipeCreationFailureStopsInsteadOfRetryingForever()
    {
        var opens = 0;
        var engine = new BrokerEngine(new MemoryCatalogue(), new UnavailableNetworkGuard(), new RefusingCoreController());
        var server = LocalIpcServer.Start("autovpn-c-fault", new ProtocolTestDispatcher(), engine, Caller(), _ =>
        {
            Interlocked.Increment(ref opens);
            throw new IOException("pipe denied");
        });
        await using (server)
        {
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(ProductLimits.IpcPipeCreateAttempts, opens);
        Assert.Equal(nameof(IOException), server.PipeFault);
    }

    private static async Task<NamedPipeClientStream> HoldAsync(string pipe, byte[] bytes)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        return client;
    }

    private static async Task<NamedPipeClientStream> HoldFrameAsync(string pipe, IpcRequest request)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        var frame = IpcFrames.Encode(request);
        await client.WriteAsync(frame);
        await client.FlushAsync();
        var header = new byte[4];
        await client.ReadExactlyAsync(header);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        var body = new byte[length];
        await client.ReadExactlyAsync(body);
        return client;
    }

    private static MemoryCatalogue FreshCatalogue()
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact
            {
                ArtifactId = "list",
                FamilyId = "black-vless",
                Enabled = true,
                Text = "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#fixture",
                ContentHash = "a",
            },
        ], DateTimeOffset.UtcNow, false);
        var node = catalogue.Nodes[0];
        catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
        {
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = DateTimeOffset.UtcNow,
            MedianLatencyMs = 40,
        });
        return catalogue;
    }

    private static CallerIdentity Caller()
    {
        return new CallerIdentity { Sid = "owner", SessionId = 1 };
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

    private sealed class GateCore : ICoreController
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops { get; private set; }

        public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = yaml;
            _ = generation;
            _ = operationId;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CoreStartResult(true, null);
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = generation;
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            Stops++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingCore : ICoreController
    {
        public int Starts { get; private set; }
        public int IgnoredStops { get; private set; }
        public long RunningGeneration { get; private set; }
        public string? RunningOperation { get; private set; }

        public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = yaml;
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            RunningGeneration = generation;
            RunningOperation = operationId;
            return Task.FromResult(new CoreStartResult(true, null));
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunningGeneration != generation || !string.Equals(RunningOperation, operationId, StringComparison.Ordinal))
            {
                IgnoredStops++;
                return Task.CompletedTask;
            }

            RunningGeneration = 0;
            RunningOperation = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FailedCore : ICoreController
    {
        public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = yaml;
            _ = generation;
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CoreStartResult(false, ReasonCodes.CoreConfigRejected));
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
