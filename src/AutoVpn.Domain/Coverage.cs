namespace AutoVpn.Domain;

public enum ArtifactClass
{
    Unknown = 0,
    VpnUriList = 1,
    ClashProxiesOnly = 2,
    ClashFull = 3,
    JsonExport = 4,
    Base64Export = 5,
    MirrorReference = 6,
    TorBridgeList = 7,
    Image = 8,
    Documentation = 9,
    Workflow = 10,
    Directory = 11,
}

public enum FamilyState
{
    Fresh = 0,
    Unchanged = 1,
    PartiallyUpdated = 2,
    Stale = 3,
    FetchFailed = 4,
    Disabled = 5,
    UnsupportedFormat = 6,
    RemovedUpstream = 7,
    DiscoveryIncomplete = 8,
}

public static class ArtifactClassifier
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".ico"];

    public static ArtifactClass ClassifyPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || !normalized.Contains('.', StringComparison.Ordinal) && !normalized.Contains('/', StringComparison.Ordinal))
        {
            if (normalized is "TOR-BRIDGES" or "Export" or "QR-codes" or ".github")
            {
                return ArtifactClass.Directory;
            }
        }

        var file = normalized.Split('/')[^1];
        var lower = normalized.ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (lower.StartsWith(".github/workflows/", StringComparison.Ordinal) || lower.StartsWith(".github/", StringComparison.Ordinal))
        {
            return ArtifactClass.Workflow;
        }

        if (lower.StartsWith("qr-codes/", StringComparison.Ordinal) || ImageExtensions.Contains(ext))
        {
            return ArtifactClass.Image;
        }

        if (lower.StartsWith("tor-bridges/", StringComparison.Ordinal) && ext == ".txt")
        {
            return ArtifactClass.TorBridgeList;
        }

        if (file.Equals("MIRRORS.md", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactClass.MirrorReference;
        }

        if (ext is ".md" || file.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactClass.Documentation;
        }

        if (lower.Contains("/clash/proxies_only/", StringComparison.Ordinal) && ext is ".yaml" or ".yml")
        {
            return ArtifactClass.ClashProxiesOnly;
        }

        if (lower.Contains("/clash/", StringComparison.Ordinal) && ext is ".yaml" or ".yml")
        {
            return ArtifactClass.ClashFull;
        }

        if (ext == ".json")
        {
            return ArtifactClass.JsonExport;
        }

        if (lower.Contains("/base64/", StringComparison.Ordinal))
        {
            return ArtifactClass.Base64Export;
        }

        if (ext == ".txt")
        {
            return ArtifactClass.VpnUriList;
        }

        if (ext.Length == 0)
        {
            return ArtifactClass.Directory;
        }

        return ArtifactClass.Unknown;
    }

    public static bool IsSubscriptionData(ArtifactClass kind)
    {
        return kind is ArtifactClass.VpnUriList or ArtifactClass.ClashProxiesOnly or ArtifactClass.ClashFull
            or ArtifactClass.JsonExport or ArtifactClass.Base64Export;
    }
}

public static class SeedFamilies
{
    public static readonly IReadOnlyList<SeedFamily> All =
    [
        new("black-mixed", "Смешанные протоколы / BLACK", "BLACK_SS+All_RUS.txt", "Export/Clash/PROXIES_ONLY/BLACK_SS+All_RUS_clash_proxies.yaml"),
        new("black-ss-weak-dpi", "Shadowsocks / слабый DPI", "BLACK_SS_WEAK_DPI_RUS.txt", null),
        new("black-vless", "VLESS / BLACK", "BLACK_VLESS_RUS.txt", "Export/Clash/PROXIES_ONLY/BLACK_VLESS_RUS_clash_proxies.yaml"),
        new("black-vless-mobile", "VLESS / мобильные", "BLACK_VLESS_RUS_mobile.txt", "Export/Clash/PROXIES_ONLY/BLACK_VLESS_RUS_mobile_clash_proxies.yaml"),
        new("white-reality-mobile", "Reality / мобильные / WHITE", "Vless-Reality-White-Lists-Rus-Mobile.txt", "Export/Clash/PROXIES_ONLY/Vless-Reality-White-Lists-Rus-Mobile-clash-proxies.yaml"),
        new("white-cidr-all", "WHITE / CIDR / все", "WHITE-CIDR-RU-all.txt", "Export/Clash/PROXIES_ONLY/WHITE-CIDR-RU-all-clash-proxies.yaml"),
        new("white-cidr-checked", "WHITE / CIDR / проверенные", "WHITE-CIDR-RU-checked.txt", "Export/Clash/PROXIES_ONLY/WHITE-CIDR-RU-checked-clash-proxies.yaml"),
        new("white-sni-all", "WHITE / SNI", "WHITE-SNI-RU-all.txt", "Export/Clash/PROXIES_ONLY/WHITE-SNI-RU-all-clash-proxies.yaml"),
    ];

    public static string? MatchFamily(string path)
    {
        var normalized = path.Replace('\\', '/');
        var file = normalized.Split('/')[^1];
        foreach (var family in All.OrderByDescending(item => item.RootFile.Length))
        {
            if (file.Equals(family.RootFile, StringComparison.OrdinalIgnoreCase) ||
                (family.ClashProxies is not null && normalized.EndsWith(family.ClashProxies, StringComparison.OrdinalIgnoreCase)))
            {
                return family.Id;
            }
        }

        foreach (var family in All.OrderByDescending(item => item.RootFile.Length))
        {
            var token = System.IO.Path.GetFileNameWithoutExtension(family.RootFile);
            if (file.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return family.Id;
            }
        }

        return null;
    }
}

public sealed record SeedFamily(string Id, string Label, string RootFile, string? ClashProxies);
