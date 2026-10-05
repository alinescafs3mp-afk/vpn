using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using AutoVpn.Contracts;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.Infrastructure.WindowsService;

public static class InstalledServiceClient
{
    public static Task<InstalledServiceCheck> QueryAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return OperatingSystem.IsWindows() ? QueryWindowsAsync(token) : Task.FromResult(new InstalledServiceCheck("Unsupported"));
    }

    [SupportedOSPlatform("windows")]
    private static async Task<InstalledServiceCheck> QueryWindowsAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(InstalledServiceProtocol.TimeoutMs);
        try
        {
            using var scm = Native.OpenSCManager(null, null, 1);
            if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var service = Native.OpenService(scm, InstalledServiceProtocol.ServiceName, 5);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ValidConfiguration(service)) return new("AuthenticationFailed");
            var before = ReadStatus(service);
            if (before.CurrentState != 4) return InstalledServiceProtocol.NonRunningCheck(before.CurrentState);
            if (before.ProcessId is 0 or > int.MaxValue) return new("Unavailable");
            using var client = new NamedPipeClientStream(".", InstalledServiceProtocol.PipeName,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            await client.ConnectAsync(InstalledServiceProtocol.TimeoutMs, budget.Token).ConfigureAwait(false);
            if (!Native.GetNamedPipeServerProcessId(client.SafePipeHandle, out var pipePid) ||
                pipePid != before.ProcessId || before.ServiceType != 0x10) return new("AuthenticationFailed");
            using var process = Native.OpenProcess(0x101000, false, pipePid);
            if (process.IsInvalid) return new("AuthenticationFailed");
            var path = new StringBuilder(32768); var length = path.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, path, ref length) ||
                !string.Equals(path.ToString(), InstalledServiceLayout.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !StillRunning(service, process, pipePid)) return new("AuthenticationFailed");
            // The handle is retained until the exchange ends. Nothing is sent before authentication.
            var id = Guid.NewGuid().ToString("N");
            var request = new ServiceStatusRequest { ProtocolVersion = 1, RequestId = id, Operation = "GetStatus" };
            await client.WriteAsync(ServiceStatusFrames.Encode(request), budget.Token).ConfigureAwait(false);
            var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(client, budget.Token).ConfigureAwait(false);
            if (!StillRunning(service, process, pipePid) || !ValidConfiguration(service)) return new("Unavailable");
            return InstalledServiceProtocol.ValidReply(reply, id, checked((int)pipePid))
                ? new("Ready", reply) : new("ProtocolError");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("Timeout"); }
        catch (TimeoutException) { return new("Timeout"); }
        catch (UnauthorizedAccessException) { return new("AccessDenied"); }
        catch (Win32Exception ex) { return new(ex.NativeErrorCode == 1060 ? "NotInstalled" : ex.NativeErrorCode == 5 ? "AccessDenied" : "Unavailable"); }
        catch (InvalidDataException) { return new("ProtocolError"); }
        catch (IOException) { return new("Unavailable"); }
    }

    [SupportedOSPlatform("windows")]
    private static bool ValidConfiguration(Native.ServiceHandle service)
    {
        Native.QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        if (Marshal.GetLastWin32Error() != 122 || required is <= 0 or > 65536) return false;
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!Native.QueryServiceConfig(service, buffer, required, out _)) return false;
            var config = Marshal.PtrToStructure<Native.ServiceConfig>(buffer);
            return config.ServiceType == 0x10 &&
                string.Equals(Marshal.PtrToStringUni(config.Account), "LocalSystem", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Marshal.PtrToStringUni(config.BinaryPath),
                    "\"" + InstalledServiceLayout.ExecutablePath + "\" --service", StringComparison.OrdinalIgnoreCase);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static Native.ServiceProcessStatus ReadStatus(Native.ServiceHandle service)
    {
        if (!Native.QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Native.ServiceProcessStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    [SupportedOSPlatform("windows")]
    private static bool StillRunning(Native.ServiceHandle service, SafeProcessHandle process, uint pid)
    {
        var status = ReadStatus(service);
        return status.ServiceType == 0x10 && status.CurrentState == 4 && status.ProcessId == pid && ProcessHandleLiveness.IsRunning(process);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceProcessStatus
        {
            internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
                CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceConfig
        {
            internal uint ServiceType, StartType, ErrorControl;
            internal IntPtr BinaryPath, LoadOrderGroup;
            internal uint TagId;
            internal IntPtr Dependencies, Account, DisplayName;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, int size, out int needed);
        internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public ServiceHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceProcessStatus status, int size, out int needed);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    }
}
