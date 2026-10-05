using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.Service;

/// <summary>Small SCM adapter. The control callback signals only; cleanup runs on ServiceMain.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeServiceHost
{
    private readonly ServiceLifecycle _lifecycle = new();
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _reportGate = new();
    private readonly ServiceMainCallback _mainCallback;
    private readonly ControlCallback _controlCallback;
    private IntPtr _statusHandle;
    private uint _checkpoint;
    private bool _reportedStopped;
    private int _exitCode;

    internal NativeServiceHost()
    {
        _mainCallback = ServiceMain;
        _controlCallback = Control;
    }

    internal int Run()
    {
        var entries = new[]
        {
            new ServiceTableEntry { Name = InstalledServiceProtocol.ServiceName, Main = _mainCallback },
            new ServiceTableEntry { Name = null, Main = null },
        };
        if (!StartServiceCtrlDispatcher(entries)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(_mainCallback); GC.KeepAlive(_controlCallback);
        return _exitCode;
    }

    private void ServiceMain(uint argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(InstalledServiceProtocol.ServiceName, _controlCallback, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) { _exitCode = Marshal.GetLastWin32Error(); return; }
        InstalledStatusServer? server = null;
        try
        {
            Report();
            var config = InstalledServiceLayout.LoadOwner();
            ServiceProcessQueryAccess.GrantToOwner(config.OwnerSid);
            server = new InstalledStatusServer(config.OwnerSid);
            if (!_lifecycle.TryMarkReady()) throw new InvalidOperationException("START_CANCELLED");
            Report();
            var finished = Task.WhenAny(_stop.Task, server.Completion).GetAwaiter().GetResult();
            if (finished == server.Completion && !_stop.Task.IsCompleted)
            {
                // Unexpected listener termination is a service failure, not a successful stop.
                server.Completion.GetAwaiter().GetResult();
                _exitCode = 1;
            }
        }
        catch (Exception)
        {
            // Never allow an exception through the unmanaged callback. Error details are not
            // emitted here: configuration paths and private inputs must not reach shared logs.
            _exitCode = 1;
        }
        finally
        {
            _lifecycle.RequestStop();
            TryReport();
            if (server is not null)
            {
                try { server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                catch (Exception) { _exitCode = 2; }
            }
            _lifecycle.MarkStopped();
            TryReport();
        }
    }

    private uint Control(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            if (control is 1 or 5) // STOP / SHUTDOWN
            {
                _lifecycle.RequestStop();
                TryReport();
                _stop.TrySetResult();
                return 0;
            }
            if (control == 4) { TryReport(); return 0; } // INTERROGATE
            return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
        catch (Exception) { _exitCode = 1; _stop.TrySetResult(); return 1064; }
    }

    private void TryReport()
    {
        try { Report(); }
        catch (Exception) { _exitCode = 1; _stop.TrySetResult(); }
    }

    private void Report()
    {
        lock (_reportGate)
        {
            if (_reportedStopped) return; // SetServiceStatus(STOPPED) is sent exactly once.
            var state = checked((uint)_lifecycle.State);
            var status = new ServiceStatus
            {
                ServiceType = 0x10, CurrentState = state,
                ControlsAccepted = state == 4 ? 1u | 4u : 0,
                Win32ExitCode = _exitCode == 0 ? 0u : 1066u,
                ServiceSpecificExitCode = checked((uint)_exitCode),
                CheckPoint = state is 2 or 3 ? ++_checkpoint : 0,
                WaitHint = state is 2 or 3 ? 10000u : 0,
            };
            if (!SetServiceStatus(_statusHandle, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _reportedStopped = state == 1;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ControlCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Name;
        [MarshalAs(UnmanagedType.FunctionPtr)] internal ServiceMainCallback? Main;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ControlCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
