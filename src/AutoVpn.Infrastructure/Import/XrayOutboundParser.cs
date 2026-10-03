using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Import;

public static class XrayOutboundParser
{
    public static IReadOnlyList<ParsedNode> ParseAll(JsonElement node, ref int stripped)
    {
        var parsed = Parse(node, ref stripped);
        if (parsed is null)
        {
            return [];
        }

        if (parsed.Semantics is null)
        {
            return [parsed];
        }

        var endpoints = ReadEndpoints(node);
        if (endpoints.Count == 0)
        {
            return [parsed];
        }

        var nodes = new List<ParsedNode>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            nodes.Add(ApplyEndpoint(parsed, endpoint));
        }

        return nodes;
    }

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

        string? path = null;
        string? hostHeader = null;
        string? serviceName = null;
        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("wsSettings", out var ws) && ws.ValueKind == JsonValueKind.Object)
        {
            path = GetString(ws, "path");
            if (ws.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                hostHeader = GetString(headers, "Host");
            }
        }

        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("grpcSettings", out var grpc) && grpc.ValueKind == JsonValueKind.Object)
        {
            serviceName = GetString(grpc, "serviceName");
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
                Path = path,
                HostHeader = hostHeader,
                ServiceName = serviceName,
            },
        };
    }

    private static ParsedNode ApplyEndpoint(ParsedNode parsed, Endpoint endpoint)
    {
        return parsed with
        {
            Semantics = parsed.Semantics! with
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
                UserId = endpoint.User ?? parsed.Semantics.UserId,
                Password = endpoint.Password ?? parsed.Semantics.Password,
                Encryption = endpoint.Encryption ?? parsed.Semantics.Encryption,
                Flow = endpoint.Flow ?? parsed.Semantics.Flow,
                AlterId = endpoint.AlterId ?? parsed.Semantics.AlterId,
            },
        };
    }

    private readonly record struct Endpoint(string Host, int Port, string? User, string? Password, string? Encryption, string? Flow, int? AlterId);

    private static List<Endpoint> ReadEndpoints(JsonElement node)
    {
        var endpoints = new List<Endpoint>();
        if (!node.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            return endpoints;
        }

        if (settings.TryGetProperty("vnext", out var vnext) && vnext.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in vnext.EnumerateArray())
            {
                var host = GetString(item, "address") ?? "";
                var port = item.TryGetProperty("port", out var portNode) && portNode.TryGetInt32(out var parsedPort) ? parsedPort : 0;
                if (item.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
                {
                    foreach (var userNode in users.EnumerateArray())
                    {
                        int? alterId = userNode.TryGetProperty("alterId", out var aid) && aid.TryGetInt32(out var aidValue) ? aidValue : null;
                        endpoints.Add(new Endpoint(host, port, GetString(userNode, "id"), GetString(userNode, "password"), GetString(userNode, "encryption"), GetString(userNode, "flow"), alterId));
                    }
                }
                else
                {
                    endpoints.Add(new Endpoint(host, port, null, null, null, null, null));
                }
            }
        }

        if (endpoints.Count == 0 && settings.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in servers.EnumerateArray())
            {
                var host = GetString(item, "address") ?? GetString(item, "server") ?? "";
                var port = item.TryGetProperty("port", out var portNode) && portNode.TryGetInt32(out var parsedPort) ? parsedPort : 0;
                endpoints.Add(new Endpoint(host, port, GetString(item, "id"), GetString(item, "password"), GetString(item, "method") ?? GetString(item, "encryption"), null, null));
            }
        }

        return endpoints;
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
