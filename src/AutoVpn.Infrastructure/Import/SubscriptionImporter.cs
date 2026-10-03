using System.Globalization;
using System.Text;
using System.Text.Json;
using AutoVpn.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AutoVpn.Infrastructure.Import;

public enum RecordDisposition
{
    Pending = 0,
    Duplicate = 1,
    Invalid = 2,
    Unsupported = 3,
    PolicyBlocked = 4,
}

public sealed record ImportRecord
{
    public required RecordDisposition Disposition { get; init; }
    public string? ReasonCode { get; init; }
    public string DisplayName { get; init; } = "";
    public string? AdvertisedCountry { get; init; }
    public bool CountryConflict { get; init; }
    public NodeSemantics? Semantics { get; init; }
    public string? Digest { get; init; }
    public IReadOnlyList<string> StrippedPolicy { get; init; } = [];
}

public sealed record ImportBatch
{
    public required IReadOnlyList<ImportRecord> Records { get; init; }
    public bool EmptyValidDocument { get; init; }
    public bool DocumentValid { get; init; } = true;
    public bool LimitExceeded { get; init; }
    public int? AdvisoryUpdateInterval { get; init; }
    public string? ProfileTitle { get; init; }
    public int ClientPolicyStripped { get; init; }

    public int Total => Records.Count;
    public int Pending => Records.Count(record => record.Disposition == RecordDisposition.Pending);
    public int Duplicates => Records.Count(record => record.Disposition == RecordDisposition.Duplicate);
    public int Invalid => Records.Count(record => record.Disposition == RecordDisposition.Invalid);
    public int Unsupported => Records.Count(record => record.Disposition == RecordDisposition.Unsupported);
    public int PolicyBlocked => Records.Count(record => record.Disposition == RecordDisposition.PolicyBlocked);

    public bool Balanced => Total == Pending + Duplicates + Invalid + Unsupported + PolicyBlocked;
}

public sealed record ImportOptions
{
    public bool AllowInsecureCertificates { get; init; }
    public int MaxRecords { get; init; } = ProductLimits.MaxCandidatesPerCycle;
}

