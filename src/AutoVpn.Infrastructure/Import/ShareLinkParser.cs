using System.Globalization;
using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Import;

public static class ShareLinkParser
{
    private static readonly HashSet<string> VlessKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encryption", "security", "sni", "fp", "pbk", "sid", "spx", "type", "flow", "host", "path",
        "servicename", "headertype", "alpn", "allowinsecure", "insecure", "packetencoding",
    };

    private static readonly HashSet<string> TrojanKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "security", "sni", "type", "host", "path", "alpn", "fp", "allowinsecure", "insecure", "servicename", "flow",
    };

    private static readonly HashSet<string> Hy2Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sni", "insecure", "obfs", "obfs-password", "alpn", "mport", "up", "down",
    };

    private static readonly HashSet<string> TuicKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "congestion_control", "udp_relay_mode", "alpn", "sni", "allow_insecure", "insecure",
    };

    public static ParsedNode Parse(string line)
    {
        var trimmed = line.Trim();
        var hash = trimmed.LastIndexOf('#');
        string? fragment = null;
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(trimmed[(hash + 1)..]);
            trimmed = trimmed[..hash];
        }

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return Fail(ReasonCodes.InvalidUri, fragment);
        }

        var scheme = trimmed[..schemeEnd].ToLowerInvariant();
        try
        {
            return scheme switch
            {
                "vless" => ParseVless(trimmed, fragment),
                "vmess" => ParseVmess(trimmed, fragment),
                "trojan" => ParseTrojan(trimmed, fragment),
                "ss" => ParseShadowsocks(trimmed, fragment),
                "hysteria2" or "hy2" => ParseHysteria2(trimmed, fragment),
                "tuic" => ParseTuic(trimmed, fragment),
                _ => new ParsedNode
                {
                    Disposition = RecordDisposition.Unsupported,
                    ReasonCode = ReasonCodes.UnsupportedProtocol,
                    DisplayName = fragment,
                },
            };
        }
        catch (UriFormatException)
        {
            return Fail(ReasonCodes.InvalidUri, fragment);
        }
        catch (JsonException)
        {
            return Fail(ReasonCodes.InvalidUri, fragment);
        }
        catch (FormatException ex)
        {
            if (ex.Message is ReasonCodes.UnsupportedSecurityOption or ReasonCodes.UnsupportedTransport)
            {
                return Unsupported(ex.Message, fragment);
            }

            return Fail(string.IsNullOrWhiteSpace(ex.Message) ? ReasonCodes.InvalidUri : ex.Message, fragment);
        }
    }

    private static ParsedNode ParseVless(string text, string? fragment)
    {
        var uri = new Uri(text);
        var query = QueryMap.Parse(uri.Query, VlessKeys);
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (!NodeSemantics.IsUuid(uuid))
        {
            return Fail(ReasonCodes.InvalidUuid, fragment);
        }

        if (!TryPort(uri, out var port))
        {
            return Fail(ReasonCodes.InvalidPort, fragment);
        }

        var semantics = new NodeSemantics
        {
            Protocol = ProtocolKind.Vless,
            Host = uri.IdnHost,
            Port = port,
            UserId = uuid,
            Encryption = query.Get("encryption"),
            Security = query.Get("security"),
            Sni = query.Get("sni"),
            Fingerprint = query.Get("fp"),
            PublicKey = query.Get("pbk"),
            ShortId = query.Get("sid"),
            SpiderX = query.Get("spx"),
            Transport = query.Get("type"),
            Flow = query.Get("flow"),
            HostHeader = query.Get("host"),
            Path = query.Get("path"),
            ServiceName = query.Get("serviceName") ?? query.Get("servicename"),
            HeaderType = query.Get("headerType") ?? query.Get("headertype"),
            Alpn = SplitAlpn(query.Get("alpn")),
            PacketEncoding = query.Get("packetEncoding"),
            SkipCertVerify = IsInsecure(query.Get("allowInsecure"), query.Get("insecure")),
        };
        return Ok(semantics, fragment);
    }

    private static ParsedNode ParseVmess(string text, string? fragment)
    {
        var payload = text["vmess://".Length..];
        if (!Base64Text.TryDecode(payload, out var json))
        {
            return Fail(ReasonCodes.InvalidUri, fragment);
        }

        JsonSafety.AssertSafe(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(ReasonCodes.InvalidUri, fragment);
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "v", "ps", "add", "port", "id", "aid", "scy", "net", "type", "host", "path", "tls", "sni", "alpn", "fp", "insecure",
        };
        foreach (var property in root.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                return new ParsedNode
                {
                    Disposition = RecordDisposition.Unsupported,
                    ReasonCode = ReasonCodes.UnsupportedSecurityOption,
                    DisplayName = fragment ?? GetString(root, "ps"),
                };
            }
        }

        var portText = GetString(root, "port");
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return Fail(ReasonCodes.InvalidPort, fragment);
        }

        var id = GetString(root, "id");
        if (!NodeSemantics.IsUuid(id))
        {
            return Fail(ReasonCodes.InvalidUuid, fragment);
        }

        var tls = GetString(root, "tls");
        var security = string.IsNullOrWhiteSpace(tls)
            ? "none"
            : tls.Equals("tls", StringComparison.OrdinalIgnoreCase) ? "tls" : tls;
        var semantics = new NodeSemantics
        {
            Protocol = ProtocolKind.Vmess,
            Host = GetString(root, "add") ?? "",
            Port = port,
            UserId = id,
            AlterId = int.TryParse(GetString(root, "aid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aid) ? aid : 0,
            Encryption = GetString(root, "scy") ?? "auto",
            Transport = GetString(root, "net"),
            HeaderType = GetString(root, "type"),
            HostHeader = GetString(root, "host"),
            Path = GetString(root, "path"),
            Security = security,
            Sni = GetString(root, "sni"),
            Fingerprint = GetString(root, "fp"),
            Alpn = SplitAlpn(GetString(root, "alpn")),
            SkipCertVerify = IsInsecure(GetString(root, "insecure"), null),
        };
        return Ok(semantics, fragment ?? GetString(root, "ps"));
    }

    private static ParsedNode ParseTrojan(string text, string? fragment)
    {
        var uri = new Uri(text);
        var query = QueryMap.Parse(uri.Query, TrojanKeys);
        if (!TryPort(uri, out var port))
        {
            return Fail(ReasonCodes.InvalidPort, fragment);
        }

        return Ok(new NodeSemantics
        {
            Protocol = ProtocolKind.Trojan,
            Host = uri.IdnHost,
            Port = port,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Security = query.Get("security") ?? "tls",
            Sni = query.Get("sni"),
            Transport = query.Get("type"),
            HostHeader = query.Get("host"),
            Path = query.Get("path"),
            Alpn = SplitAlpn(query.Get("alpn")),
            Fingerprint = query.Get("fp"),
            ServiceName = query.Get("serviceName"),
            Flow = query.Get("flow"),
            SkipCertVerify = IsInsecure(query.Get("allowInsecure"), query.Get("insecure")),
        }, fragment);
    }

    private static ParsedNode ParseShadowsocks(string text, string? fragment)
    {
        var body = text["ss://".Length..];
        string method;
        string password;
        string host;
        int port;
        string? plugin = null;
        if (body.Contains('@', StringComparison.Ordinal))
        {
            var uri = new Uri("ss://" + body);
            if (!TryPort(uri, out port))
            {
                return Fail(ReasonCodes.InvalidPort, fragment);
            }

            host = uri.IdnHost;
            var user = Uri.UnescapeDataString(uri.UserInfo);
            if (!user.Contains(':', StringComparison.Ordinal) && Base64Text.TryDecode(uri.UserInfo, out var decodedUser))
            {
                user = decodedUser;
            }

            var split = user.Split(':', 2);
            if (split.Length != 2)
            {
                return Fail(ReasonCodes.InvalidUri, fragment);
            }

            method = split[0];
            password = split[1];
            plugin = QueryMap.Parse(uri.Query, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "plugin" }).Get("plugin");
        }
        else
        {
            if (!Base64Text.TryDecode(body, out var decoded))
            {
                return Fail(ReasonCodes.InvalidUri, fragment);
            }

            var at = decoded.LastIndexOf('@');
            if (at <= 0)
            {
                return Fail(ReasonCodes.InvalidUri, fragment);
            }

            var cred = decoded[..at];
            var endpoint = decoded[(at + 1)..];
            var split = cred.Split(':', 2);
            if (split.Length != 2)
            {
                return Fail(ReasonCodes.InvalidUri, fragment);
            }

            method = split[0];
            password = split[1];
            var colon = endpoint.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                return Fail(ReasonCodes.InvalidPort, fragment);
            }

            host = endpoint[..colon].Trim('[', ']');
        }

        if (!IsSupportedCipher(method))
        {
            return new ParsedNode
            {
                Disposition = RecordDisposition.Unsupported,
                ReasonCode = ReasonCodes.UnsupportedSecurityOption,
                DisplayName = fragment,
            };
        }

        string? pluginName = null;
        string? pluginOpts = null;
        if (!string.IsNullOrEmpty(plugin))
        {
            var mapped = MapPlugin(plugin);
            if (mapped is null)
            {
                return new ParsedNode
                {
                    Disposition = RecordDisposition.Unsupported,
                    ReasonCode = ReasonCodes.UnsupportedTransport,
                    DisplayName = fragment,
                };
            }

            pluginName = mapped.Value.Name;
            pluginOpts = mapped.Value.Opts;
        }

        return Ok(new NodeSemantics
        {
            Protocol = ProtocolKind.Shadowsocks,
            Host = host,
            Port = port,
            Encryption = method,
            Password = password,
            Plugin = pluginName,
            PluginOpts = pluginOpts,
        }, fragment);
    }

    private static ParsedNode ParseHysteria2(string text, string? fragment)
    {
        var uri = new Uri(text);
        var query = QueryMap.Parse(uri.Query, Hy2Keys);
        if (!TryPort(uri, out var port))
        {
            return Fail(ReasonCodes.InvalidPort, fragment);
        }

        return Ok(new NodeSemantics
        {
            Protocol = ProtocolKind.Hysteria2,
            Host = uri.IdnHost,
            Port = port,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Security = "tls",
            Sni = query.Get("sni"),
            Alpn = SplitAlpn(query.Get("alpn")),
            Obfs = query.Get("obfs"),
            ObfsPassword = query.Get("obfs-password"),
            Up = query.Get("up"),
            Down = query.Get("down"),
            HopPorts = query.Get("mport"),
            SkipCertVerify = IsInsecure(query.Get("insecure"), null),
        }, fragment);
    }

    private static ParsedNode ParseTuic(string text, string? fragment)
    {
        var uri = new Uri(text);
        var query = QueryMap.Parse(uri.Query, TuicKeys);
        if (!TryPort(uri, out var port))
        {
            return Fail(ReasonCodes.InvalidPort, fragment);
        }

        var user = Uri.UnescapeDataString(uri.UserInfo);
        var split = user.Split(':', 2);
        if (split.Length != 2 || !NodeSemantics.IsUuid(split[0]))
        {
            return Fail(ReasonCodes.InvalidUuid, fragment);
        }

        return Ok(new NodeSemantics
        {
            Protocol = ProtocolKind.Tuic,
            Host = uri.IdnHost,
            Port = port,
            UserId = split[0],
            Password = split[1],
            Security = "tls",
            Congestion = query.Get("congestion_control"),
            UdpRelayMode = query.Get("udp_relay_mode"),
            Alpn = SplitAlpn(query.Get("alpn")),
            Sni = query.Get("sni"),
            SkipCertVerify = IsInsecure(query.Get("allow_insecure"), query.Get("insecure")),
        }, fragment);
    }

    private static bool IsSupportedCipher(string method)
    {
        return method.ToLowerInvariant() is
            "aes-128-gcm" or "aes-256-gcm" or "chacha20-ietf-poly1305" or
            "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305" or
            "aes-128-ctr" or "aes-256-ctr" or "chacha20-ietf" or "xchacha20-ietf-poly1305" or
            "none" or "plain";
    }

    private static (string Name, string Opts)? MapPlugin(string plugin)
    {
        var parts = plugin.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var name = parts[0].ToLowerInvariant();
        if (name is not ("obfs-local" or "obfs" or "v2ray-plugin"))
        {
            return null;
        }

        var options = new List<string>();
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return null;
            }

            var key = part[..eq];
            var optionValue = part[(eq + 1)..];
            if (name is "obfs-local" or "obfs")
            {
                key = key switch
                {
                    "obfs" => "mode",
                    "obfs-host" => "host",
                    _ => key,
                };
            }

            if (key is not ("obfs" or "obfs-host" or "mode" or "host" or "path" or "tls"))
            {
                return null;
            }

            options.Add(key + "=" + optionValue);
        }

        var mapped = name == "obfs-local" ? "obfs" : name;
        return (mapped, string.Join(";", options));
    }

    private static bool TryPort(Uri uri, out int port)
    {
        port = uri.Port;
        return uri.IsAbsoluteUri && port is >= 1 and <= 65535;
    }

    private static bool IsInsecure(string? left, string? right)
    {
        return IsTrue(left) || IsTrue(right);
    }

    private static bool IsTrue(string? value)
    {
        return value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes";
    }

    private static string[]? SplitAlpn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static ParsedNode Ok(NodeSemantics semantics, string? name)
    {
        return new ParsedNode { Semantics = semantics, DisplayName = name, Disposition = RecordDisposition.Pending };
    }

    private static ParsedNode Fail(string reason, string? name)
    {
        return new ParsedNode { Disposition = RecordDisposition.Invalid, ReasonCode = reason, DisplayName = name };
    }

    private static ParsedNode Unsupported(string reason, string? name)
    {
        return new ParsedNode { Disposition = RecordDisposition.Unsupported, ReasonCode = reason, DisplayName = name };
    }
}

internal sealed class QueryMap
{
    private readonly Dictionary<string, string> _values;

    private QueryMap(Dictionary<string, string> values)
    {
        _values = values;
    }

    public string? Get(string name)
    {
        return _values.TryGetValue(name, out var value) ? value : null;
    }

    public static QueryMap Parse(string query, HashSet<string> allowed)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (text.Length == 0)
        {
            return new QueryMap(values);
        }

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var rawKey = eq >= 0 ? part[..eq] : part;
            var rawValue = eq >= 0 ? part[(eq + 1)..] : "";
            var key = Uri.UnescapeDataString(rawKey);
            var value = Uri.UnescapeDataString(rawValue);
            if (!allowed.Contains(key))
            {
                throw new FormatException(ReasonCodes.UnsupportedSecurityOption);
            }

            if (values.TryGetValue(key, out var existing) && !string.Equals(existing, value, StringComparison.Ordinal))
            {
                throw new FormatException(ReasonCodes.AmbiguousField);
            }

            values[key] = value;
        }

        return new QueryMap(values);
    }
}
