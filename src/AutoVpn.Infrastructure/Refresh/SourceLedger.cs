using System.Text.Json;

namespace AutoVpn.Infrastructure.Refresh;

public sealed record SourceEntry(string Url, string? Etag, string? ContentHash, DateTimeOffset? LastSuccessUtc, string? LastReason);

public sealed class SourceLedger
{
    private readonly Dictionary<string, SourceEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SourceEntry> Entries => _entries.Values;

    public string? EtagFor(string url)
    {
        return _entries.TryGetValue(url, out var entry) ? entry.Etag : null;
    }

    public void Remember(string url, string? etag, string? contentHash, DateTimeOffset nowUtc, string? reason)
    {
        _entries[url] = new SourceEntry(url, etag, contentHash, nowUtc, reason);
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

        var json = JsonSerializer.Serialize(_entries.Values.OrderBy(entry => entry.Url, StringComparer.Ordinal).ToArray());
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
            var entries = JsonSerializer.Deserialize<SourceEntry[]>(File.ReadAllText(path)) ?? [];
            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Url))
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
}