public static class SubscriptionImporter
{
    public static ImportBatch Import(string text, ImportOptions? options = null)
    {
        try
        {
            return ImportCore(text, options);
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException or InvalidOperationException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.InvalidUri);
        }
    }

    private static ImportBatch ImportCore(string text, ImportOptions? options)
    {
        options ??= new ImportOptions();
        if (text.Length > ProductLimits.MaxArtifactBytes)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.SizeLimit);
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        if (LooksLikeHtml(normalized))
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.HtmlContent);
        }

        var advisory = ReadAdvisory(normalized);
        if (LooksLikeYaml(normalized))
        {
            var batch = ImportYaml(normalized, options);
            return batch with { AdvisoryUpdateInterval = advisory.Interval, ProfileTitle = advisory.Title ?? batch.ProfileTitle };
        }

        if (LooksLikeJson(normalized))
        {
            var batch = ImportJson(normalized, options);
            return batch with { AdvisoryUpdateInterval = advisory.Interval, ProfileTitle = advisory.Title ?? batch.ProfileTitle };
        }

        return ImportLines(normalized, options, depth: 0) with
        {
            AdvisoryUpdateInterval = advisory.Interval,
            ProfileTitle = advisory.Title,
        };
    }

    private static ImportBatch ImportLines(string text, ImportOptions options, int depth)
    {
        if (depth > ProductLimits.MaxBase64Depth)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.NestedWrapper);
        }

        var lines = text.Split('\n');
        var nonEmpty = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            nonEmpty.Add(line);
        }

        if (nonEmpty.Count == 1 && !nonEmpty[0].Contains("://", StringComparison.Ordinal) &&
            Encoding.UTF8.GetByteCount(nonEmpty[0]) <= ProductLimits.MaxArtifactBytes &&
            Base64Text.TryDecode(nonEmpty[0], out var decoded))
        {
            var inner = decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return ImportLines(inner, options, depth + 1);
        }

        for (var index = 0; index < nonEmpty.Count; index++)
        {
            if (Encoding.UTF8.GetByteCount(nonEmpty[index]) > ProductLimits.MaxLineBytes)
            {
                nonEmpty[index] = "\0OVERSIZE";
            }
        }

        var records = new List<ImportRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var limit = false;
        foreach (var line in nonEmpty)
        {
            if (records.Count >= options.MaxRecords)
            {
                limit = true;
                break;
            }

            if (line == "\0OVERSIZE")
            {
                records.Add(Invalid(ReasonCodes.SizeLimit));
                continue;
            }

            var candidate = line;
            if (!candidate.Contains("://", StringComparison.Ordinal) && Base64Text.TryDecode(candidate, out var inner) &&
                inner.Contains("://", StringComparison.Ordinal))
            {
                if (depth + 1 > ProductLimits.MaxBase64Depth)
                {
                    records.Add(Invalid(ReasonCodes.NestedWrapper));
                    continue;
                }

                candidate = inner.Trim();
            }

            var parsed = ShareLinkParser.Parse(candidate);
            records.Add(Finish(parsed, options, seen));
        }

        return new ImportBatch
        {
            Records = records,
            EmptyValidDocument = records.Count == 0 && !limit,
            LimitExceeded = limit,
        };
    }

    private static ImportBatch ImportYaml(string text, ImportOptions options)
    {
        try
        {
            YamlSafety.AssertSafe(text);
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return Single(RecordDisposition.Invalid, ReasonCodes.YamlLimit);
            }

            if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxiesNode) ||
                proxiesNode is not YamlSequenceNode proxies)
            {
                return Single(RecordDisposition.Invalid, ReasonCodes.YamlLimit);
            }

            var stripped = root.Children.Keys
                .Select(key => (key as YamlScalarNode)?.Value)
                .Where(key => key is not null && key is not "proxies")
                .Cast<string>()
                .ToArray();
            var records = new List<ImportRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var limit = false;
            foreach (var item in proxies.Children)
            {
                if (records.Count >= options.MaxRecords)
                {
                    limit = true;
                    break;
                }

                if (item is not YamlMappingNode map)
                {
                    records.Add(Invalid(ReasonCodes.YamlLimit));
                    continue;
                }

                records.Add(Finish(ClashProxyParser.Parse(map), options, seen));
            }

            return new ImportBatch
            {
                Records = records,
                EmptyValidDocument = records.Count == 0 && !limit,
                LimitExceeded = limit,
                ClientPolicyStripped = stripped.Length > 0 ? stripped.Length : 0,
            };
        }
        catch (YamlException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.YamlLimit);
        }
        catch (FormatException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.YamlLimit);
        }
        catch (ArgumentException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.DuplicateKey);
        }
    }

    private static ImportBatch ImportJson(string text, ImportOptions options)
    {
        try
        {
            JsonSafety.AssertSafe(text);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = ProductLimits.MaxJsonDepth });
            var records = new List<ImportRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stripped = 0;
            IEnumerable<JsonElement> nodes;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                nodes = document.RootElement.EnumerateArray();
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                JsonElement list;
                var named = document.RootElement.TryGetProperty("outbounds", out list) ||
                            document.RootElement.TryGetProperty("proxies", out list);
                if (!named || list.ValueKind != JsonValueKind.Array)
                {
                    return Single(RecordDisposition.Invalid, ReasonCodes.InvalidUri);
                }

                var proxiesOnly = !document.RootElement.TryGetProperty("outbounds", out _) &&
                                  document.RootElement.TryGetProperty("proxies", out _);
                if (proxiesOnly && LooksLikeClash(list))
                {
                    return ImportClashJson(list, options, document.RootElement);
                }

                nodes = list.EnumerateArray();
            }
            else
            {
                return Single(RecordDisposition.Invalid, ReasonCodes.InvalidUri);
            }
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name is not "outbounds" and not "proxies")
                    {
                        stripped++;
                    }
                }
            }

            var limit = false;
            foreach (var node in nodes)
            {
                if (records.Count >= options.MaxRecords)
                {
                    limit = true;
                    break;
                }

                IReadOnlyList<ParsedNode> parsedNodes;
                try
                {
                    parsedNodes = XrayOutboundParser.ParseAll(node, ref stripped);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    records.Add(Invalid(ReasonCodes.InvalidUri));
                    continue;
                }

                foreach (var parsed in parsedNodes)
                {
                    if (records.Count >= options.MaxRecords)
                    {
                        limit = true;
                        break;
                    }

                    records.Add(Finish(parsed, options, seen));
                }

                if (limit)
                {
                    break;
                }
            }

            return new ImportBatch
            {
                Records = records,
                EmptyValidDocument = records.Count == 0 && !limit,
                LimitExceeded = limit,
                ClientPolicyStripped = stripped,
            };
        }
        catch (FormatException ex) when (ex.Message == ReasonCodes.DuplicateKey)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.DuplicateKey);
        }
        catch (JsonException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.InvalidUri);
        }
        catch (FormatException)
        {
            return Single(RecordDisposition.Invalid, ReasonCodes.InvalidUri);
        }
    }

    private static bool LooksLikeClash(JsonElement list)
    {
        var any = false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("protocol", out _) || item.TryGetProperty("settings", out _) || item.TryGetProperty("streamSettings", out _))
            {
                return false;
            }

            if (item.TryGetProperty("type", out _) && item.TryGetProperty("server", out _))
            {
                any = true;
            }
        }

        return any;
    }

    private static ImportBatch ImportClashJson(JsonElement list, ImportOptions options, JsonElement root)
    {
        var records = new List<ImportRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stripped = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name != "proxies")
            {
                stripped++;
            }
        }

        var limit = false;
        foreach (var item in list.EnumerateArray())
        {
            if (records.Count >= options.MaxRecords)
            {
                limit = true;
                break;
            }

            records.Add(Finish(ClashProxyParser.ParseJson(item), options, seen));
        }

        return new ImportBatch
        {
            Records = records,
            EmptyValidDocument = records.Count == 0 && !limit,
            LimitExceeded = limit,
            ClientPolicyStripped = stripped,
        };
    }

    private static ImportRecord Finish(ParsedNode parsed, ImportOptions options, HashSet<string> seen)
    {
        if (parsed.Semantics is null)
        {
            return new ImportRecord
            {
                Disposition = parsed.Disposition,
                ReasonCode = parsed.ReasonCode,
                DisplayName = DisplayName.Sanitize(parsed.DisplayName),
            };
        }

        var semantics = CanonicalIdentity.Normalize(parsed.Semantics);
        var posture = semantics.Classify(options.AllowInsecureCertificates);
        var digest = CanonicalIdentity.Digest(semantics);
        if (!seen.Add(digest) && posture is SecurityPosture.Accepted or SecurityPosture.PolicyBlocked)
        {
            return new ImportRecord
            {
                Disposition = RecordDisposition.Duplicate,
                ReasonCode = "DUPLICATE",
                DisplayName = DisplayName.Sanitize(parsed.DisplayName),
                Semantics = semantics,
                Digest = digest,
            };
        }

        var country = CountryLabels.FromLabel(parsed.DisplayName);
        var disposition = posture switch
        {
            SecurityPosture.Accepted => RecordDisposition.Pending,
            SecurityPosture.PolicyBlocked => RecordDisposition.PolicyBlocked,
            SecurityPosture.Unsupported => RecordDisposition.Unsupported,
            _ => RecordDisposition.Invalid,
        };
        var reason = posture switch
        {
            SecurityPosture.PolicyBlocked when semantics.SkipCertVerify => ReasonCodes.CertVerificationDisabled,
            SecurityPosture.PolicyBlocked => ReasonCodes.PlaintextTransport,
            SecurityPosture.Unsupported when !semantics.HasClosedSecurity() => ReasonCodes.UnsupportedSecurityOption,
            SecurityPosture.Unsupported => parsed.ReasonCode ?? ReasonCodes.UnsupportedProtocol,
            SecurityPosture.Invalid when semantics.Port is < 1 or > 65535 => ReasonCodes.InvalidPort,
            SecurityPosture.Invalid when !semantics.HasRequiredCredentials() => ReasonCodes.MissingCredential,
            SecurityPosture.Invalid when EndpointSafety.IsNonPublicHost(semantics.Host) => ReasonCodes.NonPublicEndpoint,
            SecurityPosture.Invalid => parsed.ReasonCode ?? ReasonCodes.InvalidUri,
            _ => null,
        };
        return new ImportRecord
        {
            Disposition = disposition,
            ReasonCode = reason,
            DisplayName = DisplayName.Sanitize(parsed.DisplayName),
            AdvertisedCountry = country?.Name,
            CountryConflict = CountryLabels.IsConflict(parsed.DisplayName),
            Semantics = semantics,
            Digest = digest,
            StrippedPolicy = parsed.StrippedPolicy,
        };
    }

    private static bool LooksLikeHtml(string text)
    {
        var head = text.TrimStart()[..Math.Min(text.TrimStart().Length, 256)].ToLowerInvariant();
        return head.StartsWith("<!doctype", StringComparison.Ordinal) || head.StartsWith("<html", StringComparison.Ordinal) ||
               head.StartsWith("<head", StringComparison.Ordinal);
    }

    private static bool LooksLikeYaml(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("proxies:", StringComparison.Ordinal) ||
               trimmed.Contains("\nproxies:", StringComparison.Ordinal) ||
               trimmed.StartsWith("---", StringComparison.Ordinal);
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static (int? Interval, string? Title) ReadAdvisory(string text)
    {
        int? interval = null;
        string? title = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '/', ' ').Trim();
            const string intervalKey = "profile-update-interval:";
            const string titleKey = "profile-title:";
            if (line.StartsWith(intervalKey, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[intervalKey.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                interval = value;
            }

            if (line.StartsWith(titleKey, StringComparison.OrdinalIgnoreCase))
            {
                title = DisplayName.Sanitize(line[titleKey.Length..]);
            }
        }

        return (interval, title);
    }

    private static ImportBatch Single(RecordDisposition disposition, string reason)
    {
        return new ImportBatch
        {
            Records = [new ImportRecord { Disposition = disposition, ReasonCode = reason }],
            EmptyValidDocument = false,
            DocumentValid = false,
        };
    }

    private static ImportRecord Invalid(string reason)
    {
        return new ImportRecord { Disposition = RecordDisposition.Invalid, ReasonCode = reason };
    }
}

