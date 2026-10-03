using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Probe;
using Xunit;

namespace AutoVpn.UnitTests;

// Controlled authenticated peers only. These tests do not contact a subscription proxy or alter system trust.
public sealed class IndependentRound4TlsTests
{
    [Theory]
    [InlineData("HTTP/1.1 204 No Content\r\nContent-Length: 3\r\n\r\nABC")]
    [InlineData("HTTP/1.1 204 No Content\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nABC\r\n0\r\n\r\n")]
    [InlineData("HTTP/1.1 204 No Content\r\nContent-Length : 3\r\n\r\nABC")]
    public async Task Q24_Contradictory204FramingMustNotPass(string response)
    {
        await using var fixture = new TlsPeer(response);
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target, TimeSpan.FromSeconds(3), fixture.Trust, CancellationToken.None);
        Assert.True(result.Authenticated, "The negative control must reach the HTTP layer after genuine TLS.");
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Q25_Control_EmptyAuthenticated204RemainsValid()
    {
        await using var fixture = new TlsPeer("HTTP/1.1 204 No Content\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target, TimeSpan.FromSeconds(3), fixture.Trust, CancellationToken.None);
        Assert.True(result.Authenticated); Assert.Equal(204, result.Status); Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Q26_Control_UntrustedTargetCertificateIsRejected()
    {
        await using var fixture = new TlsPeer("HTTP/1.1 204 No Content\r\n\r\n");
        var result = await Socks5Client.ExchangeAsync(fixture.Endpoint, fixture.Target, TimeSpan.FromSeconds(3), null, CancellationToken.None);
        Assert.False(result.Authenticated);
    }

    [Fact]
    public void Q27_MalformedDocumentMutationCorpusDoesNotEscapeParser()
    {
        var seeds = new[]
        {
            "{\"proxies\":[{\"type\":\"trojan\",\"server\":\"203.0.113.10\",\"port\":443,\"password\":\"synthetic\"}]}",
            "{\"outbounds\":[{\"protocol\":\"vless\",\"settings\":{\"vnext\":[]}}]}",
            "proxies:\n - name: synthetic\n   type: trojan\n   server: 203.0.113.10\n   port: 443\n   password: synthetic\n",
            "vless://11111111-1111-4111-8111-111111111111@203.0.113.10:443?security=tls"
        };
        var insertions = new[] { "\uD800", "\uDC00", "\0", "\"", "[", "}", "&alias", ":", "\n" };
        var attempts = 0;
        foreach (var seed in seeds)
            foreach (var inserted in insertions)
                for (var at = 0; at <= seed.Length; at += 7)
                {
                    var candidate = seed.Insert(at, inserted);
                    var error = Record.Exception(() => SubscriptionImporter.Import(candidate));
                    Assert.True(error is null, $"Mutation {attempts} escaped as {error?.GetType().Name}; seed length={seed.Length}, offset={at}.");
                    attempts++;
                }
        Assert.True(attempts > 300);
    }

    private sealed class TlsPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(8));
        private readonly X509Certificate2 _certificate;
        private readonly Task _server;
        public Uri Target { get; } = new("https://probe.example/generate_204");
        public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
        public X509Certificate2Collection Trust => new(_certificate);
        public TlsPeer(string response)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=probe.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("probe.example");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            _listener.Start(); _server = Serve(response);
        }
        private async Task Serve(string response)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var hello = new byte[3]; await stream.ReadExactlyAsync(hello, _stop.Token);
                await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                var prefix = new byte[5]; await stream.ReadExactlyAsync(prefix, _stop.Token);
                var rest = new byte[prefix[4] + 2]; await stream.ReadExactlyAsync(rest, _stop.Token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, _stop.Token);
                await using var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
                var header = new byte[4096]; var used = 0;
                while (used < header.Length)
                {
                    var read = await ssl.ReadAsync(header.AsMemory(used), _stop.Token);
                    if (read == 0) return;
                    used += read;
                    if (Encoding.ASCII.GetString(header, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                await ssl.FlushAsync(_stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or AuthenticationException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _server.WaitAsync(TimeSpan.FromSeconds(3));
            _certificate.Dispose(); _stop.Dispose();
        }
    }
}
