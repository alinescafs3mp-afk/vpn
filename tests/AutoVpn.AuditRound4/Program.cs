using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AutoVpn.AuditRound4;

internal static class Program
{
    private const string Hash = "3122d100e8177501776109f1a6253a694611627cf4d7c7ec82705855cf8626a8";
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) return 2;
        Directory.CreateDirectory(args[1]);
        try
        {
            if (args[0] == "measure") { Measure(args[1]); return 0; }
            if (args[0] == "native" && args.Length == 3) return await Native(args[1], args[2]).ConfigureAwait(false);
            return 2;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(args[1], "harness-error.txt"), error.ToString());
            return 1;
        }
    }

    private static void Measure(string output)
    {
        var results = new List<object>();
        foreach (var count in new[] { 1000, 5000, 10000 })
        {
            var temporary = Directory.CreateTempSubdirectory("autovpn-r4-measure-");
            var protector = new CountingTestProtector();
            try
            {
                using (var catalogue = SqliteCatalogue.Open(Path.Combine(temporary.FullName, "catalogue.sqlite"), protector))
                {
                    var now = DateTimeOffset.UtcNow;
                    var nodes = Enumerable.Range(0, count).Select(i =>
                    {
                        var node = Semantics(ProtocolKind.Vless) with { Port = 10000 + i };
                        return new SnapshotNode { Digest = CanonicalIdentity.Digest(node), Semantics = node, Label = "synthetic-" + i.ToString(CultureInfo.InvariantCulture), FamilyId = "black-vless", ArtifactId = "fixture" };
                    }).ToArray();
                    var clock = Stopwatch.StartNew();
                    catalogue.ApplySnapshot(new SnapshotCommit { ArtifactId = "fixture", FamilyId = "black-vless", ContentHash = "synthetic", Complete = true, NowUtc = now, Nodes = nodes });
                    var seedMs = clock.Elapsed.TotalMilliseconds;
                    var updates = new List<object>();
                    for (var sample = 0; sample < 3; sample++)
                    {
                        var node = catalogue.Nodes[count / 2];
                        var before = GC.GetTotalAllocatedBytes(true);
                        var calls = protector.ProtectCalls;
                        clock.Restart();
                        catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot { Digest = node.Digest, NetworkEpoch = catalogue.NetworkEpoch, Health = HealthState.Healthy, LastSuccessUtc = now, MedianLatencyMs = 20 + sample });
                        updates.Add(new { milliseconds = clock.Elapsed.TotalMilliseconds, allocated_bytes = GC.GetTotalAllocatedBytes(true) - before, protector_calls = protector.ProtectCalls - calls });
                    }
                    clock.Restart();
                    var text = CataloguePresentation.Servers(catalogue, "all", now);
                    results.Add(new { nodes = count, seed_ms = seedMs, assessment_updates = updates, presentation_ms = clock.Elapsed.TotalMilliseconds, text_characters = text.Length, total_protector_calls = protector.ProtectCalls, managed_memory_bytes = GC.GetTotalMemory(false) });
                    File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), boundary = "Synthetic SQLite workload; test-only passthrough protector; no WPF painting, network, or TUN. Timings are observations, not portable SLAs.", samples = results }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("MEASURE completed N=" + count.ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                // Test-process cleanup, not a production fix for pooled connections.
                SqliteConnection.ClearAllPools(); temporary.Delete(true);
            }
        }
    }

    private static async Task<int> Native(string output, string binary)
    {
        var cases = new List<(string Name, NodeSemantics Node)>();
        foreach (var kind in new[] { ProtocolKind.Vless, ProtocolKind.Vmess, ProtocolKind.Trojan, ProtocolKind.Shadowsocks, ProtocolKind.Hysteria2, ProtocolKind.Tuic }) cases.Add((kind.ToString(), Semantics(kind)));
        cases.Add(("Vless-WS", Semantics(ProtocolKind.Vless) with { Transport = "ws", Path = "/fixture", HostHeader = "fixture.example" }));
        cases.Add(("Vless-gRPC", Semantics(ProtocolKind.Vless) with { Transport = "grpc", ServiceName = "fixture" }));
        cases.Add(("Vless-H2", Semantics(ProtocolKind.Vless) with { Transport = "h2", Path = "/fixture", HostHeader = "fixture.example" }));
        var results = new List<object>(); var all = true;
        foreach (var item in cases)
        {
            CoreValidationResult validation;
            try
            {
                var node = new CatalogueNode { NodeId = "synthetic", Digest = CanonicalIdentity.Digest(item.Node), Semantics = item.Node, Label = item.Name };
                var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest { Secret = "synthetic-controller-disabled", ControllerPort = 12789, ExternalController = false, Tun = false, LanAccess = false,
                    LoopbackHosts = new Dictionary<string, string> { [item.Node.Host] = "127.0.0.1" }, Nodes = [NodeWireFactory.FromCatalogue(node)], SelectedNodeId = node.NodeId });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                validation = await MihomoProcessController.ValidateAsync(binary, Hash, yaml, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) { validation = new CoreValidationResult(false, error.GetType().Name, "Fixture profile was not accepted; no credentials retained."); }
            all &= validation.Ok;
            results.Add(new { name = item.Name, accepted = validation.Ok, reason = validation.ReasonCode, output = validation.RedactedOutput });
        }
        File.WriteAllText(Path.Combine(output, "native-profiles.json"), JsonSerializer.Serialize(new { boundary = "Real pinned mihomo -t only, no server handshakes, TUN, or proof that unknown fields are honored.", binary_sha256 = Hash, profiles = results }, new JsonSerializerOptions { WriteIndented = true }));
        return all ? 0 : 1;
    }

    private static NodeSemantics Semantics(ProtocolKind kind) => new()
    {
        Protocol = kind, Host = "203.0.113.10", Port = 443, Security = kind == ProtocolKind.Shadowsocks ? "aead" : "tls", Sni = "fixture.example",
        UserId = kind is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic ? "11111111-1111-4111-8111-111111111111" : null,
        Password = kind is ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic ? "synthetic-only" : null,
        Encryption = kind == ProtocolKind.Shadowsocks ? "aes-128-gcm" : kind == ProtocolKind.Vmess ? "auto" : kind == ProtocolKind.Vless ? "none" : null,
        Transport = "tcp", Udp = true, Congestion = kind == ProtocolKind.Tuic ? "cubic" : null,
    };
    private sealed class CountingTestProtector : ISecretProtector
    {
        public int ProtectCalls { get; private set; }
        public string ProtectorId => "audit-only-synthetic-passthrough";
        public byte[] Protect(ReadOnlySpan<byte> bytes) { ProtectCalls++; return bytes.ToArray(); }
        public byte[] Unprotect(ReadOnlySpan<byte> bytes) => bytes.ToArray();
    }
}
