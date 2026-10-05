using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.Infrastructure.WindowsService;

/// <summary>Checks the retained process object, never an ambiguous exit-code value or a fresh PID lookup.</summary>
[SupportedOSPlatform("windows")]
public static class ProcessHandleLiveness
{
    public static bool IsRunning(SafeProcessHandle process)
    {
        if (process.IsInvalid || process.IsClosed) return false;
        try { return WaitForSingleObject(process, 0) == 0x102; } // WAIT_TIMEOUT only.
        catch (ObjectDisposedException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
