using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
using Xunit;
using Xunit.Sdk;

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
        Exception? failure = null;
        try { await ExecuteAsync(); }
        catch (Exception error)
        {
            failure = failure is null ? error :
                new AggregateException("Controlled handshake cleanup and fixture disposal both failed.", failure, error);
        }
        // Only throw after the existing target/readiness disposers have run, so
        // their unexpected errors cannot replace a retained cleanup capability.
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        async Task ExecuteAsync()
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
            var resources = new NativeProcessCleanupResources();
            Process? server=null;var tail=new StringBuilder();var outputGate=new object();
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(80));
            using var listener = CorePortLease.Reserve();
            using var socksListener = CorePortLease.Reserve();
            try
            {
                resources.Directory = Directory.CreateTempSubdirectory("autovpn-r6-native-");
                var directory = resources.Directory;
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
                resources.Process = server = new Process { StartInfo = start };
                resources.Started = server.Start();
                if (!resources.Started) throw new InvalidOperationException("Controlled native server did not start.");
                resources.Stdout=Drain(server.StandardOutput);resources.Stderr=Drain(server.StandardError);
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
            catch (Exception error) { failure = error; }
            finally
            {
                listener.Dispose();socksListener.Dispose();
                // Transfer the exact process, readers and directory together. A
                // failed bounded attempt keeps them alive through the exception's
                // capability; it must not dispose a handle under a pending reader.
                var cleanup = new OwnedProcessCleanup(resources, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                var report = await cleanup.RetryAsync(CancellationToken.None);
                Exception? cleanupFailure = !report.Complete
                    ? new OwnedProcessCleanupException(cleanup, report)
                    : !report.OutputHealthy
                        ? new XunitException("CONTROLLED_SERVER_OUTPUT_FAILED:" + report.Summary)
                        : null;
                if (cleanupFailure is not null)
                {
                    failure = failure is null ? cleanupFailure :
                        new AggregateException("Controlled handshake and owned cleanup both failed.", failure, cleanupFailure);
                }
            }
            string Tail(){lock(outputGate)return tail.ToString();}
            async Task Drain(StreamReader reader)
            {
                using var adapted = ProbeOutputDrain.AdaptOwnedProcessReader(reader);
                var decoded = adapted ?? reader;
                var chars=new char[2048];
                while(true){var count=await decoded.ReadAsync(chars.AsMemory());if(count==0)break;lock(outputGate){tail.Append(chars,0,count);if(tail.Length>4096)tail.Remove(0,tail.Length-4096);}}
            }
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
