using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;
using Xunit;

namespace AutoVpn.UnitTests;

public sealed class AstraV3CServiceTests
{
    private static ServiceStatusRequest Request(string operation = "GetStatus") => new()
        { ProtocolVersion = 1, RequestId = Guid.NewGuid().ToString("N"), Operation = operation };

    [Fact]
    public async Task StatusFramesRoundTripWithoutNetworkOrEffects()
    {
        var request = Request();
        using var input = new MemoryStream(ServiceStatusFrames.Encode(request));
        Assert.Equal(request, await ServiceStatusFrames.ReadAsync<ServiceStatusRequest>(input, CancellationToken.None));
        var reply = InstalledServiceProtocol.Answer(request, Guid.NewGuid().ToString("N"), 123, 0);
        using var output = new MemoryStream(ServiceStatusFrames.Encode(reply));
        var decoded = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(output, CancellationToken.None);
        Assert.True(InstalledServiceProtocol.ValidReply(decoded, request.RequestId, 123));
        Assert.False(decoded.CanConnect); Assert.False(decoded.CoreRunning); Assert.False(decoded.ProtectionArmed);
    }

    [Theory]
    [InlineData("Connect")][InlineData("Disconnect")][InlineData("ApplyRuntimeSet")]
    [InlineData("ReportHealth")][InlineData("RecoverOwned")][InlineData("OpenSession")]
    [InlineData("GetSnapshot")][InlineData("getstatus")][InlineData("")]
    public void StatusEndpointRefusesEveryEffectAndLegacyOperation(string operation)
    {
        var reply = InstalledServiceProtocol.Answer(Request(operation), Guid.NewGuid().ToString("N"), 1, 0);
        Assert.False(reply.Ok); Assert.Equal("OPERATION_NOT_SUPPORTED", reply.ErrorCode);
        Assert.False(reply.CanConnect); Assert.False(reply.ProtectionArmed); Assert.False(reply.CoreRunning);
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("../credential")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("11111111-1111-4111-8111-111111111111")]
    public void UntrustedIdsAreRefusedAndNotReflected(string? id)
    {
        var reply = InstalledServiceProtocol.Answer(Request() with { RequestId = id! }, Guid.NewGuid().ToString("N"), 1, 0);
        Assert.Equal("REQUEST_ID", reply.ErrorCode); Assert.Empty(reply.RequestId); Assert.False(reply.Ok);
    }

