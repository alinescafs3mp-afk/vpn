using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsWindows()) return 5;
return await ServiceLab.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class ServiceLab
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || Environment.GetEnvironmentVariable("AUTOVPN_DISPOSABLE_SERVICE_LAB") != "1") return 5;
        var phase = "AccountLogon";
        try
        {
            var user = Environment.GetEnvironmentVariable("AUTOVPN_LAB_USER") ?? "";
            var password = Environment.GetEnvironmentVariable("AUTOVPN_LAB_PASSWORD") ?? "";
            Demand(user.StartsWith("avlab", StringComparison.Ordinal) && password.Length >= 24, "OWNED_ACCOUNT_REQUIRED");
            if (!LogonUser(user, ".", password, 2, 0, out var token))
                throw new InvalidOperationException("LAB_LOGON_" + Marshal.GetLastWin32Error());
            using (token)
            {
                return await WindowsIdentity.RunImpersonatedAsync(token, async () =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    Demand(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "STANDARD_USER_REQUIRED");
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (args[0] == "negative")
                    {
                        phase = "UnsupportedOperation";
                        var id = Guid.NewGuid().ToString("N");
                        await using (var pipe = await ConnectAsync(budget.Token))
                        {
                            await pipe.WriteAsync(ServiceStatusFrames.Encode(new ServiceStatusRequest
                                { ProtocolVersion = 1, RequestId = id, Operation = "Connect" }), budget.Token);
                            var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(pipe, budget.Token);
                            Demand(!reply.Ok && reply.ErrorCode == "OPERATION_NOT_SUPPORTED" &&
                                !reply.CanConnect && !reply.ProtectionArmed && !reply.CoreRunning, "OPERATION_REFUSAL");
                        }
                        phase = "RecoveryAfterUnsupported";
                        await RequireReadyAsync(budget.Token);
                        phase = "DuplicateFrame";
                        await RejectedFrameAsync(Encoding.UTF8.GetBytes("{\"requestId\":\"a\",\"requestId\":\"b\"}"), false, budget.Token);
                        phase = "RecoveryAfterDuplicate";
                        await RequireReadyAsync(budget.Token);
                        phase = "OversizedFrame";
                        await RejectedFrameAsync([], true, budget.Token);
                        phase = "RecoveryAfterOversized";
                        await RequireReadyAsync(budget.Token);
                        phase = "IdleClient";
                        // Hold a connected client without a frame. Its two-second server budget must expire.
                        await using (var pipe = await ConnectAsync(budget.Token))
                        {
                            var read = new byte[1];
                            Demand(await pipe.ReadAsync(read, budget.Token) == 0, "IDLE_CLIENT_NOT_CLOSED");
                        }
                        phase = "RecoveryAfterIdle";
                        var last = await RequireReadyAsync(budget.Token);
                        Console.WriteLine(JsonSerializer.Serialize(new { standardUser = true, cases = 8,
                            operationRejected = true, duplicateRejected = true, oversizedRejected = true,
                            idleClosed = true, listenerSurvived = true, instanceId = last.Reply!.InstanceId }));
                        return 0;
                    }
                    phase = "QueryStatus";
                    var check = await InstalledServiceClient.QueryAsync(budget.Token);
                    var expected = args[0] switch { "ready" => "Ready", "denied" => "AccessDenied", "stopped" => "Stopped", "missing" => "NotInstalled", _ => "INVALID_VERB" };
                    if (check.State != expected)
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { phase, expected, actual = check.State,
                            diagnostic = check.Diagnostic, check.DiagnosticCode, check.NativeErrorCode }));
                    Demand(check.State == expected, "STATE_" + expected + "_ACTUAL_" + check.State + "_DETAIL_" + (check.DiagnosticCode ?? "NONE") + "_WIN32_" + (check.NativeErrorCode ?? 0));
                    if (check.State == "Ready")
                    {
                        Demand(check.Reply is { CanConnect: false, ProtectionArmed: false, CoreRunning: false }, "FALSE_VPN_CLAIM");
                        DemandReadOnlyProcessAccess(checked((uint)check.Reply!.ProcessId));
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { standardUser = true, state = check.State,
                        instanceId = check.Reply?.InstanceId, processId = check.Reply?.ProcessId,
                        canConnect = check.Reply?.CanConnect ?? false, coreRunning = check.Reply?.CoreRunning ?? false,
                        readOnlyProcessAccess = check.State == "Ready" }));
                    return 0;
                });
            }
        }
        catch (Exception ex)
        {
            // Only assertion identifiers are emitted. Never log the account password or arbitrary system text.
            var code = ex is InvalidOperationException && ex.Message.StartsWith("LAB_", StringComparison.Ordinal)
                && ex.Message.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? ex.Message : "LAB_FAILURE";
            Console.Error.WriteLine(JsonSerializer.Serialize(new { phase, code, exceptionType = ex.GetType().Name,
                hresult = ex.HResult.ToString("X8") }));
            Console.Error.WriteLine(code + ":" + ex.GetType().Name + ":" + ex.HResult.ToString("X8"));
            return 1;
        }
    }

    private static async Task<InstalledServiceCheck> RequireReadyAsync(CancellationToken token)
    {
        var check = await InstalledServiceClient.QueryAsync(token);
        if (check.State != "Ready")
            Console.Error.WriteLine(JsonSerializer.Serialize(new { expected = "Ready", actual = check.State,
                diagnostic = check.Diagnostic, check.DiagnosticCode, check.NativeErrorCode }));
        Demand(check.State == "Ready" && check.Reply is { CanConnect: false, CoreRunning: false, ProtectionArmed: false },
            "LISTENER_RECOVERY_" + check.State);
        return check;
    }
    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", InstalledServiceProtocol.PipeName,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        try { await pipe.ConnectAsync(2000, token); return pipe; }
        catch { pipe.Dispose(); throw; }
    }
    private static async Task RejectedFrameAsync(byte[] body, bool oversized, CancellationToken token)
    {
        await using var pipe = await ConnectAsync(token);
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, oversized ? 4097 : body.Length);
        await pipe.WriteAsync(header, token);
        if (!oversized) await pipe.WriteAsync(body, token);
        try { Demand(await pipe.ReadAsync(new byte[1], token) == 0, "MALFORMED_REPLIED"); }
        catch (IOException) { /* A closed/reset owned pipe is the required refusal, not a timeout. */ }
    }
    private static void DemandReadOnlyProcessAccess(uint processId)
    {
        using var query = OpenProcess(ServiceProcessQueryAccess.RequiredAccess, false, processId);
        Demand(!query.IsInvalid && ProcessHandleLiveness.IsRunning(query), "READONLY_PROCESS_HANDLE");
        // Request handles only. Never perform these operations against the running service.
        foreach (var right in new uint[] { 1, 2, 8, 0x10, 0x20, 0x40, 0x200, 0x40000, 0x80000 })
        {
            using var forbidden = OpenProcess(right, false, processId);
            var error = Marshal.GetLastWin32Error();
            Demand(forbidden.IsInvalid && error == 5, "PROCESS_MUTATION_ACCESS_" + right);
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    private static void Demand(bool value, string code) { if (!value) throw new InvalidOperationException("LAB_" + code); }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);
}
