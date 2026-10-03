using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Fetch;

public sealed record DiscoveredPath(string Path, ArtifactClass Class, string? FamilyId, long? Size);

public sealed record TreeDiscovery(bool Complete, string? CommitSha, bool Truncated, IReadOnlyList<DiscoveredPath> Paths, string? ReasonCode);

public static class GithubTreeParser
{
    public static bool TryReadCommitSha(string json, out string sha)
    {
        return TryReadCommit(json, out sha, out _);
    }

    /// <summary>
    /// A commit object is a 40-hex sha plus a tree object sha. A tree listing,
    /// a blob, or a bare sha is not a commit.
    /// </summary>
    public static bool TryReadCommit(string json, out string sha, out string treeSha)
    {
        sha = "";
        treeSha = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                !string.Equals(type.GetString(), "commit", StringComparison.Ordinal))
            {
                return false;
            }

            if (root.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
            {
                return false;
            }

            if (!Hex40(root, out sha))
            {
                return false;
            }

            if (root.TryGetProperty("commit", out var commit) &&
                commit.ValueKind == JsonValueKind.Object &&
                commit.TryGetProperty("tree", out var nested) &&
                Hex40(nested, out treeSha))
            {
                return true;
            }

            if (root.TryGetProperty("tree", out tree) && tree.ValueKind == JsonValueKind.Object && Hex40(tree, out treeSha))
            {
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Hex40(JsonElement element, out string sha)
    {
        sha = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("sha", out var shaElement) ||
            shaElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = shaElement.GetString();
        if (value is null || value.Length != 40 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        sha = value;
        return true;
    }

    public static TreeDiscovery Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Incomplete(null, false);
            }

            var truncated = root.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("sha", out var shaElement) && shaElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return Incomplete(null, truncated);
            }

            var sha = root.TryGetProperty("sha", out shaElement) ? shaElement.GetString() : null;
            if (truncated || !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
            {
                return Incomplete(sha, truncated);
            }

            var paths = new List<DiscoveredPath>();
            foreach (var item in tree.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("path", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String)
                {
                    return Incomplete(sha, false);
                }

                var path = pathElement.GetString() ?? "";
                if (item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    return Incomplete(sha, false);
                }

                var type = item.TryGetProperty("type", out typeElement) ? typeElement.GetString() : "blob";
                if (!string.Equals(type, "blob", StringComparison.Ordinal))
                {
                    continue;
                }

                if (item.TryGetProperty("size", out var sizeElement) &&
                    sizeElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                {
                    return Incomplete(sha, false);
                }

                long? size = item.TryGetProperty("size", out sizeElement) && sizeElement.TryGetInt64(out var parsed) ? parsed : null;
                var mode = item.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
                    ? modeElement.GetString()
                    : null;
                var symlink = mode is "120000" or "120777";
                paths.Add(new DiscoveredPath(
                    path,
                    symlink ? ArtifactClass.Unknown : ArtifactClassifier.ClassifyPath(path),
                    symlink ? null : SeedFamilies.MatchFamily(path),
                    size));
            }

            return new TreeDiscovery(true, sha, false, paths, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Incomplete(null, false);
        }
    }

    private static TreeDiscovery Incomplete(string? sha, bool truncated)
    {
        return new TreeDiscovery(false, sha, truncated, [], "DISCOVERY_INCOMPLETE");
    }
}
