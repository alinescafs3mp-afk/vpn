using System.Text.Json.Serialization;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Core;

/// <summary>
/// A bounded, immutable selection for the unprivileged non-TUN runtime.
/// This is not an IPC payload, an executable profile, or a reachability result.
/// Credentials stay in the internal snapshot; only its original canonical digest is public.
/// </summary>
public sealed class RuntimeNodeSelection
{
    private const int MaxScalarBytes = 4096;
    private const int MaxSelectionBytes = 16 * 1024;
    private const int MaxAlpnCount = 16;
    private const int MaxAlpnBytes = 255;

    [JsonIgnore] internal NodeSemantics Node { get; }
    public string Digest { get; }

    private RuntimeNodeSelection(NodeSemantics node, string digest)
    {
        Node = node;
        Digest = digest;
    }

    public override string ToString() => $"RuntimeNodeSelection {{ Digest = {Digest} }}";

    public static RuntimeNodeSelection Create(NodeSemantics raw)
    {
        try { return CreateCore(raw); }
        catch (SelectionRejected error) { throw new InvalidDataException(error.Code); }
        catch (Exception)
        {
            // In particular, a caller-supplied IReadOnlyList can throw while being copied.
            // Never propagate its text, an encoder error, or an inner exception with credentials.
            throw new InvalidDataException("RUNTIME_NODE_INVALID");
        }
    }

    private static RuntimeNodeSelection CreateCore(NodeSemantics raw)
    {
        if (raw is null) throw Invalid();
        var bytes = 0;
        // Check every supplied string before normalization, JSON, or canonical hashing.
        // Opaque passwords/paths are never trimmed, repaired, or Unicode-normalized here.
        foreach (var value in new[]
        {
            raw.Host, raw.UserId, raw.Password, raw.Encryption, raw.Flow, raw.Security,
            raw.Sni, raw.Fingerprint, raw.PublicKey, raw.ShortId, raw.SpiderX,
            raw.Transport, raw.Path, raw.HostHeader, raw.ServiceName, raw.HeaderType,
            raw.Plugin, raw.PluginOpts, raw.Congestion, raw.UdpRelayMode, raw.Obfs,
            raw.ObfsPassword, raw.PacketEncoding, raw.Up, raw.Down, raw.HopPorts,
        }) ValidateText(value, MaxScalarBytes, ref bytes);

        var alpn = CopyAlpn(raw.Alpn, ref bytes);
        var snapshot = raw with { Alpn = alpn };
        if (snapshot.Protocol is not (ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or
            ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic))
            throw Unsupported();
        if (snapshot.Port is < 1 or > 65535 ||
            !ProxyEndpointResolver.TryHost(snapshot.Host, out _, out var literal)) throw Invalid();
        if (literal is not null && !ProxyEndpointResolver.IsAllowedAddress(literal))
            throw new SelectionRejected(ReasonCodes.NonPublicEndpoint);
        if (snapshot.SkipCertVerify) throw new SelectionRejected(ReasonCodes.CertVerificationDisabled);

        // These options require separate semantic review. Reject even an empty supplied
        // value instead of silently discarding a field during canonical normalization.
        if (snapshot.Flow is not null || snapshot.Fingerprint is not null ||
            snapshot.PublicKey is not null || snapshot.ShortId is not null || snapshot.SpiderX is not null ||
            snapshot.HeaderType is not null || snapshot.Plugin is not null || snapshot.PluginOpts is not null ||
            snapshot.Obfs is not null || snapshot.ObfsPassword is not null ||
            snapshot.PacketEncoding is not null || snapshot.Up is not null ||
            snapshot.Down is not null || snapshot.HopPorts is not null) throw Unsupported();

        ValidateRawSecurity(snapshot);
        ValidateCredentialFields(snapshot);
        var normalized = CanonicalIdentity.Normalize(snapshot);
        normalized = normalized with
        {
            Alpn = normalized.Alpn is null ? null : Array.AsReadOnly(normalized.Alpn.ToArray()),
        };
        if (!normalized.HasRequiredCredentials()) throw Invalid();
        ValidateCipher(normalized);
        ValidateTransport(normalized);
        ValidateTlsNames(normalized);
        if (normalized.Classify(false) != SecurityPosture.Accepted) throw Unsupported();

        // The execution endpoint is resolved later. Its numeric pin must never replace this identity.
        return new RuntimeNodeSelection(normalized, CanonicalIdentity.Digest(snapshot));
    }

    private static void ValidateRawSecurity(NodeSemantics node)
    {
        var security = node.Security?.Trim().ToLowerInvariant();
        var accepted = node.Protocol switch
        {
            ProtocolKind.Vless or ProtocolKind.Vmess => security is "tls",
            ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic => security is null or "" or "tls",
            ProtocolKind.Shadowsocks => security is null or "" or "aead",
            _ => false,
        };
        // Validate before Normalize, which supplies some protocol defaults.
        if (!accepted) throw Unsupported();
    }

    private static void ValidateCredentialFields(NodeSemantics node)
    {
        if (node.Protocol is ProtocolKind.Vless or ProtocolKind.Vmess)
        {
            if (node.Password is not null) throw Unsupported();
        }
        else if (node.Protocol != ProtocolKind.Tuic && node.UserId is not null) throw Unsupported();
        if (node.Protocol is not (ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Shadowsocks) &&
            node.Encryption is not null) throw Unsupported();
        if (node.Protocol != ProtocolKind.Vmess && node.AlterId is not null) throw Unsupported();
        if (node.Protocol == ProtocolKind.Vmess && node.AlterId is not (null or 0)) throw Unsupported();
        if (node.Protocol != ProtocolKind.Tuic &&
            (node.Congestion is not null || node.UdpRelayMode is not null)) throw Unsupported();
    }

