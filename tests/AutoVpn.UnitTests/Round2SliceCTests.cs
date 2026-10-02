using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class Round2SliceCTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task Rt18SaturatedPipesRecoverAndSessionsStayBounded()
    {
        var pipe = "autovpn-rt18-" + Guid.NewGuid().ToString("N");
        var engine = new BrokerEngine(new MemoryCatalogue(), new UnavailableNetworkGuard(), new RefusingCoreController());
        var server = LocalIpcServer.Start(pipe, new IpcDispatcher(), engine, new CallerIdentity { Sid = "owner", SessionId = 1 });
        var holders = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < ProductLimits.IpcPipeInstances; index++)
            {
                var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(3000);
                await client.WriteAsync(new byte[] { 1 });
                await client.FlushAsync();
                holders.Add(client);
            }

            await Task.Delay(200);
            Assert.Equal(ProductLimits.IpcPipeInstances, server.ActiveStreamCount);
            Assert.Null(server.PipeFault);
            await Task.Delay(250);
            Assert.Equal(ProductLimits.IpcPipeInstances, server.ActiveStreamCount);
            Assert.Null(server.PipeFault);

            await holders[0].DisposeAsync();
            holders.RemoveAt(0);
            var recovered = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.True(recovered.Ok);
            Assert.Null(server.PipeFault);
            Assert.False(server.Completion.IsCompleted);

            for (var index = 0; index < 12; index++)
            {
                var response = await LocalIpcServer.RoundTripAsync(pipe, Request(IpcOperations.GetSnapshot, new { }), CancellationToken.None);
                Assert.NotNull(response);
                Assert.True(response.Ok);
            }

            await Task.Delay(100);
            Assert.InRange(server.RetainedSessionCount, 0, ProductLimits.IpcPipeInstances);

            var dispose = server.DisposeAsync().AsTask();
            var finished = await Task.WhenAny(dispose, Task.Delay(3000));
            Assert.Same(dispose, finished);
            await dispose;
        }
        finally
        {
            foreach (var holder in holders)
            {
                await holder.DisposeAsync();
            }

            if (!server.Completion.IsCompleted)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Rt20FailedCleanupRetriesTheSameOwnedHandles()
    {
        var catalogue = Catalogue();
        var guard = new RetryGuard { DisarmFailures = 1 };
        var core = new FlakyCore { FailuresLeft = 1 };
        var engine = new BrokerEngine(catalogue, guard, core);
        var node = catalogue.Nodes[0];
        var connected = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            NetworkEpoch = catalogue.NetworkEpoch,
        }), CancellationToken.None);
        Assert.True(connected.Ok);
        var operationId = connected.Snapshot!.OperationId!;
        var armed = connected.Snapshot.Generation;
        Assert.False(string.IsNullOrEmpty(operationId));

        var first = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: connected.Snapshot.Revision), CancellationToken.None);
        Assert.False(first.Ok);
        Assert.Equal("CLEANUP_UNCERTAIN", first.ErrorCode);
        Assert.Equal(TunnelPhase.RestoringNetwork, engine.State.Phase);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal(node.NodeId, catalogue.Nodes.Single(item => item.ActiveSession).NodeId);
        Assert.Empty(guard.Disarms);
        Assert.Equal([(armed, operationId)], core.Stops);

        var second = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.False(second.Ok);
        Assert.Equal("CLEANUP_UNCERTAIN", second.ErrorCode);
        Assert.Equal(TunnelPhase.RestoringNetwork, engine.State.Phase);
        Assert.True(engine.State.ProtectionArmed);
        Assert.Equal([armed], guard.Disarms);
        Assert.All(core.Stops, stop => Assert.Equal((armed, operationId), stop));

        var third = await engine.HandleAsync(Request(IpcOperations.Disconnect, new DisconnectPayload(), revision: engine.Snapshot().Revision), CancellationToken.None);
        Assert.True(third.Ok);
        Assert.Equal(TunnelPhase.Disconnected, engine.State.Phase);
        Assert.False(engine.State.ProtectionArmed);
        Assert.DoesNotContain(catalogue.Nodes, item => item.ActiveSession);
        Assert.Equal([armed, armed], guard.Disarms);
        Assert.All(core.Stops, stop => Assert.Equal((armed, operationId), stop));
        Assert.Equal(armed + 1, engine.State.Generation);
    }

    [Fact]
    public async Task Rt20RecoveryInProgressCannotArmANewGuard()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt20-");
        try
        {
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            var journal = EffectJournal.Open(path);
            journal.Record(new OwnedEffect("owned-route", "route", "synthetic"));
            var catalogue = Catalogue();
            var guard = new BlockingGuard();
            var engine = new BrokerEngine(catalogue, guard, new FlakyCore(), journal);
            var node = catalogue.Nodes[0];
            var recoverTask = Task.Run(() => engine.HandleAsync(Request(IpcOperations.RecoverOwned, new { }), CancellationToken.None));
            await guard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var connect = await engine.HandleAsync(Request(IpcOperations.Connect, new ConnectPayload
            {
                NodeId = node.NodeId,
                Digest = node.Digest,
                NetworkEpoch = catalogue.NetworkEpoch,
            }), CancellationToken.None);
            Assert.False(connect.Ok);
            Assert.Equal("RECOVERY_BLOCKED", connect.ErrorCode);
            Assert.Equal(-1, guard.Armed);
            guard.Release.TrySetResult();
            var recover = await recoverTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(recover.Ok);
            Assert.Equal(-1, guard.Armed);
            Assert.Equal(-1, guard.Disarmed);
            journal.Dispose();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rt24MultipleXrayRecordsAndClashJsonKeepTheirEndpoints()
    {
        var xray = """
            {
              "outbounds": [
                {
                  "protocol": "vless",
                  "settings": {"vnext": [
                    {"address": "203.0.113.71", "port": 443, "users": [{"id": "11111111-1111-4111-8111-111111111111", "encryption": "none"}]},
                    {"address": "203.0.113.72", "port": 8443, "users": [{"id": "11111111-1111-4111-8111-111111111111", "encryption": "none"}]}
                  ]},
                  "streamSettings": {"network": "tcp", "security": "tls", "tlsSettings": {"serverName": "www.example.com"}}
                },
                {
                  "protocol": "shadowsocks",
                  "settings": {"servers": [
                    {"address": "203.0.113.81", "port": 443, "method": "aes-256-gcm", "password": "one"},
                    {"address": "203.0.113.82", "port": 8443, "method": "aes-256-gcm", "password": "two"}
                  ]}
                }
              ]
            }
            """;
        var exported = SubscriptionImporter.Import(xray);
        var hosts = exported.Records.Select(record => record.Semantics?.Host).Where(host => host is not null).ToArray();
        Assert.Contains("203.0.113.71", hosts);
        Assert.Contains("203.0.113.72", hosts);
        Assert.Contains("203.0.113.81", hosts);
        Assert.Contains("203.0.113.82", hosts);

        var clash = """
            {"proxies":[{"name":"a","type":"vless","server":"203.0.113.40","port":443,"uuid":"11111111-1111-4111-8111-111111111111","tls":true,"servername":"www.example.com","network":"tcp"}]}
            """;
        var parsed = SubscriptionImporter.Import(clash);
        Assert.Equal(RecordDisposition.Pending, parsed.Records[0].Disposition);
        var clashNode = parsed.Records[0].Semantics;
        Assert.NotNull(clashNode);
        Assert.Equal("203.0.113.40", clashNode.Host);
        Assert.Equal("tls", clashNode.Security);
    }

    [Fact]
    public void Rt26LargeBase64CrlfAndNullLedgerStayContained()
    {
        var lines = new List<string>();
        for (var index = 0; index < 500; index++)
        {
            lines.Add($"vless://{Uuid}@203.0.113.10:{10000 + index}?encryption=none&security=tls&type=tcp&sni=www.example.com#n{index}");
        }

        var wrapped = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        Assert.True(Encoding.UTF8.GetByteCount(wrapped) > ProductLimits.MaxLineBytes);
        Assert.True(Encoding.UTF8.GetByteCount(wrapped) < ProductLimits.MaxArtifactBytes);
        var batch = SubscriptionImporter.Import(wrapped);
        Assert.Equal(500, batch.Pending);
        Assert.DoesNotContain(batch.Records, record => record.ReasonCode == ReasonCodes.SizeLimit);

        var crlf = Convert.ToBase64String(Encoding.UTF8.GetBytes(lines[0] + "\r\n" + lines[1]));
        var pair = SubscriptionImporter.Import(crlf);
        Assert.Equal(2, pair.Pending);

        var oversized = SubscriptionImporter.Import(new string('A', ProductLimits.MaxLineBytes + 1));
        Assert.Equal(ReasonCodes.SizeLimit, oversized.Records[0].ReasonCode);

        var directory = Directory.CreateTempSubdirectory("autovpn-rt26-");
        try
        {
            var path = Path.Combine(directory.FullName, "ledger.json");
            File.WriteAllText(path, "[null,{\"Url\":\"https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/pin\",\"Etag\":\"keep\"}]");
            var ledger = SourceLedger.Load(path);
            Assert.Single(ledger.Entries);
            Assert.Equal("keep", ledger.EtagFor("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/pin"));
            Assert.True(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static MemoryCatalogue Catalogue()
    {
        var catalogue = new MemoryCatalogue();
        catalogue.Settings = catalogue.Settings with { DisclosureAccepted = true };
        RefreshMerge.Ingest(catalogue, [
            new IngestArtifact
            {
                ArtifactId = "list",
                FamilyId = "black-vless",
                Enabled = true,
                Text = $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#fixture",
                ContentHash = "a",
            },
        ], DateTimeOffset.UnixEpoch, false);
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

    private static IpcRequest Request(string operation, object payload, long revision = 0)
    {
        return new IpcRequest
        {
            ProtocolVersion = ProductLimits.IpcProtocolVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            ExpectedStateRevision = revision,
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
        };
    }

    private sealed class FlakyCore : ICoreController
    {
        public List<(long Generation, string Operation)> Stops { get; } = [];
        public int FailuresLeft { get; set; }

        public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
        {
            _ = yaml;
            _ = generation;
            _ = operationId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CoreStartResult(true, null));
        }

        public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stops.Add((generation, operationId!));
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new IOException("stop failed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RetryGuard : INetworkGuard
    {
        public List<long> Disarms { get; } = [];
        public int DisarmFailures { get; set; }

        public GuardResult Arm(GuardRequest request)
        {
            _ = request;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            Disarms.Add(generation);
            if (DisarmFailures > 0)
            {
                DisarmFailures--;
                return new GuardResult(false, true, "CLEANUP_UNCERTAIN", []);
            }

            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            return new GuardResult(true, false, null, effects.Select(effect => effect.Id).ToArray());
        }
    }

    private sealed class BlockingGuard : INetworkGuard
    {
        public long Armed { get; private set; } = -1;
        public long Disarmed { get; private set; } = -1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GuardResult Arm(GuardRequest request)
        {
            Armed = request.Generation;
            return new GuardResult(true, true, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            Disarmed = generation;
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            _ = effects;
            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(5));
            return new GuardResult(false, true, "STILL_OWNED", []);
        }
    }
}
