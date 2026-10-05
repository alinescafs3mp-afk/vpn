using System.Buffers.Binary;
using System.IO.Pipes;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.UnitTests;

// Real local pipe transport on each OS. Authorization is injected here;
// only the installed Windows ServiceLab can prove the SYSTEM/user boundary.
public sealed class AstraV3FPipeCompletionTests
{
    [Theory]
    [InlineData("GetStatus")]
    [InlineData("Connect")]
    public async Task ReplyRemainsAvailableUntilClientClosesAndServerCanReuseInstance(string operation)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var fixture = await PipeFixture.ConnectAsync(budget.Token);
        var handling = fixture.HandleAsync(budget.Token);
        var request = Request(operation);
        await fixture.Client.WriteAsync(ServiceStatusFrames.Encode(request), budget.Token);
        await fixture.Observed.Flushed.Task.WaitAsync(budget.Token);
        // The writer is deliberately ahead of the reader. Disconnect is not allowed yet.
        Assert.False(handling.IsCompleted);
        var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(fixture.Client, budget.Token);
        Assert.Equal(request.RequestId, reply.RequestId);
        Assert.Equal(operation == "GetStatus", reply.Ok);
        Assert.Equal(operation == "GetStatus" ? null : "OPERATION_NOT_SUPPORTED", reply.ErrorCode);
        Assert.False(reply.CanConnect || reply.CoreRunning || reply.ProtectionArmed);
        Assert.False(handling.IsCompleted);
        await fixture.Client.DisposeAsync();
        Assert.True(await handling.WaitAsync(budget.Token));
        fixture.Server.Disconnect();

        // Keep the same server instance. The preceding client must not poison the next exchange.
        using var next = new NamedPipeClientStream(".", fixture.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = fixture.Server.WaitForConnectionAsync(budget.Token);
        await next.ConnectAsync(budget.Token); await accept;
        var second = ServiceStatusExchange.HandleConnectionAsync(fixture.Server, () => true,
            Guid.NewGuid().ToString("N"), Environment.ProcessId, () => 1, budget.Token);
        var nextRequest = Request("GetStatus");
        await next.WriteAsync(ServiceStatusFrames.Encode(nextRequest), budget.Token);
        var nextReply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(next, budget.Token);
        Assert.Equal(nextRequest.RequestId, nextReply.RequestId);
        await next.DisposeAsync();
        Assert.True(await second.WaitAsync(budget.Token));
    }

    [Fact]
    public async Task ClientHoldingResponseOpenIsCancelledWithoutWaitingOnAnUnboundedDrain()
    {
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(outer.Token);
        await using var fixture = await PipeFixture.ConnectAsync(outer.Token);
        var handling = fixture.HandleAsync(budget.Token);
        await fixture.Client.WriteAsync(ServiceStatusFrames.Encode(Request("GetStatus")), outer.Token);
        await fixture.Observed.Flushed.Task.WaitAsync(outer.Token);
        Assert.False(handling.IsCompleted);
        await budget.CancelAsync();
        Assert.False(await handling.WaitAsync(outer.Token));
        fixture.Server.Disconnect();
        Assert.False(fixture.Server.IsConnected);
    }

    [Fact]
    public async Task ClientCannotPipelineAnotherRequestAfterTheReply()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var fixture = await PipeFixture.ConnectAsync(budget.Token);
        var handling = fixture.HandleAsync(budget.Token);
        await fixture.Client.WriteAsync(ServiceStatusFrames.Encode(Request("GetStatus")), budget.Token);
        await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(fixture.Client, budget.Token);
        await fixture.Client.WriteAsync(new byte[] { 1 }, budget.Token);
        Assert.False(await handling.WaitAsync(budget.Token));
        fixture.Server.Disconnect();
    }

    [Fact]
    public async Task MalformedFrameDoesNotStartAClientCloseWaitOrCallAuthorization()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var fixture = await PipeFixture.ConnectAsync(budget.Token);
        var authorized = false;
        var handling = fixture.HandleAsync(budget.Token, () => { authorized = true; return true; });
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, InstalledServiceProtocol.MaxFrameBytes + 1);
        await fixture.Client.WriteAsync(frame, budget.Token);
        Assert.False(await handling.WaitAsync(budget.Token));
        Assert.False(authorized);
        Assert.False(fixture.Observed.Flushed.Task.IsCompleted);
    }

    [Fact]
    public async Task UnauthorizedClientGetsNoReplyAndDoesNotStartAClientCloseWait()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var fixture = await PipeFixture.ConnectAsync(budget.Token);
        var handling = fixture.HandleAsync(budget.Token, () => false);
        await fixture.Client.WriteAsync(ServiceStatusFrames.Encode(Request("GetStatus")), budget.Token);
        Assert.False(await handling.WaitAsync(budget.Token));
        Assert.False(fixture.Observed.Flushed.Task.IsCompleted);
    }

    private static ServiceStatusRequest Request(string operation) => new()
        { ProtocolVersion = InstalledServiceProtocol.Version, RequestId = Guid.NewGuid().ToString("N"), Operation = operation };

    private sealed class PipeFixture(string name, NamedPipeServerStream server, NamedPipeClientStream client) : IAsyncDisposable
    {
        public string Name { get; } = name;
        public NamedPipeServerStream Server { get; } = server;
        public NamedPipeClientStream Client { get; } = client;
        public ObservedStream Observed { get; } = new(server);
        public Task<bool> HandleAsync(CancellationToken token, Func<bool>? auth = null) =>
            ServiceStatusExchange.HandleConnectionAsync(Observed, auth ?? (() => true),
                Guid.NewGuid().ToString("N"), Environment.ProcessId, () => 0, token);
        public static async Task<PipeFixture> ConnectAsync(CancellationToken token)
        {
            var name = "AutoVPN-local-test-" + Guid.NewGuid().ToString("N");
            // Match the installed server. A zero-buffer Windows fixture requires a peer
            // read to finish its write, which contradicts this test's writer-first barrier.
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, InstalledServiceProtocol.MaxFrameBytes, InstalledServiceProtocol.MaxFrameBytes);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var accept = server.WaitForConnectionAsync(token);
                await client.ConnectAsync(token); await accept;
                return new(name, server, client);
            }
            catch { client.Dispose(); server.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Server.DisposeAsync(); }
    }

    private sealed class ObservedStream(Stream inner) : Stream
    {
        public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task FlushAsync(CancellationToken token)
        { await inner.FlushAsync(token); Flushed.TrySetResult(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
