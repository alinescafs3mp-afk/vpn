using System.Diagnostics;
using System.Text.Json;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

// Real file handles, but no child process, network or Windows image-lifetime claim.
public sealed class OwnedResourceReleaseObservationTests
{
    [Fact]
    public async Task SuccessfulCleanupObservesTheSameFileHandleBeforeAndAfterDispose()
    {
        using var file = new OwnedFile();
        using var process = new Process(); // An allocated wrapper, deliberately never started.
        var captured = file.Stream.SafeFileHandle;
        var resources = new NativeProcessCleanupResources { Binary = file.Stream, Process = process };
        var before = resources.ReleaseObservation;

        Assert.True(before.ProcessPresent);
        Assert.True(before.BinaryPresent);
        Assert.False(before.ProcessDisposeReturned);
        Assert.False(before.BinaryDisposeReturned);
        Assert.False(captured.IsClosed);
        Assert.Equal(captured.IsClosed, before.BinarySafeHandleClosed);
        Assert.Equal(captured.IsInvalid, before.BinarySafeHandleInvalid);

        var report = await Owner(resources).RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.OutputHealthy);
        var after = Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation);
        Assert.True(after.ProcessPresent);
        Assert.True(after.BinaryPresent);
        Assert.True(after.ProcessDisposeReturned);
        Assert.True(after.BinaryDisposeReturned);
        Assert.True(captured.IsClosed);
        Assert.Equal(captured.IsClosed, after.BinarySafeHandleClosed);
        // IsInvalid describes sentinel values; closing need not make it true.
        Assert.Equal(captured.IsInvalid, after.BinarySafeHandleInvalid);
        Assert.False(before.BinarySafeHandleClosed);
        Assert.False(before.BinaryDisposeReturned);
        Assert.DoesNotContain(file.Root.FullName, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectoryFailureRetainsTheHandleAndRetryCannotRewriteTheOriginalObservation()
    {
        using var file = new OwnedFile();
        var captured = file.Stream.SafeFileHandle;
        var input = file.Root.CreateSubdirectory("input");
        var native = new NativeProcessCleanupResources { Binary = file.Stream, Directory = input };
        var resources = new FailFirstDelete(native);
        var owner = Owner(resources);

        var first = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(first.Complete);
        Assert.Equal("DIRECTORY_CLEANUP_FAILED", first.Phase);
        Assert.Equal("IO", first.ExceptionKind);
        Assert.False(first.DirectoryRemoved);
        Assert.False(first.ResourcesReleased);
        Assert.True(Directory.Exists(input.FullName));
        Assert.False(captured.IsClosed);
        var retained = Assert.IsType<OwnedResourceReleaseReport>(first.ReleaseObservation);
        Assert.True(retained.BinaryPresent);
        Assert.False(retained.BinaryDisposeReturned);
        Assert.False(retained.BinarySafeHandleClosed);
        Assert.Equal(captured.IsInvalid, retained.BinarySafeHandleInvalid);
        var originalJson = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("PRIVATE_DELETE_MARKER", originalJson, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Root.FullName, originalJson, StringComparison.Ordinal);

        var retry = await owner.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(retry.Complete, retry.Summary);
        Assert.False(Directory.Exists(input.FullName));
        Assert.True(captured.IsClosed);
        var released = Assert.IsType<OwnedResourceReleaseReport>(retry.ReleaseObservation);
        Assert.True(released.BinaryDisposeReturned);
        Assert.Equal(captured.IsClosed, released.BinarySafeHandleClosed);
        Assert.Equal(captured.IsInvalid, released.BinarySafeHandleInvalid);
        Assert.Equal(2, resources.DeleteCalls);
        Assert.Equal(1, resources.ReleaseCalls);
        Assert.Same(retry, owner.LastReport);
        Assert.Same(retained, first.ReleaseObservation);
        Assert.NotSame(retained, released);
        Assert.False(first.Complete);
        Assert.False(retained.BinarySafeHandleClosed);
        Assert.False(retained.BinaryDisposeReturned);
        Assert.Equal(originalJson, JsonSerializer.Serialize(first));
    }

    [Fact]
    public async Task AbsentResourcesDoNotFabricateDisposeCallsOrHandleState()
    {
        var resources = new NativeProcessCleanupResources();
        var expected = new OwnedResourceReleaseReport(false, false, false, false, null, null);
        Assert.Equal(expected, resources.ReleaseObservation);

        var report = await Owner(resources).RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(report.Complete, report.Summary);
        Assert.True(report.ResourcesReleased);
        Assert.True(report.OutputHealthy);
        Assert.Equal(OwnedOutputState.NotStarted, report.Stdout.State);
        Assert.Equal(OwnedOutputState.NotStarted, report.Stderr.State);
        Assert.Equal(expected, Assert.IsType<OwnedResourceReleaseReport>(report.ReleaseObservation));
        Assert.Equal(expected, resources.ReleaseObservation);
    }

    private static OwnedProcessCleanup Owner(IOwnedProcessCleanupResources resources) =>
        new(resources, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private sealed class FailFirstDelete(NativeProcessCleanupResources inner) : IOwnedProcessCleanupResources
    {
        public bool Started => inner.Started;
        public Task? Stdout => inner.Stdout;
        public Task? Stderr => inner.Stderr;
        public OwnedResourceReleaseReport? ReleaseObservation => inner.ReleaseObservation;
        internal int DeleteCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        public Task StopAndWaitAsync() => inner.StopAndWaitAsync();

        public void DeleteDirectory()
        {
            DeleteCalls++;
            if (DeleteCalls == 1) throw new IOException("PRIVATE_DELETE_MARKER:" + inner.Directory!.FullName);
            inner.DeleteDirectory();
        }

        public void Release()
        {
            ReleaseCalls++;
            inner.Release();
        }
    }

    private sealed class OwnedFile : IDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("autovpn-release-observation-");
        internal FileStream Stream { get; }

        internal OwnedFile()
        {
            try
            {
                var path = Path.Combine(Root.FullName, "synthetic-input.bin");
                File.WriteAllText(path, "finite synthetic input");
                Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch { Root.Delete(recursive: true); throw; }
        }

        public void Dispose()
        {
            // These controls never start a process or asynchronous reader.
            Stream.Dispose();
            Root.Delete(recursive: true);
        }
    }
}
