using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3DReadinessSafetyTests
{
    [Theory]
    [InlineData("echo")]
    [InlineData("zero")]
    [InlineData("malformed")]
    [InlineData("eof")]
    public async Task FalseSocksSuccessCannotForgeDispatchProof(string behavior)
    {
        await using var ready = new LoopbackCoreReadiness();
        await using var proxy = new LocalSocksFixture(ready.Port, behavior);
        Assert.False(await ready.WaitAsync(proxy.Port, () => true, TimeSpan.FromMilliseconds(250), CancellationToken.None));
        Assert.Equal(0, ready.Responses);
    }

    [Fact]
    public async Task ValidProofStillRequiresOwnershipAtCompletion()
    {
        await using var ready = new LoopbackCoreReadiness();
        await using var proxy = new LocalSocksFixture(ready.Port, "relay");
        var ownershipChecks = 0;
        Assert.False(await ready.WaitAsync(proxy.Port, () => Interlocked.Increment(ref ownershipChecks) == 1,
            TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.True(ready.Responses > 0);
        Assert.Equal(2, ownershipChecks);
    }

    [Fact]
    public async Task CancelDuringSilentResponseDoesNotBecomeReadiness()
    {
        await using var ready = new LoopbackCoreReadiness();
        await using var proxy = new LocalSocksFixture(ready.Port, "silent");
        using var cancellation = new CancellationTokenSource();
        var wait = ready.WaitAsync(proxy.Port, () => true, TimeSpan.FromSeconds(3), cancellation.Token);
        await proxy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task DisposeCancelsPendingReadinessAndIsIdempotent()
    {
        var ready = new LoopbackCoreReadiness();
        await using var proxy = new LocalSocksFixture(ready.Port, "silent");
        try
        {
            var wait = ready.WaitAsync(proxy.Port, () => true, TimeSpan.FromSeconds(3), CancellationToken.None);
            await proxy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(ready.DisposeAsync().AsTask(), ready.DisposeAsync().AsTask());
            Assert.False(await wait);
            Assert.False(await ready.WaitAsync(proxy.Port, () => true, TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.Throws<ObjectDisposedException>(() => ready.AddToProfile("rules:\n  - MATCH,REJECT\n"));
        }
        finally { await ready.DisposeAsync(); }
    }

    [Fact]
    public async Task AcceptedConnectionDoesNotStandInForListeningSocket()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            Assert.True(ProbeWorker.ProcessOwnsLoopbackPort(Environment.ProcessId, port));
            listener.Stop();
            // The process still owns an established socket with this local port.
            Assert.False(ProbeWorker.ProcessOwnsLoopbackPort(Environment.ProcessId, port));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task IncompleteRulesHeaderIsRejectedWithoutIndexFailure()
    {
        await using var ready = new LoopbackCoreReadiness();
        Assert.Throws<InvalidOperationException>(() => ready.AddToProfile("mode: rule\nrules:"));
    }

    private sealed class LocalSocksFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly int _target;
        private readonly string _behavior;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port { get; }
        public LocalSocksFixture(int target, string behavior)
        {
            _target = target; _behavior = behavior;
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = ServeAsync();
        }
        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    await stream.ReadExactlyAsync(new byte[3], _stop.Token);
                    Entered.TrySetResult();
                    if (_behavior == "silent") { await Task.Delay(Timeout.Infinite, _stop.Token); }
                    if (_behavior == "eof") continue;
                    await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                    var request = new byte[10];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    if (request[3] != 1 || request[4] != 127 || request[5] != 0 || request[6] != 0 || request[7] != 1
                        || BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)) != _target)
                        throw new IOException("Unexpected non-loopback readiness request.");
                    var reply = new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 };
                    if (_behavior == "malformed") reply[0] = 4;
                    await stream.WriteAsync(reply, _stop.Token);
                    if (_behavior == "malformed") continue;
                    var challenge = new byte[32];
                    await stream.ReadExactlyAsync(challenge, _stop.Token);
                    var proof = new byte[32];
                    if (_behavior == "echo") challenge.CopyTo(proof, 0);
                    if (_behavior == "relay")
                    {
                        using var target = new TcpClient();
                        await target.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
                        await using var remote = target.GetStream();
                        await remote.WriteAsync(challenge, _stop.Token);
                        await remote.ReadExactlyAsync(proof, _stop.Token);
                    }
                    await stream.WriteAsync(proof, _stop.Token);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
                {
                    // Each task and accepted connection belongs to this fixture.
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            // Do not close the listening socket concurrently with AcceptAsync.
            try { await _server; }
            finally { _listener.Stop(); _stop.Dispose(); }
        }
    }
}
