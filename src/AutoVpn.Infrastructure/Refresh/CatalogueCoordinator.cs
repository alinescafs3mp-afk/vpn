using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Refresh;

public sealed record RefreshWorkItem
{
    public required string ArtifactId { get; init; }
    public required string FamilyId { get; init; }
    public required IReadOnlyList<Uri> Urls { get; init; }
}

public sealed record RefreshOutcome
{
    public int PublishedPending { get; init; }
    public bool Cancelled { get; init; }
    public bool RefetchPerformed { get; init; }
    public bool AnyFetchFailed { get; init; }
    public IReadOnlyList<string> SourceReasons { get; init; } = [];
}

public sealed record DiscoveryOutcome(bool Complete, string? CommitSha, IReadOnlyList<RefreshWorkItem> Items, string? ReasonCode, int UnmatchedPaths);

public static class RefreshScheduleGate
{
    public static bool ShouldRefresh(DateTimeOffset? lastSuccessUtc, DateTimeOffset nowUtc, ProductSettings settings, int seed)
    {
        var interval = RefreshSchedule.Interval(settings.RefreshIntervalMinutes, settings.RefreshJitterMinutes, seed);
        return RefreshSchedule.IsDue(lastSuccessUtc, nowUtc, interval);
    }

    public static bool AnyDue(IEnumerable<DateTimeOffset?> successes, DateTimeOffset nowUtc, ProductSettings settings, int seed)
    {
        var interval = RefreshSchedule.Interval(settings.RefreshIntervalMinutes, settings.RefreshJitterMinutes, seed);
        var seen = false;
        foreach (var success in successes)
        {
            seen = true;
            if (RefreshSchedule.IsDue(success, nowUtc, interval))
            {
                return true;
            }
        }

        return !seen;
    }
}

/// <summary>
/// One publication generation for every coordinator sharing a catalogue.
/// A newer cycle rejects an older cycle's ingest even after its download has finished.
/// </summary>
public sealed class RefreshFence
{
    private readonly object _gate = new();
    private int _cycle;

    public int Begin()
    {
        lock (_gate)
        {
            return ++_cycle;
        }
    }

    public int Capture()
    {
        lock (_gate)
        {
            return _cycle;
        }
    }

    public bool IsCurrent(int cycle)
    {
        lock (_gate)
        {
            return cycle == _cycle;
        }
    }

    public bool TryPublish(int cycle, Action publish)
    {
        lock (_gate)
        {
            if (cycle != _cycle)
            {
                return false;
            }

            publish();
            return true;
        }
    }
}

/// <summary>
/// Unelevated refresh clock. It does not download or probe until disclosure is accepted,
/// and a fresh source does not hide another source that has never succeeded.
/// </summary>
public sealed class RefreshScheduler
{
    private readonly Func<ProductSettings> _settings;
    private readonly Func<IReadOnlyList<DateTimeOffset?>> _successes;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _cycle;
    private readonly int _seed;
    private int _completed;

    public RefreshScheduler(
        Func<ProductSettings> settings,
        Func<IReadOnlyList<DateTimeOffset?>> successes,
        Func<DateTimeOffset, CancellationToken, Task> cycle,
        int seed)
    {
        _settings = settings;
        _successes = successes;
        _cycle = cycle;
        _seed = seed;
    }

    public int CompletedCycles => _completed;

    public async Task<bool> PulseAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (!settings.DisclosureAccepted)
        {
            return false;
        }

        if (!RefreshScheduleGate.AnyDue(_successes(), nowUtc, settings, _seed))
        {
            return false;
        }

        await _cycle(nowUtc, cancellationToken).ConfigureAwait(false);
        _completed++;
        return true;
    }
}

public static class SpeedMeasurement
{
    public readonly record struct MeasurementBinding(string Digest, long NetworkEpoch);

