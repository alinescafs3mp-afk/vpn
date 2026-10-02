using AutoVpn.Application;
using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.Broker;

public static class NodeWireFactory
{
    public static NodeWire FromCatalogue(CatalogueNode node)
    {
        var semantics = node.Semantics;
        return new NodeWire
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            Protocol = semantics.Protocol.ToString().ToLowerInvariant(),
            Host = semantics.Host,
            Port = semantics.Port,
            UserId = semantics.UserId,
            Password = semantics.Password,
            Encryption = semantics.Encryption,
            AlterId = semantics.AlterId,
            Flow = semantics.Flow,
            Security = semantics.Security,
            Sni = semantics.Sni,
            Fingerprint = semantics.Fingerprint,
            PublicKey = semantics.PublicKey,
            ShortId = semantics.ShortId,
            SpiderX = semantics.SpiderX,
            Alpn = semantics.Alpn?.ToArray(),
            Transport = semantics.Transport,
            Path = semantics.Path,
            HostHeader = semantics.HostHeader,
            ServiceName = semantics.ServiceName,
            HeaderType = semantics.HeaderType,
            Plugin = semantics.Plugin,
            PluginOpts = semantics.PluginOpts,
            Udp = semantics.Udp,
            SkipCertVerify = semantics.SkipCertVerify,
            Congestion = semantics.Congestion,
            UdpRelayMode = semantics.UdpRelayMode,
            Obfs = semantics.Obfs,
            ObfsPassword = semantics.ObfsPassword,
            PacketEncoding = semantics.PacketEncoding,
            Up = semantics.Up,
            Down = semantics.Down,
            HopPorts = semantics.HopPorts,
            DisplayLabel = node.Label,
            AdvertisedCountry = node.AdvertisedCountry,
            EndpointKey = semantics.Host + ":" + semantics.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
