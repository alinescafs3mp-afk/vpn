using System.Buffers.Binary;
using System.Security.Cryptography;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Runtime;
using Xunit;

namespace AutoVpn.UnitTests;
public class AstraV3RuntimeTests
{
    public static NodeWire Node()=>NodeWireFactory.FromCatalogue(new CatalogueNode
    {
        NodeId="v3-node",Digest=CanonicalIdentity.Digest(Semantics()),Semantics=Semantics(),Label="fixture",
    });
    public static NodeSemantics Semantics()=>new(){Protocol=ProtocolKind.Trojan,Host="candidate.example",Port=443,
        Security="tls",Password="synthetic-v3-password",Sni="candidate.example"};
    [Fact]public void TypedNodeMustMatchEffectiveDigest()
    {
        Assert.Equal(Semantics(),LiveNodePolicy.Validate(Node(),false));
        var wrong=new NodeWire{NodeId="v3",Digest=new string('0',64),Protocol="trojan",Host="candidate.example",Port=443,Password="synthetic",Security="tls"};
        Assert.Throws<InvalidDataException>(()=>LiveNodePolicy.Validate(wrong,false));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(32769)][InlineData(int.MaxValue)]
    public async Task OversizedOrInvalidFramesRefuseBeforeAllocation(int length)
    {
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,length);
        using var stream=new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(()=>LiveSessionWire.ReadAsync<LiveSessionRequest>(stream,CancellationToken.None));
    }
    [Fact]public async Task WireRejectsArbitraryConfigFields()
    {
        using var data=new MemoryStream();await LiveSessionWire.WriteAsync(data,new{sequence=1,operation="Start",yaml="arbitrary"},CancellationToken.None);data.Position=0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(()=>LiveSessionWire.ReadAsync<LiveSessionRequest>(data,CancellationToken.None));
    }
    [Fact]public async Task AssetTamperingDoesNotCopyExecutable()
    {
        var dir=Directory.CreateTempSubdirectory("v3-integrity-");
        try{var source=Path.Combine(dir.FullName,"source");var target=Path.Combine(dir.FullName,"target");await File.WriteAllTextAsync(source,"not-an-exe");
        await Assert.ThrowsAsync<InvalidDataException>(()=>LiveCoreSession.CopyVerifiedAsync(source,target,new string('0',64),CancellationToken.None));Assert.False(File.Exists(target));}
        finally{dir.Delete(true);}
    }
    [Fact]public async Task StartFailureCannotReportConnectedOrProtection()
    {
        await using var session=new LiveCoreSession(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()),new string('0',64));
        await session.StartAsync(new(){Node=Node()},CancellationToken.None);
        Assert.Equal("Failed",session.Snapshot.Phase);Assert.False(session.Snapshot.HasOwnedProcess);Assert.False(session.Snapshot.KillSwitchArmed);
        await session.StopAsync();Assert.Equal("Stopped",session.Snapshot.Phase);
    }
    [Fact]public async Task TunRequiresExplicitConsentAndWindowsAdmin()
    {
        await using var session=new LiveCoreSession("missing-core",new string('0',64));
        await session.StartAsync(new(){Node=Node(),Tun=true,AllowUnprotectedTun=false},CancellationToken.None);
        Assert.Equal("Failed",session.Snapshot.Phase);Assert.False(session.Snapshot.HasOwnedProcess);
    }
}
