using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Runtime;

if(!OperatingSystem.IsWindows()) return 2;
if(args.Length!=4||args[0]!="--pipe"||args[2]!="--owner"||
    !args[1].StartsWith("autovpn-session-",StringComparison.Ordinal)||args[1].Length!=48||
    !Guid.TryParseExact(args[1][16..],"N",out _)||!int.TryParse(args[3],out var ownerPid)) return 2;
using var identity=WindowsIdentity.GetCurrent();
var sid=identity.User?.Value;
if(sid is null)return 2;
using var owner=Process.GetProcessById(ownerPid);
// Hold the original OS process handle to prevent PID reuse from becoming ownership.
_ = owner.Handle;
using var lifetime=new CancellationTokenSource();
var monitor=Task.Run(async()=>{await owner.WaitForExitAsync();await lifetime.CancelAsync();});
var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),PipeAccessRights.FullControl,AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
await using var pipe=NamedPipeServerStreamAcl.Create(args[1],PipeDirection.InOut,1,PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous|(PipeOptions)0x00080000,32768,32768,security);
var core=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","core","mihomo.exe"));
// Core identity is compiled, never supplied by the caller or a writable JSON field.
const string coreHash="beb9878924d7bd38c67176b441ab5e668cc378bfe751c8dc8fef3eb5aaf566d5";
const string driverHash="DRIVER_PIN_PENDING";
var driver=Path.Combine(Path.GetDirectoryName(core)!,"wintun.dll");
await using var session=new LiveCoreSession(core,coreHash,driver:driver,driverHash:driverHash);
Task? work=null;long sequence=0;var accepted=false;var exit=0;
try
{
    using(var connection=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
    {connection.CancelAfter(TimeSpan.FromSeconds(30));await pipe.WaitForConnectionAsync(connection.Token);}
    if(!LiveSessionWire.ClientIs(pipe,ownerPid))return 3;
    var peerVerified=false;
    while(!lifetime.IsCancellationRequested)
    {
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);idle.CancelAfter(TimeSpan.FromSeconds(20));
        var request=await LiveSessionWire.ReadAsync<LiveSessionRequest>(pipe,idle.Token);
        if(!peerVerified)
        {
            var peer=PipePeer.Inspect(pipe,sid);
            if(!peer.Accepted||peer.Identity!=sid||peer.SessionId!=Process.GetCurrentProcess().SessionId)return 3;
            peerVerified=true;
        }
        if(request.Sequence!=sequence+1)throw new InvalidDataException("SEQUENCE");
        sequence=request.Sequence;
        if(request.Operation=="Start")
        {
            if(accepted||request.Node is null)throw new InvalidDataException("ONE_START_REQUIRED");
            accepted=true;
            // Task starts outside the pipe read loop: status and cancellation stay responsive.
            work=Task.Run(()=>session.StartAsync(request,lifetime.Token));
            await LiveSessionWire.WriteAsync(pipe,new LiveSessionReply(sequence,new(){Phase="Starting",NodeId=request.Node.NodeId,Tun=request.Tun}),idle.Token);
        }
        else if(request.Operation=="Status")
            await LiveSessionWire.WriteAsync(pipe,new LiveSessionReply(sequence,await session.RefreshAsync(idle.Token)),idle.Token);
        else if(request.Operation=="Stop")
        {
            session.Cancel();if(work is not null)await work.WaitAsync(TimeSpan.FromSeconds(20));
            await session.StopAsync();
            await LiveSessionWire.WriteAsync(pipe,new LiveSessionReply(sequence,session.Snapshot),CancellationToken.None);break;
        }
        else throw new InvalidDataException("OPERATION");
    }
}
catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException)
{ exit=ex is EndOfStreamException or OperationCanceledException?0:4; }
finally
{
    session.Cancel();
    try {if(work is not null)await work.WaitAsync(TimeSpan.FromSeconds(20));await session.StopAsync();}
    catch{exit=5;}
}
return exit;
