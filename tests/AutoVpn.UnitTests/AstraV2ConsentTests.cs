using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.UnitTests;

public sealed class AstraV2ConsentTests
{
    [Fact]
    public async Task RevocationAfterFirstSuccessPreventsSecondDialAndHealth()
    {
        var catalogue = new MemoryCatalogue { Settings = new ProductSettings { DisclosureAccepted = true } };
        var clock = new FakeClock();
        RefreshMerge.Ingest(catalogue, [new IngestArtifact { ArtifactId = "s", FamilyId = "f", Enabled = true,
            Text = "vless://11111111-1111-4111-8111-111111111111@203.0.113.22:443?security=tls&sni=example.com" }], clock.UtcNow, false);
        var transport = new RevokeTransport(catalogue);
        var pair = new TwoTargetProbeTransport(catalogue, transport,
            [new Uri("https://one.example/generate_204"), new Uri("https://two.example/generate_204")]);
        var result = await ProbeCoordinator.CheckAsync(catalogue, pair, pair.PrimaryTarget,
            catalogue.Nodes[0].NodeId, clock.UtcNow, SelectionPurpose.Automatic, CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        Assert.False(result.Published);
        Assert.Null(catalogue.Nodes[0].Assessment);
    }

    private sealed class RevokeTransport(MemoryCatalogue catalogue) : IProbeTransport
    {
        public int Calls;
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken token)
            => throw new InvalidOperationException("Explicit attempt required");
        public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken token)
        {
            Calls++;
            catalogue.Settings = catalogue.Settings with { DisclosureAccepted = false, Revision = 2 };
            return Task.FromResult(new ProbeObservation(true, 20, false, null, 100, ProbeClass.Success,
                target.AbsoluteUri, CanonicalIdentity.Digest(node), "test-worker") { Attempt = admission.Attempt });
        }
    }
}
