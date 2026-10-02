using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Import;

public static class XrayOutboundParser
{
    public static ParsedNode? Parse(JsonElement node, ref int stripped)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return new ParsedNode { Disposition = RecordDisposition.Invalid, ReasonCode = ReasonCodes.InvalidUri };
        }

        var protocol = GetString(node, "protocol") ?? GetString(node, "type");
        if (protocol is null)
        {
            return new ParsedNode { Disposition = RecordDisposition.Invalid, ReasonCode = ReasonCodes.InvalidUri };
        }

        if (protocol.Equals("freedom", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("blackhole", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("dns", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            stripped++;
            return null;
        }

        var kind = protocol.ToLowerInvariant() switch
        {
            "vless" => ProtocolKind.Vless,
            "vmess" => ProtocolKind.Vmess,
            "trojan" => ProtocolKind.Trojan,
            "shadowsocks" or "ss" => ProtocolKind.Shadowsocks,
            "hysteria2" or "hy2" => ProtocolKind.Hysteria2,
            "tuic" => ProtocolKind.Tuic,
            _ => ProtocolKind.Unknown,
        };
        if (kind == ProtocolKind.Unknown)
        {
            return new ParsedNode
            {
                Disposition = RecordDisposition.Unsupported,
                ReasonCode = ReasonCodes.UnsupportedProtocol,
                DisplayName = GetString(node, "tag"),
            };
        }

        var settings = node.TryGetProperty("settings", out var settingsNode) ? settingsNode : default;
        var stream = node.TryGetProperty("streamSettings", out var streamNode) ? streamNode : default;
        var strippedNames = new List<string>();
        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("sockopt", out var sockopt) &&
            sockopt.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sockopt.EnumerateObject())
            {
                if (property.NameEquals("dialerProxy") || property.Name.StartsWith("tcpKeepAlive", StringComparison.Ordinal))
                {
                    strippedNames.Add(property.Name);
                }
                else
                {
                    return new ParsedNode
                    {
                        Disposition = RecordDisposition.Unsupported,
                        ReasonCode = ReasonCodes.UnsupportedTransport,
                        DisplayName = GetString(node, "tag"),
                    };
                }
            }

            stripped++;
        }

        string? host = null;
        int port = 0;
        string? user = null;
        string? password = null;
        string? encryption = null;
        string? flow = null;
        int? alterId = null;
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("vnext", out var vnext) &&
            vnext.ValueKind == JsonValueKind.Array && vnext.GetArrayLength() > 0)
        {
            var first = vnext[0];
            host = GetString(first, "address");
            port = first.TryGetProperty("port", out var portNode) && portNode.TryGetInt32(out var parsed) ? parsed : 0;
            if (first.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
            {
                var userNode = users[0];
                user = GetString(userNode, "id");
                encryption = GetString(userNode, "encryption");
                flow = GetString(userNode, "flow");
                if (userNode.TryGetProperty("alterId", out var aid) && aid.TryGetInt32(out var aidValue))
                {
                    alterId = aidValue;
                }

                password = GetString(userNode, "password");
            }
        }
        else if (settings.ValueKind == JsonValueKind.Object)
        {
            host = GetString(settings, "address") ?? GetString(settings, "server");
            if (settings.TryGetProperty("port", out var portNode) && portNode.TryGetInt32(out var parsed))
            {
                port = parsed;
            }

            password = GetString(settings, "password");
            encryption = GetString(settings, "method") ?? GetString(settings, "encryption");
            user = GetString(settings, "id");
        }

        var network = stream.ValueKind == JsonValueKind.Object ? GetString(stream, "network") : null;
        var security = stream.ValueKind == JsonValueKind.Object ? GetString(stream, "security") : null;
        string? fingerprint = null;
        string? publicKey = null;
        string? shortId = null;
        string? sni = null;
        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("realitySettings", out var reality))
        {
            fingerprint = GetString(reality, "fingerprint");
            publicKey = GetString(reality, "publicKey");
            shortId = GetString(reality, "shortId");
            sni = GetString(reality, "serverName");
        }

        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("tlsSettings", out var tls))
        {
            sni ??= GetString(tls, "serverName");
            fingerprint ??= GetString(tls, "fingerprint");
        }

        return new ParsedNode
        {
            DisplayName = GetString(node, "tag"),
            Disposition = RecordDisposition.Pending,
            StrippedPolicy = strippedNames,
            Semantics = new NodeSemantics
            {
                Protocol = kind,
                Host = host ?? "",
                Port = port,
                UserId = user,
                Password = password,
                Encryption = encryption,
                AlterId = alterId,
                Flow = flow,
                Security = publicKey is not null ? "reality" : security,
                Sni = sni,
                Fingerprint = fingerprint,
                PublicKey = publicKey,
                ShortId = shortId,
                Transport = network,
            },
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
