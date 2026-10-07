using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

public sealed class FileUseInputTransferTests
{
    private const string PrivateText = "SYNTHETIC_PRIVATE_FILE_USE_INPUT";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OriginalWriteAndFlushFaultsRemainOwnedAndIdentifyTheirDifferentParentPhases()
    {
        // A failing write and a buffered write whose flush fails are different
        // native-I/O boundaries; both belong to the one retained Stdin operation.
        using var writeStream = new TransferStream(failWrite: true);
        using var writeWriter = Writer(writeStream);
        var writing = new NativeProcessCleanupResources { StdinWriter = writeWriter };
        var writeProgress = new FileUseParentProgress(() => 100);
        var writeError = await Assert.ThrowsAsync<IOException>(() => FileUseInputTransfer.WriteAndCloseAsync(
            writing, "request"u8.ToArray(), writeProgress.Advance, CancellationToken.None));
        var originalWrite = Assert.IsAssignableFrom<Task>(writing.Stdin);
        Assert.True(originalWrite.IsFaulted);
        Assert.Same(writeError, originalWrite.Exception!.GetBaseException());
        Assert.Equal("INPUT_WRITE", writeProgress.Latest.Phase);
        Assert.Equal(1, writeStream.WriteCalls);
        Assert.Equal(0, writeStream.AsyncFlushCalls);
        Assert.False(writing.ReleaseObservation.StdinWriter.DisposeReturned);

        var writeReport = await Owner(writing).RetryAsync().WaitAsync(Deadline);
        Assert.True(writeReport.Complete, writeReport.Summary);
        Assert.Equal(new OwnedInputReport(OwnedInputState.Failed, "IO", writeError.HResult), writeReport.Stdin);
        Assert.True(writeReport.ReleaseObservation!.StdinWriter.DisposeReturned);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(writeReport), StringComparison.Ordinal);

