using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Xunit;
using Xunit.Sdk;

namespace AutoVpn.UnitTests;

// Test-only diagnostics around the unchanged controlled native handshake.
// No certificate callback, TLS setting, retry, or production policy is changed.
public sealed class AstraV3DTlsInvestigationTests
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("vless-ws")]
    [InlineData("vmess-ws")]
    [InlineData("vless-grpc")]
    public async Task Native_Round5_V3D_ObserveControlledHandshake(string variant)
    {
        using var trace = new ControlledTlsTrace();
        try
        {
            await new Round6NativeHandshakeTests()
                .Native_Round5_R6_ActualProtocolHandshakeAndWrongCredentialNegative(variant);
        }
        catch (Exception error)
        {
            throw new XunitException("Controlled variant=" + variant + "\n" + error +
                "\nTLS events from this test process (not a production capture):\n" + trace.Snapshot());
        }
    }
}

internal sealed class ControlledTlsTrace : EventListener
{
    private readonly ConcurrentQueue<string> _events = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        // Diagnostic collection is opt-in on the disposable test runner only.
        if (Environment.GetEnvironmentVariable("AUTOVPN_TLS_DIAGNOSTICS") == "1" &&
            source.Name == "System.Net.Security")
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs item)
    {
        // EventListener may call virtual methods from its base constructor.
        var events = _events;
        if (events is null || item.EventName is not ("HandshakeStart" or "HandshakeStop" or "HandshakeFailed"))
            return;
        var payload = item.Payload is null ? string.Empty : string.Join(" | ",
            item.Payload.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
        if (payload.Length > 2048) payload = payload[..2048];
        events.Enqueue(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " +
            item.EventName + " " + payload);
        while (events.Count > 64) events.TryDequeue(out _);
    }

    public string Snapshot() => string.Join("\n", _events.ToArray());
}
