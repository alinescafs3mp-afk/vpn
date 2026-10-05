using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.Infrastructure.Probe;

public readonly record struct TlsProbeExchange(bool Authenticated, int Status, int PayloadBytes, string? Failure);

public static class Socks5Client
{
    public static async Task<int> GetStatusAsync(IPEndPoint proxy, Uri target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exchange = await ExchangeAsync(proxy, target, timeout, null, cancellationToken).ConfigureAwait(false);
        return exchange.Authenticated ? exchange.Status : 0;
    }

    public static async Task<TlsProbeExchange> ExchangeAsync(
        IPEndPoint proxy,
        Uri target,
        TimeSpan timeout,
        X509Certificate2Collection? trustAnchors,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectOnceOrRetryAsync(proxy, timeout, cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await using var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, linked.Token).ConfigureAwait(false);
        var greeting = await ReadExactAsync(stream, 2, linked.Token).ConfigureAwait(false);
        if (greeting[0] != 0x05 || greeting[1] != 0x00)
        {
            return new TlsProbeExchange(false, 0, 0, "SOCKS_AUTH");
        }

        var hostBytes = Encoding.ASCII.GetBytes(target.IdnHost);
        if (hostBytes.Length is 0 or > 255)
        {
            return new TlsProbeExchange(false, 0, 0, "SOCKS_HOST");
        }

        var request = new byte[4 + 1 + hostBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        var port = target.Port;
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        await stream.WriteAsync(request, linked.Token).ConfigureAwait(false);
        var reply = await ReadExactAsync(stream, 4, linked.Token).ConfigureAwait(false);
        if (reply[0] != 0x05 || reply[2] != 0x00)
            return new TlsProbeExchange(false, 0, 0, "SOCKS_PROTOCOL");

        if (reply[1] != 0x00)
        {
            return new TlsProbeExchange(false, 0, 0, "SOCKS_CONNECT");
        }

        var pending = reply[3] switch
        {
            0x01 => 4 + 2,
            0x03 => -1,
            0x04 => 16 + 2,
            _ => 0,
        };
        if (pending == 0)
        {
            return new TlsProbeExchange(false, 0, 0, "SOCKS_ADDRESS");
        }

        if (pending < 0)
        {
            var length = (await ReadExactAsync(stream, 1, linked.Token).ConfigureAwait(false))[0];
            await ReadExactAsync(stream, length + 2, linked.Token).ConfigureAwait(false);
        }
        else
        {
            await ReadExactAsync(stream, pending, linked.Token).ConfigureAwait(false);
        }

        await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = target.IdnHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        if (trustAnchors is { Count: > 0 })
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            foreach (var anchor in trustAnchors)
            {
                policy.CustomTrustStore.Add(anchor);
            }

            options.CertificateChainPolicy = policy;
        }

        try
        {
            await ssl.AuthenticateAsClientAsync(options, linked.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            return new TlsProbeExchange(false, 0, 0, "TLS_REJECTED");
        }
        catch (IOException)
        {
            return new TlsProbeExchange(false, 0, 0, "TLS_TRANSPORT_CLOSED");
        }
        catch (InvalidOperationException)
        {
            return new TlsProbeExchange(false, 0, 0, "TLS_STATE_INVALID");
        }

        var http = "GET " + target.PathAndQuery + " HTTP/1.1\r\nHost: " + target.IdnHost + "\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(http);
        await ssl.WriteAsync(requestBytes, linked.Token).ConfigureAwait(false);
        var header = new byte[4096];
        var read = 0;
        while (read < header.Length)
        {
            var count = await ssl.ReadAsync(header.AsMemory(read, header.Length - read), linked.Token).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
            var text = Encoding.ASCII.GetString(header, 0, read);
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            var head = text[..split];
            var parsed = await ReadProbeResponseAsync(ssl, head, text[(split + 4)..], linked.Token).ConfigureAwait(false);
            return parsed with { PayloadBytes = read };
        }

        return new TlsProbeExchange(true, 0, read, "STATUS");
    }

    private static async Task<TcpClient> ConnectOnceOrRetryAsync(IPEndPoint proxy, TimeSpan timeout, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = new TcpClient();
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(proxy.Address, proxy.Port, connect.Token).ConfigureAwait(false);
                return client;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 0)
            {
                client.Dispose();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static async Task<TlsProbeExchange> ReadProbeResponseAsync(SslStream stream, string head, string bufferedBody, CancellationToken cancellationToken)
    {
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = lineEnd >= 0 ? head[..lineEnd] : head;
        if (!(statusLine.StartsWith("HTTP/1.0 ", StringComparison.Ordinal) || statusLine.StartsWith("HTTP/1.1 ", StringComparison.Ordinal))
            || statusLine.Length < 12
            || !int.TryParse(statusLine.AsSpan(9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            || (statusLine.Length > 12 && statusLine[12] != ' '))
        {
            return new TlsProbeExchange(true, 0, 0, "STATUS");
        }

        if (HeaderFraming(head) is string framing)
        {
            return new TlsProbeExchange(true, status, 0, framing);
        }

        if (head.Contains("\r\nLocation:", StringComparison.OrdinalIgnoreCase) || status is >= 300 and < 400)
        {
            return new TlsProbeExchange(true, status, 0, "REDIRECT");
        }

        if (head.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase)
            && head.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return new TlsProbeExchange(true, status, 0, "UNEXPECTED_BODY");
        }

        var length = ContentLength(head);
        if (length is < 0 or > 8192)
        {
            return new TlsProbeExchange(true, status, 0, "STATUS");
        }

        var expected = length ?? (status == 204 ? 0 : bufferedBody.Length);
        var body = bufferedBody;
        if (body.Length < expected)
        {
            var extra = new byte[expected - body.Length];
            var filled = 0;
            try
            {
                while (filled < extra.Length)
                {
                    var count = await stream.ReadAsync(extra.AsMemory(filled, extra.Length - filled), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        return new TlsProbeExchange(true, status, 0, "INCOMPLETE");
                    }

                    filled += count;
                }
            }
            catch (OperationCanceledException)
            {
                return new TlsProbeExchange(true, status, 0, "INCOMPLETE");
            }

            body += Encoding.ASCII.GetString(extra);
        }

        if (body.TrimStart().StartsWith('<'))
        {
            return new TlsProbeExchange(true, status, body.Length, "UNEXPECTED_BODY");
        }

        if (status != 204)
        {
            return new TlsProbeExchange(true, status, body.Length, "STATUS");
        }

        if (length is > 0 || body.Length > 0)
        {
            return new TlsProbeExchange(true, status, body.Length, "FRAMING");
        }

        return new TlsProbeExchange(true, status, body.Length, null);
    }

    private static string? HeaderFraming(string head)
    {
        var split = head.Split("\r\n");
        for (var index = 1; index < split.Length; index++)
        {
            var line = split[index];
            if (line.Length == 0)
            {
                break;
            }

            if (line[0] is ' ' or '\t')
            {
                return "FRAMING";
            }

            var colon = line.IndexOf(':');
            if (colon <= 0 || !IsFieldToken(line.AsSpan(0, colon)))
            {
                return "FRAMING";
            }

            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                return "FRAMING";
            }
        }

        return null;
    }

    private static bool IsFieldToken(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (var character in name)
        {
            var token = character is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!token)
            {
                return false;
            }
        }

        return true;
    }

    private static int? ContentLength(string head)
    {
        int? length = null;
        foreach (var line in head.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || length is not null)
            {
                return -1;
            }

            length = parsed;
        }

        return length;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("SOCKS response ended early.");
            }

            read += count;
        }

        return buffer;
    }
}
