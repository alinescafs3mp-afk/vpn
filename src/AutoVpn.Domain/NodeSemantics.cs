using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AutoVpn.Domain;

public enum ProtocolKind
{
    Unknown = 0,
    Vless = 1,
    Vmess = 2,
    Trojan = 3,
    Shadowsocks = 4,
    Hysteria2 = 5,
    Tuic = 6,
}

public enum SecurityPosture
{
    Accepted = 0,
    PolicyBlocked = 1,
    Unsupported = 2,
    Invalid = 3,
}

/// <summary>
/// Connection semantics only. Display names, source paths, and measurements
/// are intentionally absent so they cannot change identity.
/// </summary>
public sealed record NodeSemantics
{
    public ProtocolKind Protocol { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? UserId { get; init; }
    public string? Password { get; init; }
    public string? Encryption { get; init; }
    public int? AlterId { get; init; }
    public string? Flow { get; init; }
    public string? Security { get; init; }
    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? SpiderX { get; init; }
    public IReadOnlyList<string>? Alpn { get; init; }
    public string? Transport { get; init; }
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? ServiceName { get; init; }
    public string? HeaderType { get; init; }
    public string? Plugin { get; init; }
    public string? PluginOpts { get; init; }
    public bool? Udp { get; init; }
    public bool SkipCertVerify { get; init; }
    public string? Congestion { get; init; }
    public string? UdpRelayMode { get; init; }
    public string? Obfs { get; init; }
    public string? ObfsPassword { get; init; }
    public string? PacketEncoding { get; init; }
    public string? Up { get; init; }
    public string? Down { get; init; }
    public string? HopPorts { get; init; }

    public SecurityPosture Classify(bool allowInsecureCertificates)
    {
        if (Protocol == ProtocolKind.Unknown)
        {
            return SecurityPosture.Unsupported;
        }

        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535)
        {
            return SecurityPosture.Invalid;
        }

        if (EndpointSafety.IsNonPublicHost(Host))
        {
            return SecurityPosture.Invalid;
        }

        if (SkipCertVerify && !allowInsecureCertificates)
        {
            return SecurityPosture.PolicyBlocked;
        }

        if (!HasClosedSecurity())
        {
            return SecurityPosture.Unsupported;
        }

        if (IsPlaintext())
        {
            return SecurityPosture.PolicyBlocked;
        }

        return HasRequiredCredentials() ? SecurityPosture.Accepted : SecurityPosture.Invalid;
    }

    public bool HasClosedSecurity()
    {
        var security = Security?.Trim().ToLowerInvariant();
        return Protocol switch
        {
            ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan => security is null or "" or "none" or "tls" or "reality",
            ProtocolKind.Shadowsocks => security is null or "" or "aead",
            ProtocolKind.Hysteria2 or ProtocolKind.Tuic => security is null or "" or "tls",
            _ => false,
        };
    }

    public bool IsPlaintext()
    {
        var security = Security?.Trim().ToLowerInvariant();
        return Protocol switch
        {
            ProtocolKind.Vless => security is null or "" or "none",
            ProtocolKind.Vmess => security is null or "" or "none",
            ProtocolKind.Trojan => security is null or "" or "none",
            ProtocolKind.Shadowsocks => IsPlainShadowsocks(Encryption),
            ProtocolKind.Hysteria2 => false,
            ProtocolKind.Tuic => false,
            _ => true,
        };
    }

    public static bool IsPlainShadowsocks(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return true;
        }

        return cipher.Trim().ToLowerInvariant() is "none" or "plain" or "dummy";
    }

    public bool HasRequiredCredentials()
    {
        return Protocol switch
        {
            ProtocolKind.Vless => IsUuid(UserId),
            ProtocolKind.Vmess => IsUuid(UserId),
            ProtocolKind.Trojan => !string.IsNullOrEmpty(Password),
            ProtocolKind.Shadowsocks => !string.IsNullOrEmpty(Password) && !string.IsNullOrEmpty(Encryption),
            ProtocolKind.Hysteria2 => !string.IsNullOrEmpty(Password),
            ProtocolKind.Tuic => IsUuid(UserId) && !string.IsNullOrEmpty(Password),
            _ => false,
        };
    }

    public static bool IsUuid(string? value)
    {
        return Guid.TryParseExact(value, "D", out _);
    }
}

