using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class RuntimeNodeEntryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static NodeSemantics Node(string password = "synthetic-secret") => new()
    {
        Protocol = ProtocolKind.Trojan, Host = "node.example", Port = 443, Security = "tls", Password = password,
    };

    [Fact]
    public async Task DuplicateSelectionsPrepareExactlyOnceAndCannotRebind()
    {
        var backend = new NodeBackend(); var factories = 0;
        await using var owner = new OwnedCoreSupervisor(() => { factories++; return backend; });
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            owner.StartNodeAsync(RuntimeNodeSelection.Create(Node()), 1, "owner", default)));
        Assert.All(results, result => Assert.True(result.Started));
        Assert.Equal(1, factories); Assert.Equal(1, backend.Starts);
        Assert.Equal("CORE_OWNER_REBOUND", (await owner.StartNodeAsync(
            RuntimeNodeSelection.Create(Node("other-secret")), 1, "owner", default)).ReasonCode);
        Assert.Equal("CORE_OWNER_REBOUND", (await owner.StartAsync("raw profile", 1, "owner", default)).ReasonCode);
        await owner.StopAsync(1, "owner", default);
        Assert.Equal(1, backend.Stops);
        Assert.Equal(ReasonCodes.Canceled, (await owner.StartNodeAsync(
            RuntimeNodeSelection.Create(Node()), 1, "owner", default)).ReasonCode);
    }

    [Fact]
    public async Task TypedDuplicateCancellationDoesNotRevokePreparation()
    {
        var backend = new NodeBackend { HoldStart = true };
        await using var owner = new OwnedCoreSupervisor(() => backend);
        var selection = RuntimeNodeSelection.Create(Node());
        var first = owner.StartNodeAsync(selection, 1, "owner", default);
        await backend.Entered.Task.WaitAsync(Limit);
        using var cancellation = new CancellationTokenSource();
        var duplicate = owner.StartNodeAsync(selection, 1, "owner", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);
        Assert.False(first.IsCompleted);
        backend.Release.TrySetResult();
        Assert.True((await first.WaitAsync(Limit)).Started);
        Assert.Equal(1, backend.Starts);
    }

    [Fact]
    public async Task StopCancelsAndJoinsTypedPreparationBeforeReleasingItsOwner()
    {
        var backend = new NodeBackend { HoldStart = true };
        await using var owner = new OwnedCoreSupervisor(() => backend);
        var selection = RuntimeNodeSelection.Create(Node());
        var start = owner.StartNodeAsync(selection, 1, "owner", default);
        await backend.Entered.Task.WaitAsync(Limit);
        await owner.StopAsync(1, "owner", default).WaitAsync(Limit);
        Assert.True(start.IsCompleted); Assert.False((await start).Started);
        Assert.Equal(1, backend.Stops); Assert.True(backend.StartFinished);
        Assert.Equal("Idle", owner.Snapshot().Phase);
        Assert.Equal(ReasonCodes.Canceled, (await owner.StartNodeAsync(selection, 1, "owner", default)).ReasonCode);
    }

    [Fact]
    public async Task RawBackendNeverReceivesAnUnvalidatedFallbackProfile()
    {
        var backend = new RawOnlyBackend();
        await using var owner = new OwnedCoreSupervisor(() => backend);
        var result = await owner.StartNodeAsync(RuntimeNodeSelection.Create(Node()), 1, "owner", default);
        Assert.Equal("CORE_NODE_RUNTIME_UNSUPPORTED", result.ReasonCode);
        Assert.Equal(0, backend.Starts); Assert.True(owner.Snapshot().CleanupPending);
        await owner.StopAsync(1, "owner", default);
        Assert.Equal(1, backend.Stops);
    }

    [Fact]
    public async Task RuntimeTypedAndRawEntryShareOneAdmissionFence()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        var selection = RuntimeNodeSelection.Create(Node());
        await runtime.StopAsync(default);
        Assert.Equal("CORE_CLOSING", (await runtime.StartNodeAsync(selection, default)).ReasonCode);
        Assert.Equal("CORE_CLOSING", (await runtime.StartAsync("[", default)).ReasonCode);
        Assert.Null(runtime.OwnedResources);
    }

    [Fact]
    public async Task AlreadyCanceledTypedAdmissionDoesNotAllocateAnOwner()
    {
        var factories = 0;
        await using var owner = new OwnedCoreSupervisor(() => { factories++; return new NodeBackend(); });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.StartNodeAsync(
            RuntimeNodeSelection.Create(Node()), 1, "owner", cancellation.Token));
        Assert.Equal(0, factories); Assert.Equal("Idle", owner.Snapshot().Phase);
        Assert.True((await owner.StartNodeAsync(RuntimeNodeSelection.Create(Node()), 1, "owner", default)).Started);
    }

    [LinuxOnlyFact]
    public async Task RuntimeStopCancelsDnsAndJoinsPreparationWithoutAllocatingPortsOrFiles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ProxyEndpointResolver(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return []; }
            finally { exited.TrySetResult(); }
        });
        var runtime = new MihomoRuntimeProcess(null, null, null, resolver);
        var start = runtime.StartNodeAsync(RuntimeNodeSelection.Create(Node()), default);
        try
        {
            await entered.Task.WaitAsync(Limit);
            await runtime.StopAsync(default).WaitAsync(Limit);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await exited.Task.WaitAsync(Limit);
            using var drain = new CancellationTokenSource(Limit);
            while (resolver.InFlight != 0) await Task.Delay(10, drain.Token);
            Assert.Equal(0, resolver.InFlight); Assert.Null(runtime.OwnedResources); Assert.False(runtime.IsRunning);
        }
        finally { await runtime.StopAsync(default); }
    }

    [LinuxOnlyFact]
    public async Task RuntimeStartupDeadlineIncludesDnsPreparation()
    {
        var resolver = new ProxyEndpointResolver(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return [];
        });
        var runtime = new MihomoRuntimeProcess(null, null, TimeSpan.FromMilliseconds(100), resolver);
        try
        {
            var result = await runtime.StartNodeAsync(RuntimeNodeSelection.Create(Node()), default).WaitAsync(Limit);
            Assert.Equal("CORE_START_TIMEOUT", result.ReasonCode);
            Assert.False(result.Started); Assert.Null(runtime.OwnedResources);
        }
        finally { await runtime.StopAsync(default); }
    }

    [LinuxOnlyFact]
    public async Task LateDnsCompletionKeepsCanceledStartupAndAdmissionSealed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ProxyEndpointResolver((_, _) => { entered.TrySetResult(); return answer.Task; });
        var runtime = new MihomoRuntimeProcess(null, null, null, resolver);
        var selection = RuntimeNodeSelection.Create(Node());
        var start = runtime.StartNodeAsync(selection, default);
        try
        {
            await entered.Task.WaitAsync(Limit);
            await runtime.StopAsync(default).WaitAsync(Limit);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Equal(1, resolver.InFlight); Assert.Null(runtime.OwnedResources);
            answer.TrySetResult([IPAddress.Parse("8.8.8.8")]);
            using var drain = new CancellationTokenSource(Limit);
            while (resolver.InFlight != 0) await Task.Delay(10, drain.Token);
            Assert.True(start.IsCanceled);
            Assert.Equal("CORE_CLOSING", (await runtime.StartNodeAsync(selection, default)).ReasonCode);
        }
        finally { answer.TrySetResult([IPAddress.Parse("8.8.8.8")]); await runtime.StopAsync(default); }
    }

    [AstraV3NativeFact] public Task NativeVlessNodeOwnsPortsAndStops() => RunNativeAsync("Vless");
    [AstraV3NativeFact] public Task NativeVmessNodeOwnsPortsAndStops() => RunNativeAsync("Vmess");
    [AstraV3NativeFact] public Task NativeTrojanNodeOwnsPortsAndStops() => RunNativeAsync("Trojan");
    [AstraV3NativeFact] public Task NativeShadowsocksNodeOwnsPortsAndStops() => RunNativeAsync("Shadowsocks");
    [AstraV3NativeFact] public Task NativeHysteria2NodeOwnsPortsAndStops() => RunNativeAsync("Hysteria2");
    [AstraV3NativeFact] public Task NativeTuicNodeOwnsPortsAndStops() => RunNativeAsync("Tuic");

    private static async Task RunNativeAsync(string protocol)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add("node-runtime");
        start.Environment["AUTOVPN_RUNTIME_LAB_PROTOCOL"] = protocol;
        start.Environment["R6_CORE_HASH"] = TestCorePins.ExpectedHash;
        start.Environment.Remove("AUTOVPN_RUNTIME_LAB_START_GATE");
        using var child = Process.Start(start)!;
        var output = new StringBuilder(); var errors = new StringBuilder();
        var stdout = ProbeOutputDrain.ReadAsync(child.StandardOutput, CancellationToken.None, output);
        var stderr = ProbeOutputDrain.ReadAsync(child.StandardError, CancellationToken.None, errors);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var drains = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(drains, result => Assert.Equal(ProbeOutputEnd.Eof, result.End));
            Assert.Equal(output.Length, drains[0].Characters);
            Assert.True(child.ExitCode == 0, output.ToString() + errors);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
            var cases = json.RootElement.GetProperty("cases"); Assert.Equal(1, cases.GetArrayLength());
            var result = cases[0]; Assert.Equal(protocol, result.GetProperty("protocol").GetString());
            foreach (var key in new[] { "passed", "ownedPorts", "authenticatedVersion", "stringsPreserved",
                "processExited", "directoryRemoved", "portsReleased", "admissionSealed" })
                Assert.True(result.GetProperty(key).GetBoolean(), key);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class NodeBackend : IOwnedNodeCoreProcess
    {
        public bool IsRunning { get; private set; }
        public bool HoldStart { get; init; }
        public int Starts;
        public int Stops;
        public bool StartFinished;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CoreStartResult> StartAsync(string yaml, CancellationToken token) =>
            throw new InvalidOperationException("Raw fallback must not be used.");
        public async Task<CoreStartResult> StartNodeAsync(RuntimeNodeSelection selection, CancellationToken token)
        {
            Interlocked.Increment(ref Starts); Entered.TrySetResult();
            try
            {
                if (HoldStart) await Release.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested(); IsRunning = true; return new(true, null);
            }
            finally { StartFinished = true; }
        }
        public Task StopAsync(CancellationToken token)
        {
            Assert.True(StartFinished); Interlocked.Increment(ref Stops); IsRunning = false; return Task.CompletedTask;
        }
    }

    private sealed class RawOnlyBackend : IOwnedCoreProcess
    {
        public bool IsRunning => false;
        public int Starts;
        public int Stops;
        public Task<CoreStartResult> StartAsync(string yaml, CancellationToken token)
        { Starts++; return Task.FromResult(new CoreStartResult(false, "unexpected")); }
        public Task StopAsync(CancellationToken token) { Stops++; return Task.CompletedTask; }
    }
}
