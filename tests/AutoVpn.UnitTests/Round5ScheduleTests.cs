using System.Net;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;
using Microsoft.Data.Sqlite;

namespace AutoVpn.UnitTests;

public sealed class Round5ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private const string LinkA =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#a";

    private const string LinkB =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.11:443?encryption=none&security=tls&type=tcp&sni=www.example.com#b";

    [Fact]
    public void LiveScheduleIgnoresImmutableTreesAndDoesNotHideASilentArtifact()
    {
        var ledger = new SourceLedger();
        var branch = "https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1";
        var immutable = "https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/2222222222222222222222222222222222222222?recursive=1";
        var content = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt";
        ledger.RememberDiscovery(branch, "\"tree\"", "{}", Now, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ledger.Remember(immutable, null, null, Now.AddDays(-30), null);
        var settings = new ProductSettings { RefreshIntervalMinutes = 15, RefreshJitterMinutes = 0 };
        Assert.False(RefreshScheduleGate.AnyDue(ledger.LiveSuccessStamps(Now.AddMinutes(10)), Now.AddMinutes(10), settings, 0));
        ledger.Remember(content, null, null, Now.AddHours(-3), null);
        Assert.True(RefreshScheduleGate.AnyDue(ledger.LiveSuccessStamps(Now.AddMinutes(10)), Now.AddMinutes(10), settings, 0));
    }

    [Fact]
    public void ContentTimeStaysOnAnUnchangedBodyAndMovesWhenTheHashChanges()
    {
        var ledger = new SourceLedger();
        var url = "https://raw.githubusercontent.com/example/feed.txt";
        ledger.Remember(url, "\"a\"", "hash-a", Now, null);
        ledger.Remember(url, "\"a\"", "hash-a", Now.AddHours(2), null);
        var kept = ledger.Find(url);
        Assert.Equal(Now, kept!.LastContentUtc);
        Assert.Equal(Now.AddHours(2), kept.LastSuccessUtc);
        Assert.Equal(Now.AddHours(2), kept.LastAttemptUtc);
        Assert.Equal(0, kept.FailureCount);
        ledger.Remember(url, "\"b\"", "hash-b", Now.AddHours(4), null);
        Assert.Equal(Now.AddHours(4), ledger.Find(url)!.LastContentUtc);
    }

    [Fact]
    public async Task RejectedAndFailedArtifactsWaitBeforeTheNextFetch()
    {
        var calls = 0;
        var handler = new Handler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var url = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt");
        var item = new RefreshWorkItem { ArtifactId = "BLACK_VLESS_RUS.txt", FamilyId = "black-vless", Urls = [url] };
        var ledger = new SourceLedger();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(new MemoryCatalogue(), fetcher, new NonTunCoreProbeTransport(null, null), ledger);
        var first = await coordinator.RefreshAsync([item], Now, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("FETCH_FAILED", Assert.Single(first.SourceReasons), StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.True(ledger.Find(url.AbsoluteUri)!.RetryAfterUtc > Now);

        var held = await coordinator.RefreshAsync([item], Now.AddMinutes(1), CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("RETRY_AFTER", Assert.Single(held.SourceReasons), StringComparison.Ordinal);
        Assert.Equal(1, calls);

        var again = await coordinator.RefreshAsync([item], Now.AddMinutes(6), CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("FETCH_FAILED", Assert.Single(again.SourceReasons), StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void LedgerRoundTripKeepsScheduleFieldsAndClampsANegativeFailureCount()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r5-ledger-");
        try
        {
            var path = Path.Combine(directory.FullName, "sources.json");
            var ledger = new SourceLedger();
            var url = "https://raw.githubusercontent.com/example/feed.txt";
            ledger.Remember(url, "\"a\"", "hash-a", Now, null);
            ledger.NoteFailure(url, Now.AddMinutes(1), "FETCH_FAILED", 30);
            ledger.Save(path);
            var loaded = SourceLedger.Load(path);
            var entry = loaded.Find(url);
            Assert.Equal(Now, entry!.LastContentUtc);
            Assert.Equal(Now.AddMinutes(1), entry.LastAttemptUtc);
            Assert.Equal(Now.AddMinutes(1).AddSeconds(30), entry.RetryAfterUtc);
            Assert.Equal(1, entry.FailureCount);
            Assert.Equal("hash-a", entry.ContentHash);

            File.WriteAllText(path, """
                {"RefreshCursor":-4,"Entries":[{"Url":"https://raw.githubusercontent.com/example/other.txt","FailureCount":-3}]}
                """);
            var hostile = SourceLedger.Load(path);
            Assert.Equal(0, hostile.Find("https://raw.githubusercontent.com/example/other.txt")!.FailureCount);
            Assert.Equal(-4, hostile.RefreshCursor);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AssessmentUpdateDoesNotRepretectSiblingCredentials()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-r5-assess-");
        try
        {
            var path = Path.Combine(directory.FullName, "catalogue.sqlite");
            var protector = new CountingProtector();
            string siblingId;
            string targetId;
            byte[] siblingBlob;
            using (var store = SqliteCatalogue.Open(path, protector))
            {
                RefreshMerge.Ingest(store, [
                    new IngestArtifact { ArtifactId = "a", FamilyId = "black-vless", Enabled = true, Text = LinkA, ContentHash = "a" },
                    new IngestArtifact { ArtifactId = "b", FamilyId = "black-vless", Enabled = true, Text = LinkB, ContentHash = "b" },
                ], Now, false);
                Assert.Equal(2, store.Nodes.Count);
                Assert.True(protector.ProtectCalls >= 2);
                var before = protector.ProtectCalls;
                siblingId = store.Nodes.Single(node => node.Semantics.Host == "203.0.113.11").NodeId;
                var target = store.Nodes.Single(node => node.Semantics.Host == "203.0.113.10");
                targetId = target.NodeId;
                siblingBlob = ReadBlob(path, siblingId);
                store.ApplyAssessment(targetId, new AssessmentSnapshot
                {
                    Digest = target.Digest,
                    NetworkEpoch = store.NetworkEpoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = Now,
                    MedianLatencyMs = 42,
                });
                Assert.Equal(before, protector.ProtectCalls);
                Assert.Equal(siblingBlob, ReadBlob(path, siblingId));
                Assert.Equal(42, store.Nodes.Single(node => node.NodeId == targetId).Assessment!.MedianLatencyMs);
                Assert.Equal(HealthState.Pending, store.Nodes.Single(node => node.NodeId == siblingId).Assessment!.Health);
                Assert.Null(store.Nodes.Single(node => node.NodeId == siblingId).Assessment!.MedianLatencyMs);
            }

            using var reopened = SqliteCatalogue.Open(path, protector);
            Assert.Equal(42, reopened.Nodes.Single(node => node.NodeId == targetId).Assessment!.MedianLatencyMs);
            Assert.Equal(HealthState.Pending, reopened.Nodes.Single(node => node.NodeId == siblingId).Assessment!.Health);
            Assert.Null(reopened.Nodes.Single(node => node.NodeId == siblingId).Assessment!.MedianLatencyMs);
            Assert.Equal("203.0.113.11", reopened.Nodes.Single(node => node.NodeId == siblingId).Semantics.Host);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static byte[] ReadBlob(string path, string nodeId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT semantics_blob FROM nodes WHERE node_id=$id;";
        command.Parameters.AddWithValue("$id", nodeId);
        return (byte[])command.ExecuteScalar()!;
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public int ProtectCalls { get; private set; }

        public string ProtectorId => "count";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            ProtectCalls++;
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return ciphertext.ToArray();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(next(request));
        }
    }
}