    public static async Task<SpeedSample?> MeasureHealthyDownloadAsync(
        ICatalogue catalogue,
        string nodeId,
        Stream stream,
        CancellationToken cancellationToken,
        MeasurementBinding? binding = null)
    {
        if (binding is null)
        {
            return null;
        }

        if (!SpeedStillBound(catalogue, nodeId, binding.Value, cancellationToken))
        {
            return null;
        }

        var read = await BoundedTransfer.ReadAsync(
            stream,
            ProductLimits.ManualDownloadBytes,
            TimeSpan.FromSeconds(ProductLimits.ManualDownloadSeconds),
            cancellationToken).ConfigureAwait(false);
        if (!SpeedStillBound(catalogue, nodeId, binding.Value, cancellationToken))
        {
            return null;
        }

        return SpeedSample.From(read);
    }

    private static bool SpeedStillBound(ICatalogue catalogue, string nodeId, MeasurementBinding binding, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || catalogue.NetworkEpoch != binding.NetworkEpoch)
        {
            return false;
        }

        var node = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        var assessment = node?.Assessment;
        return node is not null
            && !node.Excluded
            && assessment?.Health is (HealthState.Healthy or HealthState.Degraded)
            && string.Equals(assessment.Digest, binding.Digest, StringComparison.Ordinal)
            && assessment.NetworkEpoch == binding.NetworkEpoch
            && assessment.NetworkEpoch == catalogue.NetworkEpoch;
    }
}

/// <summary>
/// Unelevated catalogue workflow. It does not start a tunnel and does not mark a node healthy
/// unless the supplied probe transport reports a real success.
/// </summary>
public sealed class CatalogueCoordinator
{
    private readonly ICatalogue _catalogue;
    private readonly PolicyHttpFetcher _fetcher;
    private readonly IProbeTransport _probe;
    private readonly SourceLedger _ledger;
    private readonly IReadOnlySet<string> _probeTargets;
    private readonly RefreshFence _fence;
    private readonly ProbeByteBudget? _byteBudget;

    public CatalogueCoordinator(
        ICatalogue catalogue,
        PolicyHttpFetcher fetcher,
        IProbeTransport probe,
        SourceLedger ledger,
        IEnumerable<Uri>? probeTargets = null,
        RefreshFence? fence = null,
        ProbeByteBudget? byteBudget = null)
    {
        _catalogue = catalogue;
        _fetcher = fetcher;
        _probe = probe;
        _ledger = ledger;
        _fence = fence ?? new RefreshFence();
        _byteBudget = byteBudget;
        _probeTargets = new HashSet<string>(
            (probeTargets ?? []).Select(uri => uri.AbsoluteUri),
            StringComparer.Ordinal);
    }

    public async Task<DiscoveryOutcome> DiscoverAsync(
        ReviewedRegistry registry,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null)
    {
        var cycle = _fence.Capture();
        var schedulingUrl = registry.TreeApi.AbsoluteUri;
        var cached = _ledger.DiscoveryJsonFor(schedulingUrl);
        var held = _ledger.Find(schedulingUrl);
        if (held?.RetryAfterUtc is DateTimeOffset retryAt && retryAt > DateTimeOffset.UtcNow)
        {
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var heldTree = GithubTreeParser.Parse(cached);
                if (heldTree is { Complete: true })
                {
                    return BuildDiscovery(registry, heldTree, held.ResolvedCommit ?? registry.PinnedCommit);
                }
            }

            return new DiscoveryOutcome(false, held.ResolvedCommit, [], "RETRY_AFTER", 0);
        }