public static class CanonicalIdentity
{
    public static NodeSemantics Normalize(NodeSemantics input)
    {
        var alpn = input.Alpn is { Count: > 0 }
            ? input.Alpn.Select(static item => item.Trim()).Where(static item => item.Length > 0).ToArray()
            : null;
        return input with
        {
            Host = NormalizeHost(input.Host),
            UserId = input.UserId?.Trim().ToLowerInvariant(),
            Encryption = input.Protocol == ProtocolKind.Vless
                ? (EmptyToNull(input.Encryption) ?? "none").ToLowerInvariant()
                : EmptyToNull(input.Encryption)?.ToLowerInvariant(),
            Flow = EmptyToNull(input.Flow),
            Security = NormalizeSecurity(input),
            Sni = EmptyToNull(input.Sni),
            Fingerprint = EmptyToNull(input.Fingerprint)?.ToLowerInvariant(),
            PublicKey = EmptyToNull(input.PublicKey),
            ShortId = EmptyToNull(input.ShortId)?.ToLowerInvariant(),
            SpiderX = KeepOpaque(input.SpiderX),
            Alpn = alpn is { Length: > 0 } ? alpn : null,
            Transport = NormalizeTransport(input.Transport),
            Path = KeepOpaque(input.Path),
            HostHeader = EmptyToNull(input.HostHeader),
            ServiceName = KeepOpaque(input.ServiceName),
            HeaderType = NormalizeHeader(input.HeaderType),
            Plugin = EmptyToNull(input.Plugin)?.ToLowerInvariant(),
            PluginOpts = KeepOpaque(input.PluginOpts),
            Congestion = EmptyToNull(input.Congestion)?.ToLowerInvariant(),
            UdpRelayMode = EmptyToNull(input.UdpRelayMode)?.ToLowerInvariant(),
            Obfs = EmptyToNull(input.Obfs)?.ToLowerInvariant(),
            ObfsPassword = KeepOpaque(input.ObfsPassword),
            PacketEncoding = EmptyToNull(input.PacketEncoding)?.ToLowerInvariant(),
            Up = KeepOpaque(input.Up),
            Down = KeepOpaque(input.Down),
            HopPorts = KeepOpaque(input.HopPorts),
            AlterId = input.Protocol == ProtocolKind.Vmess ? input.AlterId ?? 0 : input.AlterId,
        };
    }

    public static string CanonicalJson(NodeSemantics raw) => CanonicalJson(raw, ProductLimits.CanonicalizerVersion);

