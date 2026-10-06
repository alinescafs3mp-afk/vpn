using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;

namespace AutoVpn.Infrastructure.Core;

/// <summary>Private construction only, after a validated selection and bounded endpoint binding.</summary>
internal static class RuntimeNodeProfile
{
    internal static string Build(RuntimeNodeSelection selection, NodeSemantics executionNode,
        int controllerPort, int socksPort, string secret)
    {
        var node = NodeWireFactory.FromCatalogue(new CatalogueNode
        {
            NodeId = selection.Digest, Digest = selection.Digest, Semantics = executionNode, Label = "",
        });
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = secret, ControllerPort = controllerPort, SocksPort = socksPort,
            Tun = false, LanAccess = false, AllowInsecureCertificates = false,
            Nodes = [node], SelectedNodeId = node.NodeId,
        });
    }
}

internal sealed record RuntimeOwnedResources(int ProcessId, int ControllerPort, int SocksPort, string DirectoryPath);