    private static void ValidateCipher(NodeSemantics node)
    {
        if (node.Protocol == ProtocolKind.Vless && node.Encryption != "none") throw Unsupported();
        if (node.Protocol == ProtocolKind.Vmess &&
            node.Encryption is not ("auto" or "aes-128-gcm" or "chacha20-poly1305")) throw Unsupported();
        if (node.Protocol == ProtocolKind.Shadowsocks &&
            node.Encryption is not ("aes-128-gcm" or "aes-256-gcm" or "chacha20-ietf-poly1305"))
            throw Unsupported();
        if (node.Protocol == ProtocolKind.Tuic &&
            (node.Congestion is not (null or "bbr" or "cubic") ||
             node.UdpRelayMode is not (null or "native" or "quic"))) throw Unsupported();
    }

    private static void ValidateTransport(NodeSemantics node)
    {
        var transport = node.Transport == "websocket" ? "ws" : node.Transport;
        if (transport == "tcp")
        {
            // For SS, Hysteria2, and TUIC this is the canonical default transport,
            // not a request to replace their protocol-native transport with TCP.
            if (node.Path is not null || node.HostHeader is not null || node.ServiceName is not null)
                throw Unsupported();
            return;
        }
        if (transport == "ws" && node.Protocol is (ProtocolKind.Vless or ProtocolKind.Vmess))
        {
            if (node.ServiceName is not null) throw Unsupported();
            if (node.Path is not null && !node.Path.StartsWith('/')) throw Invalid();
            // Pinned Mihomo's TLS WebSocket adapter selects HTTP/1.1 itself.
            if (node.Alpn is not null && (node.Alpn.Count != 1 || node.Alpn[0] != "http/1.1"))
                throw Unsupported();
            return;
        }
        if (transport == "grpc" && node.Protocol == ProtocolKind.Vless)
        {
            if (node.Path is not null || node.HostHeader is not null) throw Unsupported();
            if (node.ServiceName is not null && (node.ServiceName.Length > 256 ||
                node.ServiceName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not '_')))
                throw Invalid();
            if (node.Alpn is not null && (node.Alpn.Count != 1 || node.Alpn[0] != "h2"))
                throw Unsupported();
            return;
        }
        throw Unsupported();
    }

    private static void ValidateTlsNames(NodeSemantics node)
    {
        if (node.Protocol == ProtocolKind.Shadowsocks)
        {
            if (node.Sni is not null || node.Alpn is not null) throw Unsupported();
            return;
        }
        if (node.Sni is not null && !IsAsciiTlsName(node.Sni, allowIp: true)) throw Invalid();
        if (node.HostHeader is not null && !IsAsciiTlsName(node.HostHeader, allowIp: false)) throw Invalid();
    }

    private static bool IsAsciiTlsName(string value, bool allowIp)
    {
        // IDN endpoint hosts are accepted and canonicalized above. Explicit SNI/Host
        // values use reviewed ASCII names (punycode is permitted), without a port.
        return value.Length <= 253 && !value.Any(ch => ch > 0x7f) &&
            ProxyEndpointResolver.TryHost(value, out _, out var literal) && (allowIp || literal is null);
    }

    private static IReadOnlyList<string>? CopyAlpn(IReadOnlyList<string>? source, ref int bytes)
    {
        if (source is null) return null;
        var count = source.Count;
        if (count < 0) throw Invalid();
        if (count > MaxAlpnCount) throw TooLarge();
        if (count == 0) return null;
        var copy = new string[count];
        for (var i = 0; i < count; i++)
        {
            // Bound work by the reported count; never enumerate a caller-owned collection.
            var value = source[i];
            if (string.IsNullOrEmpty(value)) throw Invalid();
            ValidateText(value, MaxAlpnBytes, ref bytes);
            if (value.Any(ch => ch < 0x21 || ch > 0x7e)) throw Invalid();
            copy[i] = value;
        }
        return Array.AsReadOnly(copy);
    }

    private static void ValidateText(string? value, int maximumBytes, ref int totalBytes)
    {
        if (value is null) return;
        if (value.Length > maximumBytes) throw TooLarge();
        var bytes = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw Invalid();
                i++;
                bytes += 4;
            }
            else
            {
                if (char.IsLowSurrogate(ch) || char.IsControl(ch) || ch is '\u2028' or '\u2029' or '\uFFFE' or '\uFFFF')
                    throw Invalid();
                bytes += ch <= 0x7f ? 1 : ch <= 0x7ff ? 2 : 3;
            }
            if (bytes > maximumBytes) throw TooLarge();
        }
        if (totalBytes > MaxSelectionBytes - bytes) throw TooLarge();
        totalBytes += bytes;
    }

    private static SelectionRejected Invalid() => new("RUNTIME_NODE_INVALID");
    private static SelectionRejected Unsupported() => new("RUNTIME_NODE_UNSUPPORTED");
    private static SelectionRejected TooLarge() => new("RUNTIME_NODE_TOO_LARGE");
    private sealed class SelectionRejected(string code) : Exception
    {
        internal string Code { get; } = code;
    }
}
