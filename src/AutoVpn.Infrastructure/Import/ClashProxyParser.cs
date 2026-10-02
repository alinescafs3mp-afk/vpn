using System.Globalization;
using AutoVpn.Domain;
using YamlDotNet.RepresentationModel;

namespace AutoVpn.Infrastructure.Import;

public static class ClashProxyParser
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "type", "server", "port", "uuid", "password", "cipher", "udp", "tls", "skip-cert-verify",
        "servername", "sni", "client-fingerprint", "flow", "network", "alpn", "plugin", "plugin-opts",
        "reality-opts", "ws-opts", "grpc-opts", "alterId", "encryption", "obfs", "obfs-password",
        "up", "down", "auth", "congestion-controller", "udp-relay-mode",
    };

    public static ParsedNode Parse(YamlMappingNode map)
    {
        var values = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var entry in map.Children)
        {
            if (entry.Key is not YamlScalarNode key || string.IsNullOrEmpty(key.Value))
            {
                return Invalid(ReasonCodes.YamlLimit);
            }

            if (!values.TryAdd(key.Value, entry.Value))
            {
                return Invalid(ReasonCodes.DuplicateKey);
            }

            if (!KnownKeys.Contains(key.Value))
            {
                return new ParsedNode
                {
                    Disposition = RecordDisposition.Unsupported,
                    ReasonCode = ReasonCodes.UnsupportedSecurityOption,
                    DisplayName = Scalar(values, "name"),
                };
            }
        }

        var type = Scalar(values, "type")?.ToLowerInvariant();
        var name = Scalar(values, "name");
        var server = Scalar(values, "server");
        if (!int.TryParse(Scalar(values, "port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return Invalid(ReasonCodes.InvalidPort, name);
        }

        var protocol = type switch
        {
            "vless" => ProtocolKind.Vless,
            "vmess" => ProtocolKind.Vmess,
            "trojan" => ProtocolKind.Trojan,
            "ss" => ProtocolKind.Shadowsocks,
            "hysteria2" or "hy2" => ProtocolKind.Hysteria2,
            "tuic" => ProtocolKind.Tuic,
            _ => ProtocolKind.Unknown,
        };
        if (protocol == ProtocolKind.Unknown)
        {
            return new ParsedNode
            {
                Disposition = RecordDisposition.Unsupported,
                ReasonCode = ReasonCodes.UnsupportedProtocol,
                DisplayName = name,
            };
        }

        string? plugin = Scalar(values, "plugin");
        string? pluginOpts = null;
        if (values.TryGetValue("plugin-opts", out var pluginNode))
        {
            if (pluginNode is not YamlMappingNode pluginMap)
            {
                return Invalid(ReasonCodes.AmbiguousField, name);
            }

            pluginOpts = string.Join(";", pluginMap.Children.Select(child =>
                $"{(child.Key as YamlScalarNode)?.Value}={(child.Value as YamlScalarNode)?.Value}"));
        }

        if (plugin is not null && plugin.ToLowerInvariant() is not ("obfs" or "v2ray-plugin"))
        {
            return new ParsedNode
            {
                Disposition = RecordDisposition.Unsupported,
                ReasonCode = ReasonCodes.UnsupportedTransport,
                DisplayName = name,
            };
        }

        var reality = Mapping(values, "reality-opts");
        var ws = Mapping(values, "ws-opts");
        var grpc = Mapping(values, "grpc-opts");
        var wsHeaders = ws is null ? null : MappingNode(ws, "headers");
        var security = Scalar(reality, "public-key") is not null ? "reality"
            : IsTrue(Scalar(values, "tls")) ? "tls"
            : protocol is ProtocolKind.Hysteria2 or ProtocolKind.Tuic ? "tls"
            : protocol == ProtocolKind.Shadowsocks ? "aead"
            : "none";
        bool? udp = values.ContainsKey("udp") ? IsTrue(Scalar(values, "udp")) : null;
        var semantics = new NodeSemantics
        {
            Protocol = protocol,
            Host = server ?? "",
            Port = port,
            UserId = Scalar(values, "uuid"),
            Password = Scalar(values, "password") ?? Scalar(values, "auth"),
            Encryption = Scalar(values, "cipher") ?? Scalar(values, "encryption"),
            AlterId = int.TryParse(Scalar(values, "alterId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aid) ? aid : null,
            Flow = Scalar(values, "flow"),
            Security = security,
            Sni = Scalar(values, "servername") ?? Scalar(values, "sni"),
            Fingerprint = Scalar(values, "client-fingerprint"),
            PublicKey = Scalar(reality, "public-key"),
            ShortId = Scalar(reality, "short-id"),
            Alpn = Sequence(values, "alpn"),
            Transport = Scalar(values, "network"),
            Path = Scalar(ws, "path"),
            HostHeader = Scalar(wsHeaders, "Host"),
            ServiceName = Scalar(grpc, "grpc-service-name"),
            Plugin = plugin,
            PluginOpts = pluginOpts,
            Udp = udp,
            SkipCertVerify = IsTrue(Scalar(values, "skip-cert-verify")),
            Congestion = Scalar(values, "congestion-controller"),
            UdpRelayMode = Scalar(values, "udp-relay-mode"),
            Obfs = Scalar(values, "obfs"),
            ObfsPassword = Scalar(values, "obfs-password"),
            Up = Scalar(values, "up"),
            Down = Scalar(values, "down"),
        };
        return new ParsedNode { Semantics = semantics, DisplayName = name, Disposition = RecordDisposition.Pending };
    }

    private static string? Scalar(IReadOnlyDictionary<string, YamlNode>? values, string name)
    {
        if (values is null)
        {
            return null;
        }

        foreach (var pair in values)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && pair.Value is YamlScalarNode scalar)
            {
                return scalar.Value;
            }
        }

        return null;
    }

    private static string? Scalar(YamlMappingNode? map, string name)
    {
        if (map is null)
        {
            return null;
        }

        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode key && key.Value?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                entry.Value is YamlScalarNode scalar)
            {
                return scalar.Value;
            }
        }

        return null;
    }

    private static YamlMappingNode? Mapping(IReadOnlyDictionary<string, YamlNode> values, string name)
    {
        foreach (var pair in values)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value as YamlMappingNode;
            }
        }

        return null;
    }

    private static YamlMappingNode? MappingNode(YamlMappingNode map, string name)
    {
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode key && key.Value?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                return entry.Value as YamlMappingNode;
            }
        }

        return null;
    }

    private static string[]? Sequence(IReadOnlyDictionary<string, YamlNode> values, string name)
    {
        foreach (var pair in values)
        {
            if (!pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value is YamlScalarNode scalar && scalar.Value is not null)
            {
                return scalar.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (pair.Value is YamlSequenceNode sequence)
            {
                return sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? "").Where(item => item.Length > 0).ToArray();
            }
        }

        return null;
    }

    private static bool IsTrue(string? value)
    {
        return value is not null && value.Trim().ToLowerInvariant() is "true" or "yes" or "1";
    }

    private static ParsedNode Invalid(string reason, string? name = null)
    {
        return new ParsedNode { Disposition = RecordDisposition.Invalid, ReasonCode = reason, DisplayName = name };
    }
}
