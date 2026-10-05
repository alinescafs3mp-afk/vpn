using System.IO.Pipes;
using System.Runtime.Versioning;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.UnitTests;

public sealed class AstraV3FReplyLifetimeTests
{
    private static ServiceStatusRequest Request() => new()
        { ProtocolVersion = 1, RequestId = Guid.NewGuid().ToString("N"), Operation = "Connect" };
    private static Task<bool> Serve(Stream stream, CancellationToken token, bool authorized = true) =>
        ServiceStatusExchange.HandleConnectionAsync(stream, () => authorized,
            Guid.NewGuid().ToString("N"), 123, () => 0, token);

    [Fact]
    public async Task ReplyIsRetainedUntilThePeerCloses()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new GatedDuplex(ServiceStatusFrames.Encode(Request()));
        var server = Serve(stream, budget.Token);
        await stream.CloseReadEntered.Task.WaitAsync(budget.Token);
        Assert.False(server.IsCompleted);
        using var replyBytes = new MemoryStream(stream.Output.ToArray());
        var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(replyBytes, budget.Token);
        Assert.False(reply.Ok); Assert.Equal("OPERATION_NOT_SUPPORTED", reply.ErrorCode);
        Assert.False(reply.CanConnect); Assert.False(reply.CoreRunning); Assert.False(reply.ProtectionArmed);
        stream.PeerClose.TrySetResult(0);
        Assert.True(await server.WaitAsync(budget.Token));
    }

    [Fact]
    public async Task OriginalCancellationEndsTheCloseWait()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new GatedDuplex(ServiceStatusFrames.Encode(Request()));
        var server = Serve(stream, budget.Token);
        await stream.CloseReadEntered.Task.WaitAsync(budget.Token);
        await budget.CancelAsync();
        Assert.False(await server.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ExtraBytesDoNotStartAnotherExchange()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new GatedDuplex(ServiceStatusFrames.Encode(Request()));
        var server = Serve(stream, budget.Token);
        await stream.CloseReadEntered.Task.WaitAsync(budget.Token);
        var written = stream.Output.Length;
        stream.PeerClose.TrySetResult(1);
        Assert.False(await server.WaitAsync(budget.Token));
        Assert.Equal(written, stream.Output.Length);
    }

    [Fact]
    public async Task RefusedIdentityHasNoReplyOrCloseWait()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new GatedDuplex(ServiceStatusFrames.Encode(Request()));
        Assert.False(await Serve(stream, budget.Token, false));
        Assert.Equal(0, stream.Output.Length);
        Assert.False(stream.CloseReadEntered.Task.IsCompleted);
    }

    [WindowsHandleFact]
    [SupportedOSPlatform("windows")]
    public async Task LegacyDisconnectDiscardsTheUnreadReply()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = "autovpn-reply-test-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(budget.Token);
        await client.ConnectAsync(budget.Token); await accepted;
        await client.WriteAsync(ServiceStatusFrames.Encode(Request()), budget.Token);
        Assert.True(await ServiceStatusExchange.HandleAsync(server, () => true,
            Guid.NewGuid().ToString("N"), 123, () => 0, budget.Token));
        // Deterministic ordering: the client cannot start reading until disconnect completed.
        server.Disconnect();
        await Assert.ThrowsAnyAsync<IOException>(() =>
            ServiceStatusFrames.ReadAsync<ServiceStatusReply>(client, budget.Token));
    }

    [WindowsHandleFact]
    [SupportedOSPlatform("windows")]
    public async Task NativeReplySurvivesAndRetainedInstanceIsReusable()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "autovpn-reply-test-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(name);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var accepted = server.WaitForConnectionAsync(budget.Token);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(budget.Token); await accepted;
                await client.WriteAsync(ServiceStatusFrames.Encode(Request()), budget.Token);
                using var observed = new GatedDuplex([], server);
                var serving = Serve(observed, budget.Token);
                // This barrier is entered after WriteAsync/FlushAsync, before peer close.
                await observed.CloseReadEntered.Task.WaitAsync(budget.Token);
                Assert.False(serving.IsCompleted);
                var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(client, budget.Token);
                Assert.Equal("OPERATION_NOT_SUPPORTED", reply.ErrorCode);
                Assert.False(reply.Ok); Assert.False(reply.CanConnect);
                Assert.False(serving.IsCompleted);
                client.Dispose();
                Assert.True(await serving.WaitAsync(budget.Token));
                // EOF has changed IsConnected, but Disconnect is still needed before reuse.
                server.Disconnect();
            }
            finally { client.Dispose(); }
        }
    }

    private static NamedPipeServerStream NewServer(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);

    private sealed class GatedDuplex(byte[] request, Stream? native = null) : Stream
    {
        private readonly MemoryStream _input = new(request);
        private bool _wrote;
        internal MemoryStream Output { get; } = new();
        internal TaskCompletionSource CloseReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> PeerClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_wrote)
                return native is null ? await _input.ReadAsync(buffer, token) : await native.ReadAsync(buffer, token);
            CloseReadEntered.TrySetResult();
            if (native is not null) return await native.ReadAsync(buffer, token);
            var count = await PeerClose.Task.WaitAsync(token);
            if (count > 0) buffer.Span[0] = 1;
            return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (native is null) await Output.WriteAsync(buffer, token);
            else await native.WriteAsync(buffer, token);
            _wrote = true;
        }
        public override Task FlushAsync(CancellationToken token) => native?.FlushAsync(token) ?? Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] b, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Output.Dispose(); }
            // The wrapping test owns the native server; disposing this observer must not close it.
            base.Dispose(disposing);
        }
    }
}
