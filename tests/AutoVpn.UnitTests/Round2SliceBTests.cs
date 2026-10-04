using System.Net;
using System.Text;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class Round2SliceBTests
{
    private const string Alpha =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?encryption=none&security=tls&type=tcp&sni=www.example.com#alpha";

    private const string Beta =
        "vless://11111111-1111-4111-8111-111111111111@203.0.113.21:443?encryption=none&security=tls&type=tcp&sni=www.example.com#beta";

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Rt11HeadAdvancesAndOutageReusesTheCachedTree()
    {
        var catalogue = new MemoryCatalogue();
        RefreshMerge.Ingest(catalogue, [Body("kept", Alpha, "kept")], DateTimeOffset.UtcNow, false);
        var kept = Assert.Single(catalogue.Nodes).Semantics.Host;
        var registry = Registry();
        var phase = 0;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/commits/", StringComparison.Ordinal))
            {
                var commit = phase == 0 ? ShaA : ShaB;
                var treeName = phase == 0
                    ? "cccccccccccccccccccccccccccccccccccccccc"
                    : "dddddddddddddddddddddddddddddddddddddddd";
                var body = "{\"sha\":\"" + commit + "\",\"commit\":{\"tree\":{\"sha\":\"" + treeName + "\"}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            if (phase >= 2)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var treeSha = path.Contains("dddddddddddddddddddddddddddddddddddddddd", StringComparison.Ordinal)
                ? "dddddddddddddddddddddddddddddddddddddddd"
                : "cccccccccccccccccccccccccccccccccccccccc";
            var tree = treeSha.StartsWith('c')
                ? Tree(treeSha, "BLACK_VLESS_RUS.txt", "BLACK_VLESS_RUS_mobile.txt")
                : Tree(treeSha, "BLACK_VLESS_RUS_mobile.txt");
            phase++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tree, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"head-" + (phase == 1 ? ShaA : ShaB)[..4] + "\"");
            return response;
        });
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger());
        var first = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(first.Complete);
        Assert.Equal(ShaA, first.CommitSha);
        Assert.Equal(1, first.UnmatchedPaths);
        Assert.Contains(ShaA, Assert.Single(first.Items).Urls[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(ShaB, first.Items[0].Urls[0].AbsoluteUri, StringComparison.Ordinal);

        var second = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(second.Complete);
        Assert.Equal(ShaB, second.CommitSha);
        Assert.Empty(second.Items);
        Assert.Equal(1, second.UnmatchedPaths);
        Assert.Equal(kept, Assert.Single(catalogue.Nodes).Semantics.Host);

        var outage = await coordinator.DiscoverAsync(registry, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(outage.Complete);
        Assert.Equal(ShaB, outage.CommitSha);
        Assert.Empty(outage.Items);
        Assert.Equal(kept, Assert.Single(catalogue.Nodes).Semantics.Host);
    }

    [Fact]
    public async Task Rt13PersistenceFailureDoesNotAdvanceTheEtag()
    {
        var url = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/BLACK_VLESS_RUS.txt");
        var phase = 0;
        var handler = new Handler(_ =>
        {
            var body = phase == 0 ? Alpha : Beta;
            var tag = phase == 0 ? "good-a" : "good-b";
            phase++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + tag + "\"");
            return response;
        });
        var catalogue = new SnapshotGate { FailOn = 2 };
        var ledger = new SourceLedger();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), ledger);
        var item = new RefreshWorkItem { ArtifactId = "BLACK_VLESS_RUS.txt", FamilyId = "black-vless", Urls = [url] };
        var published = await coordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Contains("PUBLISHED", Assert.Single(published.SourceReasons), StringComparison.Ordinal);
        Assert.Equal("\"good-a\"", ledger.EtagFor(url.AbsoluteUri));
        Assert.Equal("203.0.113.10", Assert.Single(catalogue.Nodes).Semantics.Host);

        await Assert.ThrowsAsync<IOException>(() => coordinator.RefreshAsync([item], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2)));
        Assert.Equal("\"good-a\"", ledger.EtagFor(url.AbsoluteUri));
        Assert.Equal("203.0.113.10", Assert.Single(catalogue.Nodes).Semantics.Host);
    }

    [Fact]
    public async Task Rt14SchedulerRunsDistinctCyclesOnlyAfterConsent()
    {
        var now = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var settings = new ProductSettings
        {
            DisclosureAccepted = false,
            RefreshIntervalMinutes = ProductLimits.MinimumRefreshIntervalMinutes,
            RefreshJitterMinutes = 0,
        };
        var successes = new List<DateTimeOffset?>();
        var runs = 0;
        var scheduler = new RefreshScheduler(
            () => settings,
            () => successes.ToArray(),
            (at, _) =>
            {
                runs++;
                successes.Add(at);
                return Task.CompletedTask;
            },
            seed: 1);

        Assert.False(await scheduler.PulseAsync(now, CancellationToken.None));
        Assert.Equal(0, runs);

        settings = settings with { DisclosureAccepted = true };
        Assert.True(await scheduler.PulseAsync(now, CancellationToken.None));
        Assert.Equal(1, scheduler.CompletedCycles);
        Assert.False(await scheduler.PulseAsync(now.AddMinutes(14), CancellationToken.None));
        Assert.Equal(1, runs);

        successes.Add(null);
        Assert.True(await scheduler.PulseAsync(now.AddMinutes(14), CancellationToken.None));
        Assert.Equal(2, runs);

        successes.RemoveAll(item => item is null);
        Assert.False(await scheduler.PulseAsync(now.AddMinutes(14), CancellationToken.None));
        Assert.True(await scheduler.PulseAsync(now.AddMinutes(ProductLimits.MinimumRefreshIntervalMinutes), CancellationToken.None));
        Assert.Equal(3, scheduler.CompletedCycles);
    }

    [Fact]
    public async Task Rt15SupersededRefreshCannotPublish()
    {
        var slowUrl = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/slow.txt");
        var fastUrl = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/fast.txt");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async request =>
        {
            var slow = request.RequestUri!.AbsolutePath.EndsWith("/slow.txt", StringComparison.Ordinal);
            if (slow)
            {
                entered.TrySetResult();
                await release.Task;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(slow ? Alpha : Beta, Encoding.UTF8, "text/plain"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(slow ? "\"slow\"" : "\"fast\"");
            return response;
        });
        var catalogue = new MemoryCatalogue();
        var ledger = new SourceLedger();
        var fence = new RefreshFence();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var first = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), ledger, fence: fence);
        var second = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), ledger, fence: fence);
        fence.Begin();
        var slowTask = first.RefreshAsync([Item("slow.txt", slowUrl)], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(3));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fence.Begin();
        var fast = await second.RefreshAsync([Item("fast.txt", fastUrl)], DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(3));
        release.TrySetResult();
        var slow = await slowTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("SUPERSEDED", Assert.Single(slow.SourceReasons), StringComparison.Ordinal);
        Assert.Contains("PUBLISHED", Assert.Single(fast.SourceReasons), StringComparison.Ordinal);
        Assert.Equal("203.0.113.21", Assert.Single(catalogue.Nodes).Semantics.Host);
        Assert.Null(ledger.EtagFor(slowUrl.AbsoluteUri));
        Assert.Equal("\"fast\"", ledger.EtagFor(fastUrl.AbsoluteUri));
    }

    [Fact]
    public async Task Rt16CycleBudgetStopsExtraArtifacts()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            Interlocked.Increment(ref calls);
            var path = request.RequestUri!.AbsolutePath;
            var dash = path.LastIndexOf('-');
            var dot = path.LastIndexOf('.');
            var index = int.Parse(path[(dash + 1)..dot], System.Globalization.CultureInfo.InvariantCulture);
            var host = "203.0.113." + (10 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var body = "vless://11111111-1111-4111-8111-111111111111@" + host + ":443?encryption=none&security=tls&type=tcp&sni=www.example.com#n" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"n" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"");
            return Task.FromResult(response);
        });
        var catalogue = new MemoryCatalogue();
        using var fetcher = new PolicyHttpFetcher(handler, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" });
        var coordinator = new CatalogueCoordinator(catalogue, fetcher, new NonTunCoreProbeTransport(null, null), new SourceLedger());
        var items = new List<RefreshWorkItem>();
        for (var index = 0; index < 9; index++)
        {
            var url = new Uri("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/20c38289c29e4dba6b8f01ddd3273ec9ec169b46/n-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            items.Add(Item("n-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", url));
        }

        var outcome = await coordinator.RefreshAsync(items, DateTimeOffset.UtcNow, CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.Equal(8, calls);
        Assert.Equal(8, catalogue.Nodes.Count);
        Assert.Contains(outcome.SourceReasons, reason => reason.EndsWith(":CYCLE_BUDGET", StringComparison.Ordinal));
        Assert.Contains(catalogue.Nodes, node => node.Semantics.Host == "203.0.113.10");
        Assert.DoesNotContain(catalogue.Nodes, node => node.Semantics.Host == "203.0.113.18");
    }

    [Fact]
    public void Rt22CorruptJournalStaysRecoveryUnknownAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt22-");
        try
        {
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            using (var created = EffectJournal.Open(path))
            {
                created.Record(new OwnedEffect("owned-route", "route", "synthetic"));
            }

            File.WriteAllText(path, "this is not a sqlite journal");
            using (var damaged = EffectJournal.Open(path))
            {
                var first = damaged.Recover(new UnavailableNetworkGuard());
                Assert.False(first.Completed);
                Assert.NotNull(first.QuarantinePath);
            }

            using var restarted = EffectJournal.Open(path);
            var second = restarted.Recover(new UnavailableNetworkGuard());
            Assert.False(second.Completed);
            Assert.NotNull(second.QuarantinePath);
            Assert.Empty(restarted.OpenEffects());
            Assert.True(EffectJournal.HasUnknownMarker(path));

            restarted.Dispose(); // The test owns this live handle; close it before deliberate deletion on Windows.
            File.Delete(path);
            Assert.True(EffectJournal.HasUnknownMarker(path));
            using var replaced = EffectJournal.Open(path);
            var third = replaced.Recover(new UnavailableNetworkGuard());
            Assert.False(third.Completed);
            Assert.NotNull(third.QuarantinePath);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DuplicateRemovalIdsDoNotCompleteJournalRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt22-dup-");
        try
        {
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            using var journal = EffectJournal.Open(path);
            journal.Record(new OwnedEffect("one", "route", "synthetic"));
            journal.Record(new OwnedEffect("two", "route", "synthetic"));
            var recovery = journal.Recover(new RepeatingGuard("one"));
            Assert.False(recovery.Completed);
            Assert.Equal(1, recovery.RemovedEffects);
            Assert.Equal(1, recovery.OpenEffects);
            Assert.Equal("two", Assert.Single(journal.OpenEffects()).Id);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingJournalAfterOpenRequiresReconciliation()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt22-missing-");
        try
        {
            var unseen = Path.Combine(directory.FullName, "never.sqlite");
            Assert.False(EffectJournal.RequiresReconciliation(unseen));
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            using (var journal = EffectJournal.Open(path))
            {
                journal.Record(new OwnedEffect("owned-route", "route", "synthetic"));
            }

            File.Delete(path);
            Assert.True(EffectJournal.RequiresReconciliation(path));
            Assert.True(File.Exists(EffectJournal.PresenceMarkerPath(path)));
            Assert.False(EffectJournal.HasUnknownMarker(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void QuarantineResidueWithoutAJournalRequiresReconciliation()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-rt22-residue-");
        try
        {
            var path = Path.Combine(directory.FullName, "effects.sqlite");
            File.WriteAllText(path + ".quarantine-20261003010101000", "old");
            Assert.True(EffectJournal.RequiresReconciliation(path));
            File.Delete(path + ".quarantine-20261003010101000");
            File.WriteAllText(path + "-wal", "wal");
            Assert.True(EffectJournal.RequiresReconciliation(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static RefreshWorkItem Item(string artifact, Uri url)
    {
        return new RefreshWorkItem { ArtifactId = artifact, FamilyId = "black-vless", Urls = [url] };
    }

    private static IngestArtifact Body(string artifact, string text, string hash)
    {
        return new IngestArtifact { ArtifactId = artifact, FamilyId = "black-vless", Enabled = true, Text = text, ContentHash = hash };
    }

    private static ReviewedRegistry Registry()
    {
        return new ReviewedRegistry
        {
            Owner = "igareck",
            Repository = "vpn-configs-for-russia",
            PinnedCommit = "20c38289c29e4dba6b8f01ddd3273ec9ec169b46",
            TreeApi = new Uri("https://api.github.com/repos/igareck/vpn-configs-for-russia/git/trees/main?recursive=1"),
            FamilyIds = ["black-vless"],
            FetchOrigins =
            [
                new ApprovedFetchOrigin("api.github.com", 443, "/repos/igareck/vpn-configs-for-russia/git/trees/"),
                new ApprovedFetchOrigin("raw.githubusercontent.com", 443, "/igareck/vpn-configs-for-russia/"),
            ],
            ApprovedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.github.com", "raw.githubusercontent.com" },
            RejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ProbeTargets = [],
        };
    }

    private static string Tree(string sha, params string[] paths)
    {
        var entries = string.Join(',', paths.Select(path => "{\"path\":\"" + path + "\",\"type\":\"blob\",\"size\":12}"));
        return "{\"sha\":\"" + sha + "\",\"truncated\":false,\"tree\":[" + entries + "]}";
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> next) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(request => Task.FromResult(sync(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return next(request);
        }
    }

    private sealed class RepeatingGuard(string id) : INetworkGuard
    {
        public GuardResult Arm(GuardRequest request)
        {
            _ = request;
            return new GuardResult(false, false, null, []);
        }

        public GuardResult Disarm(long generation)
        {
            _ = generation;
            return new GuardResult(true, false, null, []);
        }

        public GuardResult Recover(IReadOnlyList<OwnedEffect> effects)
        {
            _ = effects;
            return new GuardResult(true, false, null, [id, id]);
        }
    }

    private sealed class SnapshotGate : ICatalogue
    {
        private readonly MemoryCatalogue _inner = new();
        private int _snapshots;

        public int FailOn { get; init; } = int.MaxValue;

        public long NetworkEpoch => _inner.NetworkEpoch;

        public ProductSettings Settings
        {
            get => _inner.Settings;
            set => _inner.Settings = value;
        }

        public IReadOnlyList<CatalogueNode> Nodes => _inner.Nodes;

        public void SetNetworkEpoch(long epoch)
        {
            _inner.SetNetworkEpoch(epoch);
        }

        public void ApplySnapshot(SnapshotCommit commit)
        {
            if (++_snapshots == FailOn)
            {
                throw new IOException("disk");
            }

            _inner.ApplySnapshot(commit);
        }

        public void ApplyAssessment(string nodeId, AssessmentSnapshot assessment)
        {
            _inner.ApplyAssessment(nodeId, assessment);
        }

        public int EvictOverflow(DateTimeOffset nowUtc)
        {
            return _inner.EvictOverflow(nowUtc);
        }

        public bool TrySetFavorite(string nodeId, bool favorite)
        {
            return _inner.TrySetFavorite(nodeId, favorite);
        }

        public bool TrySetExcluded(string nodeId, bool excluded)
        {
            return _inner.TrySetExcluded(nodeId, excluded);
        }

        public void SetActiveNode(string? nodeId)
        {
            _inner.SetActiveNode(nodeId);
        }

        public IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context)
        {
            return _inner.Eligible(context);
        }
    }
}
