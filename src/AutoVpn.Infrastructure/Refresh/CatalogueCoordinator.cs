using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Fetch;
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
}

public static class SpeedMeasurement
{
    public static async Task<SpeedSample?> MeasureHealthyDownloadAsync(
        ICatalogue catalogue,
        string nodeId,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var node = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node?.Assessment?.Health is not (HealthState.Healthy or HealthState.Degraded))
        {
            return null;
        }

        var read = await BoundedTransfer.ReadAsync(
            stream,
            ProductLimits.ManualDownloadBytes,
            TimeSpan.FromSeconds(ProductLimits.ManualDownloadSeconds),
            cancellationToken).ConfigureAwait(false);
        return SpeedSample.From(read);
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

    public CatalogueCoordinator(
        ICatalogue catalogue,
        PolicyHttpFetcher fetcher,
        IProbeTransport probe,
        SourceLedger ledger,
        IEnumerable<Uri>? probeTargets = null)
    {
        _catalogue = catalogue;
        _fetcher = fetcher;
        _probe = probe;
        _ledger = ledger;
        _probeTargets = new HashSet<string>(
            (probeTargets ?? []).Select(uri => uri.AbsoluteUri),
            StringComparer.Ordinal);
    }

    public async Task<DiscoveryOutcome> DiscoverAsync(
        ReviewedRegistry registry,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null)
    {
        var fetch = await _fetcher.GetAsync(
            registry.TreeApi,
            _ledger.EtagFor(registry.TreeApi.AbsoluteUri),
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
            var cached = _ledger.DiscoveryJsonFor(registry.TreeApi.AbsoluteUri);
            var reused = string.IsNullOrWhiteSpace(cached) ? null : GithubTreeParser.Parse(cached);
            if (reused is { Complete: true })
            {
                return BuildDiscovery(registry, reused);
            }

            fetch = await _fetcher.GetAsync(
                registry.TreeApi,
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
            return new DiscoveryOutcome(false, null, [], fetch.ReasonCode ?? ReasonCodes.FetchFailed, 0);
        }

        var parsed = GithubTreeParser.Parse(fetch.Body);
        if (!parsed.Complete)
        {
            return new DiscoveryOutcome(false, parsed.CommitSha, [], parsed.ReasonCode ?? "DISCOVERY_INCOMPLETE", 0);
        }

        _ledger.RememberDiscovery(registry.TreeApi.AbsoluteUri, fetch.Etag, fetch.Body, DateTimeOffset.UtcNow);
        return BuildDiscovery(registry, parsed);
    }

    private static DiscoveryOutcome BuildDiscovery(ReviewedRegistry registry, TreeDiscovery parsed)
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

            var urls = ReviewedRegistryLoader.ContentUrls(registry, path.Path, parsed.CommitSha);
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

        return new DiscoveryOutcome(true, parsed.CommitSha, items, null, unmatched);
    }

    public async Task<RefreshOutcome> RefreshAsync(
        IReadOnlyList<RefreshWorkItem> items,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        TimeSpan? attemptTimeout = null,
        int maxRetries = 0)
    {
        var gate = new SemaphoreSlim(ProductLimits.SourceDownloadConcurrency, ProductLimits.SourceDownloadConcurrency);
        var downloads = new Task<DownloadResult>[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            downloads[index] = DownloadAsync(item, gate, cancellationToken, attemptTimeout, maxRetries);
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

            if (download.Result is null)
            {
                failed = true;
                reasons.Add(download.Item.ArtifactId + ":" + (download.Reason ?? ReasonCodes.FetchFailed));
                RefreshMerge.Ingest(_catalogue, [FailedArtifact(download.Item)], nowUtc, _catalogue.Settings.AllowInsecureCertificates);
                continue;
            }

            var report = RefreshMerge.Ingest(_catalogue, [download.Result], nowUtc, _catalogue.Settings.AllowInsecureCertificates);
            pending += report.PublishedPending;
            var published = report.Committed && !download.Result.NotModified;
            failed |= report.AnyFetchFailed || (!download.Result.NotModified && !report.Committed);
            if (published && download.RememberUrl is not null)
            {
                _ledger.Remember(download.RememberUrl, download.Etag, download.ContentHash, nowUtc, null);
            }
            else if (!download.Result.NotModified && !report.Committed && download.RememberUrl is not null)
            {
                _ledger.RememberRejected(download.RememberUrl, download.Etag);
            }

            var label = download.Result.NotModified
                ? ReasonCodes.NotModified
                : published ? "PUBLISHED" : "REJECTED";
            reasons.Add(download.Item.ArtifactId + ":" + label);
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

        return await ProbeCoordinator.RunAsync(_catalogue, _probe, target, nowUtc, cancellationToken, budget).ConfigureAwait(false);
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
            var refetch = false;
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
                    return new DownloadResult(item, new IngestArtifact
                    {
                        ArtifactId = item.ArtifactId,
                        FamilyId = item.FamilyId,
                        Enabled = true,
                        NotModified = true,
                    }, false, false, null, null, null, null);
                }

                if (fetch.Body is not null && fetch.ReasonCode is null)
                {
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
            }

            return new DownloadResult(item, null, false, refetch, reason ?? ReasonCodes.FetchFailed, null, null, null);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
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
        string? ContentHash);
}