public sealed record ParsedNode
{
    public NodeSemantics? Semantics { get; init; }
    public string? DisplayName { get; init; }
    public RecordDisposition Disposition { get; init; } = RecordDisposition.Invalid;
    public string? ReasonCode { get; init; }
    public IReadOnlyList<string> StrippedPolicy { get; init; } = [];
}

public static class Base64Text
{
    public static bool TryDecode(string text, out string decoded)
    {
        decoded = "";
        var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (compact.Length < 8 || compact.Length % 4 == 1)
        {
            return false;
        }

        var standard = compact.Contains('+') || compact.Contains('/');
        var url = compact.Contains('-') || compact.Contains('_');
        if (standard && url)
        {
            return false;
        }

        var padded = compact.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        if (remainder > 0)
        {
            padded = padded.PadRight(padded.Length + (4 - remainder), '=');
        }

        try
        {
            var bytes = Convert.FromBase64String(padded);
            decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Length > 0 && decoded.All(ch => ch is '\n' or '\r' or '\t' || !char.IsControl(ch));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class JsonSafety
{
    public static void AssertSafe(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            MaxDepth = ProductLimits.MaxJsonDepth,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var sets = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    sets.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (sets.Count > 0)
                    {
                        sets.Pop();
                    }

                    break;
                case JsonTokenType.PropertyName:
                    string name;
                    try
                    {
                        name = reader.GetString() ?? "";
                    }
                    catch (InvalidOperationException)
                    {
                        throw new FormatException(ReasonCodes.InvalidUri);
                    }

                    if (sets.Count == 0 || !sets.Peek().Add(name))
                    {
                        throw new FormatException(ReasonCodes.DuplicateKey);
                    }

                    break;
            }
        }
    }
}

public static class YamlSafety
{
    private static bool IsSafeTag(TagName tag)
    {
        if (tag.IsEmpty || tag.IsNonSpecific)
        {
            return true;
        }

        if (!tag.IsGlobal)
        {
            return false;
        }

        return tag.Value is "tag:yaml.org,2002:str" or "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq"
            or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:bool"
            or "tag:yaml.org,2002:null" or "tag:yaml.org,2002:binary";
    }

    public static void AssertSafe(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var anchors = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case AnchorAlias:
                    throw new FormatException(ReasonCodes.YamlLimit);
                case NodeEvent node:
                    if (!IsSafeTag(node.Tag))
                    {
                        throw new FormatException(ReasonCodes.YamlLimit);
                    }

                    if (!node.Anchor.IsEmpty)
                    {
                        anchors++;
                        if (anchors > ProductLimits.MaxYamlAnchors)
                        {
                            throw new FormatException(ReasonCodes.YamlLimit);
                        }
                    }

                    if (node is MappingStart or SequenceStart)
                    {
                        depth++;
                        if (depth > ProductLimits.MaxYamlDepth)
                        {
                            throw new FormatException(ReasonCodes.YamlLimit);
                        }
                    }

                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
            }
        }
    }
}
