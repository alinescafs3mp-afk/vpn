using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.UnitTests;

public sealed class OwnedInputReleaseTests
{
    private const string PrivateText = "SYNTHETIC_PRIVATE_HELPER_REQUEST";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PendingWriteRetainsOriginalWriterAndDirectoryUntilExplicitRetry()
    {
        using var file = new WriterFile();
        var input = file.Root.CreateSubdirectory("input");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new NativeProcessCleanupResources
        {
            StdinWriter = file.Writer, Directory = input,
            Stdout = Task.CompletedTask, Stderr = Task.CompletedTask,
        };
        var write = WriteAfterGateAsync(file.Writer, gate.Task);
        native.Stdin = write;
        var owner = Owner(native);
        try
        {
            Assert.Throws<InvalidOperationException>(native.CloseStdin);
            var first = await owner.RetryAsync().WaitAsync(Deadline);
            Assert.Equal("OUTPUT_JOIN_FAILED", first.Phase);
            Assert.Equal("TIMEOUT", first.ExceptionKind);
            Assert.Equal(OwnedInputState.Pending, first.Stdin.State);
            Assert.Equal(OwnedOutputState.Eof, first.Stdout.State);
            Assert.Equal(OwnedOutputState.Eof, first.Stderr.State);
            Assert.False(first.InputJoined);
            Assert.False(first.Complete);
            Assert.False(first.DirectoryRemoved);
            Assert.False(first.ResourcesReleased);
            Assert.True(Directory.Exists(input.FullName));
            Assert.Equal(0, file.Writer.DisposeCalls);
            Assert.False(file.Handle.IsClosed);
            var held = Assert.IsType<OwnedResourceReleaseReport>(first.ReleaseObservation).StdinWriter;
            Assert.True(held.Present);
            Assert.False(held.DisposeReturned);
            Assert.False(held.SafeHandleClosed);
            var originalJson = JsonSerializer.Serialize(first);

            gate.SetResult();
            await write.WaitAsync(Deadline);
            Assert.Same(first, owner.LastReport);
            Assert.Equal(0, file.Writer.DisposeCalls);
            Assert.True(Directory.Exists(input.FullName));
            var retry = await owner.RetryAsync().WaitAsync(Deadline);
            Assert.True(retry.Complete, retry.Summary);
            Assert.True(retry.InputJoined);
            Assert.Equal(OwnedInputState.Completed, retry.Stdin.State);
            Assert.Equal(1, file.Writer.DisposeCalls);
            Assert.True(file.Handle.IsClosed);
            Assert.False(Directory.Exists(input.FullName));
            var released = Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation).StdinWriter;
            Assert.True(released.DisposeReturned);
            Assert.Equal(file.Handle.IsClosed, released.SafeHandleClosed);
            Assert.Equal(file.Handle.IsInvalid, released.SafeHandleInvalid);
            Assert.Equal("finite request", File.ReadAllText(file.Path));
            Assert.Equal(originalJson, JsonSerializer.Serialize(first));
        }
        finally
        {
            gate.TrySetResult();
            await write.WaitAsync(Deadline);
            await owner.RetryAsync().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task SettledOriginalWriteFaultReleasesWriterWithoutErasingFailureOrChangingOutputHealth()
    {
        using var stream = new FaultOnceWriteStream();
        using var writer = new TrackingWriter(stream);
        var native = new NativeProcessCleanupResources { StdinWriter = writer };
        // Exceed the writer's actual buffer so the original WriteAsync faults in
        // its underlying stream rather than a separately manufactured task.
        var write = writer.WriteAsync(new string('x', 4096));
        native.Stdin = write;
        var original = await Assert.ThrowsAsync<IOException>(async () => { await write; });

        var report = await Owner(native).RetryAsync().WaitAsync(Deadline);

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.InputJoined);
        Assert.True(report.OutputHealthy); // No output readers were started.
        Assert.Equal(OwnedInputState.Failed, report.Stdin.State);
        Assert.Equal("IO", report.Stdin.ExceptionKind);
        Assert.Equal(original.HResult, report.Stdin.HResult);
        Assert.True(write.IsFaulted);
        Assert.Same(original, write.Exception!.GetBaseException());
        Assert.Equal(1, writer.DisposeCalls);
        Assert.Equal(new OwnedWriterReleaseReport(true, true, null, null),
            Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation).StdinWriter);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateText, report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedInputFlushCanCloseItsHandleWhileCleanupStillRetainsLaterResources()
    {
        using var file = new WriterFile(failFirstWrite: true);
        using var reader = new TrackingReader();
        using var process = new Process();
        using var binary = new FileStream(System.IO.Path.Combine(file.Root.FullName, "binary.bin"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var binaryHandle = binary.SafeFileHandle;
        var input = file.Root.CreateSubdirectory("input");
        var native = new NativeProcessCleanupResources
        {
            StdinWriter = file.Writer, Stdin = Task.CompletedTask,
            StdoutReader = reader, Stdout = Task.CompletedTask,
            Process = process, Binary = binary, Directory = input,
        };
        // This original operation leaves bytes buffered. StreamWriter.Dispose
        // encounters the real underlying write failure, then closes its stream
        // in finally even though Dispose itself does not return successfully.
        var write = file.Writer.WriteAsync("buffered input");
        native.Stdin = write;
        await write;
        var owner = Owner(native);

        var first = await owner.RetryAsync().WaitAsync(Deadline);

        Assert.Equal("RESOURCE_RELEASE_FAILED", first.Phase);
        Assert.Equal("IO", first.ExceptionKind);
        Assert.False(first.Complete);
        Assert.True(first.InputJoined);
        Assert.True(first.ReadersJoined);
        Assert.True(first.DirectoryRemoved);
        Assert.False(Directory.Exists(input.FullName));
        var partial = Assert.IsType<OwnedResourceReleaseReport>(first.ReleaseObservation);
        Assert.True(partial.StdinWriter.Present);
        Assert.False(partial.StdinWriter.DisposeReturned);
        Assert.True(partial.StdinWriter.SafeHandleClosed);
        Assert.True(file.Handle.IsClosed);
        Assert.False(partial.StdoutReader.DisposeReturned);
        Assert.False(partial.ProcessDisposeReturned);
        Assert.False(partial.BinaryDisposeReturned);
        Assert.False(binaryHandle.IsClosed);
        Assert.Equal(1, file.Writer.DisposeCalls);
        Assert.Equal(0, reader.DisposeCalls);
        var originalJson = JsonSerializer.Serialize(first);

        var retry = await owner.RetryAsync().WaitAsync(Deadline);

        Assert.True(retry.Complete, retry.Summary);
        Assert.Equal(2, file.Writer.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);
        var released = Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation);
        Assert.True(released.StdinWriter.DisposeReturned);
        Assert.True(released.StdinWriter.SafeHandleClosed);
        Assert.True(released.StdoutReader.DisposeReturned);
        Assert.True(released.ProcessDisposeReturned);
        Assert.True(released.BinaryDisposeReturned);
        Assert.True(binaryHandle.IsClosed);
        Assert.Equal(originalJson, JsonSerializer.Serialize(first));
    }

    [Fact]
    public async Task NormalEofCloseIsNotRepeatedWhenALaterResourceNeedsCleanupRetry()
    {
        using var writer = new TrackingWriter(new MemoryStream());
        using var reader = new TrackingReader(failFirstDispose: true);
        var native = new NativeProcessCleanupResources
        {
            StdinWriter = writer, StdoutReader = reader, Stdout = Task.CompletedTask,
        };
        var write = writer.WriteAsync("finite input");
        native.Stdin = write;
        await write;
        native.CloseStdin();
        Assert.Equal(1, writer.DisposeCalls);
        Assert.True(native.ReleaseObservation.StdinWriter.DisposeReturned);
        var owner = Owner(native);

        var first = await owner.RetryAsync().WaitAsync(Deadline);

        Assert.Equal("RESOURCE_RELEASE_FAILED", first.Phase);
        Assert.False(first.Complete);
        Assert.Equal(1, writer.DisposeCalls);
        Assert.Equal(1, reader.DisposeCalls);
        var originalJson = JsonSerializer.Serialize(first);
        var retry = await owner.RetryAsync().WaitAsync(Deadline);
        Assert.True(retry.Complete, retry.Summary);
        Assert.Equal(1, writer.DisposeCalls);
        Assert.Equal(2, reader.DisposeCalls);
        Assert.Equal(new OwnedWriterReleaseReport(true, true, null, null),
            Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation).StdinWriter);
        Assert.Equal(originalJson, JsonSerializer.Serialize(first));
    }