        using var flushStream = new TransferStream(failAsyncFlush: true);
        using var flushWriter = Writer(flushStream);
        var flushing = new NativeProcessCleanupResources { StdinWriter = flushWriter };
        var flushProgress = new FileUseParentProgress(() => 200);
        var flushError = await Assert.ThrowsAsync<IOException>(() => FileUseInputTransfer.WriteAndCloseAsync(
            flushing, "request"u8.ToArray(), flushProgress.Advance, CancellationToken.None));
        var originalFlush = Assert.IsAssignableFrom<Task>(flushing.Stdin);
        Assert.True(originalFlush.IsFaulted);
        Assert.Same(flushError, originalFlush.Exception!.GetBaseException());
        Assert.Equal("INPUT_FLUSH", flushProgress.Latest.Phase);
        Assert.Equal(1, flushStream.WriteCalls);
        Assert.Equal(1, flushStream.AsyncFlushCalls);
        Assert.False(flushing.ReleaseObservation.StdinWriter.DisposeReturned);
        var flushReport = await Owner(flushing).RetryAsync().WaitAsync(Deadline);
        Assert.True(flushReport.Complete, flushReport.Summary);
        Assert.Equal(new OwnedInputReport(OwnedInputState.Failed, "IO", flushError.HResult), flushReport.Stdin);
        Assert.True(flushReport.ReleaseObservation!.StdinWriter.DisposeReturned);
        Assert.True(originalWrite.IsFaulted);
        Assert.True(originalFlush.IsFaulted);
    }

    [Fact]
    public async Task CloseFaultKeepsSuccessfulOriginalInputAndFreezesParentPrefixAtTheExactDeadline()
    {
        long elapsed = 1999;
        using var stream = new TransferStream(failCloseFlush: true, afterAsyncFlush: () => elapsed = 2000);
        using var writer = Writer(stream);
        var resources = new NativeProcessCleanupResources { StdinWriter = writer };
        var progress = new FileUseParentProgress(() => elapsed);
        var failure = await Assert.ThrowsAsync<IOException>(() => FileUseInputTransfer.WriteAndCloseAsync(
            resources, "request"u8.ToArray(), progress.Advance, CancellationToken.None));
        var original = Assert.IsAssignableFrom<Task>(resources.Stdin);

        Assert.True(original.IsCompletedSuccessfully);
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(1, stream.AsyncFlushCalls);
        Assert.Equal(new FileUseParentPhaseReport("INPUT_CLOSE", 2000), progress.Latest);
        Assert.Equal(new FileUseParentPhaseReport("INPUT_FLUSH", 1999), progress.BeforeDeadline);
        var before = progress.BeforeDeadline;
        Assert.Equal("IO", OwnedProcessCleanup.Kind(failure));
        Assert.True(resources.ReleaseObservation.StdinWriter.Present);
        Assert.False(resources.ReleaseObservation.StdinWriter.DisposeReturned);
        Assert.Equal(1, stream.DisposeCalls); // StreamWriter's finally closed its underlying stream.

        var cleanup = await Owner(resources).RetryAsync().WaitAsync(Deadline);

        Assert.True(cleanup.Complete, cleanup.Summary);
        Assert.Equal(new OwnedInputReport(OwnedInputState.Completed, "NONE", 0), cleanup.Stdin);
        Assert.True(cleanup.ReleaseObservation!.StdinWriter.DisposeReturned);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.Same(before, progress.BeforeDeadline);
        Assert.Equal(new FileUseParentPhaseReport("INPUT_CLOSE", 2000), progress.Latest);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(cleanup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledWaitRetainsPendingOriginalInputAndLateFaultNeedsExplicitCleanupRetry()
    {
        using var stream = new PendingWriteStream();
        using var writer = Writer(stream);
        using var cancellation = new CancellationTokenSource();
        var root = Directory.CreateTempSubdirectory("autovpn-input-transfer-");
        var input = root.CreateSubdirectory("input");
        var resources = new NativeProcessCleanupResources { StdinWriter = writer, Directory = input };
        var progress = new FileUseParentProgress(() => 100);
        var transfer = FileUseInputTransfer.WriteAndCloseAsync(resources, "request"u8.ToArray(),
            progress.Advance, cancellation.Token);
        var original = Assert.IsAssignableFrom<Task>(resources.Stdin);
        var owner = Owner(resources);
        var originalFailure = new IOException(PrivateText);
        try
        {
            Assert.False(original.IsCompleted);
            Assert.Equal("INPUT_WRITE", progress.Latest.Phase);
            cancellation.Cancel();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transfer.WaitAsync(Deadline));
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.True(transfer.IsCanceled);
            Assert.False(original.IsCompleted);
            Assert.Same(original, resources.Stdin);
            Assert.Equal(0, stream.DisposeCalls);
            Assert.Equal(0, stream.AsyncFlushCalls);

            var first = await owner.RetryAsync().WaitAsync(Deadline);

            Assert.Equal("OUTPUT_JOIN_FAILED", first.Phase);
            Assert.Equal("TIMEOUT", first.ExceptionKind);
            Assert.Equal(OwnedInputState.Pending, first.Stdin.State);
            Assert.False(first.InputJoined);
            Assert.False(first.Complete);
            Assert.False(first.DirectoryRemoved);
            Assert.False(first.ResourcesReleased);
            Assert.True(Directory.Exists(input.FullName));
            Assert.False(first.ReleaseObservation!.StdinWriter.DisposeReturned);
            Assert.Equal(0, stream.DisposeCalls);
            var originalReport = JsonSerializer.Serialize(first);

            stream.Fail(originalFailure);
            var lateFailure = await Assert.ThrowsAsync<IOException>(() => original.WaitAsync(Deadline));
            Assert.Same(originalFailure, lateFailure);
            Assert.True(original.IsFaulted);
            Assert.Same(first, owner.LastReport);
            Assert.Equal(0, stream.DisposeCalls);
            Assert.True(Directory.Exists(input.FullName));
            var retry = await owner.RetryAsync().WaitAsync(Deadline);
            Assert.True(retry.Complete, retry.Summary);
            Assert.True(retry.InputJoined);
            Assert.Equal(new OwnedInputReport(OwnedInputState.Failed, "IO", originalFailure.HResult), retry.Stdin);
            Assert.True(retry.ReleaseObservation!.StdinWriter.DisposeReturned);
            Assert.False(Directory.Exists(input.FullName));
            Assert.Equal(1, stream.DisposeCalls);
            Assert.Equal(0, stream.AsyncFlushCalls);
            Assert.Equal("INPUT_WRITE", progress.Latest.Phase);
            Assert.Equal(originalReport, JsonSerializer.Serialize(first));
            Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(retry), StringComparison.Ordinal);
        }
        finally
        {
            stream.Fail(originalFailure);
            try { await original.WaitAsync(Deadline); }
            catch (IOException) { }
            await owner.RetryAsync().WaitAsync(Deadline);
            root.Delete(true);
        }
    }

    private static StreamWriter Writer(Stream stream) => new(stream, new UTF8Encoding(false, true));
    private static OwnedProcessCleanup Owner(NativeProcessCleanupResources resources) =>
        new(resources, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private sealed class TransferStream(bool failWrite = false, bool failAsyncFlush = false,
        bool failCloseFlush = false, Action? afterAsyncFlush = null) : MemoryStream
    {
        internal int WriteCalls { get; private set; }
        internal int AsyncFlushCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        private bool _closeFailed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return failWrite ? ValueTask.FromException(new IOException(PrivateText))
                : base.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            AsyncFlushCalls++;
            if (failAsyncFlush) return Task.FromException(new IOException(PrivateText));
            afterAsyncFlush?.Invoke();
            return Task.CompletedTask;
        }
        public override void Flush()
        {
            if (failCloseFlush && !_closeFailed)
            {
                _closeFailed = true;
                throw new IOException(PrivateText);
            }
            base.Flush();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class PendingWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AsyncFlushCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        // Model an already dispatched operation which cannot honor cancellation.
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_write.Task);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            AsyncFlushCalls++;
            return Task.CompletedTask;
        }
        internal void Fail(Exception error) => _write.TrySetException(error);
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }
}
