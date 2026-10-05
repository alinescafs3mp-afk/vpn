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
        bool allowInsecureProxyCertificates = false,
        int? readinessPort = null)
    {
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "probe-controller-disabled",
            ControllerPort = controllerPort,
            SocksPort = socksPort,
            ProbeReadinessPort = readinessPort,
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
        LastDiagnostic = null;
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
        await using var readiness = new ProbeLoopbackReadiness();
        try
        {
            var controllerPort = ReservePort(listeners);
            var socksPort = ReservePort(listeners);
            string yaml;
            try
            {
                yaml = BuildProbeYaml(catalogueNode, controllerPort, socksPort, _fixture?.LoopbackHosts, admission.AllowInsecureProxyCertificates, readiness.Port);
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

            if (!await readiness.WaitAsync(socksPort, _connectTimeout, cancellationToken).ConfigureAwait(false))
            {
                LastDiagnostic = "CORE_ROUTING_NOT_READY";
                return Fail(ProbeClass.CoreFailure, "CORE_ROUTING_NOT_READY", digest, target, session.WorkerId);
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
