using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Runtime;

/// <summary>
/// One real core, one owner, one start/stop lifetime. The V3 TUN is explicitly unprotected:
/// strict-route is NOT a crash-persistent kill switch. No system proxy settings are changed.
/// </summary>
public sealed class LiveCoreSession : IAsyncDisposable
{
    private readonly string _binary;
    private readonly string _hash;
    private readonly ProbeEndpointFixture? _fixture;
    private readonly Uri[] _targets;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private LiveSessionSnapshot _snapshot = new();
    private Process? _process;
    private WindowsProcessJob? _job;
    private DirectoryInfo? _directory;
    private HttpClient? _api;
    private Task[] _drains = [];
    private string _inertYaml = "";
    private bool _started;
    private readonly string? _driver;
    private readonly string? _driverHash;
    public LiveSessionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public LiveCoreSession(string binary, string hash, IReadOnlyList<Uri>? targets = null,
        ProbeEndpointFixture? fixture = null, string? driver = null, string? driverHash = null)
    {
        _binary = Path.GetFullPath(binary); _hash = hash; _fixture = fixture;
        _driver = driver; _driverHash = driverHash;
        _targets = targets?.ToArray() ?? [new("https://cp.cloudflare.com/generate_204"), new("https://www.gstatic.com/generate_204")];
        if (_targets.Length != 2 || _targets.Any(t => !t.IsAbsoluteUri || t.Scheme != "https" || t.UserInfo.Length != 0))
            throw new ArgumentException("Two reviewed HTTPS targets are required.");
    }

