using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.Broker;

namespace AutoVpn.Infrastructure.WindowsService;

/// <summary>
/// A single retained pipe instance prevents name-squatting between requests. Every client
/// gets a two-second frame/write/close budget. Only authenticated read-only status is implemented.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstalledStatusServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly string _ownerSid;
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly Task _loop;
    private int _disposed;

    public InstalledStatusServer(string ownerSid)
    {
        if (!ServiceOwnerConfiguration.IsAccountSid(ownerSid)) throw new InvalidDataException("SERVICE_OWNER_INVALID");
        _ownerSid = ownerSid;
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        _pipe = NamedPipeServerStreamAcl.Create(InstalledServiceProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            InstalledServiceProtocol.MaxFrameBytes, InstalledServiceProtocol.MaxFrameBytes, acl);
        _loop = ServeAsync();
    }

    public Task Completion => _loop;

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                budget.CancelAfter(InstalledServiceProtocol.TimeoutMs);
                try
                {
                    await ServiceStatusExchange.HandleConnectionAsync(_pipe, () =>
                    {
                        var peer = PipePeer.Inspect(_pipe, _ownerSid);
                        return peer.Accepted && peer.Verified;
                    }, _instanceId, Environment.ProcessId, () => (long)_uptime.Elapsed.TotalSeconds, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException or UnauthorizedAccessException)
                {
                    // Do not log untrusted bytes, request fields, paths, or exception text.
                }
                // EOF marks the pipe broken, so IsConnected may already be false.
                // The retained server instance still needs an explicit disconnect before reuse.
                finally { _pipe.Disconnect(); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
            catch (IOException) when (_stop.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _loop.ConfigureAwait(false); return; }
        await _stop.CancelAsync().ConfigureAwait(false);
        _pipe.Dispose();
        try { await _loop.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
