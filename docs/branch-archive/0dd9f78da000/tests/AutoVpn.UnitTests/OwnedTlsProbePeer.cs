using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AutoVpn.UnitTests;

/// <summary>Sequential, owned TLS target for the native handshake suite. No fire-and-forget client tasks.</summary>
internal sealed class OwnedTlsProbePeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _loop;
    private int _accepts;
    internal int Port { get; }
    internal int Accepts => Volatile.Read(ref _accepts);

    internal OwnedTlsProbePeer(X509Certificate2 certificate)
    {
        _certificate = certificate;
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = ServeAsync();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate, ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, limit.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _accepts);
                var bytes = new byte[4096]; var length = 0;
                while (length < bytes.Length)
                {
                    var count = await ssl.ReadAsync(bytes.AsMemory(length), limit.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                    var header = Encoding.ASCII.GetString(bytes, 0, length);
                    if (!header.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
                    if (!header.StartsWith("GET /generate_204 HTTP/1.1\r\n", StringComparison.Ordinal))
                        throw new InvalidDataException("Unexpected synthetic probe request.");
                    await ssl.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), limit.Token).ConfigureAwait(false);
                    await ssl.FlushAsync(limit.Token).ConfigureAwait(false);
                    break;
                }
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            { if (_stop.IsCancellationRequested) break; }
            catch (InvalidOperationException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); _listener.Stop();
        // The caller may dispose its certificate only after this join.
        await _loop.ConfigureAwait(false); _stop.Dispose();
    }
}
