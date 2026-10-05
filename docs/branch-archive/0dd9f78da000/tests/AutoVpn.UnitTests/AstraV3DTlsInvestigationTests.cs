using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Xunit.Abstractions;

namespace AutoVpn.UnitTests;

// Controlled loopback fixtures only. Every failed attempt remains a failure;
// repeating the scenario is an investigation, never a retry-until-green policy.
public sealed class AstraV3DTlsInvestigationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("vless-ws")]
    [InlineData("vmess-ws")]
    [InlineData("vless-grpc")]
    public async Task Native_Round5_V3D_RepeatedAuthenticatedHandshakes(string variant)
    {
        using var trace = new TlsFailureTrace();
        var failures = new List<string>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await new Round6NativeHandshakeTests()
                    .Native_Round5_R6_ActualProtocolHandshakeAndWrongCredentialNegative(variant);
                output.WriteLine($"{variant}/{attempt}: PASS positive and wrong-credential negative");
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                var report = $"{variant}/{attempt}: {ex.Message}\nProcess-scoped TLS failure events: {trace.Snapshot()}";
                failures.Add(report);
                output.WriteLine(report);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private sealed class TlsFailureTrace : EventListener
    {
        private readonly ConcurrentQueue<string> _events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.Security")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "HandshakeFailed" || _events is null) return;
            var text = string.Join(" | ", eventData.Payload?.Select(value => value?.ToString() ?? "null") ?? []);
            _events.Enqueue(text.Length <= 1600 ? text : text[..1600]);
            while (_events.Count > 16) _events.TryDequeue(out _);
        }

        public string Snapshot() => string.Join("\n", _events.ToArray());
    }
}
