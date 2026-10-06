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
    private readonly ProxyEndpointResolver _endpoints;

    public NonTunCoreProbeTransport(string? binaryPath, string? expectedSha256, TimeSpan? connectTimeout = null, ProbeEndpointFixture? fixture = null)
        : this(binaryPath, expectedSha256, connectTimeout, fixture, ProxyEndpointResolver.System) { }

    internal NonTunCoreProbeTransport(string? binaryPath, string? expectedSha256, TimeSpan? connectTimeout,
        ProbeEndpointFixture? fixture, ProxyEndpointResolver endpoints)
    {
        _endpoints = endpoints;
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
        var result = await ProbeCoreAsync(node, target, admission, cancellationToken).ConfigureAwait(false);
        return result with { Attempt = admission.Attempt };
    }

    private async Task<ProbeObservation> ProbeCoreAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken cancellationToken)
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

        // DNS admission consumes the existing startup budget, not an extra retry budget.
        var startup = Stopwatch.StartNew();
        var executionNode = node;
        if (_fixture?.LoopbackHosts?.ContainsKey(node.Host) != true)
        {
            var endpoint = await _endpoints.ResolveAsync(node, _connectTimeout, cancellationToken).ConfigureAwait(false);
            if (!endpoint.Succeeded)
            {
                var kind = endpoint.ReasonCode == ReasonCodes.Canceled ? ProbeClass.Canceled :
                    endpoint.ReasonCode is ReasonCodes.NonPublicEndpoint or "ENDPOINT_NAME_INVALID" or
                        "ENDPOINT_BINDING_UNSUPPORTED" ? ProbeClass.Unsupported : ProbeClass.Environment;
                return Fail(kind, endpoint.ReasonCode!, digest, target);
            }
            executionNode = endpoint.ExecutionNode!;
        }
        // This execution copy never replaces the original identity or stored catalogue.
        var catalogueNode = new CatalogueNode
        {
            NodeId = "probe",
            Digest = digest,
            Semantics = executionNode,
            Label = node.Host,
            FirstSeenUtc = DateTimeOffset.UnixEpoch,
            LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
        var directory = Directory.CreateTempSubdirectory("autovpn-probe-");
        var cleanupOwnedByWorker = false;
        var listeners = new List<CorePortLease>();
        try
        {
            // Declared before the core session so the local endpoint remains
            // bound until that session's cleanup completes.
            await using var readiness = new LoopbackCoreReadiness();
            var controllerPort = ReservePort(listeners, CorePortRole.Controller);
            var socksPort = ReservePort(listeners, CorePortRole.Socks);
            string yaml;
            try
            {
                yaml = BuildProbeYaml(catalogueNode, controllerPort, socksPort, _fixture?.LoopbackHosts, admission.AllowInsecureProxyCertificates);
                yaml = readiness.AddToProfile(yaml);
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
                listener.Dispose();
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
            var startupRemaining = _connectTimeout - startup.Elapsed;
            if (startupRemaining <= TimeSpan.Zero)
                return Fail(ProbeClass.CoreFailure, "CORE_NOT_READY", digest, target);
            cancellationToken.ThrowIfCancellationRequested();
            cleanupOwnedByWorker = true;
            await using var session = await ProbeWorker.StartAsync(start, socksPort, startupRemaining, cancellationToken, directory.FullName).ConfigureAwait(false);
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

            // Mihomo can own its SOCKS port while normal dispatch is suspended.
            // Prove local dispatch within the SAME startup budget before making
            // the one authenticated candidate exchange. Never retry TLS to green.
            var remaining = _connectTimeout - startup.Elapsed;
            if (remaining > TimeSpan.FromMinutes(1)) remaining = TimeSpan.FromMinutes(1);
            if (remaining <= TimeSpan.Zero || !await readiness.WaitAsync(socksPort,
                () => session.OwnsListeningPort(socksPort), remaining, cancellationToken).ConfigureAwait(false))
            {
                LastDiagnostic = "CORE_NOT_READY";
                return Fail(ProbeClass.CoreFailure, "CORE_NOT_READY", digest, target, session.WorkerId);
            }

            // Measure the candidate exchange, not local process creation/readiness.
            var watch = Stopwatch.StartNew();
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
            if (!exchange.Authenticated || exchange.Failure is not null || exchange.Status != 204)
            {
                LastDiagnostic = exchange.Failure + " " + session.OutputTail;
                return new ProbeObservation(false, null, false, exchange.Failure ?? ReasonCodes.ProbeFailed, exchange.PayloadBytes, ProbeClass.CandidateFailure, target.AbsoluteUri, digest, worker);
            }

            var latency = (int)Math.Clamp(watch.ElapsedMilliseconds, 0, int.MaxValue);
            return new ProbeObservation(true, latency, false, null, exchange.PayloadBytes, ProbeClass.Success, target.AbsoluteUri, digest, worker);
        }
        catch (OperationCanceledException)
        {
            return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
        }
        catch (CorePortReservationException ex)
        {
            LastDiagnostic = ex.Report is { } report
                ? "CORE_PORT_UNAVAILABLE:" + report.Summary
                : "CORE_PORT_UNAVAILABLE";
            return Fail(ProbeClass.CoreFailure, "CORE_PORT_UNAVAILABLE", digest, target);
        }
        catch (ProbeCleanupException ex)
        {
            // Preserve the bounded report, not native exception text or child output.
            // The public outcome remains a cleanup failure even after a good exchange.
            LastDiagnostic = ex.Report is { } report
                ? "CORE_CLEANUP_REQUIRED:" + report.Summary
                : "CORE_CLEANUP_REQUIRED";
            return Fail(ProbeClass.CoreFailure, "CORE_CLEANUP_REQUIRED", digest, target);
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Dispose();
            }

            try
            {
                if (!cleanupOwnedByWorker && directory.Exists)
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

    private static int ReservePort(List<CorePortLease> held, CorePortRole role)
    {
        var listener = CorePortLease.Reserve(role);
        held.Add(listener);
        return listener.Port;
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

public sealed class ProbeWorker : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly CancellationTokenSource _output;
    private readonly Task<ProbeOutputResult> _stdout;
    private readonly Task<ProbeOutputResult> _stderr;
    private readonly string? _directory;
    private int _disposed;
    private readonly object _cleanupGate = new();
    private Task? _cleanupTask;
    private bool _resourcesReleased;
    private bool _processExitConfirmed;
    private bool _outputFailed;
    private ProbeCleanupReport? _cleanupReport;
    public ProbeCleanupReport? CleanupReport => Volatile.Read(ref _cleanupReport);

    private ProbeWorker(Process? process, CancellationTokenSource output, Task<ProbeOutputResult> stdout, Task<ProbeOutputResult> stderr, string? directory, bool ready, string workerId, StringBuilder tail)
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
    // Compatibility name. This has always counted decoded characters, not bytes.
    public int OutputBytes { get; private set; }
    public int OutputCharacters => OutputBytes;
    public bool DirectoryRemoved { get; private set; }
    public string? Diagnostic { get; private set; }

    public bool OwnsListeningPort(int port)
    {
        try
        {
            return Volatile.Read(ref _disposed) == 0 && _process is { HasExited: false }
                && ProcessOwnsLoopbackPort(_process.Id, port);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

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
        if (cancellationToken.IsCancellationRequested)
            return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
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
        var stdout = ProbeOutputDrain.ReadAsync(process.StandardOutput, output.Token);
        var stderr = ProbeOutputDrain.ReadAsync(process.StandardError, output.Token, sessionTail);
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
        var failed = new ProbeWorker(null, new CancellationTokenSource(), Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.NotStarted)), Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.NotStarted)), directoryToDelete, false, "", new StringBuilder());
        await failed.DisposeAsync().ConfigureAwait(false);
        return failed;
    }

    public static bool ProcessOwnsLoopbackPort(int pid, int port)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsOwnsLoopbackPort(pid, port);
        }

        try
        {
            var needle = "0100007F:" + port.ToString("X4", CultureInfo.InvariantCulture);
            var inodes = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists("/proc/net/tcp"))
            {
                foreach (var line in File.ReadLines("/proc/net/tcp"))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 10 || !fields[3].Equals("0A", StringComparison.OrdinalIgnoreCase)
                        || !fields[1].Equals(needle, StringComparison.OrdinalIgnoreCase))
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

    private static bool WindowsOwnsLoopbackPort(int pid, int port)
    {
        var size = 0;
        var probe = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidListener, 0);
        if (probe != NativeMethods.ErrorInsufficientBuffer || size <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return false;
            }

            var count = Marshal.ReadInt32(buffer);
            var row = 24;
            for (var index = 0; index < count; index++)
            {
                var address = IntPtr.Add(buffer, 4 + (index * row));
                var localAddress = unchecked((uint)Marshal.ReadInt32(address, 4));
                var localPort = unchecked((uint)Marshal.ReadInt32(address, 8));
                var owner = Marshal.ReadInt32(address, 20);
                var hostPort = ((int)(localPort & 0xFF) << 8) | (int)((localPort >> 8) & 0xFF);
                if (owner == pid && hostPort == port && localAddress == 0x0100007F)
                {
                    return true;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    private static class NativeMethods
    {
        public const uint AfInet = 2;
        public const int TcpTableOwnerPidListener = 3;
        public const uint ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, uint family, int tableClass, uint reserved);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null;
        Task cleanup;
        lock (_cleanupGate)
        {
            // Concurrent callers join one attempt. A failed attempt remains
            // observable and can be retried after the cause has been removed.
            if (_cleanupTask is null || _cleanupTask.IsFaulted || _cleanupTask.IsCanceled)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleanupTask = owner.Task;
                Interlocked.Exchange(ref _disposed, 1);
            }
            cleanup = _cleanupTask;
        }
        // Publish ownership before invoking cancellation callbacks, even when
        // process exit and the rest of cleanup can complete synchronously.
        if (owner is not null) _ = CompleteCleanupAsync(owner);
        return new ValueTask(cleanup);
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource owner)
    {
        try
        {
            await CleanupAsync().ConfigureAwait(false);
            owner.TrySetResult();
        }
        catch (OperationCanceledException ex) { owner.TrySetCanceled(ex.CancellationToken); }
        catch (Exception ex) { owner.TrySetException(ex); }
    }

    private async Task CleanupAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        var phase = "PROCESS_STOP";
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (!_resourcesReleased)
            {
                if (_process is not null)
                {
                    try
                    {
                        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && _process.HasExited)
                    {
                        // A natural exit may race Kill. Still wait on the retained object.
                    }
                    await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                _processExitConfirmed = true;
                phase = "OUTPUT_DRAIN";
                _output.Cancel();
                var counts = await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                OutputBytes = (int)Math.Min(int.MaxValue, (long)counts[0].Characters + counts[1].Characters);
                _outputFailed = counts.Any(result => result.End == ProbeOutputEnd.Failed);
                _output.Dispose();
                _process?.Dispose();
                _resourcesReleased = true;
            }
            phase = "DIRECTORY_CLEANUP";
            DirectoryRemoved = false;
            await DeleteDirectoryAsync(_directory).ConfigureAwait(false);
            DirectoryRemoved = true;
            // Failed readers have completed, so their owned handles/files can be released.
            // The probe still fails: a read fault must not silently become a healthy observation.
            phase = "OUTPUT_RESULT";
            if (_outputFailed) throw new ProbeCleanupException();
            Diagnostic = null;
            Volatile.Write(ref _disposed, 2);
            Volatile.Write(ref _cleanupReport, Report("COMPLETED", null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
            or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            Diagnostic = phase + "_FAILED";
            var report = Report(phase + "_FAILED", ex);
            Volatile.Write(ref _cleanupReport, report);
            throw new ProbeCleanupException(report);
        }

        ProbeCleanupReport Report(string stage, Exception? error) => new(stage, _processExitConfirmed,
            _resourcesReleased, DirectoryRemoved, elapsed.ElapsedMilliseconds, error switch
            {
                null => "NONE", TimeoutException => "TIMEOUT", OperationCanceledException => "CANCELED",
                UnauthorizedAccessException => "ACCESS", Win32Exception => "WIN32",
                IOException => "IO", _ => "STATE",
            }, error?.HResult ?? 0, _stdout.Status, _stderr.Status,
            _stdout.IsCompletedSuccessfully ? _stdout.GetAwaiter().GetResult() : null,
            _stderr.IsCompletedSuccessfully ? _stderr.GetAwaiter().GetResult() : null,
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
    }

    private static async Task DeleteDirectoryAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException ex) when (OperatingSystem.IsWindows() &&
                ((ex.HResult & 0xffff) is 32 or 33) && elapsed.Elapsed < TimeSpan.FromSeconds(1))
            {
                // Only a transient sharing/lock violation, after confirmed
                // owned-process exit. No handshake or test is retried.
                await Task.Delay(25).ConfigureAwait(false);
            }
            // Let the cleanup boundary retain the real error kind and HResult.
            // Returning false here used to replace that cause with a generic IO error.
        }
    }
}