    public async Task StartAsync(LiveSessionRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await _lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (_started) throw new InvalidOperationException("SESSION_ALREADY_USED");
            _started = true;
            if (request.Node is null) throw new InvalidDataException("NODE_REQUIRED");
            _ = LiveNodePolicy.Validate(request.Node, request.AllowInsecureProxy);
            if (request.Tun && (!OperatingSystem.IsWindows() || !request.AllowUnprotectedTun || !IsElevated()))
                throw new InvalidOperationException("EXPLICIT_UNPROTECTED_TUN_AND_UAC_REQUIRED");
            var state = new LiveSessionSnapshot { Phase="Starting", NodeId=request.Node.NodeId, Tun=request.Tun };
            Volatile.Write(ref _snapshot, state);
            _directory = SecureDirectory(request.Tun);
            var executable = Path.Combine(_directory.FullName, OperatingSystem.IsWindows() ? "mihomo.exe" : "mihomo");
            await CopyVerifiedAsync(_binary, executable, _hash, deadline.Token).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (request.Tun)
            {
                if (_driver is null || _driverHash is null) throw new InvalidOperationException("WINTUN_MISSING");
                await CopyVerifiedAsync(_driver, Path.Combine(_directory.FullName,"wintun.dll"), _driverHash, deadline.Token).ConfigureAwait(false);
            }
            using var controllerReserve = new TcpListener(IPAddress.Loopback, 0); controllerReserve.Start();
            using var proxyReserve = new TcpListener(IPAddress.Loopback, 0); proxyReserve.Start();
            var controllerPort = ((IPEndPoint)controllerReserve.LocalEndpoint).Port;
            var proxyPort = ((IPEndPoint)proxyReserve.LocalEndpoint).Port;
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var device = "AutoVPN-V3-" + Guid.NewGuid().ToString("N")[..8];
            _inertYaml = $"mode: rule\nlog-level: silent\nallow-lan: false\nbind-address: 127.0.0.1\nexternal-controller: '127.0.0.1:{controllerPort}'\nsecret: '{secret}'\ntun:\n  enable: false\ndns:\n  enable: false\nrules:\n  - MATCH,REJECT\n";
            var yaml = MihomoProfileGenerator.Build(new ProfileBuildRequest
            {
                Secret=secret, ControllerPort=controllerPort, Tun=request.Tun, TunDeviceName=device,
                Stack="gvisor", LanAccess=request.LanAccess, AllowInsecureCertificates=request.AllowInsecureProxy,
                LoopbackHosts=_fixture?.LoopbackHosts, Nodes=[request.Node], SelectedNodeId=request.Node.NodeId,
            }) + $"mixed-port: {proxyPort}\n";
            // User-domain DNS uses the chosen proxy; endpoint bootstrap remains separately explicit.
            if (_fixture is null) yaml = yaml.Replace("https://1.1.1.1/dns-query", "https://1.1.1.1/dns-query#AUTO_SELECT", StringComparison.Ordinal)
                .Replace("https://8.8.8.8/dns-query", "https://8.8.8.8/dns-query#AUTO_SELECT", StringComparison.Ordinal);
            var config = Path.Combine(_directory.FullName,"start.yaml");
            await File.WriteAllTextAsync(config, _inertYaml, deadline.Token).ConfigureAwait(false);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory=_directory.FullName, UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardOutput=true, RedirectStandardError=true,
            };
            start.ArgumentList.Add("-d"); start.ArgumentList.Add(_directory.FullName);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(config);
            start.Environment["HOME"] = _directory.FullName;
            controllerReserve.Stop(); proxyReserve.Stop();
            _process = Process.Start(start) ?? throw new IOException("CORE_SPAWN");
            _drains = [DrainAsync(_process.StandardOutput), DrainAsync(_process.StandardError)];
            // The first profile cannot create a TUN, dial a proxy or open an application proxy.
            if (OperatingSystem.IsWindows()) _job = new WindowsProcessJob(_process);
            _api = new HttpClient(new SocketsHttpHandler { UseProxy=false, AllowAutoRedirect=false })
            { BaseAddress=new Uri($"http://127.0.0.1:{controllerPort}/"), Timeout=TimeSpan.FromSeconds(5) };
            _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            var ready = Stopwatch.StartNew();
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException("CORE_EXIT_BEFORE_READY");
                if (ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, controllerPort))
                {
                    try { using var response = await _api.GetAsync("version", deadline.Token).ConfigureAwait(false); if(response.IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                }
                if (ready.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("CORE_READINESS_TIMEOUT");
                await Task.Delay(60, deadline.Token).ConfigureAwait(false);
            }
            state = state with { ProcessId=_process.Id, ProxyPort=proxyPort, HasOwnedProcess=true, DeviceName=request.Tun?device:null };
            Volatile.Write(ref _snapshot, state);
            await ReloadAsync(yaml, deadline.Token).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while (!ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, proxyPort))
            {
                if (_process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(8)) throw new IOException("CORE_LISTENER_NOT_OWNED");
                await Task.Delay(60, deadline.Token).ConfigureAwait(false);
            }
            var latencies = new List<int>();
            foreach (var target in _targets)
            {
                deadline.Token.ThrowIfCancellationRequested(); watch.Restart();
                TlsProbeExchange exchange;
                if (request.Tun)
                {
                    var addresses = await Dns.GetHostAddressesAsync(target.IdnHost, deadline.Token).ConfigureAwait(false);
                    var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                        ?? throw new IOException("TARGET_IPV4_REQUIRED");
                    if (!WindowsTunRoute.OwnsRoute(device, address)) throw new IOException("TUN_ROUTE_NOT_OWNED");
                    exchange = await Socks5Client.ExchangeDirectAsync(address, target, TimeSpan.FromSeconds(8), null, deadline.Token).ConfigureAwait(false);
                }
                else exchange = await Socks5Client.ExchangeAsync(new(IPAddress.Loopback,proxyPort), target,
                    TimeSpan.FromSeconds(8), _fixture?.TrustAnchors, deadline.Token).ConfigureAwait(false);
                if (!exchange.Authenticated || exchange.Status != 204 || exchange.Failure is not null)
                    throw new IOException("LIVE_HTTPS_CHECK:" + (exchange.Failure ?? "STATUS"));
                latencies.Add((int)watch.ElapsedMilliseconds);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new IOException("CORE_EXIT_DURING_VERIFICATION");
            Volatile.Write(ref _snapshot, state with { Phase="Connected", VerifiedUtc=DateTimeOffset.UtcNow,
                HttpsLatencyMs=latencies.Sum()/latencies.Count });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException
            or HttpRequestException or SocketException or Win32Exception or UnauthorizedAccessException)
        {
            var reason = ex is OperationCanceledException ? "CANCELED_OR_START_TIMEOUT" : SafeError(ex);
            Volatile.Write(ref _snapshot, Snapshot with { Phase="Failed", Error=reason });
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is IOException or TimeoutException or Win32Exception)
            { Volatile.Write(ref _snapshot, Snapshot with { Error=reason+";CLEANUP_REQUIRED", HasOwnedProcess=true }); }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<LiveSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        // Never race disposal of controller/process against cleanup.
        if (!await _lifecycle.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Snapshot;
        try
        {
            if (_process is not null && _process.HasExited && Snapshot.Phase == "Connected")
                Volatile.Write(ref _snapshot, Snapshot with { Phase="Failed", Error="CORE_EXITED_NO_KILL_SWITCH", HasOwnedProcess=false });
            if (_api is not null && Snapshot.Phase == "Connected")
            {
                try
                {
                    using var response = await _api.GetAsync("connections", HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var bodyDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); bodyDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                    await response.Content.LoadIntoBufferAsync(1024*1024,bodyDeadline.Token).ConfigureAwait(false);
                    using var document=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(bodyDeadline.Token).ConfigureAwait(false));
                    var root=document.RootElement;
                    Volatile.Write(ref _snapshot, Snapshot with { DownloadBytes=root.GetProperty("downloadTotal").GetInt64(), UploadBytes=root.GetProperty("uploadTotal").GetInt64() });
                }
                catch(Exception ex) when(ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
            }
            return Snapshot;
        }
        finally { _lifecycle.Release(); }
    }
    public void Cancel() => _stop.Cancel();
    public async Task StopAsync()
    {
        _stop.Cancel();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupAsync().ConfigureAwait(false);
            Volatile.Write(ref _snapshot, Snapshot with { Phase="Stopped", Error=null, HasOwnedProcess=false, ProcessId=null,ProxyPort=null });
        }
        finally { _lifecycle.Release(); }
    }
    private async Task ReloadAsync(string yaml,CancellationToken token)
    {
        using var payload = new StringContent(JsonSerializer.Serialize(new { payload=yaml }), Encoding.UTF8,"application/json");
        using var response = await _api!.PutAsync("configs?force=true", payload,token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("CORE_PROFILE_REJECTED:"+(int)response.StatusCode);
    }
    private async Task CleanupAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                if (_api is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try { await ReloadAsync(_inertYaml,deadline.Token).ConfigureAwait(false); }
                    catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException) { }
                }
                if (!_process.HasExited) _process.Kill(entireProcessTree:true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            await Task.WhenAll(_drains).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _process.Dispose(); _process=null;
        }
        _job?.Dispose(); _job=null; _api?.Dispose(); _api=null;
        Volatile.Write(ref _snapshot, Snapshot with { HasOwnedProcess=false, ProcessId=null });
        if (_directory is not null) { _directory.Delete(true); _directory=null; }
        if (Snapshot.Tun && Snapshot.DeviceName is string device)
        {
            var watch=Stopwatch.StartNew();
            while (NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name==device && n.OperationalStatus==OperationalStatus.Up))
            {
                if(watch.Elapsed>TimeSpan.FromSeconds(5)) throw new IOException("TUN_ADAPTER_STILL_UP");
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private static async Task DrainAsync(StreamReader reader)
    { var buffer=new char[4096]; while(await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)>0) { } }
    private static string SafeError(Exception ex)
    {
        // Never retain native output, a credential-bearing profile, URL or arbitrary exception text.
        var text=ex.Message;
        return text.Length<96 && text.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or ':' or '-') ? text : ex.GetType().Name;
    }
    public static async Task CopyVerifiedAsync(string source,string destination,string expected,CancellationToken token)
    {
        if(expected.Length!=64) throw new InvalidDataException("ASSET_HASH_REQUIRED");
        await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        var actual=Convert.ToHexString(await SHA256.HashDataAsync(input,token).ConfigureAwait(false));
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASSET_HASH_MISMATCH");
        input.Position=0;
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
        await input.CopyToAsync(output,token).ConfigureAwait(false);
    }
    private static bool IsElevated()
    {
        if(!OperatingSystem.IsWindows()) return false;
        using var identity=WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static DirectoryInfo SecureDirectory(bool elevated)
    {
        var parent=elevated?Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData):Path.GetTempPath();
        var info=new DirectoryInfo(Path.Combine(parent,"AutoVPN-session-"+Guid.NewGuid().ToString("N")));
        if(OperatingSystem.IsWindows())
        {
            using var identity=WindowsIdentity.GetCurrent();
            var owner=elevated?new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null):identity.User!;
            var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false); security.SetOwner(owner);
            foreach(var sid in new[]{owner,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            info.Create(security);
        }
        else { info.Create(); File.SetUnixFileMode(info.FullName,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute); }
        return info;
    }
}

public static class WindowsTunRoute
{
    public static bool OwnsRoute(string device,IPAddress address)
    {
        if(!OperatingSystem.IsWindows()||address.AddressFamily!=AddressFamily.InterNetwork) return false;
        var nic=NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n=>n.Name==device&&n.OperationalStatus==OperationalStatus.Up);
        if(nic is null) return false;
        var bytes=new byte[28];bytes[0]=2;address.GetAddressBytes().CopyTo(bytes,4);
        return GetBestInterfaceEx(bytes,out var index)==0 && index==nic.GetIPProperties().GetIPv4Properties().Index;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] address,out uint index);
}
