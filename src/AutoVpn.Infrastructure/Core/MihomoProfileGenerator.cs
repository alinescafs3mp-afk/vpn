using System.Globalization;
using System.Text;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Core;

public sealed record ProfileBuildRequest
{
    public required string Secret { get; init; }
    public required int ControllerPort { get; init; }
    public int? SocksPort { get; init; }
    public bool Tun { get; init; }
    public bool LanAccess { get; init; } = true;
    public string TunDeviceName { get; init; } = "AutoVPN";
    public string TunAddress { get; init; } = "172.19.0.1/30";
    public string TunAddress6 { get; init; } = "fdfe:dcba:9876::1/126";
    public int Mtu { get; init; } = 1500;
    public string Stack { get; init; } = "mixed";
    public bool AllowInsecureCertificates { get; init; }
    public required IReadOnlyList<NodeWire> Nodes { get; init; }
    public string? SelectedNodeId { get; init; }
}

public static class MihomoProfileGenerator
{
    public static string Build(ProfileBuildRequest request)
    {
        if (request.Secret.Length < 16)
        {
            throw new InvalidOperationException("Controller secret is too short.");
        }

        if (request.ControllerPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Controller port is invalid.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("allow-lan: false");
        builder.AppendLine("bind-address: 127.0.0.1");
        builder.AppendLine("mode: rule");
        builder.AppendLine("log-level: info");
        builder.AppendLine("ipv6: true");
        builder.AppendLine("find-process-mode: 'off'");
        builder.AppendLine($"external-controller: '127.0.0.1:{request.ControllerPort.ToString(CultureInfo.InvariantCulture)}'");
        builder.AppendLine($"secret: '{Yaml(request.Secret)}'");
        builder.AppendLine("external-ui: ''");
        builder.AppendLine("profile:");
        builder.AppendLine("  store-selected: false");
        if (request.SocksPort is int socks)
        {
            builder.AppendLine("listeners:");
            builder.AppendLine("  - name: probe-socks");
            builder.AppendLine("    type: socks");
            builder.AppendLine($"    port: {socks.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine("    listen: 127.0.0.1");
        }

        if (request.Tun)
        {
            builder.AppendLine("tun:");
            builder.AppendLine("  enable: true");
            builder.AppendLine($"  device: '{Yaml(request.TunDeviceName)}'");
            builder.AppendLine($"  stack: '{Yaml(request.Stack)}'");
            builder.AppendLine("  auto-route: true");
            builder.AppendLine("  auto-detect-interface: true");
            builder.AppendLine("  strict-route: true");
            builder.AppendLine($"  mtu: {request.Mtu.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"  inet4-address: ['{Yaml(request.TunAddress)}']");
            builder.AppendLine($"  inet6-address: ['{Yaml(request.TunAddress6)}']");
            builder.AppendLine("  dns-hijack:");
            builder.AppendLine("    - any:53");
            builder.AppendLine("    - tcp://any:53");
        }

        builder.AppendLine("dns:");
        builder.AppendLine("  enable: true");
        builder.AppendLine("  ipv6: true");
        builder.AppendLine("  enhanced-mode: redir-host");
        builder.AppendLine("  nameserver:");
        builder.AppendLine("    - https://1.1.1.1/dns-query");
        builder.AppendLine("    - https://8.8.8.8/dns-query");
        builder.AppendLine("  proxy-server-nameserver:");
        builder.AppendLine("    - 1.1.1.1");
        builder.AppendLine("    - 8.8.8.8");
        builder.AppendLine("proxies:");
        if (request.Nodes.Count == 0)
        {
            builder.AppendLine("  []");
        }

        foreach (var node in request.Nodes)
        {
            AppendProxy(builder, node, request.AllowInsecureCertificates);
        }

        var names = new List<string>(request.Nodes.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in request.Nodes)
        {
            var name = CoreNodeName(node);
            if (name.Length < 2 || !seenNames.Add(name))
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            names.Add(name);
        }

        var selected = request.Nodes.FirstOrDefault(node => node.NodeId == request.SelectedNodeId);
        builder.AppendLine("proxy-groups:");
        builder.AppendLine("  - name: AUTO_SELECT");
        builder.AppendLine("    type: select");
        builder.AppendLine("    proxies:");
        if (names.Count == 0)
        {
            builder.AppendLine("      - REJECT");
        }
        else
        {
            if (selected is not null)
            {
                builder.AppendLine($"      - '{CoreNodeName(selected)}'");
            }

            foreach (var name in names.Where(name => selected is null || name != CoreNodeName(selected)))
            {
                builder.AppendLine($"      - '{name}'");
            }
        }

        builder.AppendLine("rules:");
        builder.AppendLine("  - AND,((NETWORK,udp),(DST-PORT,53)),AUTO_SELECT");
        builder.AppendLine("  - AND,((NETWORK,tcp),(DST-PORT,53)),AUTO_SELECT");
        if (request.LanAccess)
        {
            foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16" })
            {
                builder.AppendLine($"  - IP-CIDR,{cidr},DIRECT,no-resolve");
            }

            builder.AppendLine("  - IP-CIDR6,fc00::/7,DIRECT,no-resolve");
            builder.AppendLine("  - IP-CIDR6,fe80::/10,DIRECT,no-resolve");
            builder.AppendLine("  - IP-CIDR6,::1/128,DIRECT,no-resolve");
        }

        builder.AppendLine(names.Count == 0 ? "  - MATCH,REJECT" : "  - MATCH,AUTO_SELECT");
        return builder.ToString();
    }

    public static string CoreNodeName(NodeWire node)
    {
        var safe = new string(node.NodeId.Where(ch => char.IsAsciiLetterOrDigit(ch)).ToArray());
        return "n" + safe;
    }

    public static bool EnablesTun(string yaml)
    {
        return yaml.Contains("tun:", StringComparison.Ordinal) && yaml.Contains("enable: true", StringComparison.Ordinal);
    }

    private static void AppendProxy(StringBuilder builder, NodeWire node, bool allowInsecureCertificates)
    {
        if (EndpointSafety.IsNonPublicHost(node.Host))
        {
            throw new InvalidOperationException(ReasonCodes.NonPublicEndpoint);
        }

        if (node.SkipCertVerify && !allowInsecureCertificates)
        {
            throw new InvalidOperationException(ReasonCodes.CertVerificationDisabled);
        }

        var type = TypeName(node.Protocol);
        if (type.Length == 0 || type.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
        }

        if (!AcceptedSecurity(type, node.Security))
        {
            throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
        }

        builder.AppendLine($"  - name: '{CoreNodeName(node)}'");
        builder.AppendLine($"    type: {type}");
        builder.AppendLine($"    server: '{Yaml(node.Host)}'");
        builder.AppendLine($"    port: {node.Port.ToString(CultureInfo.InvariantCulture)}");
        Write(builder, "uuid", node.UserId);
        Write(builder, "password", node.Password);
        Write(builder, "cipher", type is "ss" or "vmess" ? node.Encryption : null);
        Write(builder, "encryption", type == "vless" ? node.Encryption : null);
        if (node.AlterId is int alter)
        {
            builder.AppendLine($"    alterId: {alter.ToString(CultureInfo.InvariantCulture)}");
        }

        if (node.Udp is bool udp)
        {
            builder.AppendLine($"    udp: {(udp ? "true" : "false")}");
        }

        Write(builder, "flow", node.Flow);
        if (node.Security is "tls" or "reality")
        {
            builder.AppendLine("    tls: true");
        }

        if (node.SkipCertVerify)
        {
            builder.AppendLine("    skip-cert-verify: true");
        }

        Write(builder, SniKey(type), node.Sni);
        Write(builder, "client-fingerprint", node.Fingerprint);
        Write(builder, "network", node.Transport);
        Write(builder, "packet-encoding", node.PacketEncoding);
        Write(builder, "up", node.Up);
        Write(builder, "down", node.Down);
        if (type == "hysteria2")
        {
            Write(builder, "ports", node.HopPorts);
        }
        if (node.PublicKey is not null)
        {
            builder.AppendLine("    reality-opts:");
            builder.AppendLine($"      public-key: '{Yaml(node.PublicKey)}'");
            if (node.ShortId is not null)
            {
                builder.AppendLine($"      short-id: '{Yaml(node.ShortId)}'");
            }

            if (node.SpiderX is not null)
            {
                builder.AppendLine($"      spider-x: '{Yaml(node.SpiderX)}'");
            }
        }

        if (node.Alpn is { Length: > 0 })
        {
            builder.AppendLine("    alpn:");
            foreach (var item in node.Alpn)
            {
                builder.AppendLine($"      - '{Yaml(item)}'");
            }
        }

        AppendTransport(builder, node);

        Write(builder, "plugin", node.Plugin);
        if (!string.IsNullOrEmpty(node.PluginOpts))
        {
            AppendPluginOpts(builder, node.PluginOpts);
        }

        Write(builder, "obfs", node.Obfs);
        Write(builder, "obfs-password", node.ObfsPassword);
        Write(builder, "congestion-controller", node.Congestion);
        Write(builder, "udp-relay-mode", node.UdpRelayMode);
    }

    private static bool AcceptedSecurity(string type, string? security)
    {
        var value = security?.Trim().ToLowerInvariant();
        return type switch
        {
            "vless" or "vmess" or "trojan" => value is "tls" or "reality",
            "ss" => value is null or "" or "aead",
            "hysteria2" or "tuic" => value is null or "" or "tls",
            _ => false,
        };
    }

    private static string SniKey(string type)
    {
        return type is "trojan" or "hysteria2" or "tuic" ? "sni" : "servername";
    }

    private static void AppendTransport(StringBuilder builder, NodeWire node)
    {
        var transport = node.Transport?.Trim().ToLowerInvariant();
        if (node.HeaderType is not null && transport is not ("http" or "h2"))
        {
            throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
        }

        var hasPath = node.Path is not null || node.HostHeader is not null;
        var hasGrpc = node.ServiceName is not null;
        if (transport is "ws" or "websocket")
        {
            if (hasGrpc)
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            AppendWs(builder, node);
            return;
        }

        if (transport == "grpc")
        {
            if (node.Path is not null)
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            if (hasGrpc)
            {
                builder.AppendLine("    grpc-opts:");
                builder.AppendLine($"      grpc-service-name: '{Yaml(node.ServiceName!)}'");
            }

            return;
        }

        if (transport is "http" or "h2")
        {
            if (hasGrpc)
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            AppendHttp(builder, node);
            return;
        }

        if (hasPath || hasGrpc)
        {
            throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
        }
    }

    private static void AppendWs(StringBuilder builder, NodeWire node)
    {
        if (node.Path is null && node.HostHeader is null)
        {
            return;
        }

        builder.AppendLine("    ws-opts:");
        if (node.Path is not null)
        {
            builder.AppendLine($"      path: '{Yaml(node.Path)}'");
        }

        if (node.HostHeader is not null)
        {
            builder.AppendLine("      headers:");
            builder.AppendLine($"        Host: '{Yaml(node.HostHeader)}'");
        }
    }

    private static void AppendHttp(StringBuilder builder, NodeWire node)
    {
        if (node.Path is null && node.HostHeader is null)
        {
            return;
        }

        builder.AppendLine("    http-opts:");
        if (node.Path is not null)
        {
            builder.AppendLine($"      path:");
            builder.AppendLine($"        - '{Yaml(node.Path)}'");
        }

        if (node.HostHeader is not null)
        {
            builder.AppendLine("      headers:");
            builder.AppendLine($"        Host:");
            builder.AppendLine($"          - '{Yaml(node.HostHeader)}'");
        }
    }

    private static string TypeName(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            "shadowsocks" => "ss",
            "hysteria2" => "hysteria2",
            _ => protocol.ToLowerInvariant(),
        };
    }

    private static void Write(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.AppendLine($"    {key}: '{Yaml(value)}'");
        }
    }

    private static void AppendPluginOpts(StringBuilder builder, string opts)
    {
        builder.AppendLine("    plugin-opts:");
        foreach (var part in opts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            var key = part[..eq];
            if (key is not ("obfs" or "obfs-host" or "mode" or "host" or "path" or "tls"))
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }

            builder.AppendLine($"      {key}: '{Yaml(part[(eq + 1)..])}'");
        }
    }

    private static string Yaml(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029' or '\u0085')
            {
                throw new InvalidOperationException(ReasonCodes.CoreConfigRejected);
            }
        }

        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
