using System.Text;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Import;

namespace AutoVpn.UnitTests;

public class ImportTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public void VlessTxtYamlAndBase64ShareOneDigestButFingerprintDoesNot()
    {
        var uri = $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=reality&sni=www.example.com&fp=chrome&pbk=PUBLICKEY&sid=abcd&type=tcp&flow=xtls-rprx-vision#Germany";
        var wrapped = Convert.ToBase64String(Encoding.UTF8.GetBytes(uri + "\n"));
        var yaml = """
            proxies:
              - name: other label
                type: vless
                server: 203.0.113.10
                port: 443
                uuid: 11111111-1111-4111-8111-111111111111
                udp: true
                flow: xtls-rprx-vision
                tls: true
                servername: www.example.com
                client-fingerprint: chrome
                network: tcp
                reality-opts:
                  public-key: PUBLICKEY
                  short-id: abcd
            """;
        var otherFingerprint = yaml.Replace("chrome", "firefox", StringComparison.Ordinal);
        var direct = SubscriptionImporter.Import(uri);
        var encoded = SubscriptionImporter.Import(wrapped);
        var fromYaml = SubscriptionImporter.Import(yaml);
        var variant = SubscriptionImporter.Import(otherFingerprint);

        Assert.True(direct.Balanced);
        Assert.Equal(RecordDisposition.Pending, direct.Records[0].Disposition);
        Assert.Equal(direct.Records[0].Digest, encoded.Records[0].Digest);
        Assert.Equal(direct.Records[0].Digest, fromYaml.Records[0].Digest);
        Assert.NotEqual(direct.Records[0].Digest, variant.Records[0].Digest);
        Assert.Equal("Германия", direct.Records[0].AdvertisedCountry);
    }

    [Fact]
    public void InsecureAndPlaintextArePolicyBlockedUntilOptIn()
    {
        var insecure = $"hysteria2://secret@203.0.113.11:443?sni=www.example.com&insecure=1#node";
        var blocked = SubscriptionImporter.Import(insecure);
        var allowed = SubscriptionImporter.Import(insecure, new ImportOptions { AllowInsecureCertificates = true });
        var plain = $"vless://{Uuid}@203.0.113.12:443?encryption=none&security=none&type=tcp#plain";
        var plainBatch = SubscriptionImporter.Import(plain);

        Assert.Equal(ReasonCodes.CertVerificationDisabled, blocked.Records[0].ReasonCode);
        Assert.Equal(RecordDisposition.PolicyBlocked, blocked.Records[0].Disposition);
        Assert.Equal(RecordDisposition.Pending, allowed.Records[0].Disposition);
        Assert.Equal(ReasonCodes.PlaintextTransport, plainBatch.Records[0].ReasonCode);
    }

    [Fact]
    public void PrivateAndMetadataDestinationsAreRejected()
    {
        var loopback = $"trojan://secret@127.0.0.1:443?security=tls#x";
        var mapped = $"trojan://secret@[::ffff:10.1.2.3]:443?security=tls#x";
        var metadata = $"trojan://secret@metadata.google.internal:443?security=tls#x";
        Assert.Equal(ReasonCodes.NonPublicEndpoint, SubscriptionImporter.Import(loopback).Records[0].ReasonCode);
        Assert.Equal(ReasonCodes.NonPublicEndpoint, SubscriptionImporter.Import(mapped).Records[0].ReasonCode);
        Assert.Equal(ReasonCodes.NonPublicEndpoint, SubscriptionImporter.Import(metadata).Records[0].ReasonCode);
    }

    [Fact]
    public void BracketedIpv6AndDocumentationRangeParse()
    {
        var uri = $"vless://{Uuid}@[2001:db8::10]:8443?encryption=none&security=tls&sni=www.example.com&type=tcp#node";
        var batch = SubscriptionImporter.Import(uri);
        Assert.Equal(RecordDisposition.Pending, batch.Records[0].Disposition);
        Assert.Equal("2001:db8::10", batch.Records[0].Semantics!.Host);
    }

    [Fact]
    public void ShadowsocksUserInfoAndLegacyBase64Match()
    {
        var user = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:secret-password"));
        var sip002 = $"ss://{user}@203.0.113.20:8388#alpha";
        var legacy = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:secret-password@203.0.113.20:8388")) + "#beta";
        var left = SubscriptionImporter.Import(sip002);
        var right = SubscriptionImporter.Import(legacy);
        Assert.Equal(RecordDisposition.Pending, left.Records[0].Disposition);
        Assert.Equal(left.Records[0].Digest, right.Records[0].Digest);
        Assert.NotEqual(left.Records[0].DisplayName, right.Records[0].DisplayName);
    }

    [Fact]
    public void UnsupportedPluginIsReported()
    {
        var uri = "ss://YWVzLTI1Ni1nY206c2VjcmV0@203.0.113.21:8388?plugin=xray-obfs;mode=weird#name";
        var batch = SubscriptionImporter.Import(uri);
        Assert.Equal(RecordDisposition.Unsupported, batch.Records[0].Disposition);
        Assert.Equal(ReasonCodes.UnsupportedTransport, batch.Records[0].ReasonCode);
    }

    [Fact]
    public void VmessTrocjanTuicAndHy2Alias()
    {
        var vmessJson = """{"v":"2","ps":"vm","add":"203.0.113.30","port":"443","id":"11111111-1111-4111-8111-111111111111","aid":"0","scy":"auto","net":"ws","type":"none","host":"www.example.com","path":"/ws","tls":"tls","sni":"www.example.com"}""";
        var vmess = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson));
        var trojan = "trojan://secret@203.0.113.31:443?security=tls&sni=www.example.com&type=tcp#tr";
        var tuic = $"tuic://{Uuid}:secret@203.0.113.32:443?congestion_control=bbr&alpn=h3&sni=www.example.com#tu";
        var hy2 = "hy2://secret@203.0.113.33:443?sni=www.example.com#hy";
        Assert.Equal(RecordDisposition.Pending, SubscriptionImporter.Import(vmess).Records[0].Disposition);
        Assert.Equal(RecordDisposition.Pending, SubscriptionImporter.Import(trojan).Records[0].Disposition);
        Assert.Equal(RecordDisposition.Pending, SubscriptionImporter.Import(tuic).Records[0].Disposition);
        Assert.Equal(ProtocolKind.Hysteria2, SubscriptionImporter.Import(hy2).Records[0].Semantics!.Protocol);
    }

    [Fact]
    public void YamlEscapesAliasesTagsAndDuplicateKeys()
    {
        var escaped = """
            proxies:
              - name: "p\x61ss"
                type: ss
                server: 203.0.113.40
                port: 8388
                cipher: aes-256-gcm
                password: "p\x61ss"
            """;
        var batch = SubscriptionImporter.Import(escaped);
        Assert.Equal("pass", batch.Records[0].Semantics!.Password);
        Assert.Equal(RecordDisposition.Invalid, SubscriptionImporter.Import("proxies: &a\n- name: x\n").Records[0].Disposition);
        var tagged = "proxies:\n- name: x\n  type: !!python/object:os.system ss\n  server: 203.0.113.1\n  port: 1\n  cipher: aes-256-gcm\n  password: a\n";
        Assert.Equal(ReasonCodes.YamlLimit, SubscriptionImporter.Import(tagged).Records[0].ReasonCode);
        var duplicate = """
            proxies:
              - name: a
                name: b
                type: ss
                server: 203.0.113.41
                port: 8388
                cipher: aes-256-gcm
                password: secret
            """;
        Assert.Contains(SubscriptionImporter.Import(duplicate).Records[0].ReasonCode, new[] { ReasonCodes.DuplicateKey, ReasonCodes.YamlLimit });
    }

    [Fact]
    public void DisplayNameStripsBidiButIdentityIgnoresLabel()
    {
        var uri = $"vless://{Uuid}@203.0.113.50:443?encryption=none&security=tls&sni=www.example.com&type=tcp#\u202eGermany";
        var clean = $"vless://{Uuid}@203.0.113.50:443?encryption=none&security=tls&sni=www.example.com&type=tcp#Germany";
        var dirty = SubscriptionImporter.Import(uri);
        Assert.DoesNotContain('\u202e', dirty.Records[0].DisplayName);
        Assert.Equal(SubscriptionImporter.Import(clean).Records[0].Digest, dirty.Records[0].Digest);
    }

    [Fact]
    public void HtmlEmptyCommentsAndAccountingBalance()
    {
        var html = SubscriptionImporter.Import("<!DOCTYPE html><html><body>login</body></html>");
        Assert.Equal(ReasonCodes.HtmlContent, html.Records[0].ReasonCode);
        var empty = SubscriptionImporter.Import("# profile-update-interval: 1\n# Количество: 0\n\n");
        Assert.True(empty.EmptyValidDocument);
        Assert.Equal(1, empty.AdvisoryUpdateInterval);
        Assert.True(empty.Balanced);
        var batch = SubscriptionImporter.Import("vless://not-a-uuid@203.0.113.61:443#x\n" +
                                                $"trojan://secret@203.0.113.62:443?security=tls#y\n" +
                                                $"trojan://secret@203.0.113.62:443?security=tls#z\n");
        Assert.True(batch.Balanced);
        Assert.Equal(batch.Total, batch.Pending + batch.Duplicates + batch.Invalid + batch.Unsupported + batch.PolicyBlocked);
        Assert.Equal(1, batch.Duplicates);
    }

    [Fact]
    public void XrayExportStripsClientPolicyAndMapsRawToTcp()
    {
        var json = """
            {
              "dns": {"servers": ["1.1.1.1"]},
              "outbounds": [
                {
                  "tag": "node",
                  "protocol": "vless",
                  "settings": {"vnext": [{"address": "203.0.113.70", "port": 443, "users": [{"id": "11111111-1111-4111-8111-111111111111", "encryption": "none", "flow": "xtls-rprx-vision"}]}]},
                  "streamSettings": {"network": "raw", "security": "reality", "sockopt": {"dialerProxy": "fragment", "tcpKeepAliveIdle": 300}, "realitySettings": {"fingerprint": "chrome", "publicKey": "PUBLICKEY", "shortId": "abcd", "serverName": "www.example.com"}}
                },
                {"tag": "direct", "protocol": "freedom", "settings": {"domainStrategy": "UseIP"}}
              ]
            }
            """;
        var batch = SubscriptionImporter.Import(json);
        Assert.Single(batch.Records);
        var semantics = Assert.IsType<NodeSemantics>(batch.Records[0].Semantics, exactMatch: false);
        Assert.Equal("tcp", semantics.Transport);
        Assert.Equal("reality", semantics.Security);
        Assert.Contains("dialerProxy", batch.Records[0].StrippedPolicy);
        Assert.True(batch.ClientPolicyStripped > 0);
    }

    [Fact]
    public void CanonicalJsonIsIdempotent()
    {
        var batch = SubscriptionImporter.Import($"vless://{Uuid}@Example.COM:443?security=tls&encryption=none&type=tcp&sni=www.example.com#A");
        var once = CanonicalIdentity.CanonicalJson(batch.Records[0].Semantics!);
        var twice = CanonicalIdentity.CanonicalJson(CanonicalIdentity.Normalize(batch.Records[0].Semantics!));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void BrokenJsonIsNotReportedAsADuplicateKey()
    {
        var broken = SubscriptionImporter.Import("{");
        Assert.Equal(ReasonCodes.InvalidUri, broken.Records[0].ReasonCode);
        var duplicate = SubscriptionImporter.Import("""{"outbounds":[],"outbounds":[]}""");
        Assert.Equal(ReasonCodes.DuplicateKey, duplicate.Records[0].ReasonCode);
    }
}
