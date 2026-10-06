using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Core;

/// <summary>Execution-only copy. Never serialize credentials or replace the catalogue identity.</summary>
public sealed class ProxyEndpointResult
{
    internal ProxyEndpointResult(NodeSemantics? node, string? reason) { ExecutionNode = node; ReasonCode = reason; }
    [JsonIgnore] public NodeSemantics? ExecutionNode { get; }
    public string? ReasonCode { get; }
    public bool Succeeded => ExecutionNode is not null && ReasonCode is null;
    public override string ToString() => $"ProxyEndpointResult {{ Succeeded = {Succeeded}, ReasonCode = {ReasonCode} }}";
}

/// <summary>
/// One bounded lookup, validate every answer, then pin one numeric address.
/// No cache, DNS retry, connection, or reachability claim. Resolver order is retained.
/// A timed-out OS lookup retains its admission slot until it actually completes.
/// </summary>
public sealed class ProxyEndpointResolver
{
    public static ProxyEndpointResolver System { get; } = new(
        (host, token) => Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, token), 4);
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;
    private readonly int _maximum;
    private int _inFlight;
    internal int InFlight => Volatile.Read(ref _inFlight);

    internal ProxyEndpointResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup, int maximum = 4)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        _lookup = lookup; _maximum = maximum;
    }

    public async Task<ProxyEndpointResult> ResolveAsync(NodeSemantics node, TimeSpan budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (token.IsCancellationRequested) return Fail(ReasonCodes.Canceled);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(1)) return Fail("ENDPOINT_DNS_TIMEOUT");
        if (node.Port is < 1 or > 65535 || !TryHost(node.Host, out var host, out var literal))
            return Fail("ENDPOINT_NAME_INVALID");
        var original = node with { Alpn = node.Alpn is null ? null : Array.AsReadOnly(node.Alpn.ToArray()) };
        if (literal is not null)
            return IsAllowedAddress(literal) ? new(original with { Host = host }, null) : Fail(ReasonCodes.NonPublicEndpoint);
        // Plugin-specific host defaults require separate review before numeric rebinding.
        if (!string.IsNullOrEmpty(node.Plugin) || !string.IsNullOrEmpty(node.PluginOpts))
            return Fail("ENDPOINT_BINDING_UNSUPPORTED");

        if (Interlocked.Increment(ref _inFlight) > _maximum)
        {
            Interlocked.Decrement(ref _inFlight);
            return Fail("ENDPOINT_DNS_BUSY");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        try
        {
            // Absolute DNS name prevents search-suffix expansion of an imported name.
            var result = await LookupOwnedAsync(host + ".", deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return Fail(ReasonCodes.Canceled);
            if (deadline.IsCancellationRequested) return Fail("ENDPOINT_DNS_TIMEOUT");
            if (result.Error is not null) return Fail(result.Error);
            var answers = result.Addresses;
            if (answers is null || answers.Length == 0) return Fail("ENDPOINT_DNS_EMPTY");
            if (answers.Length > 32) return Fail("ENDPOINT_DNS_LIMIT");
            // Do not pick a good-looking answer out of a mixed public/private set.
            var copy = new IPAddress[answers.Length];
            for (var i = 0; i < answers.Length; i++)
            {
                var answer = answers[i];
                if (answer is null) return Fail(ReasonCodes.NonPublicEndpoint);
                copy[i] = answer.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPAddress(answer.GetAddressBytes(), answer.ScopeId)
                    : new IPAddress(answer.GetAddressBytes());
                if (!IsAllowedAddress(copy[i])) return Fail(ReasonCodes.NonPublicEndpoint);
            }
            token.ThrowIfCancellationRequested();
            return Pin(original, host, copy[0]);
        }
        catch (OperationCanceledException)
        { return Fail(token.IsCancellationRequested ? ReasonCodes.Canceled : "ENDPOINT_DNS_TIMEOUT"); }
    }

    private async Task<LookupResult> LookupOwnedAsync(string host, CancellationToken token)
    {
        try { return new(await _lookup(host, token).ConfigureAwait(false), null); }
        catch (OperationCanceledException) { return new(null, "ENDPOINT_DNS_CANCELED"); }
        catch (Exception) { return new(null, "ENDPOINT_DNS_FAILED"); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private readonly record struct LookupResult(IPAddress[]? Addresses, string? Error);
    private static ProxyEndpointResult Fail(string reason) => new(null, reason);

    private static ProxyEndpointResult Pin(NodeSemantics node, string host, IPAddress address)
    {
        var transport = node.Transport?.ToLowerInvariant() ?? "tcp";
        var sni = node.Sni;
        var hostHeader = node.HostHeader;
        var tls = node.Security?.ToLowerInvariant() is "tls" or "reality" ||
            node.Protocol is ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic;
        // Mihomo v1.19.32: VLESS/VMess WS uses explicit SNI, then Host, then server.
        if (tls && string.IsNullOrEmpty(sni))
            sni = (node.Protocol is ProtocolKind.Vless or ProtocolKind.Vmess) &&
                (transport is "ws" or "websocket") && !string.IsNullOrEmpty(hostHeader) ? hostHeader : host;
        // WS's HTTP host defaults to the original server (Trojan uses its SNI).
        // HTTP transport similarly derives its default Host from the server.
        if (string.IsNullOrEmpty(hostHeader) && (transport is "ws" or "websocket" or "http"))
            hostHeader = node.Protocol == ProtocolKind.Trojan && (transport is "ws" or "websocket") ? sni : host;
        var numeric = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        return new(node with { Host = numeric, Sni = sni, HostHeader = hostHeader }, null);
    }

    internal static bool TryHost(string? input, out string host, out IPAddress? address)
    {
        host = ""; address = null;
        if (string.IsNullOrEmpty(input) || input.Length > 253 || input.Any(char.IsControl) ||
            input.Any(char.IsWhiteSpace) || input.Contains('%')) return false;
        var raw = input;
        if (raw.StartsWith('['))
        {
            if (!raw.EndsWith(']')) return false;
            raw = raw[1..^1];
            if (!IPAddress.TryParse(raw, out address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else if (raw.Contains('[') || raw.Contains(']')) return false;
        if (address is not null || IPAddress.TryParse(raw, out address))
        {
            host = address.ToString(); return true;
        }
        try { host = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(raw).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        if (host.EndsWith('.')) host = host[..^1];
        if (host.Length is < 3 or > 253) return false;
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length is < 1 or > 63 ||
            label.StartsWith('-') || label.EndsWith('-') ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return false;
        if (host is "metadata.google.internal" or "metadata.google.com" ||
            host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) ||
            host.EndsWith(".home.arpa", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Conservative product policy, not an assertion of global routability.</summary>
    internal static bool IsAllowedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (EndpointSafety.IsNonPublicAddress(address) || EndpointSafety.IsDocumentationAddress(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 192 && b[1] == 88 && b[2] == 99);
        // Deliberately excludes NAT64, IPv4-compatible and non-global space.
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || (b[0] & 0xe0) != 0x20) return false;
        // IETF special-purpose assignments, 6to4 and the newer documentation range.
        return !(b[0] == 0x20 && b[1] == 0x01 && b[2] < 2) &&
            !(b[0] == 0x20 && b[1] == 0x02) &&
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }
}
