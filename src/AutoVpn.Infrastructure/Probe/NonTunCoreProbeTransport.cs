using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// Probes through a local Mihomo socks listener with tun disabled.
/// A missing or untrusted binary does not mark the node healthy.
/// </summary>
public sealed class NonTunCoreProbeTransport : IProbeTransport
{
    private readonly string? _binaryPath;
    private readonly string? _expectedSha256;
    private readonly TimeSpan _connectTimeout;

    public NonTunCoreProbeTransport(string? binaryPath, string? expectedSha256, TimeSpan? connectTimeout = null)
    {
        _binaryPath = binaryPath;
        _expectedSha256 = expectedSha256;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds);
    }

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

    public static string BuildProbeYaml(CatalogueNode node, int controllerPort, int socksPort)
    {
        return MihomoProfileGenerator.Build(new ProfileBuildRequest
        {
            Secret = "0123456789abcdef",
            ControllerPort = controllerPort,
            SocksPort = socksPort,
            Tun = false,
            LanAccess = false,
            AllowInsecureCertificates = false,
            Nodes = [NodeWireFactory.FromCatalogue(node)],
            SelectedNodeId = node.NodeId,
        });
    }

    public async Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
    {
        if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
        {
            return new ProbeObservation(false, null, false, ReasonCodes.OffRegistryRedirect);
        }

        if (string.IsNullOrWhiteSpace(_binaryPath) || !File.Exists(_binaryPath))
        {
            return new ProbeObservation(false, null, false, "CORE_MISSING");
        }

        if (string.IsNullOrWhiteSpace(_expectedSha256))
        {
            return new ProbeObservation(false, null, false, "CORE_HASH");
        }

        string actual;
        try
        {
            actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(_binaryPath, cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException)
        {
            return new ProbeObservation(false, null, false, ReasonCodes.Canceled);
        }
        catch (IOException)
        {
            return new ProbeObservation(false, null, false, "CORE_MISSING");
        }

        if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeObservation(false, null, false, "CORE_HASH");
        }

        var catalogueNode = new CatalogueNode
        {
            NodeId = "probe",
            Digest = CanonicalIdentity.Digest(node),
            Semantics = node,
            Label = node.Host,
            FirstSeenUtc = DateTimeOffset.UnixEpoch,
            LastSeenUtc = DateTimeOffset.UnixEpoch,
        };
        var controllerPort = FreePort();
        var socksPort = FreePort();
        var yaml = BuildProbeYaml(catalogueNode, controllerPort, socksPort);
        if (MihomoProfileGenerator.EnablesTun(yaml))
        {
            return new ProbeObservation(false, null, false, ReasonCodes.NotWindows);
        }

        var directory = Directory.CreateTempSubdirectory("autovpn-probe-");
        Process? process = null;
        try
        {
            var config = Path.Combine(directory.FullName, "config.yaml");
            await File.WriteAllTextAsync(config, yaml, cancellationToken).ConfigureAwait(false);
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(_binaryPath),
                    WorkingDirectory = directory.FullName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(config);
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(directory.FullName);
            process.StartInfo.Environment["HOME"] = directory.FullName;
            try
            {
                if (!process.Start())
                {
                    return new ProbeObservation(false, null, false, "CORE_START_FAILED");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return new ProbeObservation(false, null, false, "CORE_START_FAILED");
            }

            var watch = Stopwatch.StartNew();
            if (!await WaitForPortAsync(socksPort, cancellationToken).ConfigureAwait(false))
            {
                return new ProbeObservation(false, null, false, "CORE_START_FAILED");
            }

            var status = await Socks5Client.GetStatusAsync(new IPEndPoint(IPAddress.Loopback, socksPort), target, _connectTimeout, cancellationToken).ConfigureAwait(false);
            if (status is not (200 or 204))
            {
                return new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed, 0);
            }

            var latency = (int)Math.Clamp(watch.ElapsedMilliseconds, 0, int.MaxValue);
            return new ProbeObservation(true, latency, false, null, 0);
        }
        catch (OperationCanceledException)
        {
            return new ProbeObservation(false, null, false, ReasonCodes.Canceled);
        }
        finally
        {
            TryKill(process);
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<bool> WaitForPortAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _connectTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromMilliseconds(200));
                await client.ConnectAsync(IPAddress.Loopback, port, linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}

public static class Socks5Client
{
    public static async Task<int> GetStatusAsync(IPEndPoint proxy, Uri target, TimeSpan timeout, CancellationToken cancellationToken)
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
            return 0;
        }

        var host = System.Text.Encoding.ASCII.GetBytes(target.IdnHost);
        if (host.Length is 0 or > 255)
        {
            return 0;
        }

        var request = new byte[4 + 1 + host.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)host.Length;
        host.CopyTo(request, 5);
        var port = target.Port;
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        await stream.WriteAsync(request, linked.Token).ConfigureAwait(false);
        var reply = await ReadExactAsync(stream, 4, linked.Token).ConfigureAwait(false);
        if (reply[1] != 0x00)
        {
            return 0;
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
            return 0;
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

        var http = "GET " + target.PathAndQuery + " HTTP/1.1\r\nHost: " + target.IdnHost + "\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(http), linked.Token).ConfigureAwait(false);
        var header = new byte[64];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read, header.Length - read), linked.Token).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
            var text = System.Text.Encoding.ASCII.GetString(header, 0, read);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd > 0)
            {
                var parts = text[..lineEnd].Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
                {
                    return status;
                }

                return 0;
            }
        }

        return 0;
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
