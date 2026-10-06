using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

public sealed class AstraV3FOutputTests
{
    [Fact]
    public async Task EmptyStreamReportsEofInsteadOfCancellation()
    {
        using var reader = Reader("");
        var result = await ProbeOutputDrain.ReadAsync(reader, CancellationToken.None);
        Assert.Equal(new ProbeOutputResult(0, ProbeOutputEnd.Eof), result);
    }

    [Fact]
    public async Task RetentionCapDoesNotStopDrainingAndCountsCharactersHonestly()
    {
        var text = string.Concat(Enumerable.Repeat("тест🙂", 3000));
        using var reader = Reader(text);
        var prefix = new StringBuilder();
        var result = await ProbeOutputDrain.ReadAsync(reader, CancellationToken.None, prefix);
        Assert.Equal(text.Length, result.Characters);
        Assert.Equal(ProbeOutputEnd.Eof, result.End);
        Assert.Equal(text[..2000], prefix.ToString());
        Assert.NotEqual(Encoding.UTF8.GetByteCount(text), result.Characters);
    }

    [Fact]
    public async Task AlreadyCanceledRequestDoesNotRead()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        using var reader = new FaultReader(() => throw new Exception("MUST_NOT_READ"));
        var result = await ProbeOutputDrain.ReadAsync(reader, stop.Token);
        Assert.Equal(ProbeOutputEnd.Canceled, result.End);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task PendingReadCancellationIsObservedWithoutAnEof()
    {
        using var stop = new CancellationTokenSource();
        using var reader = new PendingReader();
        var task = ProbeOutputDrain.ReadAsync(reader, stop.Token);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(task.IsCompleted);
        stop.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ProbeOutputEnd.Canceled, result.End);
        Assert.Equal(ProbeOutputError.None, result.Error);
    }

    [Theory]
    [InlineData(0, ProbeOutputError.Io)]
    [InlineData(1, ProbeOutputError.UnexpectedCancellation)]
    [InlineData(2, ProbeOutputError.Disposed)]
    [InlineData(3, ProbeOutputError.InvalidState)]
    [InlineData(4, ProbeOutputError.Decoding)]
    public async Task UnexpectedReadFaultsAreNotSuccessfulEofAndDoNotExposeTheirMessage(int kind, ProbeOutputError expected)
    {
        const string secret = "SYNTHETIC_PRIVATE_OUTPUT_DO_NOT_RETAIN";
        Exception fault = kind switch
        {
            0 => new IOException(secret), 1 => new OperationCanceledException(secret),
            2 => new ObjectDisposedException(secret), 3 => new InvalidOperationException(secret),
            _ => new DecoderFallbackException(secret),
        };
        using var reader = new FaultReader(() => fault);
        var result = await ProbeOutputDrain.ReadAsync(reader, CancellationToken.None);
        Assert.Equal(ProbeOutputEnd.Failed, result.End);
        Assert.Equal(expected, result.Error);
        Assert.Equal(fault.HResult, result.HResult);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IoFaultIsNotLaunderedByAConcurrentCancellation()
    {
        using var stop = new CancellationTokenSource();
        using var reader = new FaultReader(() => { stop.Cancel(); return new IOException("PRIVATE_IO_TEXT"); });
        var result = await ProbeOutputDrain.ReadAsync(reader, stop.Token);
        Assert.Equal(ProbeOutputEnd.Failed, result.End);
        Assert.Equal(ProbeOutputError.Io, result.Error);
    }

    [Fact]
    public async Task CompletedReadFaultReleasesOwnedFilesButStillRejectsTheProbe()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3f-fault-");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "private-config"), "SYNTHETIC_SECRET");
        var worker = SyntheticWorker(Task.FromResult(new ProbeOutputResult(10, ProbeOutputEnd.Failed, ProbeOutputError.Io, unchecked((int)0x80070005))), directory.FullName);
        try
        {
            var error = await Assert.ThrowsAsync<ProbeCleanupException>(() => worker.DisposeAsync().AsTask());
            var report = Assert.IsType<ProbeCleanupReport>(error.Report);
            Assert.True(report.ProcessExitConfirmed);
            Assert.True(report.ResourcesReleased);
            Assert.True(report.DirectoryRemoved);
            Assert.Equal("OUTPUT_RESULT_FAILED", report.Phase);
            Assert.Equal(ProbeOutputEnd.Failed, report.Stdout!.End);
            Assert.DoesNotContain(directory.FullName, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC_SECRET", error.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(directory.FullName));
            await Assert.ThrowsAsync<ProbeCleanupException>(() => worker.DisposeAsync().AsTask());
        }
        finally { if (Directory.Exists(directory.FullName)) directory.Delete(true); }
    }

    [Fact]
    public async Task IncompleteReaderRetainsFilesAndCleanupCanJoinItsLaterCompletion()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3f-pending-");
        var pending = new TaskCompletionSource<ProbeOutputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = SyntheticWorker(pending.Task, directory.FullName);
        try
        {
            var error = await Assert.ThrowsAsync<ProbeCleanupException>(() => worker.DisposeAsync().AsTask());
            var report = Assert.IsType<ProbeCleanupReport>(error.Report);
            Assert.Equal("OUTPUT_DRAIN_FAILED", report.Phase);
            Assert.Equal("TIMEOUT", report.ExceptionKind);
            Assert.True(report.ProcessExitConfirmed);
            Assert.False(report.ResourcesReleased);
            Assert.True(Directory.Exists(directory.FullName));
            pending.SetResult(new(0, ProbeOutputEnd.Canceled));
            await worker.DisposeAsync();
            Assert.Equal("COMPLETED", worker.CleanupReport!.Phase);
            Assert.True(worker.CleanupReport.ResourcesReleased);
            Assert.False(Directory.Exists(directory.FullName));
        }
        finally
        {
            pending.TrySetResult(new(0, ProbeOutputEnd.Canceled));
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActualQuietChildHasAnObservedTerminalReport()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-v3f-child-");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = directory.FullName, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add("sleep");
        await using var worker = await ProbeWorker.StartAsync(start, null, TimeSpan.FromSeconds(2), CancellationToken.None, directory.FullName);
        Assert.True(worker.Ready);
        await worker.DisposeAsync();
        var report = Assert.IsType<ProbeCleanupReport>(worker.CleanupReport);
        Assert.Equal("COMPLETED", report.Phase);
        Assert.True(report.ProcessExitConfirmed && report.ResourcesReleased && report.DirectoryRemoved);
        Assert.Contains(report.Stdout!.End, new[] { ProbeOutputEnd.Eof, ProbeOutputEnd.Canceled });
        Assert.Contains(report.Stderr!.End, new[] { ProbeOutputEnd.Eof, ProbeOutputEnd.Canceled });
        Assert.Equal(worker.OutputCharacters, worker.OutputBytes);
    }

    [Fact]
    public async Task ReentrantCleanupJoinsThePublishedAttemptBeforeOutputCancellationReturns()
    {
        var directory = Directory.CreateTempSubdirectory("autovpn-pipe-reentrant-");
        using var stop = new CancellationTokenSource();
        var worker = SyntheticWorker(Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.Eof)),
            directory.FullName, stop);
        Task? rejoined = null;
        var completedInsideCallback = true;
        using var registration = stop.Token.Register(() =>
        {
            rejoined = worker.DisposeAsync().AsTask();
            completedInsideCallback = rejoined.IsCompleted;
        });
        try
        {
            var cleanup = worker.DisposeAsync().AsTask();
            await cleanup;
            Assert.Same(cleanup, rejoined);
            Assert.False(completedInsideCallback);
            Assert.Equal("COMPLETED", worker.CleanupReport!.Phase);
            Assert.True(worker.CleanupReport.ResourcesReleased);
            Assert.False(Directory.Exists(directory.FullName));
        }
        finally { await worker.DisposeAsync(); }
    }

    private static ProbeWorker SyntheticWorker(Task<ProbeOutputResult> stdout, string directory,
        CancellationTokenSource? output = null)
    {
        // Exercise the real cleanup state machine with a controlled reader result, not an OS claim.
        var constructor = typeof(ProbeWorker).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (ProbeWorker)constructor.Invoke(new object?[] { null, output ?? new CancellationTokenSource(), stdout,
            Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.Eof)), directory, false, "", new StringBuilder() });
    }
    private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);
    private sealed class FaultReader(Func<Exception> fault) : StreamReader(Stream.Null)
    {
        public int Calls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromException<int>(fault()); }
    }
    private sealed class PendingReader : StreamReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingReader() : base(Stream.Null) { }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        { Entered.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
