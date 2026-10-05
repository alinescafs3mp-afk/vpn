using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Runtime;

public static class LiveNodePolicy
{
    public static NodeSemantics Validate(NodeWire node, bool allowInsecure)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.NodeId) || node.NodeId.Length > 128 || node.Digest?.Length != 64)
            throw new InvalidDataException("NODE_IDENTITY");
        if (!Enum.TryParse<ProtocolKind>(node.Protocol, true, out var kind) || !Enum.IsDefined(kind))
            throw new InvalidDataException("NODE_PROTOCOL");
        var semantics = new NodeSemantics
        {
            Protocol=kind, Host=node.Host, Port=node.Port, UserId=node.UserId, Password=node.Password,
            Encryption=node.Encryption, AlterId=node.AlterId, Flow=node.Flow, Security=node.Security,
            Sni=node.Sni, Fingerprint=node.Fingerprint, PublicKey=node.PublicKey, ShortId=node.ShortId,
            SpiderX=node.SpiderX, Alpn=node.Alpn, Transport=node.Transport, Path=node.Path,
            HostHeader=node.HostHeader, ServiceName=node.ServiceName, HeaderType=node.HeaderType,
            Plugin=node.Plugin, PluginOpts=node.PluginOpts, Udp=node.Udp, SkipCertVerify=node.SkipCertVerify,
            Congestion=node.Congestion, UdpRelayMode=node.UdpRelayMode, Obfs=node.Obfs, ObfsPassword=node.ObfsPassword,
            PacketEncoding=node.PacketEncoding, Up=node.Up, Down=node.Down, HopPorts=node.HopPorts,
        };
        if (!string.Equals(CanonicalIdentity.Digest(semantics), node.Digest, StringComparison.Ordinal))
            throw new InvalidDataException("NODE_DIGEST");
        var posture = semantics.Classify(allowInsecure);
        if (posture != SecurityPosture.Accepted)
            throw new InvalidDataException("NODE_POLICY:" + posture);
        return semantics;
    }
}
