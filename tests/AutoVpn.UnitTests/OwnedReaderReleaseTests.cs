using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.UnitTests;

public sealed class OwnedReaderReleaseTests
{
    [Fact]
    public async Task PendingReadRetainsOriginalReaderAndDirectoryUntilExplicitRetry()
    {
        using var file = new ReaderFile("finite output"u8.ToArray());
        var input = file.Root.CreateSubdirectory("input");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new NativeProcessCleanupResources { StdoutReader = file.Reader, Directory = input };
        var drain = ReadAfterGateAsync(file.Reader, gate.Task);
        native.Stdout = drain;
        var owner = Owner(native);
        try
        {
            var first = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("OUTPUT_JOIN_FAILED", first.Phase);
            Assert.Equal(OwnedOutputState.Pending, first.Stdout.State);
            Assert.False(first.Complete);
            Assert.False(first.ReadersJoined);
            Assert.True(Directory.Exists(input.FullName));
            Assert.Equal(0, file.Reader.DisposeCalls);
            Assert.False(file.Handle.IsClosed);
            var original = Assert.IsType<OwnedResourceReleaseReport>(first.ReleaseObservation).StdoutReader;
            Assert.True(original.Present);
            Assert.False(original.DisposeReturned);
            Assert.False(original.SafeHandleClosed);
            var originalJson = JsonSerializer.Serialize(first);

            gate.SetResult();
            Assert.Equal("finite output", await drain.WaitAsync(TimeSpan.FromSeconds(5)));
            var retry = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(retry.Complete, retry.Summary);
            Assert.Equal(OwnedOutputState.Eof, retry.Stdout.State);
            Assert.False(Directory.Exists(input.FullName));
            Assert.Equal(1, file.Reader.DisposeCalls);
            Assert.True(file.Handle.IsClosed);
            var released = Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation).StdoutReader;
            Assert.True(released.DisposeReturned);
            Assert.Equal(file.Handle.IsClosed, released.SafeHandleClosed);
            Assert.Equal(file.Handle.IsInvalid, released.SafeHandleInvalid);
            Assert.Equal(originalJson, JsonSerializer.Serialize(first));
        }
        finally
        {
            gate.TrySetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SettledDecodingFailureReleasesOriginalReaderWithoutRewritingItsFault()
    {
        using var file = new ReaderFile([0xff]);
        var native = new NativeProcessCleanupResources { StdoutReader = file.Reader };
        var drain = file.Reader.ReadToEndAsync();
        native.Stdout = drain;
        var original = await Assert.ThrowsAsync<DecoderFallbackException>(async () => { await drain; });

        var report = await Owner(native).RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.ReadersJoined);
        Assert.False(report.OutputHealthy);
        Assert.Equal(OwnedOutputState.Failed, report.Stdout.State);
        Assert.Equal("DECODING", report.Stdout.ExceptionKind);
        Assert.Equal(original.HResult, report.Stdout.HResult);
        Assert.True(drain.IsFaulted);
        Assert.Same(original, drain.Exception!.GetBaseException());
        Assert.Equal(1, file.Reader.DisposeCalls);
        Assert.True(file.Handle.IsClosed);
        var released = Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation).StdoutReader;
        Assert.True(released.Present);
        Assert.True(released.DisposeReturned);
        Assert.Equal(file.Handle.IsClosed, released.SafeHandleClosed);
        Assert.Equal(file.Handle.IsInvalid, released.SafeHandleInvalid);
    }

    [Fact]
    public async Task PartialReaderDisposeFailureRetainsLaterResourcesAndRetrySkipsSuccessfulDispose()
    {
        using var file = new ReaderFile("complete"u8.ToArray());
        using var second = new TrackingReader(new MemoryStream(), failFirstDispose: true);
        using var process = new Process();
        using var binary = new FileStream(Path.Combine(file.Root.FullName, "binary.bin"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.Read);
        var binaryHandle = binary.SafeFileHandle;
        var input = file.Root.CreateSubdirectory("input");
        var native = new NativeProcessCleanupResources
        {
            StdoutReader = file.Reader, StderrReader = second, Process = process, Binary = binary, Directory = input,
        };
        native.Stdout = file.Reader.ReadToEndAsync();
        native.Stderr = second.ReadToEndAsync();
        var owner = Owner(native);

        var first = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RESOURCE_RELEASE_FAILED", first.Phase);
        Assert.Equal("IO", first.ExceptionKind);
        Assert.False(first.Complete);
        Assert.True(first.ReadersJoined);
        Assert.True(first.DirectoryRemoved);
        Assert.False(Directory.Exists(input.FullName));
        var partial = Assert.IsType<OwnedResourceReleaseReport>(first.ReleaseObservation);
        Assert.True(partial.StdoutReader.DisposeReturned);
        Assert.True(partial.StdoutReader.SafeHandleClosed);
        Assert.True(partial.StderrReader.Present);
        Assert.False(partial.StderrReader.DisposeReturned);
        Assert.Null(partial.StderrReader.SafeHandleClosed);
        Assert.Null(partial.StderrReader.SafeHandleInvalid);
        Assert.False(partial.ProcessDisposeReturned);
        Assert.False(partial.BinaryDisposeReturned);
        Assert.False(binaryHandle.IsClosed);
        Assert.Equal(1, file.Reader.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        var originalJson = JsonSerializer.Serialize(first);

        var retry = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(retry.Complete, retry.Summary);
        Assert.Equal(1, file.Reader.DisposeCalls);
        Assert.Equal(2, second.DisposeCalls);
        var released = Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation);
        Assert.True(released.StderrReader.DisposeReturned);
        Assert.True(released.ProcessDisposeReturned);
        Assert.True(released.BinaryDisposeReturned);
        Assert.True(binaryHandle.IsClosed);
        Assert.Equal(originalJson, JsonSerializer.Serialize(first));
    }

    [Fact]
    public async Task PartialAllocationDisposesCapturedReaderWithoutInventingAbsentHandles()
    {
        using var reader = new TrackingReader(new MemoryStream());
        var native = new NativeProcessCleanupResources { StdoutReader = reader };

        var report = await Owner(native).RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Complete, report.Summary);
        Assert.Equal(OwnedOutputState.NotStarted, report.Stdout.State);
        Assert.Equal(OwnedOutputState.NotStarted, report.Stderr.State);
        Assert.Equal(1, reader.DisposeCalls);
        var released = Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation);
        Assert.Equal(new OwnedReaderReleaseReport(true, true, null, null), released.StdoutReader);
        Assert.Equal(new OwnedReaderReleaseReport(false, false, null, null), released.StderrReader);
    }

    private static OwnedProcessCleanup Owner(NativeProcessCleanupResources resources) =>
        new(resources, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private static async Task<string> ReadAfterGateAsync(StreamReader reader, Task gate)
    {
        await gate;
        return await reader.ReadToEndAsync();
    }

    private sealed class TrackingReader(Stream stream, bool failFirstDispose = false)
        : StreamReader(stream, new UTF8Encoding(false, true))
    {
        internal int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                if (failFirstDispose && DisposeCalls == 1) throw new IOException("SYNTHETIC_READER_DISPOSE_FAILURE");
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ReaderFile : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("autovpn-reader-release-");
        internal TrackingReader Reader { get; }
        internal SafeFileHandle Handle { get; }

        internal ReaderFile(byte[] bytes)
        {
            FileStream? stream = null;
            try
            {
                var path = Path.Combine(Root.FullName, "output.bin");
                File.WriteAllBytes(path, bytes);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Handle = stream.SafeFileHandle;
                Reader = new TrackingReader(stream);
            }
            catch
            {
                stream?.Dispose();
                Root.Delete(true);
                throw;
            }
        }

        public void Dispose()
        {
            Reader.Dispose();
            Root.Delete(true);
        }
    }
}
