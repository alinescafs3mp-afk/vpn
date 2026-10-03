using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Refresh;

public sealed record SourceEntry(
    string Url,
    string? Etag,
    string? ContentHash,
    DateTimeOffset? LastSuccessUtc,
    string? LastReason,
    string? DiscoveryJson = null,
    string? RejectedEtag = null,
    string? ResolvedCommit = null,
    DateTimeOffset? LastAttemptUtc = null,
    DateTimeOffset? LastContentUtc = null,
    DateTimeOffset? RetryAfterUtc = null,
    int FailureCount = 0);

public sealed class SourceLedger
{
    private readonly Dictionary<string, SourceEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SourceEntry> Entries => _entries.Values;

    public int RefreshCursor { get; set; }

    public SourceEntry? Find(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry : null;
    }

    public string? EtagFor(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry.Etag : null;
    }

    public void Remember(string url, string? etag, string? contentHash, DateTimeOffset nowUtc, string? reason)
    {
        _entries.TryGetValue(url, out var existing);
        var contentChanged = existing?.ContentHash is null
            ? contentHash is not null
            : !string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal);
        _entries[url] = new SourceEntry(
            url,
            etag,
            contentHash,
            nowUtc,
            reason,
            existing?.DiscoveryJson,
            null,
            existing?.ResolvedCommit,
            nowUtc,
            contentChanged ? nowUtc : existing?.LastContentUtc,
            null,
            0);
    }

    public void RememberDiscovery(string url, string? etag, string body, DateTimeOffset nowUtc, string? resolvedCommit = null)
    {
        _entries.TryGetValue(url, out var existing);
        _entries[url] = new SourceEntry(
            url,
            etag,
            existing?.ContentHash,
            nowUtc,
            null,
            body,
            existing?.RejectedEtag,
            resolvedCommit ?? existing?.ResolvedCommit,
            nowUtc,
            existing?.LastContentUtc,
            null,
            0);
    }

    public string? ResolvedCommitFor(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry.ResolvedCommit : null;
    }

    public void RememberRejected(string url, string? etag, DateTimeOffset nowUtc)
    {
        _entries.TryGetValue(url, out var existing);
        var failures = (existing?.FailureCount ?? 0) + 1;
        // A last-good etag must stay eligible for conditional revalidation. Backoff applies
        // only when this URL has never accepted a representation.
        var retry = existing?.Etag is null
            ? QuarantineSchedule.NextRetry(nowUtc, failures, 0)
            : existing.RetryAfterUtc;
        if (existing is null)
        {
            _entries[url] = new SourceEntry(url, null, null, null, "REJECTED", null, etag, null, nowUtc, null, retry, failures);
            return;
        }

        _entries[url] = existing with
        {
            RejectedEtag = etag,
            LastReason = "REJECTED",
            LastAttemptUtc = nowUtc,
            RetryAfterUtc = retry,
            FailureCount = failures,
        };
    }

    public void NoteFailure(string url, DateTimeOffset nowUtc, string reason, int? retryAfterSeconds)
    {
        _entries.TryGetValue(url, out var existing);
        var failures = (existing?.FailureCount ?? 0) + 1;
        var retry = retryAfterSeconds is int seconds && seconds > 0
            ? nowUtc.AddSeconds(Math.Min(seconds, 24 * 60 * 60))
            : QuarantineSchedule.NextRetry(nowUtc, failures, 0);
        if (existing is null)
        {
            _entries[url] = new SourceEntry(url, null, null, null, reason, null, null, null, nowUtc, null, retry, failures);
            return;
        }

        _entries[url] = existing with
        {
            LastReason = reason,
            LastAttemptUtc = nowUtc,
            RetryAfterUtc = retry,
            FailureCount = failures,
        };
    }

    public static bool IsImmutableTreeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        const string marker = "/git/trees/";
        var at = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var segment = path[(at + marker.Length)..];
        if (segment.Length != 40)
        {
            return false;
        }

        foreach (var ch in segment)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    public IEnumerable<SourceEntry> LiveEntries()
    {
        foreach (var entry in _entries.Values)
        {
            if (!IsImmutableTreeUrl(entry.Url))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Stamps for the one-minute pulse. An entry inside RetryAfterUtc is reported as
    /// checked at <paramref name="nowUtc"/> so a failed family does not force another
    /// fetch, while a missing success stamp still means due. Immutable tree URLs are omitted.
    /// </summary>
    public IReadOnlyList<DateTimeOffset?> LiveSuccessStamps(DateTimeOffset nowUtc)
    {
        var stamps = new List<DateTimeOffset?>();
        foreach (var entry in LiveEntries())
        {
            stamps.Add(entry.RetryAfterUtc is DateTimeOffset retry && retry > nowUtc
                ? nowUtc
                : entry.LastSuccessUtc);
        }

        return stamps;
    }

    public string? DiscoveryJsonFor(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry.DiscoveryJson : null;
    }

    public void ClearEtag(string url)
    {
        if (_entries.TryGetValue(url, out var entry))
        {
            _entries[url] = entry with { Etag = null };
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new LedgerFile(RefreshCursor, _entries.Values.OrderBy(entry => entry.Url, StringComparer.Ordinal).ToArray()));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    public static SourceLedger Load(string path)
    {
        var ledger = new SourceLedger();
        if (!File.Exists(path))
        {
            return ledger;
        }

        try
        {
            var text = File.ReadAllText(path).TrimStart();
            SourceEntry[] entries;
            if (text.StartsWith('['))
            {
                entries = JsonSerializer.Deserialize<SourceEntry[]>(text) ?? [];
            }
            else
            {
                var file = JsonSerializer.Deserialize<LedgerFile>(text);
                ledger.RefreshCursor = file?.RefreshCursor ?? 0;
                entries = file?.Entries ?? [];
            }

            foreach (var entry in entries)
            {
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
                {
                    var stored = entry.FailureCount < 0 ? entry with { FailureCount = 0 } : entry;
                    ledger._entries[stored.Url] = stored;
                }
            }
        }
        catch (JsonException)
        {
            var quarantined = path + ".quarantine";
            File.Move(path, quarantined, overwrite: true);
        }

        return ledger;
    }

    private sealed record LedgerFile(int RefreshCursor, SourceEntry[] Entries);
}
