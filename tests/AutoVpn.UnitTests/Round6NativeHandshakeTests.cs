using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Probe;
using Xunit;

namespace AutoVpn.UnitTests;

public sealed class Round6NativeHandshakeTests
{
    // The name intentionally belongs to the explicitly provisioned native suite, never no-core CI.
    [Theory]
    [InlineData("vless")]
    [InlineData("vmess")]
    [InlineData("trojan")]
    [InlineData("vless-ws")]
    [InlineData("vmess-ws")]
    [InlineData("vless-grpc")]
    public async Task Native_Round5_R6_ActualProtocolHandshakeAndWrongCredentialNegative(string variant)
    {
        using var trace = new ControlledTlsTrace();
        var binary=Environment.GetEnvironmentVariable("R5_CORE_PATH");var hash=Environment.GetEnvironmentVariable("R5_CORE_HASH");
        Assert.False(string.IsNullOrWhiteSpace(binary));Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.Equal(hash!.ToLowerInvariant(),Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(binary!))).ToLowerInvariant());
        using var certificate=Certificate();
        await using var target=await TlsPeer.StartAsync(certificate,"HTTP/1.1 204 No Content\r\n\r\n");
        // This second Mihomo process is the controlled server, not the probe
        // client. It has the same listener-before-dispatch startup ordering.
        await using var inboundReadiness = new LoopbackCoreReadiness();
        var directory=Directory.CreateTempSubdirectory("autovpn-r6-native-");
        Process? server=null;Task? stdout=null;Task? stderr=null;var tail=new StringBuilder();var outputGate=new object();
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(80));
        using var listener = CorePortLease.Reserve();
        using var socksListener = CorePortLease.Reserve();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"cert.pem"),certificate.ExportCertificatePem(),budget.Token);
            using var rsa=certificate.GetRSAPrivateKey()!;
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"key.pem"),rsa.ExportPkcs8PrivateKeyPem(),budget.Token);
            var port=listener.Port;
            var socksPort=socksListener.Port;
            var protocol=variant.StartsWith("vmess",StringComparison.Ordinal)?"vmess":variant=="trojan"?"trojan":"vless";
            const string user="11111111-1111-4111-8111-111111111111";
            const string password="round6-synthetic-trojan-password";
            var transport=variant.EndsWith("-ws",StringComparison.Ordinal)?"ws":variant.EndsWith("-grpc",StringComparison.Ordinal)?"grpc":"tcp";
            var credentials=protocol=="trojan"?"      password: '"+password+"'\n":"      uuid: '"+user+"'\n"+(protocol=="vmess"?"      alterId: 0\n":"");
            var options=transport=="ws"?"    ws-path: '/round6-path'\n":transport=="grpc"?"    grpc-service-name: 'round6-service'\n":"";
            var yaml="mixed-port: 0\nsocks-port: "+socksPort.ToString(CultureInfo.InvariantCulture)+"\nbind-address: '127.0.0.1'\nallow-lan: false\nmode: rule\nlog-level: warning\nipv6: false\ndns:\n  enable: false\ntun:\n  enable: false\nhosts:\n  'probe.example': '127.0.0.1'\nlisteners:\n  - name: 'controlled-peer'\n    type: "+protocol+"\n    listen: '127.0.0.1'\n    port: "+port.ToString(CultureInfo.InvariantCulture)+"\n    users:\n    - username: 'fixture'\n"+credentials+"    certificate: 'cert.pem'\n    private-key: 'key.pem'\n"+options+"rules:\n  - MATCH,DIRECT\n";
            yaml = inboundReadiness.AddToProfile(yaml);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName,"server.yaml"),yaml,budget.Token);
            var start=new ProcessStartInfo(binary!) {WorkingDirectory=directory.FullName,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-d");start.ArgumentList.Add(directory.FullName);start.ArgumentList.Add("-f");start.ArgumentList.Add(Path.Combine(directory.FullName,"server.yaml"));
            listener.Dispose();socksListener.Dispose();
            server=Process.Start(start)??throw new InvalidOperationException("Controlled native server did not start.");
            stdout=Drain(server.StandardOutput);stderr=Drain(server.StandardError);
            var ready=false;var watch=Stopwatch.StartNew();
            bool Owns() => !server.HasExited && ProbeWorker.ProcessOwnsLoopbackPort(server.Id, socksPort);
            while(watch.Elapsed<TimeSpan.FromSeconds(10)&&!server.HasExited)
            {
                if (Owns() && ProbeWorker.ProcessOwnsLoopbackPort(server.Id, port)) { ready=true;break; }
                await Task.Delay(40,budget.Token);
            }
            Assert.True(ready,"Controlled inbound listener readiness failed: "+Tail());
            var remaining = TimeSpan.FromSeconds(10) - watch.Elapsed;
            Assert.True(remaining > TimeSpan.Zero && await inboundReadiness.WaitAsync(socksPort, Owns, remaining, budget.Token),
                "Controlled inbound dispatch readiness failed: " + Tail());
            var node=new NodeSemantics {Protocol=protocol=="vmess"?ProtocolKind.Vmess:protocol=="trojan"?ProtocolKind.Trojan:ProtocolKind.Vless,
                Host="candidate.example",Port=port,UserId=protocol=="trojan"?null:user,Password=protocol=="trojan"?password:null,
                Security="tls",Encryption=protocol=="vmess"?"auto":"none",Sni="candidate.example",Transport=transport,
                Path=transport=="ws"?"/round6-path":null,ServiceName=transport=="grpc"?"round6-service":null,SkipCertVerify=true};
            var fixture=new ProbeEndpointFixture {LoopbackHosts=new Dictionary<string,string>{["candidate.example"]="127.0.0.1",["probe.example"]="127.0.0.1"},TrustAnchors=new X509Certificate2Collection(certificate)};
            var probe=new NonTunCoreProbeTransport(binary,hash,TimeSpan.FromSeconds(12),fixture);
            var url=new Uri("https://probe.example:"+target.Port.ToString(CultureInfo.InvariantCulture)+"/generate_204");
            // Synthetic proxy certificate opts in locally; target HTTPS authentication remains mandatory.
            var good=await probe.ProbeAsync(node,url,new ProbeAdmission(true),budget.Token);
            Assert.True(good.Success,"Positive "+variant+": "+probe.LastDiagnostic+"; controlled peer="+Tail()+"\nTLS events (process-wide observation, not proof of correlation):\n"+trace.Snapshot());Assert.True(target.Accepts>0);
            var before=target.Accepts;
            var broken=protocol=="trojan"?node with {Password="incorrect-synthetic-password"}:node with {UserId="22222222-2222-4222-8222-222222222222"};
            var bad=await probe.ProbeAsync(broken,url,new ProbeAdmission(true),budget.Token);
            Assert.False(bad.Success,"Incorrect "+variant+" credentials reached an authenticated success.");Assert.Equal(before,target.Accepts);
        }
        finally
        {
            listener.Dispose();socksListener.Dispose();
            if(server is not null)
            {
                try{if(!server.HasExited)server.Kill(entireProcessTree:true);await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
                finally{server.Dispose();}
            }
            if(stdout is not null&&stderr is not null)await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(5));
            directory.Delete(true);
        }
        string Tail(){lock(outputGate)return tail.ToString();}
        async Task Drain(StreamReader reader)
        {
            var chars=new char[2048];
            while(true){var count=await reader.ReadAsync(chars.AsMemory());if(count==0)break;lock(outputGate){tail.Append(chars,0,count);if(tail.Length>4096)tail.Remove(0,tail.Length-4096);}}
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=probe.example",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("probe.example");san.AddDnsName("candidate.example");request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx,"round6"),"round6",X509KeyStorageFlags.Exportable);
    }
}
