using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// An owned, loopback-only data-plane check. Listening is not proof that Mihomo
/// has finished initializing its dispatch path. This check never dials a proxy
/// candidate and never substitutes for the authenticated HTTPS probe.
/// </summary>
public sealed class LoopbackCoreReadiness : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Task _server;
    private int _responses;
    private Task? _disposeTask;
    private readonly object _disposeGate = new();

    public LoopbackCoreReadiness()
    {
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = ServeAsync();
    }

    public int Port { get; }
    public int Responses => Volatile.Read(ref _responses);

    /// <summary>
    /// Adds one exact TCP loopback endpoint, never a general DIRECT fallback.
    /// Call only on a generated non-TUN probe profile. The endpoint stays bound
    /// until the owning core has stopped, preventing local port reuse.
    /// </summary>
    public string AddToProfile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (MihomoProfileGenerator.EnablesTun(yaml))
            throw new InvalidOperationException("Readiness is restricted to non-TUN probe profiles.");
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var marker = "rules:" + newline;
        var lines = yaml.Split(newline, StringSplitOptions.None);
        if (lines.Count(line => line == "rules:") != 1)
            throw new InvalidOperationException("Expected one generated rules section.");
        var offset = yaml.StartsWith(marker, StringComparison.Ordinal) ? 0 :
            yaml.IndexOf(newline + marker, StringComparison.Ordinal) + newline.Length;
        // The CIDR condition does not resolve hostnames. The exception can only
        // match this instance's already-bound 127.0.0.1 TCP endpoint.
        var rule = "  - AND,((NETWORK,tcp),(DST-PORT," + Port.ToString(CultureInfo.InvariantCulture) +
            "),(IP-CIDR,127.0.0.1/32,no-resolve)),DIRECT" + newline;
        return yaml.Insert(offset + marker.Length, rule);
    }

    public async Task<bool> WaitAsync(int socksPort, Func<bool> ownsListener,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownsListener);
        if (socksPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(socksPort));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(timeout);
        try
        {
            // Retries apply to our local startup endpoint only, not a candidate
            // TLS failure. Every positive candidate still requires its own TLS.
            for (var attempt = 0; attempt < 64; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!ownsListener()) return false;
                if (await ExchangeAsync(socksPort, deadline.Token).ConfigureAwait(false))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    return ownsListener();
                }
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> ExchangeAsync(int socksPort, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, socksPort, attempt.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, attempt.Token).ConfigureAwait(false);
            var method = new byte[2];
            await stream.ReadExactlyAsync(method, attempt.Token).ConfigureAwait(false);
            if (method[0] != 5 || method[1] != 0) return false;
            var request = new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 0 };
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), checked((ushort)Port));
            await stream.WriteAsync(request, attempt.Token).ConfigureAwait(false);
            var response = new byte[4];
            await stream.ReadExactlyAsync(response, attempt.Token).ConfigureAwait(false);
            if (response[0] != 5 || response[1] != 0 || response[2] != 0) return false;
            var remaining = response[3] switch { 1 => 6, 4 => 18, 3 => -1, _ => 0 };
            if (remaining == 0) return false;
            if (remaining == -1)
            {
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, attempt.Token).ConfigureAwait(false);
                if (length[0] == 0) return false;
                remaining = length[0] + 2;
            }
            await stream.ReadExactlyAsync(new byte[remaining], attempt.Token).ConfigureAwait(false);
            var challenge = RandomNumberGenerator.GetBytes(32);
            await stream.WriteAsync(challenge, attempt.Token).ConfigureAwait(false);
            var proof = new byte[32];
            await stream.ReadExactlyAsync(proof, attempt.Token).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(_key, challenge));
        }
        catch (Exception error) when (error is IOException or SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                await using var stream = client.GetStream();
                var challenge = new byte[32];
                await stream.ReadExactlyAsync(challenge, deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync(HMACSHA256.HashData(_key, challenge), deadline.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _responses);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                // A malformed local client does not terminate the owned accept loop.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _server.ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); }
    }
}
