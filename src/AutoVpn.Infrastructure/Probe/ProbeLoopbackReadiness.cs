using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// An owned loopback challenge proves that a non-TUN core is routing, not merely
/// listening. It never contacts the candidate or a public target. Keep this
/// listener alive until the probe worker has stopped so its exception rule cannot
/// point at a reused local port while the worker still exists.
/// </summary>
public sealed class ProbeLoopbackReadiness : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _challenge = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _answer = RandomNumberGenerator.GetBytes(32);
    private readonly Task _server;
    private readonly CancellationToken _lifetimeToken;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public int Port { get; }

    public ProbeLoopbackReadiness()
    {
        _lifetimeToken = _stop.Token;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = ServeAsync();
    }

    public async Task<bool> WaitAsync(int socksPort, TimeSpan timeout, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(socksPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(socksPort, 65535);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeToken);
        deadline.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                // Only readiness exchanges are retried; no candidate TLS exchange is here.
                if (await ExchangeAsync(socksPort, deadline.Token).ConfigureAwait(false))
                { deadline.Token.ThrowIfCancellationRequested(); return true; }
                if (watch.Elapsed >= timeout) return false;
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        return false;
    }

    private async Task<bool> ExchangeAsync(int socksPort, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, socksPort, attempt.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, attempt.Token).ConfigureAwait(false);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, attempt.Token).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] != 0) return false;
            var connect = new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(Port >> 8), (byte)Port };
            await stream.WriteAsync(connect, attempt.Token).ConfigureAwait(false);
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, attempt.Token).ConfigureAwait(false);
            if (reply[0] != 5 || reply[1] != 0 || reply[2] != 0) return false;
            var length = reply[3] switch { 1 => 6, 4 => 18, 3 => -1, _ => 0 };
            if (length == 0) return false;
            if (length == -1)
            {
                var domainLength = new byte[1];
                await stream.ReadExactlyAsync(domainLength, attempt.Token).ConfigureAwait(false);
                if (domainLength[0] == 0) return false;
                length = domainLength[0] + 2;
            }
            await stream.ReadExactlyAsync(new byte[length], attempt.Token).ConfigureAwait(false);
            await stream.WriteAsync(_challenge, attempt.Token).ConfigureAwait(false);
            var answer = new byte[_answer.Length];
            await stream.ReadExactlyAsync(answer, attempt.Token).ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            return CryptographicOperations.FixedTimeEquals(answer, _answer);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
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
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                limit.CancelAfter(TimeSpan.FromMilliseconds(500));
                await using var stream = client.GetStream();
                var challenge = new byte[_challenge.Length];
                await stream.ReadExactlyAsync(challenge, limit.Token).ConfigureAwait(false);
                if (CryptographicOperations.FixedTimeEquals(challenge, _challenge))
                    await stream.WriteAsync(_answer, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) break;
            }
            catch (InvalidOperationException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _server.ConfigureAwait(false);
        _stop.Dispose();
        CryptographicOperations.ZeroMemory(_challenge);
        CryptographicOperations.ZeroMemory(_answer);
    }
}