    [Fact]
    public void OldOrFutureProtocolCannotPass()
    {
        foreach (var version in new[] { -1, 0, 2, int.MaxValue })
            Assert.Equal("PROTOCOL_VERSION", InstalledServiceProtocol.Answer(Request() with { ProtocolVersion = version },
                Guid.NewGuid().ToString("N"), 1, 0).ErrorCode);
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("null")] [InlineData("{")]
    [InlineData("{\"protocolVersion\":1,\"protocolVersion\":1,\"requestId\":\"x\",\"operation\":\"GetStatus\"}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"x\",\"operation\":\"GetStatus\",\"node\":{\"password\":\"secret\"}}")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"x\",\"operation\":\"GetStatus\",\"profilePath\":\"c:\\\\evil\"}")]
    [InlineData("{\"ProtocolVersion\":1,\"requestId\":\"x\",\"operation\":\"GetStatus\"}")]
    public void FramesRejectMissingUnknownDuplicateAndMalformedData(string json) =>
        Assert.Throws<InvalidDataException>(() => ServiceStatusFrames.Decode<ServiceStatusRequest>(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData(-1)][InlineData(0)][InlineData(4097)][InlineData(int.MaxValue)]
    public async Task LengthIsValidatedBeforeAllocatingBody(int length)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceStatusFrames.ReadAsync<ServiceStatusRequest>(input, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedAndCancelledReadsDoNotSucceed()
    {
        var bytes = ServiceStatusFrames.Encode(Request());
        using var input = new MemoryStream(bytes[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ServiceStatusFrames.ReadAsync<ServiceStatusRequest>(input, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstalledServiceClient.QueryAsync(cancelled.Token));
    }

    [Fact]
    public void ClaimsIdentityAndCorrelationAreAllValidated()
    {
        var request = Request();
        var reply = InstalledServiceProtocol.Answer(request, Guid.NewGuid().ToString("N"), 123, 0);
        foreach (var altered in new[] { reply with { CanConnect = true }, reply with { CoreRunning = true },
            reply with { ProtectionArmed = true }, reply with { ProcessId = 456 }, reply with { RequestId = Guid.NewGuid().ToString("N") },
            reply with { InstanceId = "" }, reply with { UptimeSeconds = -1 }, reply with { Mode = "VPN" },
            reply with { Phase = "Connected" }, reply with { ServiceName = "OtherService" }, reply with { ProtocolVersion = 2 },
            reply with { Ok = false }, reply with { ErrorCode = "FAILURE" } })
            Assert.False(InstalledServiceProtocol.ValidReply(altered, request.RequestId, 123));
        foreach (var field in new[] { "protocolVersion", "mode", "phase", "serviceName", "canConnect", "coreRunning", "protectionArmed" })
        {
            var element = JsonSerializer.SerializeToNode(reply, IpcJson.Options)!.AsObject(); element.Remove(field);
            Assert.Throws<InvalidDataException>(() => ServiceStatusFrames.Decode<ServiceStatusReply>(Encoding.UTF8.GetBytes(element.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("S-1-5-21-123-456-789-1001")][InlineData("S-1-5-21-0-0-0-4294967295")]
    public void OwnerMustBeCanonicalAccountSid(string sid) => Assert.Equal(sid,
        ServiceOwnerConfiguration.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"ownerSid\":\"" + sid + "\"}")).OwnerSid);

    [Theory]
    [InlineData(null)][InlineData("")][InlineData("S-1-5-18")][InlineData("S-1-5-19")][InlineData("S-1-5-20")]
    [InlineData("S-1-1-0")][InlineData("S-1-5-32-544")][InlineData("S-1-5-21-1-2-3-0")]
    [InlineData("S-1-5-21-01-2-3-4")][InlineData("S-1-5-21-1-2-3-4294967296")]
    [InlineData("S-1-5-21-1-2-3-4 --command evil")][InlineData("s-1-5-21-1-2-3-4")]
    [InlineData("S-1-5-21-1-2-3-4-5")]
    public void BroadAndMalformedOwnersAreRejected(string? sid) => Assert.False(ServiceOwnerConfiguration.IsAccountSid(sid));

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"ownerSid\":\"S-1-5-21-1-2-3-4\"}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":\"S-1-5-21-1-2-3-4\",\"root\":\"evil\"}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":null}")]
    [InlineData("{\"schemaVersion\":1,\"ownerSid\":\"S-1-5-21-1-2-3-4\",\"ownerSid\":\"S-1-5-18\"}")]
    public void OwnerConfigIsNotAnExtensibleExecutableProfile(string json) =>
        Assert.Throws<InvalidDataException>(() => ServiceOwnerConfiguration.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void StoppedServiceNeverReturnsToRunning()
    {
        var lifecycle = new ServiceLifecycle(); Assert.Equal(2, lifecycle.State);
        Assert.True(lifecycle.TryMarkReady()); Assert.False(lifecycle.TryMarkReady());
        Assert.Throws<InvalidOperationException>(lifecycle.MarkStopped);
        Assert.True(lifecycle.RequestStop()); Assert.False(lifecycle.RequestStop()); Assert.False(lifecycle.TryMarkReady());
        lifecycle.MarkStopped(); Assert.Equal(1, lifecycle.State); Assert.False(lifecycle.RequestStop()); Assert.False(lifecycle.TryMarkReady());
    }

    [Fact]
    public async Task StopDuringStartupWinsOverLateReady()
    {
        for (var i = 0; i < 100; i++)
        {
            var lifecycle = new ServiceLifecycle();
            await Task.WhenAll(Task.Run(() => lifecycle.RequestStop()), Task.Run(() => lifecycle.TryMarkReady()));
            Assert.Equal(3, lifecycle.State); Assert.False(lifecycle.TryMarkReady());
            lifecycle.MarkStopped(); Assert.Equal(1, lifecycle.State);
        }
    }
}