    public static string CanonicalJson(NodeSemantics raw, int version)
    {
        var node = version <= 1 ? NormalizeLegacy(raw) : Normalize(raw);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", version);
            writer.WriteString("protocol", node.Protocol.ToString().ToLowerInvariant());
            writer.WriteString("host", node.Host);
            writer.WriteNumber("port", node.Port);
            Write(writer, "userId", node.UserId);
            Write(writer, "password", node.Password);
            Write(writer, "encryption", node.Encryption);
            if (node.AlterId is int alter)
            {
                writer.WriteNumber("alterId", alter);
            }

            Write(writer, "flow", node.Flow);
            Write(writer, "security", node.Security);
            Write(writer, "sni", node.Sni);
            Write(writer, "fingerprint", node.Fingerprint);
            Write(writer, "publicKey", node.PublicKey);
            Write(writer, "shortId", node.ShortId);
            Write(writer, "spiderX", node.SpiderX);
            if (node.Alpn is { Count: > 0 })
            {
                writer.WritePropertyName("alpn");
                writer.WriteStartArray();
                foreach (var item in node.Alpn)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            Write(writer, "transport", node.Transport);
            Write(writer, "path", node.Path);
            Write(writer, "hostHeader", node.HostHeader);
            Write(writer, "serviceName", node.ServiceName);
            Write(writer, "headerType", node.HeaderType);
            Write(writer, "plugin", node.Plugin);
            Write(writer, "pluginOpts", node.PluginOpts);
            if (version >= 3)
            {
                writer.WriteBoolean("udp", node.Udp ?? false);
            }
            else if (node.Udp is false)
            {
                writer.WriteBoolean("udp", false);
            }

            writer.WriteBoolean("skipCertVerify", node.SkipCertVerify);
            Write(writer, "congestion", node.Congestion);
            Write(writer, "udpRelayMode", node.UdpRelayMode);
            Write(writer, "obfs", node.Obfs);
            Write(writer, "obfsPassword", node.ObfsPassword);
            Write(writer, "packetEncoding", node.PacketEncoding);
            Write(writer, "up", node.Up);
            Write(writer, "down", node.Down);
            Write(writer, "hopPorts", node.HopPorts);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Digest(NodeSemantics node) => Digest(node, ProductLimits.CanonicalizerVersion);

    public static string Digest(NodeSemantics node, int version)
    {
        var bytes = SHA256Hash(Encoding.UTF8.GetBytes(CanonicalJson(node, version)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] SHA256Hash(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }

    public static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            return ip.ToString();
        }

        var idn = new IdnMapping();
        try
        {
            return idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return trimmed.ToLowerInvariant();
        }
    }

    private static string NormalizeSecurity(NodeSemantics input)
    {
        if (!string.IsNullOrWhiteSpace(input.PublicKey))
        {
            return "reality";
        }

        var value = EmptyToNull(input.Security)?.ToLowerInvariant();
        if (value is "tls" or "reality" or "none")
        {
            return value;
        }

        if (input.Protocol is ProtocolKind.Hysteria2 or ProtocolKind.Tuic)
        {
            return "tls";
        }

        if (input.Protocol == ProtocolKind.Trojan && value is null)
        {
            return "tls";
        }

        if (input.Protocol == ProtocolKind.Shadowsocks)
        {
            return "aead";
        }

        return value ?? "none";
    }

    private static NodeSemantics NormalizeLegacy(NodeSemantics input)
    {
        var current = Normalize(input);
        return current with
        {
            Path = EmptyToNull(input.Path),
            ServiceName = EmptyToNull(input.ServiceName),
            PluginOpts = EmptyToNull(input.PluginOpts),
            ObfsPassword = EmptyToNull(input.ObfsPassword),
            SpiderX = EmptyToNull(input.SpiderX),
            Up = EmptyToNull(input.Up),
            Down = EmptyToNull(input.Down),
            HopPorts = EmptyToNull(input.HopPorts),
        };
    }

    private static string NormalizeTransport(string? transport)
    {
        var value = EmptyToNull(transport)?.ToLowerInvariant() ?? "tcp";
        return value == "raw" ? "tcp" : value;
    }

    private static string? NormalizeHeader(string? header)
    {
        var value = EmptyToNull(header)?.ToLowerInvariant();
        return value is null or "none" ? null : value;
    }

    private static string? EmptyToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? KeepOpaque(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

public static class EndpointSafety
{
    private static readonly string[] MetadataHosts =
    [
        "metadata.google.internal",
        "metadata.google.com",
        "metadata",
    ];

    public static bool IsNonPublicHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var normalized = CanonicalIdentity.NormalizeHost(host);
        if (MetadataHosts.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalized, out var ip) && IsNonPublicAddress(ip);
    }

    public static bool IsNonPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            return false;
        }

        var v4 = ip.GetAddressBytes();
        if (v4.Length != 4)
        {
            return true;
        }

        return v4[0] switch
        {
            0 or 10 or 127 => true,
            169 when v4[1] == 254 => true,
            172 when v4[1] is >= 16 and <= 31 => true,
            192 when v4[1] == 168 => true,
            100 when v4[1] is >= 64 and <= 127 => true,
            192 when v4[1] == 0 && v4[2] == 0 => true,
            192 when v4[1] == 0 && v4[2] == 2 => false,
            198 when v4[1] == 18 => true,
            255 => true,
            _ when v4[0] >= 224 => true,
            _ => false,
        };
    }

    public static bool IsDocumentationAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return ip.GetAddressBytes()[0] == 0x20 && ip.GetAddressBytes()[1] == 0x01 &&
                   ip.GetAddressBytes()[2] == 0x0d && ip.GetAddressBytes()[3] == 0xb8;
        }

        var b = ip.GetAddressBytes();
        return (b[0], b[1], b[2]) is (192, 0, 2) or (198, 51, 100) or (203, 0, 113);
    }
}
