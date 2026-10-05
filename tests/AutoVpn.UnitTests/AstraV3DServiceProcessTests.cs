using System.Diagnostics;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.UnitTests;

public sealed class AstraV3DServiceProcessTests
{
    [Theory]
    [InlineData(1u, "Stopped")] [InlineData(2u, "Starting")] [InlineData(3u, "Stopping")]
    [InlineData(4u, "Unavailable")] [InlineData(5u, "Resuming")] [InlineData(6u, "Pausing")]
    [InlineData(7u, "Paused")] [InlineData(0u, "Unavailable")] [InlineData(uint.MaxValue, "Unavailable")]
    public void ScmStateDoesNotInventReadinessOrAnInstalledVpn(uint state, string expected)
    {
        var result = InstalledServiceProtocol.NonRunningCheck(state);
        Assert.Equal(expected, result.State); Assert.Null(result.Reply); Assert.NotEqual("Ready", result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [WindowsHandleFact]
    public void RetainedLiveProcessHandleIsNotSignaled()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows-only test was not skipped.");
        using var self = Process.GetCurrentProcess();
        Assert.True(ProcessHandleLiveness.IsRunning(self.SafeHandle));
    }

    [WindowsHandleFact]
    public async Task Exited259IsNotMistakenForStillActive()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows-only test was not skipped.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add("exit259");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned fixture did not start.");
        var handle = child.SafeHandle;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(259, child.ExitCode);
            Assert.False(ProcessHandleLiveness.IsRunning(handle));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    [WindowsHandleFact]
    public void InvalidAndClosedHandlesFailClosed()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows-only test was not skipped.");
        using var handle = new SafeProcessHandle(IntPtr.Zero, ownsHandle: true);
        Assert.False(ProcessHandleLiveness.IsRunning(handle));
        handle.Dispose(); Assert.False(ProcessHandleLiveness.IsRunning(handle));
    }
}

public sealed class WindowsHandleFactAttribute : FactAttribute
{
    public WindowsHandleFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Native Windows process handle proof is NOT_RUN on this platform.";
    }
}
