using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.Infrastructure.Core;

// The caller owns this exact process handle until the returned task settles.
// Neither HasExited nor an exit-code observation substitutes for native signal.
internal static class WindowsProcessExitWaiter
{
    internal const uint Signaled = 0;
    internal const uint TimedOut = 0x102;
    internal const uint Failed = uint.MaxValue;

    internal readonly record struct WaitResult(uint Status, int ErrorCode = 0);
    internal delegate WaitResult NativeWait(SafeProcessHandle handle, uint milliseconds);

    internal static Task WaitAsync(SafeProcessHandle processHandle, TimeSpan budget) =>
        WaitAsync(processHandle, budget, out _);

    internal static Task WaitAsync(SafeProcessHandle processHandle, TimeSpan budget, out bool? initiallySignaled)
    {
        initiallySignaled = null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WINDOWS_PROCESS_SIGNAL_REQUIRED");
        var started = Stopwatch.GetTimestamp();
        return WaitAsync(processHandle, budget, WaitNative, () => Stopwatch.GetElapsedTime(started), out initiallySignaled);
    }

    // A narrow deterministic seam: no PID lookup, alternate handle or native duplication.
    internal static Task WaitAsync(SafeProcessHandle processHandle, TimeSpan budget,
        NativeWait wait, Func<TimeSpan> elapsed) => WaitAsync(processHandle, budget, wait, elapsed, out _);

    internal static Task WaitAsync(SafeProcessHandle processHandle, TimeSpan budget,
        NativeWait wait, Func<TimeSpan> elapsed, out bool? initiallySignaled)
    {
        initiallySignaled = null;
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(elapsed);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(budget));

        try
        {
            RequireHandle(processHandle);
            var initial = wait(processHandle, 0);
            if (initial.Status != TimedOut)
            {
                RequireSignal(initial);
                initiallySignaled = true;
                return Task.CompletedTask;
            }
            initiallySignaled = false;
        }
        catch (Exception error) { return Task.FromException(error); }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                // Include the zero-time probe and scheduling delay in this same budget.
                // Floor rather than round up: never extend the owner's deadline.
                var remaining = budget - elapsed();
                var milliseconds = Math.Floor(remaining.TotalMilliseconds);
                if (milliseconds < 1) throw new TimeoutException("PROCESS_SIGNAL_TIMEOUT");
                RequireHandle(processHandle);
                RequireSignal(wait(processHandle, checked((uint)milliseconds)));
                // No further handle access after the native call returns. Completion
                // may allow the owner to dispose the original Process immediately.
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "AutoVPN process signal" };
        try { worker.Start(); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }

    private static void RequireHandle(SafeProcessHandle handle)
    {
        if (handle.IsClosed) throw new ObjectDisposedException("OWNED_PROCESS_HANDLE");
        if (handle.IsInvalid) throw new InvalidOperationException("OWNED_PROCESS_HANDLE_INVALID");
    }

    private static void RequireSignal(WaitResult result)
    {
        if (result.Status == Signaled) return;
        if (result.Status == TimedOut) throw new TimeoutException("PROCESS_SIGNAL_TIMEOUT");
        if (result.Status == Failed) throw new Win32Exception(result.ErrorCode);
        throw new InvalidOperationException("PROCESS_SIGNAL_UNEXPECTED_RESULT");
    }

    private static WaitResult WaitNative(SafeProcessHandle handle, uint milliseconds)
    {
        var status = WaitForSingleObject(handle, milliseconds);
        return new(status, status == Failed ? Marshal.GetLastPInvokeError() : 0);
    }

    // SafeHandle marshalling protects the exact owned handle during this call.
    // The owner also retains it between the initial probe and the finite worker wait.
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
