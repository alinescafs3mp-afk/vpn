using System.ComponentModel;
using System.Diagnostics;
using AutoVpn.Infrastructure.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.UnitTests;

// Managed controls for the native-status boundary. These are not Windows image-lifetime evidence.
public sealed class WindowsProcessExitWaiterTests
{
    [Fact]
    public async Task InitialNativeSignalCompletesWithoutSchedulingOrReadingTheClock()
    {
        using var handle = Handle();
        var calls = 0;
        var invokingThread = Environment.CurrentManagedThreadId;
        var task = WindowsProcessExitWaiter.WaitAsync(handle, TimeSpan.FromSeconds(5), (actual, milliseconds) =>
        {
            Assert.Same(handle, actual);
            Assert.Equal(invokingThread, Environment.CurrentManagedThreadId);
            Assert.Equal(0u, milliseconds);
            calls++;
            return new(WindowsProcessExitWaiter.Signaled);
        }, () => throw new InvalidOperationException("CLOCK_MUST_NOT_BE_READ"), out var initiallySignaled);

        Assert.True(initiallySignaled);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
        Assert.Equal(1, calls);
        Assert.False(handle.IsClosed);
    }

    [Fact]
    public async Task PendingProbeUsesOneDedicatedBackgroundThreadAndOnlyRemainingBudget()
    {
        using var handle = Handle();
        var calls = 0;
        var invokingThread = Environment.CurrentManagedThreadId;
        var task = WindowsProcessExitWaiter.WaitAsync(handle, TimeSpan.FromSeconds(5), (actual, milliseconds) =>
        {
            Assert.Same(handle, actual);
            if (Interlocked.Increment(ref calls) == 1)
            {
                Assert.Equal(0u, milliseconds);
                return new(WindowsProcessExitWaiter.TimedOut);
            }
            Assert.NotEqual(invokingThread, Environment.CurrentManagedThreadId);
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.True(Thread.CurrentThread.IsBackground);
            Assert.Equal(3749u, milliseconds); // 5000 - 1250.25, floored.
            return new(WindowsProcessExitWaiter.Signaled);
        }, () => TimeSpan.FromMilliseconds(1250.25), out var initiallySignaled);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(initiallySignaled);
        Assert.Equal(2, calls);
        Assert.False(handle.IsClosed);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(5001)]
    public async Task SchedulingThatConsumesBudgetDoesNotStartAnotherNativeWait(int elapsedMilliseconds)
    {
        using var handle = Handle();
        var calls = 0;
        var task = WindowsProcessExitWaiter.WaitAsync(handle, TimeSpan.FromSeconds(5), (_, milliseconds) =>
        {
            Assert.Equal(0u, milliseconds);
            Interlocked.Increment(ref calls);
            return new(WindowsProcessExitWaiter.TimedOut);
        }, () => TimeSpan.FromMilliseconds(elapsedMilliseconds));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("PROCESS_SIGNAL_TIMEOUT", error.Message);
        Assert.Equal(1, calls);
        Assert.False(handle.IsClosed);
    }

    [Theory]
    [InlineData(false, 0x102u)]
    [InlineData(false, 0xFFFFFFFFu)]
    [InlineData(false, 0x80u)]
    [InlineData(true, 0xFFFFFFFFu)]
    [InlineData(true, 0x80u)]
    public async Task OnlyNativeSignalCanCompleteSuccessfully(bool initial, uint status)
    {
        using var handle = Handle();
        var calls = 0;
        var task = WindowsProcessExitWaiter.WaitAsync(handle, TimeSpan.FromSeconds(5), (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            return !initial && call == 1
                ? new(WindowsProcessExitWaiter.TimedOut)
                : new(status, 6);
        }, () => TimeSpan.Zero, out var initiallySignaled);

        if (status == WindowsProcessExitWaiter.TimedOut)
            await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        else if (status == WindowsProcessExitWaiter.Failed)
        {
            var error = await Assert.ThrowsAsync<Win32Exception>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(6, error.NativeErrorCode);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("PROCESS_SIGNAL_UNEXPECTED_RESULT", error.Message);
        }
        Assert.Equal(initial ? 1 : 2, calls);
        Assert.Equal(initial ? (bool?)null : false, initiallySignaled);
        Assert.False(handle.IsClosed);
    }

    [Fact]
    public async Task ClosedAndInvalidHandlesAreRefusedBeforeNativeAccess()
    {
        using var closed = Handle();
        closed.Dispose();
        using var invalid = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var calls = 0;
        WindowsProcessExitWaiter.WaitResult Wait(SafeProcessHandle _, uint __)
        {
            calls++;
            return new(WindowsProcessExitWaiter.Signaled);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => WindowsProcessExitWaiter.WaitAsync(
            closed, TimeSpan.FromSeconds(1), Wait, () => TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsProcessExitWaiter.WaitAsync(
            invalid, TimeSpan.FromSeconds(1), Wait, () => TimeSpan.Zero));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task InvalidBudgetsAreRefusedBeforeNativeAccess(int milliseconds)
    {
        using var handle = Handle();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WindowsProcessExitWaiter.WaitAsync(
            handle, TimeSpan.FromMilliseconds(milliseconds),
            (_, _) => throw new InvalidOperationException("NATIVE_MUST_NOT_RUN"), () => TimeSpan.Zero));
    }

    [WindowsHandleFact]
    public async Task RealCurrentProcessRemainsNonsignaledAndFiniteNativeWaitTimesOut()
    {
        // This process is observed only: no child or kill. Both waits use the same
        // captured handle; the helper never reopens a PID or duplicates a handle.
        using var process = Process.GetCurrentProcess();
        var handle = process.SafeHandle;
        var task = WindowsProcessExitWaiter.WaitAsync(handle, TimeSpan.FromMilliseconds(50),
            out var initiallySignaled);
        // Await settlement before any assertion or disposal of this owned Process wrapper.
        var error = await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.False(initiallySignaled);
        Assert.True(task.IsCompleted);
        Assert.Equal("PROCESS_SIGNAL_TIMEOUT", error.Message);
        Assert.False(handle.IsClosed);
        Assert.False(process.HasExited);
    }

    // No native resource is owned; the deterministic seam must never call Win32.
    private static SafeProcessHandle Handle() => new(new IntPtr(42), ownsHandle: false);
}