    [Fact]
    public async Task PartialCaptureOwnsWriterEvenWhenBaseStreamGetterThrowsAndAbsentInputStaysAbsent()
    {
        using var writer = new ThrowingBaseStreamWriter(new MemoryStream());
        var native = new NativeProcessCleanupResources();
        Assert.Throws<IOException>(() => native.StdinWriter = writer);
        Assert.Same(writer, native.StdinWriter);

        var report = await Owner(native).RetryAsync().WaitAsync(Deadline);

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.InputJoined);
        Assert.Equal(OwnedInputState.NotStarted, report.Stdin.State);
        Assert.Equal(1, writer.DisposeCalls);
        Assert.Equal(new OwnedWriterReleaseReport(true, true, null, null),
            Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation).StdinWriter);
        var absent = await Owner(new NativeProcessCleanupResources()).RetryAsync().WaitAsync(Deadline);
        Assert.True(absent.Complete, absent.Summary);
        Assert.Equal(new OwnedInputReport(OwnedInputState.NotStarted, "NONE", 0), absent.Stdin);
        Assert.Equal(new OwnedWriterReleaseReport(false, false, null, null),
            Assert.IsType<OwnedResourceReleaseReport>(absent.ReleaseObservation).StdinWriter);
    }

    [Fact]
    public async Task OriginalCanceledWriteStaysCanceledAfterItsWriterIsReleased()
    {
        using var writer = new TrackingWriter(new MemoryStream());
        var native = new NativeProcessCleanupResources { StdinWriter = writer };
        var cancellation = new CancellationToken(canceled: true);
        var write = writer.WriteAsync("canceled input".AsMemory(), cancellation);
        native.Stdin = write;
        var original = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await write; });

        var report = await Owner(native).RetryAsync().WaitAsync(Deadline);

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.InputJoined);
        Assert.True(write.IsCanceled);
        Assert.Equal(cancellation, original.CancellationToken);
        Assert.Equal(new OwnedInputReport(OwnedInputState.Canceled, "CANCELED", 0), report.Stdin);
        Assert.Equal(1, writer.DisposeCalls);
        Assert.True(Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation).StdinWriter.DisposeReturned);
    }

    private static OwnedProcessCleanup Owner(NativeProcessCleanupResources resources) =>
        new(resources, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private static async Task WriteAfterGateAsync(StreamWriter writer, Task gate)
    {
        await gate;
        await writer.WriteAsync("finite request");
    }

    private class TrackingWriter(Stream stream)
        : StreamWriter(stream, new UTF8Encoding(false, true), bufferSize: 1024)
    {
        internal int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingBaseStreamWriter(Stream stream) : TrackingWriter(stream)
    {
        public override Stream BaseStream => throw new IOException(PrivateText);
    }

    private sealed class TrackingReader(bool failFirstDispose = false) : StreamReader(new MemoryStream())
    {
        internal int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                if (failFirstDispose && DisposeCalls == 1) throw new IOException(PrivateText);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FaultOnceWriteStream : MemoryStream
    {
        private bool _failed;
        private void FailFirstWrite()
        {
            if (_failed) return;
            _failed = true;
            throw new IOException(PrivateText);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            FailFirstWrite();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            FailFirstWrite();
            base.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { FailFirstWrite(); }
            catch (IOException error) { return ValueTask.FromException(error); }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FaultOnceWriteFileStream(string path)
        : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
    {
        private bool _failed;
        private void FailFirstWrite()
        {
            if (_failed) return;
            _failed = true;
            throw new IOException(PrivateText);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            FailFirstWrite();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            FailFirstWrite();
            base.Write(buffer);
        }
    }

    private sealed class WriterFile : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("autovpn-input-release-");
        internal string Path { get; }
        internal TrackingWriter Writer { get; }
        internal SafeFileHandle Handle { get; }

        internal WriterFile(bool failFirstWrite = false)
        {
            FileStream? stream = null;
            try
            {
                Path = System.IO.Path.Combine(Root.FullName, "stdin.bin");
                stream = failFirstWrite ? new FaultOnceWriteFileStream(Path)
                    : new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
                Handle = stream.SafeFileHandle;
                Writer = new TrackingWriter(stream);
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
            Writer.Dispose();
            Root.Delete(true);
        }
    }
}
