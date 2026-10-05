using System.Diagnostics;
using System.Text;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3GAvailablePipeTests
{
    [Fact]
    public async Task IdleReaderDoesNotReturnFalseEofAndCancellationCompletes()
    {
        var source = new Reader((_, _) => null);
        using var stream = new AvailablePipeReadStream(source);
        using var stop = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[16], stop.Token).AsTask();
        Assert.False(read.IsCompleted); Assert.True(source.Calls > 0);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AlreadyCanceledReadDoesNotPoll()
    {
        var source = new Reader((_, _) => throw new Exception("MUST_NOT_POLL"));
        using var stream = new AvailablePipeReadStream(source);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1], stop.Token).AsTask());
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task EmptyReadDoesNotPoll()
    {
        var source = new Reader((_, _) => throw new Exception("MUST_NOT_POLL"));
        using var stream = new AvailablePipeReadStream(source);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty)); Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task ExplicitEofIsPreserved()
    {
        using var stream = new AvailablePipeReadStream(new Reader((_, _) => 0));
        Assert.Equal(0, await stream.ReadAsync(new byte[4]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public async Task InvalidNativeCountsFailClosed(int count)
    {
        using var stream = new AvailablePipeReadStream(new Reader((_, _) => count));
        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[16]).AsTask());
    }

    [Fact]
    public async Task ConcurrentReaderIsRefusedAndFirstCanStillCancel()
    {
        using var stream = new AvailablePipeReadStream(new Reader((_, _) => null));
        using var stop = new CancellationTokenSource();
        var first = stream.ReadAsync(new byte[1], stop.Token).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ReadAsync(new byte[1]).AsTask());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitUnicodeAndBomKeepCharacterCountsAndRetention(bool bom)
    {
        var text = string.Concat(Enumerable.Repeat("тест🙂", 600));
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : []).Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        // Explicit UTF-8 BOM, including when the encoding instance has no preamble.
        if (bom) bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var offset = 0;
        using var stream = new AvailablePipeReadStream(new Reader((b, _) =>
        { if (offset == bytes.Length) return 0; b[0] = bytes[offset++]; return 1; }));
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024);
        var prefix = new StringBuilder();
        var result = await ProbeOutputDrain.ReadAsync(reader, CancellationToken.None, prefix);
        Assert.Equal(ProbeOutputEnd.Eof, result.End);
        Assert.Equal(text.Length, result.Characters); Assert.Equal(text[..2000], prefix.ToString());
    }

    [Fact]
    public async Task ReadFaultIsNotConvertedIntoEofOrCancellation()
    {
        using var stop = new CancellationTokenSource();
        using var stream = new AvailablePipeReadStream(new Reader((_, _) =>
        { stop.Cancel(); throw new IOException("SYNTHETIC_PRIVATE_MESSAGE"); }));
        using var reader = new StreamReader(stream);
        var result = await ProbeOutputDrain.ReadAsync(reader, stop.Token);
        Assert.Equal(ProbeOutputEnd.Failed, result.End); Assert.Equal(ProbeOutputError.Io, result.Error);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_MESSAGE", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadChunkIsBoundedAndDisposalPreventsNewReads()
    {
        var source = new Reader((b, count) => { Assert.Equal(4096, count); Array.Fill(b, (byte)1); return count; });
        using var stream = new AvailablePipeReadStream(source);
        var bytes = new byte[65536]; Assert.Equal(4096, await stream.ReadAsync(bytes));
        Assert.Equal(0, bytes[4096]); stream.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(bytes).AsTask());
    }

    [WindowsHandleFact]
    public Task LegacyQuietPipesOccupyTheBoundedPool() => RunControlAsync("pool-legacy");

    [WindowsHandleFact]
    public Task AvailableQuietPipesLeaveTheBoundedPoolUsable() => RunControlAsync("pool-available");

    private static async Task RunControlAsync(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.Contains("CONTROL_PASSED", await stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class Reader(Func<byte[], int, int?> read) : IAvailablePipeReader
    {
        public int Calls { get; private set; }
        public int? ReadAvailable(byte[] buffer, int count) { Calls++; return read(buffer, count); }
    }
}
