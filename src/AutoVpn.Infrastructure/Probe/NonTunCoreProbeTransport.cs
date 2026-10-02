using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
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

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// Optional dial map for a controlled test. Values may only be loopback literals.
/// Production callers leave this unset.
/// </summary>
public sealed class ProbeEndpointFixture
{
    public IReadOnlyDictionary<string, string>? LoopbackHosts { get; init; }
    public X509Certificate2Collection? TrustAnchors { get; init; }
}

/// <summary>
/// Probes through a local Mihomo socks listener with tun disabled.
/// A missing or untrusted binary does not mark the node healthy.
/// </summary>
public sealed class NonTunCoreProbeTransport : IProbeTransport
{
    private readonly string? _binaryPath;
    private readonly string? _expectedSha256;
    private readonly TimeSpan _connectTimeout;
    private readonly ProbeEndpointFixture? _fixture;

    public NonTunCoreProbeTransport(string? binaryPath, string? expectedSha256, TimeSpan? connectTimeout = null, ProbeEndpointFixture? fixture = null)
    {
        _binaryPath = binaryPath;
        _expectedSha256 = expectedSha256;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds);
        _fixture = fixture;
    }

    public string? LastDiagnostic { get; private set; }

    public bool CanRun
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_binaryPath) || string.IsNullOrWhiteSpace(_expectedSha256) || !File.Exists(_binaryPath))
            {
                return false;
            }

            try
            {
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_binaryPath)));
                return string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public static string BuildProbeYaml(
        CatalogueNode node,
        int controllerPort,
        int socksPort,
        IReadOnlyDictionary<string, string>? loopbackHosts = null,
        bool allowInsecureProxyCertificates = false)
    {
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "probe-controller-disabled",
            ControllerPort = controllerPort,
            SocksPort = socksPort,
            Tun = false,
            LanAccess = false,
            AllowInsecureCertificates = allowInsecureProxyCertificates,
            ExternalController = false,
            LoopbackHosts = loopbackHosts,
            Nodes = [NodeWireFactory.FromCatalogue(node)],
            SelectedNodeId = node.NodeId,
        });
    }

    public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
    {
        return ProbeAsync(node, target, new ProbeAdmission(false), cancellationToken);
    }

    public async Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken cancellationToken)
    {
        var digest = CanonicalIdentity.Digest(node);
        if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
        {
            return Fail(ProbeClass.Unsupported, ReasonCodes.OffRegistryRedirect, digest, target);
        }

        if (string.IsNullOrWhiteSpace(_binaryPath) || !File.Exists(_binaryPath))
        {
            return Fail(ProbeClass.CoreFailure, "CORE_MISSING", digest, target);
        }

        if (string.IsNullOrWhiteSpace(_expectedSha256))
        {
            return Fail(ProbeClass.CoreFailure, "CORE_HASH", digest, target);
        }

        string actual;
        try
        {
            actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(_binaryPath, cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException)
        {
            return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
        }
        catch (IOException)
        {
            return Fail(ProbeClass.CoreFailure, "CORE_MISSING", digest, target);
        }

        if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(ProbeClass.CoreFailure, "CORE_HASH", digest, target);
        }

        var catalogueNode = new CatalogueNode
        {
            NodeId = "probe",
            Digest = digest,
            Semantics = node,
            Label = node.Host,
            FirstSeenUtc = DateTimeOffset.UnixEpoch,
            LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
        var directory = Directory.CreateTempSubdirectory("autovpn-probe-");
        var listeners = new List<TcpListener>();
        try
        {
            var controllerPort = ReservePort(listeners);
            var socksPort = ReservePort(listeners);
            string yaml;
            try
            {
                yaml = BuildProbeYaml(catalogueNode, controllerPort, socksPort, _fixture?.LoopbackHosts, admission.AllowInsecureProxyCertificates);
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ProbeClass.Unsupported, string.IsNullOrWhiteSpace(ex.Message) ? ReasonCodes.CoreConfigRejected : ex.Message, digest, target);
            }

            if (MihomoProfileGenerator.EnablesTun(yaml))
            {
                return Fail(ProbeClass.Unsupported, ReasonCodes.NotWindows, digest, target);
            }

            var config = Path.Combine(directory.FullName, "config.yaml");
            await File.WriteAllTextAsync(config, yaml, cancellationToken).ConfigureAwait(false);
            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            listeners.Clear();
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(_binaryPath),
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(config);
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(directory.FullName);
            start.Environment["HOME"] = directory.FullName;
            var watch = Stopwatch.StartNew();
            await using var session = await ProbeWorker.StartAsync(start, socksPort, _connectTimeout, cancellationToken, directory.FullName).ConfigureAwait(false);
            LastDiagnostic = session.Diagnostic;
            if (cancellationToken.IsCancellationRequested)
            {
                return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
            }

            if (!session.Ready)
            {
                LastDiagnostic = session.OutputTail;
                return Fail(ProbeClass.CoreFailure, "CORE_START_FAILED", digest, target);
            }

            TlsProbeExchange exchange;
            try
            {
                exchange = await Socks5Client.ExchangeAsync(
                    new IPEndPoint(IPAddress.Loopback, socksPort),
                    target,
                    _connectTimeout,
                    _fixture?.TrustAnchors,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target, session.WorkerId);
            }

            var worker = session.WorkerId + ":" + node.Host + ":" + node.Port.ToString(CultureInfo.InvariantCulture);
            if (!exchange.Authenticated || exchange.Status is not (200 or 204))
            {
                LastDiagnostic = exchange.Failure + " " + session.OutputTail;
                return new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed, exchange.PayloadBytes, ProbeClass.CandidateFailure, target.AbsoluteUri, digest, worker);
            }

            var latency = (int)Math.Clamp(watch.ElapsedMilliseconds, 0, int.MaxValue);
            return new ProbeObservation(true, latency, false, null, exchange.PayloadBytes, ProbeClass.Success, target.AbsoluteUri, digest, worker);
        }
        catch (OperationCanceledException)
        {
            return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            try
            {
                if (directory.Exists)
                {
                    directory.Delete(recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static ProbeObservation Fail(ProbeClass kind, string reason, string digest, Uri target, string? worker = null)
    {
        return new ProbeObservation(false, null, false, reason, 0, kind, target.AbsoluteUri, digest, worker);
    }

    private static int ReservePort(List<TcpListener> held)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        held.Add(listener);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(proxy.Address, proxy.Port, linked.Token).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is AuthenticationException or IOException or InvalidOperationException)
        {
            return new TlsProbeExchange(false, 0, 0, "TLS_REJECTED");
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
            var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            var statusLine = lineEnd > 0 ? head[..lineEnd] : head;
            var parts = statusLine.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            {
                return new TlsProbeExchange(true, 0, read, "STATUS");
            }

            if (head.Contains("\r\nLocation:", StringComparison.OrdinalIgnoreCase) || status is >= 300 and < 400)
            {
                return new TlsProbeExchange(true, status, read, "REDIRECT");
            }

            var body = text[(split + 4)..];
            if (body.TrimStart().StartsWith('<'))
            {
                return new TlsProbeExchange(true, status, read, "UNEXPECTED_BODY");
            }

            if (status is not (200 or 204))
            {
                return new TlsProbeExchange(true, status, read, "STATUS");
            }

            return new TlsProbeExchange(true, status, read, null);
        }

        return new TlsProbeExchange(true, 0, read, "STATUS");
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

public sealed class ProbeWorker : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly CancellationTokenSource _output;
    private readonly Task<int> _stdout;
    private readonly Task<int> _stderr;
    private readonly string? _directory;
    private int _disposed;

    private ProbeWorker(Process? process, CancellationTokenSource output, Task<int> stdout, Task<int> stderr, string? directory, bool ready, string workerId, StringBuilder tail)
    {
        _process = process;
        _output = output;
        _stdout = stdout;
        _stderr = stderr;
        _directory = directory;
        Ready = ready;
        WorkerId = workerId;
        _tail = tail;
    }

    private readonly StringBuilder _tail;

    public bool Ready { get; }
    public string WorkerId { get; }
    public int OutputBytes { get; private set; }
    public bool DirectoryRemoved { get; private set; }
    public string? Diagnostic { get; private set; }

    public string OutputTail
    {
        get
        {
            lock (_tail)
            {
                return _tail.ToString();
            }
        }
    }

    public static async Task<ProbeWorker> StartAsync(
        ProcessStartInfo startInfo,
        int? readyPort,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? directoryToDelete)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var sessionTail = new StringBuilder();
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
        }

        var output = new CancellationTokenSource();
        var stdout = CountAsync(process.StandardOutput, output.Token, null);
        var stderr = CountAsync(process.StandardError, output.Token, sessionTail);
        var ready = false;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                break;
            }

            if (readyPort is null)
            {
                ready = true;
                break;
            }

            if (ProcessOwnsLoopbackPort(process.Id, readyPort.Value))
            {
                ready = true;
                break;
            }

            await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
        }

        var workerId = ready
            ? process.Id.ToString(CultureInfo.InvariantCulture) + ":" + (readyPort?.ToString(CultureInfo.InvariantCulture) ?? "0")
            : "";
        var session = new ProbeWorker(process, output, stdout, stderr, directoryToDelete, ready, workerId, sessionTail);
        if (!ready)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        return session;
    }

    private static async Task<ProbeWorker> FailedStartAsync(string? directoryToDelete)
    {
        var failed = new ProbeWorker(null, new CancellationTokenSource(), Task.FromResult(0), Task.FromResult(0), directoryToDelete, false, "", new StringBuilder());
        await failed.DisposeAsync().ConfigureAwait(false);
        return failed;
    }

    public static bool ProcessOwnsLoopbackPort(int pid, int port)
    {
        try
        {
            var needle = "0100007F:" + port.ToString("X4", CultureInfo.InvariantCulture);
            var inodes = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists("/proc/net/tcp"))
            {
                foreach (var line in File.ReadLines("/proc/net/tcp"))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 10 || !fields[1].Equals(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    inodes.Add(fields[9]);
                }
            }

            if (inodes.Count == 0)
            {
                return false;
            }

            var fdDirectory = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/fd";
            if (!Directory.Exists(fdDirectory))
            {
                return false;
            }

            foreach (var fd in Directory.EnumerateFiles(fdDirectory))
            {
                string link;
                try
                {
                    link = new FileInfo(fd).LinkTarget ?? "";
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var inode in inodes)
                {
                    if (link.Contains("socket:[" + inode + "]", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            if (_process is { HasExited: false })
            {
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }

        _output.Cancel();
        try
        {
            OutputBytes = await _stdout.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            OutputBytes += await _stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            Diagnostic = ex.GetType().Name;
        }

        _output.Dispose();
        _process?.Dispose();
        DirectoryRemoved = DeleteDirectory(_directory);
    }

    private static async Task<int> CountAsync(StreamReader reader, CancellationToken cancellationToken, StringBuilder? tail)
    {
        var buffer = new char[1024];
        var total = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (tail is not null && tail.Length < 2000)
                {
                    lock (tail)
                    {
                        var room = 2000 - tail.Length;
                        if (room > 0)
                        {
                            tail.Append(buffer, 0, Math.Min(count, room));
                        }
                    }
                }

                if (total > 1024 * 1024)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        return total;
    }

    private static bool DeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
