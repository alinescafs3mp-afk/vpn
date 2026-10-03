using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
using Xunit;

namespace AutoVpn.UnitTests;

public sealed class IndependentRound5WireTests
{
    [Theory]
    [InlineData(" Transfer-Encoding: chunked")]
    [InlineData("X Bad: value")]
    [InlineData("X\0Bad: value")]
    public async Task W01_InvalidFieldNamesCannotAuthenticateAsValidHttp(string field)
    {
        await using var peer = new Peer("HTTP/1.1 204 No Content\r\n" + field + "\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(peer.Endpoint, peer.Target, TimeSpan.FromSeconds(12), peer.Trust, CancellationToken.None);
        Assert.True(result.Authenticated, peer.Error);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task W02_ControlValidAuthenticated204IsAccepted()
    {
        await using var peer = new Peer("HTTP/1.1 204 No Content\r\nX-Test: value\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(peer.Endpoint, peer.Target, TimeSpan.FromSeconds(12), peer.Trust, CancellationToken.None);
        Assert.True(result.Authenticated, peer.Error); Assert.Null(result.Failure); Assert.Equal(204,result.Status);
    }

    [Fact]
    public async Task W03_ControlUntrustedCertificateIsRejected()
    {
        await using var peer = new Peer("HTTP/1.1 204 No Content\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(peer.Endpoint, peer.Target, TimeSpan.FromSeconds(12), null, CancellationToken.None);
        Assert.False(result.Authenticated);
    }

    // These tests require a separately hash-checked core and are selected only by the native jobs.
    [Fact]
    public async Task Native_Round5_RealCorePositiveAndWrongCredentialNegative()
    {
        var binary = Environment.GetEnvironmentVariable("R5_CORE_PATH"); var hash = Environment.GetEnvironmentVariable("R5_CORE_HASH");
        Assert.False(string.IsNullOrWhiteSpace(binary)); Assert.False(string.IsNullOrWhiteSpace(hash));
        using var certificate = Certificate();
        await using var targetPeer = await TlsPeer.StartAsync(certificate,"HTTP/1.1 204 No Content\r\n\r\n");
        const string password = "round5-synthetic-ss-secret";
        await using var remote = await ShadowsocksAeadServer.StartAsync(password,targetPeer.Port);
        var node = new NodeSemantics { Protocol=ProtocolKind.Shadowsocks, Host="candidate.example",Port=remote.Port,Password=password,Encryption="aes-256-gcm" };
        var fixture = new ProbeEndpointFixture { LoopbackHosts = new Dictionary<string,string> { ["candidate.example"]="127.0.0.1",["probe.example"]="127.0.0.1" }, TrustAnchors=new X509Certificate2Collection(certificate) };
        var target=new Uri("https://probe.example:" + targetPeer.Port.ToString(CultureInfo.InvariantCulture) + "/generate_204");
        var transport=new NonTunCoreProbeTransport(binary,hash,TimeSpan.FromSeconds(20),fixture);
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var good=await transport.ProbeAsync(node,target,budget.Token);
        Assert.True(good.Success,transport.LastDiagnostic); Assert.True(remote.Handshakes>0); Assert.True(targetPeer.Accepts>0);
        Assert.Equal(CanonicalIdentity.Digest(node),good.CandidateDigest); Assert.False(string.IsNullOrEmpty(good.WorkerId));
        var before=targetPeer.Accepts;
        var broken=await transport.ProbeAsync(node with { Password="wrong-round5-synthetic-secret" },target,budget.Token);
        Assert.False(broken.Success,transport.LastDiagnostic); Assert.Equal(before,targetPeer.Accepts);
    }

    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("ss")]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    [InlineData("vless-ws")]
    [InlineData("vless-grpc")]
    [InlineData("vless-h2")]
    public async Task Native_Round5_ProtocolProfileAcceptedByExactCore(string variant)
    {
        var binary=Environment.GetEnvironmentVariable("R5_CORE_PATH"); var hash=Environment.GetEnvironmentVariable("R5_CORE_HASH");
        Assert.False(string.IsNullOrWhiteSpace(binary)); Assert.False(string.IsNullOrWhiteSpace(hash));
        var protocol=variant switch { "vmess"=>ProtocolKind.Vmess,"trojan"=>ProtocolKind.Trojan,"ss"=>ProtocolKind.Shadowsocks,"hysteria2"=>ProtocolKind.Hysteria2,"tuic"=>ProtocolKind.Tuic,_=>ProtocolKind.Vless };
        var node=new NodeSemantics { Protocol=protocol,Host="candidate.example",Port=443,UserId="11111111-1111-4111-8111-111111111111",Password="synthetic-only",
            Security=protocol==ProtocolKind.Shadowsocks?"aead":"tls",Encryption=protocol==ProtocolKind.Shadowsocks?"aes-256-gcm":protocol==ProtocolKind.Vmess?"auto":"none",
            Sni="candidate.example",Transport=variant=="vless-ws"?"ws":variant=="vless-grpc"?"grpc":variant=="vless-h2"?"h2":"tcp",
            Path=variant is "vless-ws" or "vless-h2"?"/opaque":null,ServiceName=variant=="vless-grpc"?"synthetic-service":null,Congestion=protocol==ProtocolKind.Tuic?"bbr":null };
        var record=new CatalogueNode { NodeId="synthetic",Digest=CanonicalIdentity.Digest(node),Semantics=node,Label="synthetic" };
        var yaml=MihomoProfileGenerator.Build(new ProfileBuildRequest { Secret="synthetic-controller-secret",ControllerPort=12789,Tun=false,ExternalController=false,LanAccess=false,
            LoopbackHosts=new Dictionary<string,string> { ["candidate.example"]="127.0.0.1" },Nodes=[NodeWireFactory.FromCatalogue(record)],SelectedNodeId=record.NodeId });
        Assert.False(MihomoProfileGenerator.EnablesTun(yaml));
        var result=await MihomoProcessController.ValidateAsync(binary!,hash!,yaml,CancellationToken.None);
        Assert.True(result.Ok,result.ReasonCode + ":" + result.RedactedOutput);
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=probe.example",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("probe.example");request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx,"round5"),"round5",X509KeyStorageFlags.Exportable);
    }
    private sealed class Peer:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new(TimeSpan.FromSeconds(20));
        private readonly X509Certificate2 _certificate=Certificate();private readonly Task _server;
        public string? Error { get;private set; }
        public Uri Target { get; }=new("https://probe.example/generate_204");
        public IPEndPoint Endpoint=>(IPEndPoint)_listener.LocalEndpoint;
        public X509Certificate2Collection Trust=>new(_certificate);
        public Peer(string response) { _listener.Start();_server=Serve(response); }
        private async Task Serve(string response)
        {
            try
            {
                using var client=await _listener.AcceptTcpClientAsync(_stop.Token);await using var stream=client.GetStream();
                var hello=new byte[3];await stream.ReadExactlyAsync(hello,_stop.Token);await stream.WriteAsync(new byte[]{5,0},_stop.Token);
                var prefix=new byte[5];await stream.ReadExactlyAsync(prefix,_stop.Token);var rest=new byte[prefix[4]+2];await stream.ReadExactlyAsync(rest,_stop.Token);
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},_stop.Token);
                await using var tls=new SslStream(stream,false);await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate=_certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13 },_stop.Token);
                var header=new byte[4096];var used=0;
                while(used<header.Length) { var read=await tls.ReadAsync(header.AsMemory(used),_stop.Token);if(read==0)return;used+=read;if(Encoding.ASCII.GetString(header,0,used).Contains("\r\n\r\n",StringComparison.Ordinal))break; }
                await tls.WriteAsync(Encoding.ASCII.GetBytes(response),_stop.Token);await tls.FlushAsync(_stop.Token);await Task.Delay(Timeout.Infinite,_stop.Token);
            }
            catch(Exception ex) when(ex is OperationCanceledException or IOException or SocketException or AuthenticationException) { Error=ex.GetType().Name; }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel();_listener.Stop();await _server.WaitAsync(TimeSpan.FromSeconds(3));_certificate.Dispose();_stop.Dispose(); }
    }
}
