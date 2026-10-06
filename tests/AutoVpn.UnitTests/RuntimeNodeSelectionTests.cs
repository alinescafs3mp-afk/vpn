using System.Collections;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

public sealed class RuntimeNodeSelectionTests
{
    private const string Uuid = "11111111-1111-4111-8111-111111111111";
    private const string Invalid = "RUNTIME_NODE_INVALID";
    private const string Unsupported = "RUNTIME_NODE_UNSUPPORTED";
    private const string TooLarge = "RUNTIME_NODE_TOO_LARGE";

    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    [InlineData(ProtocolKind.Trojan)]
    [InlineData(ProtocolKind.Shadowsocks)]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    public void EachBasicProtocolHasAClosedSelection(ProtocolKind protocol)
    {
        var raw = Node(protocol);
        var selection = RuntimeNodeSelection.Create(raw);
        Assert.Equal(CanonicalIdentity.Digest(raw), selection.Digest);
        Assert.Equal(protocol, selection.Node.Protocol);
        Assert.Equal(SecurityPosture.Accepted, selection.Node.Classify(false));
        Assert.Equal(64, selection.Digest.Length);
    }

    [Fact]
    public void NullInputDoesNotExposeAnArgumentException() => Reject(null!, Invalid);

    public static IEnumerable<object[]> StringFields() => typeof(NodeSemantics).GetProperties()
        .Where(property => property.PropertyType == typeof(string)).OrderBy(property => property.Name, StringComparer.Ordinal)
        .Select(property => new object[] { property.Name });