        var resolved = await ResolveCommitAsync(registry, cancellationToken, attemptTimeout).ConfigureAwait(false);
        var commit = resolved.Sha;
        var cacheAgrees = CacheAgrees(cached, _ledger.ResolvedCommitFor(schedulingUrl), commit, resolved.TreeSha);
        var treeUrl = resolved.TreeSha is null
            ? registry.TreeApi
            : new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(registry.Owner)}/{Uri.EscapeDataString(registry.Repository)}/git/trees/{resolved.TreeSha}?recursive=1");
        var fetch = await _fetcher.GetAsync(
            treeUrl,
            cacheAgrees ? _ledger.EtagFor(schedulingUrl) : null,
            ProductLimits.MaxArtifactBytes,
            cancellationToken,
            attemptTimeout,
            maxRetries: 0).ConfigureAwait(false);
        if (fetch.ReasonCode == ReasonCodes.Canceled)
        {
            return new DiscoveryOutcome(false, null, [], ReasonCodes.Canceled, 0);
        }

        if (fetch.NotModified)
        {
            var reused = string.IsNullOrWhiteSpace(cached) || !cacheAgrees ? null : GithubTreeParser.Parse(cached);
            if (reused is { Complete: true } && TreeAgrees(reused, resolved.TreeSha))
            {
                if (!_fence.TryPublish(cycle, () => _ledger.RememberDiscovery(schedulingUrl, fetch.Etag ?? _ledger.EtagFor(schedulingUrl), cached!, DateTimeOffset.UtcNow, commit)))
                {
                    return new DiscoveryOutcome(false, commit, [], "SUPERSEDED", 0);
                }

                return BuildDiscovery(registry, reused, commit);
            }

            fetch = await _fetcher.GetAsync(
                treeUrl,
                null,
                ProductLimits.MaxArtifactBytes,
                cancellationToken,
                attemptTimeout,
                maxRetries: 0).ConfigureAwait(false);
        }

        if (fetch.ReasonCode == ReasonCodes.Canceled)
        {
            return new DiscoveryOutcome(false, null, [], ReasonCodes.Canceled, 0);
        }

        if (fetch.NotModified || fetch.Body is null || fetch.ReasonCode is not null)
        {
            if (fetch.ReasonCode != ReasonCodes.Canceled && cacheAgrees)
            {
                var reused = GithubTreeParser.Parse(cached!);
                if (reused is { Complete: true } && TreeAgrees(reused, resolved.TreeSha))
                {
                    return BuildDiscovery(registry, reused, commit);
                }
            }

            return DiscoveryFailed(schedulingUrl, null, fetch.ReasonCode ?? ReasonCodes.FetchFailed, 0);
        }

        var parsed = GithubTreeParser.Parse(fetch.Body);
        if (!parsed.Complete || !TreeAgrees(parsed, resolved.TreeSha))
        {
            return DiscoveryFailed(schedulingUrl, parsed.CommitSha, parsed.ReasonCode ?? "DISCOVERY_INCOMPLETE", 0);
        }

        if (!_fence.TryPublish(cycle, () => _ledger.RememberDiscovery(schedulingUrl, fetch.Etag, fetch.Body, DateTimeOffset.UtcNow, commit)))
        {
            return new DiscoveryOutcome(false, commit, [], "SUPERSEDED", 0);
        }

        return BuildDiscovery(registry, parsed, commit);
    }

    private static bool CacheAgrees(string? cached, string? cachedCommit, string commit, string? treeSha)
    {
        if (string.IsNullOrWhiteSpace(cached))
        {
            return false;
        }

        if (cachedCommit is not null && !string.Equals(cachedCommit, commit, StringComparison.Ordinal))
        {
            return false;
        }

        if (treeSha is null)
        {
            return true;
        }

        var parsed = GithubTreeParser.Parse(cached);
        return parsed.Complete && string.Equals(parsed.CommitSha, treeSha, StringComparison.Ordinal);
    }

    private static bool TreeAgrees(TreeDiscovery parsed, string? treeSha)
    {
        return treeSha is null || string.Equals(parsed.CommitSha, treeSha, StringComparison.Ordinal);
    }

    private readonly record struct ResolvedCommit(string Sha, string? TreeSha);

    private async Task<ResolvedCommit> ResolveCommitAsync(ReviewedRegistry registry, CancellationToken cancellationToken, TimeSpan? attemptTimeout)
    {
        var requested = TreeSegment(registry.TreeApi);
        if (requested.Length == 40 && requested.All(Uri.IsHexDigit))
        {
            return new ResolvedCommit(requested, null);
        }

        var url = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(registry.Owner)}/{Uri.EscapeDataString(registry.Repository)}/commits/{Uri.EscapeDataString(requested)}");
        var fetch = await _fetcher.GetAsync(url, null, ProductLimits.MaxArtifactBytes, cancellationToken, attemptTimeout, maxRetries: 0).ConfigureAwait(false);
        if (fetch.Body is not null && fetch.ReasonCode is null && GithubTreeParser.TryReadCommit(fetch.Body, out var sha, out var treeSha))
        {
            return new ResolvedCommit(sha, treeSha);
        }

        return new ResolvedCommit(registry.PinnedCommit, null);
    }

    private static string TreeSegment(Uri treeApi)
    {
        var path = treeApi.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static DiscoveryOutcome BuildDiscovery(ReviewedRegistry registry, TreeDiscovery parsed, string commit)
    {
        var items = new List<RefreshWorkItem>();
        var unmatched = 0;
        foreach (var path in parsed.Paths)
        {
            if (!ArtifactClassifier.IsSubscriptionData(path.Class))
            {
                continue;
            }

            if (path.FamilyId is null || !registry.FamilyIds.Contains(path.FamilyId, StringComparer.Ordinal))
            {
                unmatched++;
                continue;
            }

            var urls = ReviewedRegistryLoader.ContentUrls(registry, path.Path, commit);
            if (urls.Count == 0)
            {
                unmatched++;
                continue;
            }

            items.Add(new RefreshWorkItem
            {
                ArtifactId = path.Path,
                FamilyId = path.FamilyId,
                Urls = urls,
            });
        }

        return new DiscoveryOutcome(true, commit, items, null, unmatched);
    }

    public async Task<RefreshOutcome> RefreshAsync(
        IReadOnlyList<RefreshWorkItem> items,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null,
        int maxRetries = 0)
    {
        var cycle = _fence.Capture();
        var gate = new SemaphoreSlim(ProductLimits.SourceDownloadConcurrency, ProductLimits.SourceDownloadConcurrency);
        var downloads = new List<Task<DownloadResult>>(items.Count);
        var budgetSkipped = new List<RefreshWorkItem>();
        var count = items.Count;
        var origin = NormalizeCursor(_ledger.RefreshCursor, count);
        long reserved = 0;
        var started = 0;
        for (var index = 0; index < count; index++)
        {
            var item = items[(origin + index) % count];
            if (HeldByRetry(item, nowUtc))
            {
                budgetSkipped.Add(item);
                continue;
            }

            if (reserved > 0 && reserved + ProductLimits.MaxArtifactBytes > ProductLimits.MaxRefreshBytes)
            {
                budgetSkipped.Add(item);
                continue;
            }

            reserved += ProductLimits.MaxArtifactBytes;
            started++;
            downloads.Add(DownloadAsync(item, gate, cancellationToken, attemptTimeout, maxRetries));
        }

        if (count > 0)
        {
            _ledger.RefreshCursor = (origin + Math.Max(started, 1)) % count;
        }

        DownloadResult[] finished;
        try
        {
            finished = await Task.WhenAll(downloads).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        var reasons = new List<string>();
        var pending = 0;
        var cancelled = false;
        var refetch = false;
        var failed = false;
        foreach (var skipped in budgetSkipped)
        {
            failed = true;
            reasons.Add(skipped.ArtifactId + ":" + (HeldByRetry(skipped, nowUtc) ? "RETRY_AFTER" : "CYCLE_BUDGET"));
        }

        foreach (var download in finished)
        {
            if (download.Cancelled)
            {
                cancelled = true;
                reasons.Add(download.Item.ArtifactId + ":CANCELED");
                continue;
            }

            if (download.RefetchPerformed)
            {
                refetch = true;
            }

            var accepted = _fence.TryPublish(cycle, () =>
            {
                if (download.Result is null)
                {
                    failed = true;
                    reasons.Add(download.Item.ArtifactId + ":" + (download.Reason ?? ReasonCodes.FetchFailed));
                    foreach (var url in download.Item.Urls)
                    {
                        _ledger.NoteFailure(url.AbsoluteUri, nowUtc, download.Reason ?? ReasonCodes.FetchFailed, download.RetryAfterSeconds);
                    }

                    RefreshMerge.Ingest(_catalogue, [FailedArtifact(download.Item)], nowUtc, _catalogue.Settings.AllowInsecureCertificates);
                    return;
                }

                var report = RefreshMerge.Ingest(_catalogue, [download.Result], nowUtc, _catalogue.Settings.AllowInsecureCertificates);
                pending += report.PublishedPending;
                var published = report.Committed && !download.Result.NotModified;
                failed |= report.AnyFetchFailed || (!download.Result.NotModified && !report.Committed);
                if (download.Result.NotModified && download.RememberUrl is not null)
                {
                    _ledger.Remember(download.RememberUrl, download.Etag, download.ContentHash, nowUtc, null);
                }
                else if (published && download.RememberUrl is not null)
                {
                    _ledger.Remember(download.RememberUrl, download.Etag, download.ContentHash, nowUtc, null);
                }
                else if (!download.Result.NotModified && !report.Committed && download.RememberUrl is not null)
                {
                    _ledger.RememberRejected(download.RememberUrl, download.Etag, nowUtc);
                }

                var label = download.Result.NotModified
                    ? ReasonCodes.NotModified
                    : published ? "PUBLISHED" : "REJECTED";
                reasons.Add(download.Item.ArtifactId + ":" + label);
            });
            if (!accepted)
            {
                reasons.Add(download.Item.ArtifactId + ":SUPERSEDED");
            }
        }

        return new RefreshOutcome
        {
            PublishedPending = pending,
            Cancelled = cancelled,
            RefetchPerformed = refetch,
            AnyFetchFailed = failed,
            SourceReasons = reasons,
        };
    }

    public async Task<ProbeReport> ProbeAsync(Uri target, DateTimeOffset nowUtc, CancellationToken cancellationToken, TimeSpan? budget = null)
    {
        if (_probeTargets.Count > 0 && !_probeTargets.Contains(target.AbsoluteUri))
        {
            return new ProbeReport(0, 0, 0, false);
        }

        if (_probe is NonTunCoreProbeTransport core && !core.CanRun)
        {
            return new ProbeReport(0, 0, 0, false);
        }

        return await ProbeCoordinator.RunAsync(_catalogue, _probe, target, nowUtc, cancellationToken, budget, spent: _byteBudget).ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadAsync(
        RefreshWorkItem item,
        SemaphoreSlim gate,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout,
        int maxRetries)
    {
        var acquired = false;
        try
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException)
            {
                return new DownloadResult(item, null, true, false, ReasonCodes.Canceled, null, null, null);
            }

            string? reason = null;
            int? retryAfter = null;
            var refetch = false;
            DownloadResult? ineligible = null;
            foreach (var url in item.Urls)
            {
                var known = _catalogue.Nodes.Any(node => node.ArtifactFamilies.ContainsKey(item.ArtifactId));
                var fetch = await _fetcher.GetAsync(
                    url,
                    _ledger.EtagFor(url.AbsoluteUri),
                    ProductLimits.MaxArtifactBytes,
                    cancellationToken,
                    attemptTimeout,
                    maxRetries).ConfigureAwait(false);
                if (fetch.ReasonCode == ReasonCodes.Canceled)
                {
                    return new DownloadResult(item, null, true, false, ReasonCodes.Canceled, null, null, null);
                }

                var prior = _ledger.Find(url.AbsoluteUri);
                if (fetch.NotModified && !known)
                {
                    fetch = await _fetcher.GetAsync(url, null, ProductLimits.MaxArtifactBytes, cancellationToken, attemptTimeout, maxRetries: 0).ConfigureAwait(false);
                    refetch = true;
                    if (fetch.ReasonCode == ReasonCodes.Canceled)
                    {
                        return new DownloadResult(item, null, true, true, ReasonCodes.Canceled, null, null, null);
                    }
                }

                if (fetch.NotModified && known)
                {
                    var remember = prior?.ContentHash is not null && prior.Etag is not null;
                    return new DownloadResult(item, new IngestArtifact
                    {
                        ArtifactId = item.ArtifactId,
                        FamilyId = item.FamilyId,
                        Enabled = true,
                        NotModified = true,
                    }, false, refetch, null, remember ? url.AbsoluteUri : null, remember ? prior!.Etag : null, remember ? prior!.ContentHash : null);
                }

                if (fetch.Body is not null && fetch.ReasonCode is null)
                {
                    if (!DocumentEligible(fetch.Body))
                    {
                        ineligible ??= new DownloadResult(item, new IngestArtifact
                        {
                            ArtifactId = item.ArtifactId,
                            FamilyId = item.FamilyId,
                            Enabled = true,
                            Text = fetch.Body,
                            ContentHash = fetch.ContentHash,
                        }, false, refetch, null, url.AbsoluteUri, fetch.Etag, fetch.ContentHash);
                        reason = ReasonCodes.InvalidUri;
                        continue;
                    }

                    return new DownloadResult(item, new IngestArtifact
                    {
                        ArtifactId = item.ArtifactId,
                        FamilyId = item.FamilyId,
                        Enabled = true,
                        Text = fetch.Body,
                        ContentHash = fetch.ContentHash,
                    }, false, refetch, null, url.AbsoluteUri, fetch.Etag, fetch.ContentHash);
                }

                reason = fetch.ReasonCode ?? ReasonCodes.FetchFailed;
                retryAfter = fetch.RetryAfterSeconds ?? retryAfter;
            }

            if (ineligible is not null)
            {
                return ineligible with { RefetchPerformed = refetch };
            }

            return new DownloadResult(item, null, false, refetch, reason ?? ReasonCodes.FetchFailed, null, null, null, retryAfter);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    private DiscoveryOutcome DiscoveryFailed(string schedulingUrl, string? commit, string? reason, int unmatched)
    {
        if (reason != ReasonCodes.Canceled && !string.Equals(reason, "SUPERSEDED", StringComparison.Ordinal))
        {
            _ledger.NoteFailure(schedulingUrl, DateTimeOffset.UtcNow, reason ?? ReasonCodes.FetchFailed, null);
        }

        return new DiscoveryOutcome(false, commit, [], reason, unmatched);
    }

    private bool HeldByRetry(RefreshWorkItem item, DateTimeOffset nowUtc)
    {
        if (item.Urls.Count == 0)
        {
            return false;
        }

        foreach (var url in item.Urls)
        {
            var entry = _ledger.Find(url.AbsoluteUri);
            if (entry?.RetryAfterUtc is not DateTimeOffset retry || retry <= nowUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static int NormalizeCursor(int cursor, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var mod = (long)cursor % count;
        if (mod < 0)
        {
            mod += count;
        }

        return (int)mod;
    }

    private static bool DocumentEligible(string body)
    {
        var batch = SubscriptionImporter.Import(body);
        return batch.EmptyValidDocument
            || batch.Records.Any(record => record.Disposition is RecordDisposition.Pending or RecordDisposition.PolicyBlocked or RecordDisposition.Duplicate);
    }

    private static IngestArtifact FailedArtifact(RefreshWorkItem item)
    {
        return new IngestArtifact
        {
            ArtifactId = item.ArtifactId,
            FamilyId = item.FamilyId,
            Enabled = true,
            FetchFailed = true,
        };
    }

    private sealed record DownloadResult(
        RefreshWorkItem Item,
        IngestArtifact? Result,
        bool Cancelled,
        bool RefetchPerformed,
        string? Reason,
        string? RememberUrl,
        string? Etag,
        string? ContentHash,
        int? RetryAfterSeconds = null);
}
