using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Core;

/// <summary>
/// Concrete, unprivileged, non-TUN runtime. A process/port is not proof of a VPN.
/// TUN and elevated execution stay refused until the installed protection/recovery
/// path exists. No environment variable or IPC flag can bypass this boundary.
/// </summary>
public sealed class MihomoRuntimeProcess : IOwnedNodeCoreProcess
{
    private readonly string? _binaryPath;
    private readonly string? _expectedHash;
    private readonly TimeSpan _startupTimeout;
    private readonly ProxyEndpointResolver _endpoints;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _startCancellation;
    private Task<CoreStartResult>? _startTask;
    private Task? _stopTask;
    private bool _stopRequested;
    private bool _attempted;
    private Process? _process;
    private FileStream? _binary;
    private DirectoryInfo? _directory;
    private Task? _stdout;
    private Task? _stderr;
    private RuntimeProfileContract? _profile;
    private bool _started;
    private int _stopped;

    public MihomoRuntimeProcess(string? binaryPath, string? expectedSha256, TimeSpan? startupTimeout = null)
        : this(binaryPath, expectedSha256, startupTimeout, ProxyEndpointResolver.System) { }

    internal MihomoRuntimeProcess(string? binaryPath, string? expectedSha256, TimeSpan? startupTimeout,
        ProxyEndpointResolver endpoints)
    {
        _binaryPath = binaryPath;
        _expectedHash = expectedSha256;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        if (_startupTimeout <= TimeSpan.Zero || _startupTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    public bool IsRunning
    {
        get
        {
            if (Volatile.Read(ref _stopped) != 0 || !_started || _process is null) return false;
            try { return !_process.HasExited; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            { return true; } // Unknown is not a confirmed exit.
        }
    }

    public Task<CoreStartResult> StartAsync(string yaml, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return AdmitStartAsync(token => StartCoreAsync(yaml, null, null, token), cancellationToken);
    }

    public Task<CoreStartResult> StartNodeAsync(RuntimeNodeSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return AdmitStartAsync(token => PrepareNodeAsync(selection, token), cancellationToken);
    }

    private Task<CoreStartResult> AdmitStartAsync(Func<CancellationToken, Task<CoreStartResult>> start,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            if (_stopRequested) return Task.FromResult(new CoreStartResult(false, "CORE_CLOSING"));
            if (_attempted) throw new InvalidOperationException("CORE_ALREADY_ATTEMPTED");
            _attempted = true;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startCancellation = cancellation;
            // Publish the task under the same gate as Stop. Backend work never runs under this gate.
            _startTask = Task.Run(() => StartWithDeadlineAsync(start, cancellation.Token), CancellationToken.None);
            return _startTask;
        }
    }

    private async Task<CoreStartResult> StartWithDeadlineAsync(Func<CancellationToken, Task<CoreStartResult>> start,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_startupTimeout);
        try
        {
            var result = await start(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return deadline.IsCancellationRequested ? new(false, "CORE_START_TIMEOUT") : result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "CORE_START_TIMEOUT"); }
    }

    private async Task<CoreStartResult> PrepareNodeAsync(RuntimeNodeSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && IsPrivilegedWindows())
            return new(false, "PRIVILEGED_RUNTIME_NOT_VALIDATED");
        var endpoint = await _endpoints.ResolveAsync(selection.Node, _startupTimeout, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!endpoint.Succeeded) return new(false, endpoint.ReasonCode);
        try
        {
            using var controller = CorePortLease.Reserve();
            using var socks = CorePortLease.Reserve();
            token.ThrowIfCancellationRequested();
            var yaml = RuntimeNodeProfile.Build(selection, endpoint.ExecutionNode!, controller.Port, socks.Port,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            return await StartCoreAsync(yaml, controller, socks, token).ConfigureAwait(false);
        }
        catch (CorePortReservationException) { return new(false, "CORE_PORT_UNAVAILABLE"); }
    }

    private async Task<CoreStartResult> StartCoreAsync(string yaml, CorePortLease? controller,
        CorePortLease? socks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeProfileContract profile;
        try { profile = RuntimeProfileContract.Parse(yaml); }
        catch (InvalidDataException ex)
        {
            return new(false, ex.Message == "WINDOWS_TUN_NOT_VALIDATED"
                ? UnavailableNetworkGuard.PlatformReason() : "CORE_PROFILE_INVALID");
        }
        _profile = profile;
        if (OperatingSystem.IsWindows() && IsPrivilegedWindows())
            return new(false, "PRIVILEGED_RUNTIME_NOT_VALIDATED");
        if (string.IsNullOrWhiteSpace(_binaryPath) || !File.Exists(_binaryPath)) return new(false, "CORE_MISSING");
        if (_expectedHash is null || _expectedHash.Length != 64 || !_expectedHash.All(Uri.IsHexDigit))
            return new(false, "CORE_HASH");
        // The handle remains owned until the process and all temporary inputs are gone.
        _binary = new FileStream(Path.GetFullPath(_binaryPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(_binary, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, _expectedHash, StringComparison.OrdinalIgnoreCase)) return new(false, "CORE_HASH");
        _directory = CreatePrivateDirectory();
        var config = Path.Combine(_directory.FullName, "config.yaml");
        await using (var file = new FileStream(config, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false, true)))
        {
            await writer.WriteAsync(yaml.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_binaryPath), WorkingDirectory = _directory.FullName,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-f", config, "-d", _directory.FullName }) start.ArgumentList.Add(argument);
        start.Environment["HOME"] = _directory.FullName;
        _process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        // Hold both transport namespaces through all preparation. The final handoff is not
        // atomic: readiness still requires this exact child's ownership of both TCP ports.
        controller?.Dispose();
        socks?.Dispose();
        _started = _process.Start();
        if (!_started) return new(false, "CORE_START_FAILED");
        // Exactly one reader per fresh owned handle; no retained native text or imported credentials.
        _stdout = ReadOutputAsync(_process.StandardOutput);
        _stderr = ReadOutputAsync(_process.StandardError);
        while (IsRunning)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stdout.IsFaulted || _stderr.IsFaulted) return new(false, "CORE_OUTPUT_READ_FAILED");
            if (ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, profile.SocksPort) &&
                ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, profile.ControllerPort) &&
                await ControllerReadyAsync(profile, cancellationToken).ConfigureAwait(false) &&
                IsRunning && ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, profile.ControllerPort) &&
                ProbeWorker.ProcessOwnsLoopbackPort(_process.Id, profile.SocksPort))
                return new(true, null);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return new(false, "CORE_EXITED_DURING_START");
    }

    // Internal evidence surface for the native fixture. Never contains controller/proxy secrets
    // and never permits a caller to select a PID, path, listener, or cleanup target.
    internal RuntimeOwnedResources? OwnedResources => _started && _process is not null &&
        _profile is not null && _directory is not null && Volatile.Read(ref _stopped) == 0
        ? new(_process.Id, _profile.ControllerPort, _profile.SocksPort, _directory.FullName) : null;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            // Admission is sealed even before Start or when this particular waiter is canceled.
            _stopRequested = true;
            if (_stopTask is null || _stopTask.IsFaulted || _stopTask.IsCanceled)
                _stopTask = Task.Run(StopCoreAsync, CancellationToken.None);
            // Only the wait is cancelable. Ownership and the actual cleanup task are retained.
            return _stopTask.WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (_startCancellation is not null)
        {
            try { await _startCancellation.CancelAsync().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { throw new IOException("CORE_START_CLEANUP_UNCERTAIN"); }
            catch (Exception) { /* A throwing startup cancellation callback does not waive cleanup. */ }
        }
        if (_startTask is not null)
        {
            try { await _startTask.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { throw new IOException("CORE_START_CLEANUP_UNCERTAIN"); }
            catch (Exception) { /* Failed or canceled startup may still own files and a process. */ }
        }
        // Startup is now finished and cannot create new resources behind the cleanup operation.
        if (_started && _process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (!_process.HasExited) throw new IOException("CORE_CLEANUP_UNCERTAIN");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
            { throw new IOException("CORE_CLEANUP_UNCERTAIN"); }
            try
            {
                await Task.WhenAll(_stdout ?? Task.CompletedTask, _stderr ?? Task.CompletedTask)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            { throw new IOException("CORE_OUTPUT_CLEANUP_UNCERTAIN"); }
        }
        // Do not erase configuration or release the binary if process exit is unconfirmed.
        if (_directory is not null && Directory.Exists(_directory.FullName))
        {
            if ((File.GetAttributes(_directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("CORE_DIRECTORY_REPLACED");
            _directory.Delete(recursive: true);
        }
        if (_binary is not null) await _binary.DisposeAsync().ConfigureAwait(false);
        _process?.Dispose();
        _startCancellation?.Dispose();
        _profile = null;
        Volatile.Write(ref _stopped, 1);
    }

    internal static async Task ReadOutputAsync(StreamReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            // A canceled startup must not stop draining output before its owned child exits.
            var result = await ProbeOutputDrain.ReadAsync(reader, CancellationToken.None).ConfigureAwait(false);
            if (result.End != ProbeOutputEnd.Eof) throw new IOException("CORE_OUTPUT_READ_FAILED");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or
            ObjectDisposedException or InvalidOperationException or DecoderFallbackException)
        {
            // Keep the failure, but not potentially secret child output or exception text.
            throw new IOException("CORE_OUTPUT_READ_FAILED");
        }
    }

    private static async Task<bool> ControllerReadyAsync(RuntimeProfileContract profile, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "http://127.0.0.1:" + profile.ControllerPort.ToString(CultureInfo.InvariantCulture) + "/version");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ControllerSecret);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > 4096) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[4097]; var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length > 4096) return false;
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
                string.Equals(version.GetString()?.TrimStart('v'), ProductLimits.CoreVersion.TrimStart('v'), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { return false; }
    }

    private static DirectoryInfo CreatePrivateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "autovpn-runtime-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) return CreateWindowsDirectory(path);
        return Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsPrivilegedWindows()
    {
        // Process.Start uses the primary process token. A temporary thread identity is
        // therefore not evidence that the native child will be unprivileged.
        using var impersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (impersonation is not null) return true;
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        var principal = new WindowsPrincipal(identity);
        return identity.IsSystem || current.SessionId == 0 ||
            principal.IsInRole(WindowsBuiltInRole.Administrator) ||
            principal.IsInRole(new SecurityIdentifier(WellKnownSidType.ServiceSid, null)) ||
            identity.User == new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null) ||
            identity.User == new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
    }

    [SupportedOSPlatform("windows")]
    private static DirectoryInfo CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("RUNTIME_OWNER_REQUIRED");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        FileSystemAclExtensions.Create(directory, acl);
        return directory;
    }
}
