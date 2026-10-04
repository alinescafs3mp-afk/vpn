using System.Diagnostics;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class AstraR1ContractTests
{
    private static readonly CallerIdentity Owner = new() { Sid = "test-owner", SessionId = 7 };
    private static IpcRequest Request(string id, string operation, string? lease = null, long sequence = 0)
        => new() { ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = id,
            Operation = operation, SessionToken = lease, CommandSequence = sequence,
            Payload = JsonSerializer.SerializeToElement(new { }) };
    private static IpcResponse Success(IpcRequest r) => new() { RequestId = r.RequestId, Ok = true };
    private static string Open(IpcDispatcher d) => d.Dispatch(Request("open-" + Guid.NewGuid(), IpcOperations.OpenSession), Owner, Success).SessionToken!;

    [Fact]
    public void WireRequiresCurrentLeaseAndRejectsVersionOne()
    {
        var d = new IpcDispatcher(); var effects = 0;
        IpcResponse Run(IpcRequest r) { effects++; return Success(r); }
        Assert.Equal("SESSION_REQUIRED", d.Dispatch(Request("no-session", IpcOperations.Connect), Owner, Run).ErrorCode);
        var lease = Open(d);
        Assert.Equal("PROTOCOL", d.Dispatch(Request("v1", IpcOperations.Connect, lease, 1) with { ProtocolVersion = 1 }, Owner, Run).ErrorCode);
        Assert.Equal(0, effects);
        Assert.True(d.Dispatch(Request("valid", IpcOperations.Connect, lease, 1), Owner, Run).Ok);
        d.ResetOwner();
        Assert.Equal("SESSION_REQUIRED", d.Dispatch(Request("old", IpcOperations.Disconnect, lease, 2), Owner, Run).ErrorCode);
        Assert.Equal(1, effects);
    }

    [Fact]
    public void ExactWindowRejectsConsumedSequenceEvenWithNewRequestId()
    {
        var d = new IpcDispatcher(); var lease = Open(d); var effects = 0;
        IpcResponse Run(IpcRequest r) { effects++; return Success(r); }
        Assert.True(d.Dispatch(Request("later", IpcOperations.Connect, lease, 2), Owner, Run).Ok);
        Assert.True(d.Dispatch(Request("earlier", IpcOperations.Connect, lease, 1), Owner, Run).Ok);
        Assert.Equal(ReasonCodes.ReplayExpired, d.Dispatch(Request("different-id", IpcOperations.Disconnect, lease, 1), Owner, Run).ErrorCode);
        Assert.Equal(2, effects);
    }

    [Fact]
    public void LeaseCannotBeRotatedByAnotherOwner()
    {
        var d = new IpcDispatcher(); var lease = Open(d);
        var other = new CallerIdentity { Sid = "different-owner", SessionId = 7 };
        Assert.Equal("OWNER", d.Dispatch(Request("steal", IpcOperations.OpenSession), other, Success).ErrorCode);
        Assert.True(d.Dispatch(Request("safe", IpcOperations.Disconnect, lease, 1), Owner, Success).Ok);
    }

    [Fact]
    public async Task PendingMutationsDoNotConsumeThreadsOrSafetyCapacity()
    {
        var d = new IpcDispatcher(); var lease = Open(d);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(1, 64).Select(i => d.DispatchAsync(Request("mut-" + i, IpcOperations.Connect, lease, i), Owner,
            async r => { await release.Task; return Success(r); })).ToArray();
        try
        {
            var safety = await d.DispatchAsync(Request("safety", IpcOperations.Disconnect, lease, 65), Owner, r => Task.FromResult(Success(r)))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(safety.Ok);
            Assert.Equal("BUSY", (await d.DispatchAsync(Request("extra", IpcOperations.Connect, lease, 66), Owner, r => Task.FromResult(Success(r)))).ErrorCode);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public void RetiredSafetyIsRejectedAndFreshSafetyAlwaysAdvances()
    {
        var d = new IpcDispatcher(); var lease = Open(d); var effects = 0;
        IpcResponse Run(IpcRequest r) { effects++; return Success(r); }
        var first = Request("first", IpcOperations.Disconnect, lease, 1);
        Assert.True(d.Dispatch(first, Owner, Run).Ok);
        for (var i = 2; i <= 100_001; i++)
            Assert.True(d.Dispatch(Request("request-" + i, IpcOperations.ReportHealth, lease, i), Owner, Run).Ok);
        // Evict the first response from the separately reserved safety-result cache.
        for (var i = 100_002; i <= 100_065; i++)
            Assert.True(d.Dispatch(Request("safety-" + i, IpcOperations.Disconnect, lease, i), Owner, Run).Ok);
        Assert.Equal(ReasonCodes.ReplayExpired, d.Dispatch(first, Owner, Run).ErrorCode);
        Assert.True(d.Dispatch(Request("fresh-safety", IpcOperations.Disconnect, lease, 100_066), Owner, Run).Ok);
        Assert.Equal(100_066, effects);
    }

    [Fact]
    public void EmptySnapshotSurvivesSqliteRestartAndRequiresTheCorrectHash()
    {
        var root = Directory.CreateTempSubdirectory("autovpn-astra-empty-");
        try
        {
            var path = Path.Combine(root.FullName, "catalogue.sqlite");
            using (var c = SqliteCatalogue.Open(path, new PassthroughSecretProtector()))
            {
                c.ApplySnapshot(new SnapshotCommit { ArtifactId = "empty", FamilyId = "family", ContentHash = "hash-v1", Complete = true,
                    NowUtc = DateTimeOffset.UtcNow, Nodes = [] });
                Assert.True(c.HasCommittedSnapshot("empty", "hash-v1"));
            }
            using var reopened = SqliteCatalogue.Open(path, new PassthroughSecretProtector());
            Assert.True(reopened.HasCommittedSnapshot("empty", "hash-v1"));
            Assert.False(reopened.HasCommittedSnapshot("empty", "wrong-hash"));
            Assert.False(reopened.HasCommittedSnapshot("missing", "hash-v1"));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnboundTransportCannotPublishEvenWithPlausibleProofStrings()
    {
        var c = new MemoryCatalogue();
        var link = "vless://11111111-1111-4111-8111-111111111111@203.0.113.22:443?security=tls&sni=example.com";
        RefreshMerge.Ingest(c, [new IngestArtifact { ArtifactId = "source", FamilyId = "family", Enabled = true, Text = link }], DateTimeOffset.UtcNow, false);
        var prior = c.Nodes.Single().Assessment;
        var outcome = await ProbeCoordinator.RunAsync(c, new Unbound(), new Uri("https://probe.example/generate_204"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, outcome.Succeeded);
        Assert.Equal(prior, c.Nodes.Single().Assessment);
    }

    [Fact]
    public async Task OwnedManagedChildFloodsBothPipesAndCanBeStoppedWithoutSiblingKill()
    {
        var firstDir = Directory.CreateTempSubdirectory("autovpn-astra-flood-");
        var secondDir = Directory.CreateTempSubdirectory("autovpn-astra-sibling-");
        var first = await ProbeWorker.StartAsync(Child("flood"), null, TimeSpan.FromSeconds(3), CancellationToken.None, firstDir.FullName);
        var second = await ProbeWorker.StartAsync(Child("sleep"), null, TimeSpan.FromSeconds(3), CancellationToken.None, secondDir.FullName);
        try
        {
            Assert.True(first.Ready); Assert.True(second.Ready);
            await Task.Delay(600);
            await first.DisposeAsync();
            Assert.True(first.DirectoryRemoved);
            Assert.True(first.OutputBytes > 8192);
            Assert.InRange(first.OutputTail.Length, 0, 2000);
            Assert.True(Directory.Exists(secondDir.FullName));
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
        Assert.False(Directory.Exists(firstDir.FullName)); Assert.False(Directory.Exists(secondDir.FullName));
    }

    private static ProcessStartInfo Child(string mode)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll");
        Assert.True(File.Exists(dll), "Build must include the managed child fixture.");
        var host = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(dll); info.ArgumentList.Add(mode); return info;
    }

    private sealed class Unbound : IProbeTransport
    {
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken token)
            => Task.FromResult(new ProbeObservation(true, 12, false, null, 100, ProbeClass.Success, target.AbsoluteUri, CanonicalIdentity.Digest(node), "plausible-but-unbound"));
    }
}
