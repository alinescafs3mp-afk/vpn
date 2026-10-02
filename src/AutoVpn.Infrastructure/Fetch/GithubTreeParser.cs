using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Fetch;

public sealed record DiscoveredPath(string Path, ArtifactClass Class, string? FamilyId, long? Size);

public sealed record TreeDiscovery(bool Complete, string? CommitSha, bool Truncated, IReadOnlyList<DiscoveredPath> Paths, string? ReasonCode);

public static class GithubTreeParser
{
    public static TreeDiscovery Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var truncated = root.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            var sha = root.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() : null;
            if (truncated || !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            {
                return new TreeDiscovery(false, sha, truncated, [], "DISCOVERY_INCOMPLETE");
            }

            var paths = new List<DiscoveredPath>();
            foreach (var item in tree.EnumerateArray())
            {
                if (!item.TryGetProperty("path", out var pathElement))
                {
                    continue;
                }

                var path = pathElement.GetString() ?? "";
                var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "blob";
                if (!string.Equals(type, "blob", StringComparison.Ordinal))
                {
                    continue;
                }

                long? size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed) ? parsed : null;
                paths.Add(new DiscoveredPath(path, ArtifactClassifier.ClassifyPath(path), SeedFamilies.MatchFamily(path), size));
            }

            return new TreeDiscovery(true, sha, false, paths, null);
        }
        catch (JsonException)
        {
            return new TreeDiscovery(false, null, false, [], "DISCOVERY_INCOMPLETE");
        }
    }
}