    [Theory]
    [MemberData(nameof(StringFields))]
    public void EveryStringRejectsUnpairedUtf16BeforeNormalizationOrHashing(string propertyName)
    {
        var raw = Node(ProtocolKind.Trojan);
        typeof(NodeSemantics).GetProperty(propertyName)!.SetValue(raw,
            "SYNTHETIC_PRIVATE_" + new string((char)0xd800, 1));
        Reject(raw, Invalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MalformedSurrogateShapesAreNotReplaced(int shape)
    {
        var value = shape switch
        {
            0 => new string((char)0xd800, 1),
            1 => new string((char)0xdc00, 1),
            2 => new string(new[] { (char)0xd800, 'x' }),
            3 => new string(new[] { 'x', (char)0xdc00 }),
            4 => new string(new[] { (char)0xdc00, (char)0xd800 }),
            _ => new string(new[] { (char)0xd800, (char)0xd800 }),
        };
        Reject(Node(ProtocolKind.Trojan) with { Password = value }, Invalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(127)]
    [InlineData(133)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0xfffe)]
    [InlineData(0xffff)]
    public void ControlBreakAndNonYamlCharactersAreRejected(int codePoint) =>
        Reject(Node(ProtocolKind.Trojan) with { Password = "before" + (char)codePoint + "after" }, Invalid);

    [Fact]
    public void OpaquePasswordsPreserveUnicodeQuotesWhitespaceAndLiteralEscapes()
    {
        var password = "  пароль🙂 ' \" \\uD83D\\uDE42 e\u0301  ";
        var raw = Node(ProtocolKind.Trojan) with { Password = password };
        var selection = RuntimeNodeSelection.Create(raw);
        Assert.Equal(password, selection.Node.Password);
        Assert.Equal(password, raw.Password);
        Assert.Equal(CanonicalIdentity.Digest(raw), selection.Digest);
        var composed = RuntimeNodeSelection.Create(raw with { Password = "é" });
        var decomposed = RuntimeNodeSelection.Create(raw with { Password = "e\u0301" });
        Assert.NotEqual(composed.Digest, decomposed.Digest);
    }

    [Fact]
    public void ScalarLimitsCountUtf8BytesWithoutSplittingSurrogatePairs()
    {
        var raw = Node(ProtocolKind.Trojan);
        Assert.Equal(4096, RuntimeNodeSelection.Create(raw with { Password = new string('x', 4096) }).Node.Password!.Length);
        Reject(raw with { Password = new string('x', 4097) }, TooLarge);
        var emoji = string.Concat(Enumerable.Repeat("🙂", 1024));
        Assert.Equal(emoji, RuntimeNodeSelection.Create(raw with { Password = emoji }).Node.Password);
        Reject(raw with { Password = emoji + "🙂" }, TooLarge);
    }

    [Fact]
    public void AggregateBudgetPrecedesUnsupportedFieldNormalization()
    {
        var block = new string('x', 4096);
        Reject(Node(ProtocolKind.Trojan) with
        { Password = block, Path = block, ServiceName = block, PluginOpts = block, SpiderX = block }, TooLarge);
    }

    [Fact]
    public void AlpnSnapshotCannotBeChangedThroughTheCallerOrReturnedCollection()
    {
        var alpn = new[] { "h2", "http/1.1" };
        var raw = Node(ProtocolKind.Trojan) with { Alpn = alpn };
        var original = CanonicalIdentity.Digest(raw);
        var selection = RuntimeNodeSelection.Create(raw);
        alpn[0] = "changed";
        Assert.Equal("h2", selection.Node.Alpn![0]);
        Assert.Equal(original, selection.Digest);
        Assert.NotSame(alpn, selection.Node.Alpn);
        var list = Assert.IsAssignableFrom<IList<string>>(selection.Node.Alpn);
        Assert.Throws<NotSupportedException>(() => list[0] = "changed-again");
        Assert.Equal(original, CanonicalIdentity.Digest(selection.Node));
    }

    [Fact]
    public void AlpnRejectsEmptyNullPaddedBrokenAndOversizedMembers()
    {
        foreach (var bad in new[] { "", " h2", "h2 ", "h2\n", "🙂", new string((char)0xd800, 1) })
            Reject(Node(ProtocolKind.Trojan) with { Alpn = new[] { bad } }, Invalid);
        Reject(Node(ProtocolKind.Trojan) with { Alpn = new string[] { null! } }, Invalid);
        Reject(Node(ProtocolKind.Trojan) with { Alpn = new[] { new string('a', 256) } }, TooLarge);
        Reject(Node(ProtocolKind.Trojan) with { Alpn = Enumerable.Repeat("h2", 17).ToArray() }, TooLarge);
        Assert.Null(RuntimeNodeSelection.Create(Node(ProtocolKind.Trojan) with { Alpn = Array.Empty<string>() }).Node.Alpn);
    }

    [Fact]
    public void AlpnCopyUsesBoundedIndexesInsteadOfAnUntrustedEnumerator()
    {
        var source = new IndexOnlyList(1, _ => "h2");
        var selection = RuntimeNodeSelection.Create(Node(ProtocolKind.Trojan) with { Alpn = source });
        Assert.Equal(1, source.Reads);
        Assert.Equal("h2", selection.Node.Alpn![0]);
    }

    [Fact]
    public void ExcessiveAlpnCountDoesNotReadAnyElements()
    {
        var source = new IndexOnlyList(int.MaxValue, _ => throw new Exception("SYNTHETIC_PRIVATE"));
        Reject(Node(ProtocolKind.Trojan) with { Alpn = source }, TooLarge);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void UnexpectedCollectionExceptionsAreSanitizedWithoutAnInnerException()
    {
        var source = new IndexOnlyList(1, _ => throw new InvalidDataException("SYNTHETIC_PRIVATE"));
        Reject(Node(ProtocolKind.Trojan) with { Alpn = source }, Invalid);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1%3")]
    [InlineData("localhost")]
    [InlineData("vpn.local")]
    [InlineData("metadata.google.internal")]
    [InlineData("https://vpn.example.net")]
    [InlineData("vpn.example.net:443")]
    [InlineData("a..example.net")]
    [InlineData("bad name.example.net")]
    public void NonPublicAndMalformedEndpointsAreNotSelectable(string host)
    {
        var error = Assert.Throws<InvalidDataException>(() => RuntimeNodeSelection.Create(Node(ProtocolKind.Trojan) with { Host = host }));
        Assert.Contains(error.Message, new[] { Invalid, ReasonCodes.NonPublicEndpoint });
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void PublicNumericAndIdnHostsNeedNoNetworkLookupDuringSelection()
    {
        foreach (var host in new[] { "8.8.8.8", "2606:4700:4700::1111", "[2606:4700:4700::1111]", "пример.рф", "VPN.EXAMPLE.NET." })
        {
            var raw = Node(ProtocolKind.Trojan) with { Host = host };
            Assert.Equal(CanonicalIdentity.Digest(raw), RuntimeNodeSelection.Create(raw).Digest);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidPortsAreRefused(int port) => Reject(Node(ProtocolKind.Trojan) with { Port = port }, Invalid);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(999)]
    public void UnknownProtocolValuesAreNotDefaulted(int protocol) =>
        Reject(Node(ProtocolKind.Trojan) with { Protocol = (ProtocolKind)protocol }, Unsupported);

    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    [InlineData(ProtocolKind.Trojan)]
    [InlineData(ProtocolKind.Shadowsocks)]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    public void EveryProtocolRequiresItsOwnCredentials(ProtocolKind protocol)
    {
        var raw = Node(protocol);
        Reject(protocol is ProtocolKind.Vless or ProtocolKind.Vmess
            ? raw with { UserId = "not-a-uuid" } : raw with { Password = null }, Invalid);
        if (protocol == ProtocolKind.Tuic) Reject(raw with { UserId = "not-a-uuid" }, Invalid);
    }

    [Fact]
    public void CredentialsAndProtocolSpecificFieldsAreNeverSilentlyDiscarded()
    {
        foreach (var raw in new[]
        {
            Node(ProtocolKind.Vless) with { Password = "extra" },
            Node(ProtocolKind.Vmess) with { Password = "" },
            Node(ProtocolKind.Shadowsocks) with { UserId = Uuid },
            Node(ProtocolKind.Hysteria2) with { UserId = Uuid },
            Node(ProtocolKind.Trojan) with { Encryption = "none" },
            Node(ProtocolKind.Tuic) with { Encryption = "none" },
            Node(ProtocolKind.Vless) with { AlterId = 0 },
            Node(ProtocolKind.Vmess) with { AlterId = 1 },
            Node(ProtocolKind.Shadowsocks) with { Sni = "vpn.example.net" },
            Node(ProtocolKind.Shadowsocks) with { Alpn = new[] { "h2" } },
            Node(ProtocolKind.Vless) with { Congestion = "bbr" },
            Node(ProtocolKind.Hysteria2) with { UdpRelayMode = "native" },
        }) Reject(raw, Unsupported);
    }

    [Fact]
    public void UnsupportedFeaturesAreRefusedEvenWhenSuppliedAsEmptyStrings()
    {
        foreach (var propertyName in new[] { "Flow", "Fingerprint", "PublicKey", "ShortId", "SpiderX", "HeaderType",
            "Plugin", "PluginOpts", "Obfs", "ObfsPassword", "PacketEncoding", "Up", "Down", "HopPorts" })
        foreach (var value in new[] { "", "SYNTHETIC_PRIVATE" })
        {
            var raw = Node(ProtocolKind.Trojan);
            typeof(NodeSemantics).GetProperty(propertyName)!.SetValue(raw, value);
            Reject(raw, Unsupported);
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Vless, "none")]
    [InlineData(ProtocolKind.Vmess, "none")]
    [InlineData(ProtocolKind.Trojan, "none")]
    [InlineData(ProtocolKind.Shadowsocks, "none")]
    [InlineData(ProtocolKind.Hysteria2, "reality")]
    [InlineData(ProtocolKind.Tuic, "reality")]
    public void SecurityIsValidatedBeforeProtocolDefaulting(ProtocolKind protocol, string security) =>
        Reject(Node(protocol) with { Security = security }, Unsupported);

    [Fact]
    public void CertificateVerificationCannotBeDisabled() =>
        Reject(Node(ProtocolKind.Trojan) with { SkipCertVerify = true }, ReasonCodes.CertVerificationDisabled);

    [Theory]
    [InlineData(ProtocolKind.Vless, "ws")]
    [InlineData(ProtocolKind.Vmess, "ws")]
    [InlineData(ProtocolKind.Vless, "grpc")]
    public void ReviewedTransportsPreserveTheirMeaningfulOptions(ProtocolKind protocol, string transport)
    {
        var raw = Node(protocol) with
        {
            Transport = transport, Path = transport == "ws" ? "/путь/🙂?a='b'&c=\\literal" : null,
            HostHeader = transport == "ws" ? "front.example.net" : null,
            ServiceName = transport == "grpc" ? "fixture.service" : null,
            Alpn = new[] { transport == "grpc" ? "h2" : "http/1.1" },
        };
        var selected = RuntimeNodeSelection.Create(raw);
        Assert.Equal(raw.Path, selected.Node.Path);
        Assert.Equal(raw.ServiceName, selected.Node.ServiceName);
        Assert.Equal(raw.HostHeader, selected.Node.HostHeader);
        Assert.Equal(CanonicalIdentity.Digest(raw), selected.Digest);
    }

    [Theory]
    [InlineData(ProtocolKind.Trojan, "ws")]
    [InlineData(ProtocolKind.Vmess, "grpc")]
    [InlineData(ProtocolKind.Shadowsocks, "ws")]
    [InlineData(ProtocolKind.Hysteria2, "grpc")]
    [InlineData(ProtocolKind.Tuic, "ws")]
    [InlineData(ProtocolKind.Vless, "h2")]
    [InlineData(ProtocolKind.Vless, "http")]
    public void UnreviewedTransportCombinationsAreExplicitlyUnsupported(ProtocolKind protocol, string transport) =>
        Reject(Node(protocol) with { Transport = transport }, Unsupported);

    [Fact]
    public void TransportOptionsCannotCrossTheirProtocolBoundaries()
    {
        var tcp = Node(ProtocolKind.Vless);
        foreach (var raw in new[] { tcp with { Path = "/x" }, tcp with { HostHeader = "front.example.net" },
            tcp with { ServiceName = "service" }, tcp with { Transport = "ws", ServiceName = "service" },
            tcp with { Transport = "ws", Alpn = new[] { "h2" } },
            tcp with { Transport = "grpc", Path = "/x" },
            tcp with { Transport = "grpc", HostHeader = "front.example.net" },
            tcp with { Transport = "grpc", Alpn = new[] { "http/1.1" } } }) Reject(raw, Unsupported);
        Reject(tcp with { Transport = "ws", Path = "missing-leading-slash" }, Invalid);
        Reject(tcp with { Transport = "grpc", ServiceName = "path/fragment" }, Invalid);
    }

    [Fact]
    public void ExplicitTlsAndHttpHostNamesCannotIncludePortsOrOpaqueText()
    {
        var raw = Node(ProtocolKind.Vless);
        Reject(raw with { Sni = "user@vpn.example.net" }, Invalid);
        Reject(raw with { Sni = "vpn.example.net:443" }, Invalid);
        Reject(raw with { Sni = "пример.рф" }, Invalid);
        Reject(raw with { Transport = "ws", HostHeader = "front.example.net:443" }, Invalid);
        Assert.Equal("xn--e1afmkfd.xn--p1ai", RuntimeNodeSelection.Create(raw with { Sni = "xn--e1afmkfd.xn--p1ai" }).Node.Sni);
    }

    [Fact]
    public void CipherAndTuicOptionsHaveClosedAcceptedSets()
    {
        foreach (var cipher in new[] { "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305" })
            Assert.Equal(cipher, RuntimeNodeSelection.Create(Node(ProtocolKind.Shadowsocks) with { Encryption = cipher }).Node.Encryption);
        foreach (var cipher in new[] { "auto", "aes-128-gcm", "chacha20-poly1305" })
            Assert.Equal(cipher, RuntimeNodeSelection.Create(Node(ProtocolKind.Vmess) with { Encryption = cipher }).Node.Encryption);
        Reject(Node(ProtocolKind.Shadowsocks) with { Encryption = "none" }, Unsupported);
        Reject(Node(ProtocolKind.Vmess) with { Encryption = "unknown" }, Unsupported);
        Reject(Node(ProtocolKind.Vless) with { Encryption = "auto" }, Unsupported);
        foreach (var congestion in new[] { "bbr", "cubic" })
        foreach (var relay in new[] { "native", "quic" })
            Assert.Equal(relay, RuntimeNodeSelection.Create(Node(ProtocolKind.Tuic) with { Congestion = congestion, UdpRelayMode = relay }).Node.UdpRelayMode);
        Reject(Node(ProtocolKind.Tuic) with { Congestion = "typo" }, Unsupported);
        Reject(Node(ProtocolKind.Tuic) with { UdpRelayMode = "typo" }, Unsupported);
    }

    [Fact]
    public void CanonicalIdentityIsBasedOnTheOriginalSnapshotNotAFutureIpPin()
    {
        var raw = Node(ProtocolKind.Vmess) with { Host = "ПРИМЕР.РФ", Security = " TLS ", Encryption = " AUTO ",
            UserId = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE", Transport = " raw " };
        var selection = RuntimeNodeSelection.Create(raw);
        Assert.Equal(CanonicalIdentity.Digest(raw), selection.Digest);
        Assert.Equal("tcp", selection.Node.Transport);
        Assert.Equal("auto", selection.Node.Encryption);
        Assert.Equal("xn--e1afmkfd.xn--p1ai", selection.Node.Host);
        Assert.Equal("ПРИМЕР.РФ", raw.Host);
        Assert.NotEqual(selection.Digest, CanonicalIdentity.Digest(selection.Node with { Host = "8.8.8.8" }));
    }

    [Fact]
    public void DefaultJsonAndTextContainNoCredentialSnapshot()
    {
        const string password = "SYNTHETIC_PRIVATE_PASSWORD";
        var selection = RuntimeNodeSelection.Create(Node(ProtocolKind.Trojan) with { Password = password });
        foreach (var text in new[] { selection.ToString(), JsonSerializer.Serialize(selection),
            JsonSerializer.Serialize(selection, new JsonSerializerOptions { IncludeFields = true }) })
        {
            Assert.DoesNotContain(password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", text, StringComparison.Ordinal);
            Assert.DoesNotContain("vpn.example.net", text, StringComparison.Ordinal);
            Assert.Contains(selection.Digest, text, StringComparison.Ordinal);
        }
    }

    private static InvalidDataException Reject(NodeSemantics raw, string code)
    {
        var error = Assert.Throws<InvalidDataException>(() => RuntimeNodeSelection.Create(raw));
        Assert.Equal(code, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", error.ToString(), StringComparison.Ordinal);
        return error;
    }

    private static NodeSemantics Node(ProtocolKind protocol) => new()
    {
        Protocol = protocol, Host = "vpn.example.net", Port = 443,
        UserId = protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic ? Uuid : null,
        Password = protocol is ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic ? "synthetic-password" : null,
        Security = protocol == ProtocolKind.Shadowsocks ? "aead" : "tls",
        Encryption = protocol switch { ProtocolKind.Vless => "none", ProtocolKind.Vmess => "auto", ProtocolKind.Shadowsocks => "aes-128-gcm", _ => null },
        Transport = "tcp", Congestion = protocol == ProtocolKind.Tuic ? "bbr" : null,
    };

    private sealed class IndexOnlyList(int count, Func<int, string> read) : IReadOnlyList<string>
    {
        public int Count => count;
        public int Reads { get; private set; }
        public string this[int index] { get { Reads++; return read(index); } }
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("SYNTHETIC_PRIVATE_ENUMERATOR");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
