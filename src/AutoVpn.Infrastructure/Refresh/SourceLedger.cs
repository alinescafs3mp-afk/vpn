using System.Text.Json;

namespace AutoVpn.Infrastructure.Refresh;

public sealed record SourceEntry(
    string Url,
    string? Etag,
    string? ContentHash,
    DateTimeOffset? LastSuccessUtc,
    string? LastReason,
    string? DiscoveryJson = null,
    string? RejectedEtag = null,
    string? ResolvedCommit = null);

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
        _entries[url] = new SourceEntry(url, etag, contentHash, nowUtc, reason, existing?.DiscoveryJson, null);
    }

    public void RememberDiscovery(string url, string? etag, string body, DateTimeOffset nowUtc, string? resolvedCommit = null)
    {
        _entries.TryGetValue(url, out var existing);
        _entries[url] = new SourceEntry(url, etag, existing?.ContentHash, nowUtc, null, body, existing?.RejectedEtag, resolvedCommit ?? existing?.ResolvedCommit);
    }

    public string? ResolvedCommitFor(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry.ResolvedCommit : null;
    }

    public void RememberRejected(string url, string? etag)
    {
        _entries.TryGetValue(url, out var existing);
        if (existing is null)
        {
            _entries[url] = new SourceEntry(url, null, null, null, "REJECTED", null, etag);
            return;
        }

        _entries[url] = existing with { RejectedEtag = etag };
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
                    ledger._entries[entry.Url] = entry;
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
