using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.Runtime;

public sealed record LiveSessionReply(long Sequence, LiveSessionSnapshot Snapshot);
public static class LiveSessionWire
{
    public const int MaximumBytes = 32768;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var data=JsonSerializer.SerializeToUtf8Bytes(value,IpcJson.Options);
        if(data.Length>MaximumBytes) throw new InvalidDataException("FRAME_TOO_LARGE");
        var header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,data.Length);
        await stream.WriteAsync(header,token).ConfigureAwait(false); await stream.WriteAsync(data,token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
        var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <1 or >MaximumBytes)throw new InvalidDataException("FRAME_LENGTH");
        var body=new byte[length];await stream.ReadExactlyAsync(body,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body,IpcJson.RequestOptions)??throw new InvalidDataException("FRAME_NULL");
    }
    public static bool ClientIs(NamedPipeServerStream pipe,int expectedPid)
        => OperatingSystem.IsWindows()&&GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pid)&&pid==expectedPid;
    public static bool ServerIs(NamedPipeClientStream pipe,int expectedPid)
        => OperatingSystem.IsWindows()&&GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out var pid)&&pid==expectedPid;
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
}

/// <summary>Owns one exact launched host and its pipe. No reconnect, discovery by name or shared DB.</summary>
public sealed class LiveSessionClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private NamedPipeClientStream? _pipe;
    private Process? _host;
    private long _sequence;
    public LiveSessionSnapshot Snapshot { get; private set; } = new();
    public bool OwnsHost => _host is not null && !_host.HasExited;
    public async Task StartAsync(string hostPath, LiveSessionRequest start,CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(_host is not null)throw new InvalidOperationException("HOST_ALREADY_STARTED");
        var name="autovpn-session-"+Guid.NewGuid().ToString("N");
        var info=new ProcessStartInfo(Path.GetFullPath(hostPath)) {UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
        if(start.Tun)info.Verb="runas";
        info.ArgumentList.Add("--pipe");info.ArgumentList.Add(name);
        info.ArgumentList.Add("--owner");info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Explicit per-connect UAC for TUN. Same-account only; no credential is put on command line.
        _host=Process.Start(info)??throw new IOException("HOST_SPAWN");
        _pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if(!LiveSessionWire.ServerIs(_pipe,_host.Id))throw new UnauthorizedAccessException("HOST_PID_MISMATCH");
            await SendAsync(start with {Operation="Start"},deadline.Token).ConfigureAwait(false);
        }
        catch { _pipe.Dispose();_pipe=null; throw; }
    }
    public async Task<LiveSessionSnapshot> StatusAsync(CancellationToken token)
    {
        if(_host is not null && _host.HasExited)
        { Snapshot=Snapshot with {Phase="Failed",Error="HOST_EXITED",HasOwnedProcess=false};return Snapshot; }
        return await SendAsync(new(){Operation="Status"},token).ConfigureAwait(false);
    }
    private async Task<LiveSessionSnapshot> SendAsync(LiveSessionRequest request,CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_pipe is null)throw new IOException("HOST_UNAVAILABLE");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(request.Operation=="Stop"?25:8));
            request=request with {Sequence=++_sequence};
            await LiveSessionWire.WriteAsync(_pipe,request,deadline.Token).ConfigureAwait(false);
            var reply=await LiveSessionWire.ReadAsync<LiveSessionReply>(_pipe,deadline.Token).ConfigureAwait(false);
            if(reply.Sequence!=request.Sequence)throw new InvalidDataException("REPLY_SEQUENCE");
            return Snapshot=reply.Snapshot;
        }
        finally{_gate.Release();}
    }
    public async Task StopAsync()
    {
        if(_pipe is not null && OwnsHost)
        {
            try { await SendAsync(new(){Operation="Stop"},CancellationToken.None).ConfigureAwait(false); }
            finally { _pipe.Dispose();_pipe=null; }
        }
        if(_host is not null)
        {
            // Do not impersonate a clean Stop by killing an elevated worker from the desktop.
            // EOF invokes the worker's own cleanup; its Job terminates children on worker death.
            await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            if(_host.ExitCode!=0)throw new IOException("HOST_RECOVERY_NOT_CONFIRMED");
            _host.Dispose();_host=null;
        }
        Snapshot=new();
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
