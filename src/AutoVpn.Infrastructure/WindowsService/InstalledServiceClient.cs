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
        return OperatingSystem.IsWindows()
            ? QueryWindowsAsync(token)
            : Task.FromResult(new InstalledServiceCheck("Unsupported"));
    }

    [SupportedOSPlatform("windows")]
    private static Task<InstalledServiceCheck> QueryWindowsAsync(CancellationToken token) =>
        InstalledServiceQuery.RunAsync(() => new WindowsSession(), token);

    [SupportedOSPlatform("windows")]
    private sealed class WindowsSession : IInstalledServiceSession
    {
        private Native.ServiceHandle? _manager;
        private Native.ServiceHandle? _service;
        private NamedPipeClientStream? _pipe;
        private SafeProcessHandle? _process;
        private Native.ServiceHandle Service => _service ?? throw new InvalidOperationException();
        public Stream Pipe => _pipe ?? throw new InvalidOperationException();

        public void OpenManager()
        {
            _manager = Native.OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT only
            if (_manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void OpenService()
        {
            _service = Native.OpenService(_manager ?? throw new InvalidOperationException(),
                InstalledServiceProtocol.ServiceName, 5); // QUERY_CONFIG | QUERY_STATUS
            if (_service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void ValidateConfiguration()
        {
            var unexpectedSuccess = Native.QueryServiceConfig(Service, IntPtr.Zero, 0, out var required);
            var error = Marshal.GetLastWin32Error();
            if (unexpectedSuccess || error != 122)
                throw new ServiceIdentityException(ServiceCheckFailure.NativeCall, unexpectedSuccess ? null : error);
            if (required is <= 0 or > 65536) throw new ServiceIdentityException(ServiceCheckFailure.ConfigurationSize);
            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (!Native.QueryServiceConfig(Service, buffer, required, out _))
                    throw new ServiceIdentityException(ServiceCheckFailure.NativeCall, Marshal.GetLastWin32Error());
                var config = Marshal.PtrToStructure<Native.ServiceConfig>(buffer);
                if (config.ServiceType != 0x10) throw new ServiceIdentityException(ServiceCheckFailure.ServiceType);
                if (!string.Equals(Marshal.PtrToStringUni(config.Account), "LocalSystem", StringComparison.OrdinalIgnoreCase))
                    throw new ServiceIdentityException(ServiceCheckFailure.ServiceAccount);
                if (!string.Equals(Marshal.PtrToStringUni(config.BinaryPath),
                    "\"" + InstalledServiceLayout.ExecutablePath + "\" --service", StringComparison.OrdinalIgnoreCase))
                    throw new ServiceIdentityException(ServiceCheckFailure.ServiceExecutable);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public ServiceProcessIdentity ReadStatus()
        {
            if (!Native.QueryServiceStatusEx(Service, 0, out var status, Marshal.SizeOf<Native.ServiceProcessStatus>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(status.ServiceType, status.CurrentState, status.ProcessId);
        }
        public async Task ConnectAsync(CancellationToken token)
        {
            _pipe = new NamedPipeClientStream(".", InstalledServiceProtocol.PipeName,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            await _pipe.ConnectAsync(InstalledServiceProtocol.TimeoutMs, token).ConfigureAwait(false);
        }
        public uint GetPipeProcessId()
        {
            if (!Native.GetNamedPipeServerProcessId((_pipe ?? throw new InvalidOperationException()).SafePipeHandle, out var pid))
                throw new ServiceIdentityException(ServiceCheckFailure.NativeCall, Marshal.GetLastWin32Error());
            return pid;
        }
        public void OpenProcess(uint processId)
        {
            // Preserve the retained-handle liveness check. Do not fall back to PID-only
            // authentication or grant broader process rights when Windows denies access.
            const uint queryLimitedAndSynchronize = 0x101000;
            _process = Native.OpenProcess(queryLimitedAndSynchronize, false, processId);
            if (_process.IsInvalid)
                throw new ServiceIdentityException(ServiceCheckFailure.ProcessAccess, Marshal.GetLastWin32Error());
        }
        public void ValidateProcessImage()
        {
            var path = new StringBuilder(32768); var length = path.Capacity;
            if (!Native.QueryFullProcessImageName(_process ?? throw new InvalidOperationException(), 0, path, ref length))
                throw new ServiceIdentityException(ServiceCheckFailure.ProcessImageQuery, Marshal.GetLastWin32Error());
            if (!string.Equals(path.ToString(), InstalledServiceLayout.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new ServiceIdentityException(ServiceCheckFailure.ProcessImageMismatch);
        }
        public bool IsProcessRunning() => ProcessHandleLiveness.IsRunning(_process ?? throw new InvalidOperationException());
        public void Dispose()
        {
            _pipe?.Dispose();
            _process?.Dispose();
            _service?.Dispose();
            _manager?.Dispose();
        }
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
