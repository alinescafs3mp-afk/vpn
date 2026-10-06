using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class RuntimePreparationTests
{
    [Fact]
    public async Task StopBeforeStartSealsAdmission()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        await runtime.StopAsync(default);
        var result = await runtime.StartAsync("[", default);
        Assert.False(result.Started); Assert.Equal("CORE_CLOSING", result.ReasonCode);
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task CanceledStopWaiterStillSealsAdmission()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await runtime.StopAsync(canceled.Token); }
        catch (OperationCanceledException) { }
        await runtime.StopAsync(default);
        Assert.Equal("CORE_CLOSING", (await runtime.StartAsync("[", default)).ReasonCode);
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task ConcurrentStopsShareTheRetainedTask()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        var first = runtime.StopAsync(default);
        var stops = Enumerable.Range(0, 16).Select(_ => runtime.StopAsync(default)).ToArray();
        Assert.All(stops, stop => Assert.Same(first, stop));
        await Task.WhenAll(stops);
        Assert.Same(first, runtime.StopAsync(default));
        Assert.False(runtime.IsRunning);
    }

    [Fact]
    public async Task FailedStartCannotBeRetriedOnTheSameInstance()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        try
        {
            Assert.Equal("CORE_PROFILE_INVALID", (await runtime.StartAsync("[", default)).ReasonCode);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync("[", default));
            Assert.Equal("CORE_ALREADY_ATTEMPTED", error.Message);
        }
        finally { await runtime.StopAsync(default); }
    }

    [Fact]
    public async Task AlreadyCanceledStartDoesNotConsumeTheAttempt()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.StartAsync("[", canceled.Token));
            Assert.Equal("CORE_PROFILE_INVALID", (await runtime.StartAsync("[", default)).ReasonCode);
        }
        finally { await runtime.StopAsync(default); }
    }

    [Fact]
    public async Task StopAfterFailedStartupIsRepeatable()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        Assert.False((await runtime.StartAsync("[", default)).Started);
        await runtime.StopAsync(default);
        await runtime.StopAsync(default);
        Assert.False(runtime.IsRunning);
        Assert.Equal("CORE_CLOSING", (await runtime.StartAsync("[", default)).ReasonCode);
    }

    [Fact]
    public async Task NullProfileDoesNotConsumeTheAttempt()
    {
        var runtime = new MihomoRuntimeProcess(null, null);
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.StartAsync(null!, default));
            Assert.Equal("CORE_PROFILE_INVALID", (await runtime.StartAsync("[", default)).ReasonCode);
        }
        finally { await runtime.StopAsync(default); }
    }

    [Fact]
    public async Task StartupStopRaceAlwaysSealsTheInstance()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var runtime = new MihomoRuntimeProcess(null, null);
            var start = runtime.StartAsync("[", default);
            await runtime.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            try { Assert.False((await start).Started); }
            catch (OperationCanceledException) { }
            Assert.True(start.IsCompleted);
            Assert.False(runtime.IsRunning);
            Assert.Equal("CORE_CLOSING", (await runtime.StartAsync("[", default)).ReasonCode);
        }
    }

    [Fact]
    public async Task RuntimeOutputReadsToActualEof()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 131072)));
        using var reader = new StreamReader(input);
        await MihomoRuntimeProcess.ReadOutputAsync(reader);
        Assert.Equal(input.Length, input.Position);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("state")]
    [InlineData("cancellation")]
    public async Task RuntimeOutputFaultsRemainFailuresWithoutPrivateText(string kind)
    {
        const string secret = "synthetic-private-output";
        Exception failure = kind switch
        {
            "io" => new IOException(secret),
            "disposed" => new ObjectDisposedException(secret),
            "state" => new InvalidOperationException(secret),
            _ => new OperationCanceledException(secret),
        };
        using var stream = new ControlledInput(failure);
        using var reader = new StreamReader(stream);
        var error = await Assert.ThrowsAsync<IOException>(() => MihomoRuntimeProcess.ReadOutputAsync(reader));
        Assert.Equal("CORE_OUTPUT_READ_FAILED", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeOutputRejectsInvalidUtf8()
    {
        using var stream = new MemoryStream(new byte[] { 0xff });
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var error = await Assert.ThrowsAsync<IOException>(() => MihomoRuntimeProcess.ReadOutputAsync(reader));
        Assert.Equal("CORE_OUTPUT_READ_FAILED", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task RuntimeOutputRejectsNullReader()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => MihomoRuntimeProcess.ReadOutputAsync(null!));
    }

    [Fact]
    public async Task RuntimeOutputDoesNotTreatAnIdleStreamAsEof()
    {
        using var stream = new ControlledInput();
        using var reader = new StreamReader(stream);
        var output = MihomoRuntimeProcess.ReadOutputAsync(reader);
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(output.IsCompleted);
        }
        finally { stream.Release.TrySetResult(); }
        await output.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RuntimeOutputDoesNotDisposeTheOriginalReader()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("owned-output"));
        using var reader = new StreamReader(stream);
        await MihomoRuntimeProcess.ReadOutputAsync(reader);
        Assert.True(stream.CanRead);
        Assert.Equal(-1, reader.Read());
    }

    [WindowsHandleFact]
    public async Task WindowsRuntimeOutputKeepsTheBoundedPoolResponsive()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add("pool-runtime");
        using var process = Process.Start(start)!;
        var output = new StringBuilder(); var errors = new StringBuilder();
        var stdout = ProbeOutputDrain.ReadAsync(process.StandardOutput, CancellationToken.None, output);
        var stderr = ProbeOutputDrain.ReadAsync(process.StandardError, CancellationToken.None, errors);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var drains = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(drains, result => Assert.Equal(ProbeOutputEnd.Eof, result.End));
            Assert.True(process.ExitCode == 0, output.ToString() + errors);
            Assert.Contains("CONTROL_PASSED:pool-runtime", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [AstraV3NativeFact]
    public async Task NativeRuntimeStopsItsActiveCoreAndSealsAdmission()
    {
        using var controller = CorePortLease.Reserve(); using var socks = CorePortLease.Reserve();
        var yaml = AstraV3ProfileTests.Profile(controller.Port, socks.Port);
        var runtime = new MihomoRuntimeProcess(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH"), TestCorePins.ExpectedHash);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        controller.Dispose(); socks.Dispose();
        try
        {
            var started = await runtime.StartAsync(yaml, budget.Token);
            Assert.True(started.Started, started.ReasonCode); Assert.True(runtime.IsRunning);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => runtime.StopAsync(budget.Token)));
            Assert.False(runtime.IsRunning);
            Assert.Equal("CORE_CLOSING", (await runtime.StartAsync(yaml, default)).ReasonCode);
        }
        finally { await runtime.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [AstraV3NativeFact]
    public async Task NativeStartupStopRaceIsJoinedWithoutAResurrectedCore()
    {
        using var controller = CorePortLease.Reserve(); using var socks = CorePortLease.Reserve();
        var yaml = AstraV3ProfileTests.Profile(controller.Port, socks.Port);
        var runtime = new MihomoRuntimeProcess(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH"), TestCorePins.ExpectedHash);
        controller.Dispose(); socks.Dispose();
        var startup = runtime.StartAsync(yaml, default);
        try
        {
            await runtime.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(startup.IsCompleted);
            try { await startup; } catch (OperationCanceledException) { }
            Assert.False(runtime.IsRunning);
            Assert.Equal("CORE_CLOSING", (await runtime.StartAsync(yaml, default)).ReasonCode);
        }
        finally { await runtime.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class ControlledInput(Exception? failure = null) : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (failure is not null) throw failure;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] b, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
