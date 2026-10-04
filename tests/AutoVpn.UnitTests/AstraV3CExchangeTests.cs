using System.Buffers.Binary;
using System.Text;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;
using Xunit;

namespace AutoVpn.UnitTests;

public sealed class AstraV3CExchangeTests
{
    private static ServiceStatusRequest Request() => new()
        { ProtocolVersion = 1, RequestId = Guid.NewGuid().ToString("N"), Operation = "GetStatus" };

    [Theory]
    [InlineData("[]")][InlineData("null")][InlineData("{}")] [InlineData("{")]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":1}")]
    public async Task MalformedClientDoesNotEscapeExchangeOrReachAuthorization(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        using var stream = new MemoryStream(); stream.Write(header); stream.Write(body); stream.Position = 0;
        var checks = 0;
        Assert.False(await ServiceStatusExchange.HandleAsync(stream, () => { checks++; return true; },
            Guid.NewGuid().ToString("N"), 123, () => 0, CancellationToken.None));
        Assert.Equal(0, checks); Assert.Equal(body.Length + 4, stream.Length);
    }

    [Fact]
    public async Task UnauthorizedClientGetsNoReplyAndNextExchangeStillWorks()
    {
        var request = Request(); var bytes = ServiceStatusFrames.Encode(request);
        using var denied = new MemoryStream(); denied.Write(bytes); denied.Position = 0;
        Assert.False(await ServiceStatusExchange.HandleAsync(denied, () => false,
            Guid.NewGuid().ToString("N"), 123, () => 0, CancellationToken.None));
        Assert.Equal(bytes.Length, denied.Length);
        using var accepted = new MemoryStream(); accepted.Write(bytes); accepted.Position = 0;
        Assert.True(await ServiceStatusExchange.HandleAsync(accepted, () => true,
            Guid.NewGuid().ToString("N"), 123, () => 0, CancellationToken.None));
        accepted.Position = bytes.Length;
        var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(accepted, CancellationToken.None);
        Assert.True(InstalledServiceProtocol.ValidReply(reply, request.RequestId, 123));
    }

    [Fact]
    public async Task TruncatedAndCancelledClientReadsAreContained()
    {
        var bytes = ServiceStatusFrames.Encode(Request());
        using var truncated = new MemoryStream(bytes[..^1]);
        Assert.False(await ServiceStatusExchange.HandleAsync(truncated, () => throw new InvalidOperationException(),
            Guid.NewGuid().ToString("N"), 123, () => 0, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var stream = new MemoryStream(bytes);
        Assert.False(await ServiceStatusExchange.HandleAsync(stream, () => throw new InvalidOperationException(),
            Guid.NewGuid().ToString("N"), 123, () => 0, cancelled.Token));
    }

}
