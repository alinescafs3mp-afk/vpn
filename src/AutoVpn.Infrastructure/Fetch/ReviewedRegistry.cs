using System.Text.Json;

namespace AutoVpn.Infrastructure.Fetch;

public sealed record ReviewedRegistry
{
    public required string Owner { get; init; }
    public required string Repository { get; init; }
    public required string PinnedCommit { get; init; }
    public required Uri TreeApi { get; init; }
    public required IReadOnlyList<string> FamilyIds { get; init; }
    public required IReadOnlyList<ApprovedFetchOrigin> FetchOrigins { get; init; }
    public required IReadOnlySet<string> ApprovedHosts { get; init; }
    public required IReadOnlySet<string> RejectedHosts { get; init; }
    public required IReadOnlyList<Uri> ProbeTargets { get; init; }
}

public static class ReviewedRegistryLoader
{
    private static readonly IReadOnlyDictionary<string, string> ContentTemplates =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["raw.githubusercontent.com"] = "https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}",
            ["gitlab.com"] = "https://gitlab.com/{owner}/{repo}/-/raw/{commit}/{path}",
            ["codeberg.org"] = "https://codeberg.org/{owner}/{repo}/raw/commit/{commit}/{path}",
            ["gitea.com"] = "https://gitea.com/{owner}/{repo}/raw/commit/{commit}/{path}",
            ["git.sr.ht"] = "https://git.sr.ht/~{owner}/{repo}/blob/{commit}/{path}",
        };

    public static ReviewedRegistry Load(string configDirectory)
    {
        var source = ReadObject(Path.Combine(configDirectory, "source-manifest.json"));
        var mirrors = ReadObject(Path.Combine(configDirectory, "mirrors.json"));
        var probes = ReadObject(Path.Combine(configDirectory, "probe-targets.json"));
        var owner = RequiredString(source, "owner");
        var repository = RequiredString(source, "repository");
        var commit = RequiredString(source, "commit");
        if (!IsCommit(commit))
        {
            throw new InvalidDataException("В реестре источников указан неполный идентификатор коммита.");
        }

        var treeApi = new Uri(RequiredString(source, "treeApi"), UriKind.Absolute);
        var families = RequiredStringArray(source, "families");
        var approvedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!mirrors.TryGetProperty("approved", out var approved) || approved.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Список одобренных зеркал повреждён.");
        }

        foreach (var item in approved.EnumerateArray())
        {
            approvedHosts.Add(RequiredString(item, "host"));
        }

        if (mirrors.TryGetProperty("rejected", out var rejected) && rejected.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rejected.EnumerateArray())
            {
                rejectedHosts.Add(RequiredString(item, "host"));
            }
        }

        if (approvedHosts.Overlaps(rejectedHosts))
        {
            throw new InvalidDataException("Один и тот же хост не может быть одновременно одобрен и отклонён.");
        }

        var origins = new List<ApprovedFetchOrigin>
        {
            new("api.github.com", 443, $"/repos/{owner}/{repository}/git/trees/"),
        };
        foreach (var host in approvedHosts)
        {
            if (!ContentTemplates.ContainsKey(host))
            {
                continue;
            }

            var prefix = host.Equals("git.sr.ht", StringComparison.OrdinalIgnoreCase)
                ? $"/~{owner}/{repository}/"
                : $"/{owner}/{repository}/";
            origins.Add(new ApprovedFetchOrigin(host, 443, prefix));
        }

        if (!origins.Any(origin => origin.Matches(treeApi)))
        {
            throw new InvalidDataException("Адрес дерева источников не входит в одобренный префикс.");
        }

        var targets = new List<Uri>();
        if (!probes.TryGetProperty("targets", out var targetArray) || targetArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Список целей проверки повреждён.");
        }

        foreach (var item in targetArray.EnumerateArray())
        {
            var url = new Uri(RequiredString(item, "url"), UriKind.Absolute);
            if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            {
                throw new InvalidDataException("Цель проверки должна быть HTTPS без учётных данных в адресе.");
            }

            targets.Add(url);
        }

        return new ReviewedRegistry
        {
            Owner = owner,
            Repository = repository,
            PinnedCommit = commit,
            TreeApi = treeApi,
            FamilyIds = families,
            FetchOrigins = origins,
            ApprovedHosts = approvedHosts,
            RejectedHosts = rejectedHosts,
            ProbeTargets = targets,
        };
    }

    public static IReadOnlyList<Uri> ContentUrls(ReviewedRegistry registry, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            return [];
        }

        var encoded = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        var urls = new List<Uri>();
        foreach (var host in PreferredHosts(registry))
        {
            if (registry.RejectedHosts.Contains(host) || !ContentTemplates.TryGetValue(host, out var template))
            {
                continue;
            }

            var text = template
                .Replace("{owner}", Uri.EscapeDataString(registry.Owner), StringComparison.Ordinal)
                .Replace("{repo}", Uri.EscapeDataString(registry.Repository), StringComparison.Ordinal)
                .Replace("{commit}", Uri.EscapeDataString(registry.PinnedCommit), StringComparison.Ordinal)
                .Replace("{path}", encoded, StringComparison.Ordinal);
            var url = new Uri(text, UriKind.Absolute);
            if (registry.FetchOrigins.Any(origin => origin.Matches(url)))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) || path.StartsWith('/'))
        {
            return false;
        }

        var parts = path.Split('/');
        return parts.All(part => part.Length > 0 && part != "." && part != "..");
    }

    private static IEnumerable<string> PreferredHosts(ReviewedRegistry registry)
    {
        if (registry.ApprovedHosts.Contains("raw.githubusercontent.com"))
        {
            yield return "raw.githubusercontent.com";
        }

        foreach (var host in registry.ApprovedHosts.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                yield return host;
            }
        }
    }

    private static JsonElement ReadObject(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Реестр должен быть JSON-объектом.");
        }

        return document.RootElement.Clone();
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("В реестре отсутствует текстовое поле " + name + ".");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("В реестре пустое поле " + name + ".");
        }

        return text;
    }

    private static IReadOnlyList<string> RequiredStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("В реестре отсутствует список " + name + ".");
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException("Список " + name + " содержит нетекст.");
            }

            items.Add(item.GetString()!);
        }

        return items;
    }

    private static bool IsCommit(string value)
    {
        return value.Length == 40 && value.All(character => char.IsAsciiHexDigit(character));
    }
}
