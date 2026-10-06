using System.Text.Json;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

// These gates exercise cleanup ownership, not Windows process/handle behavior.
public sealed class OwnedProcessCleanupTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GateBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private const string PrivateText = "SYNTHETIC_PRIVATE_CONFIG_AND_BINARY_PATH";

    [Fact]
    public async Task PendingOutputRetainsResourcesAndLateEofRequiresAnExplicitRetry()
    {
        var output = Gate();
        var resources = new Resources(output.Task, Task.CompletedTask);
        var owner = Owner(resources);

        var first = await owner.RetryAsync().WaitAsync(TestDeadline);

        Assert.Equal("OUTPUT_JOIN_FAILED", first.Phase);
        Assert.Equal("TIMEOUT", first.ExceptionKind);
        Assert.True(first.ProcessExitConfirmed);
        Assert.False(first.ReadersJoined);
        Assert.Equal(OwnedOutputState.Pending, first.Stdout.State);
        Assert.Equal(OwnedOutputState.Eof, first.Stderr.State);
        AssertRetained(resources, first);

        output.SetResult();
        await output.Task.WaitAsync(TestDeadline);
        Assert.Same(first, owner.LastReport);
        AssertRetained(resources, first);

        var retry = owner.RetryAsync();
        var completed = await retry.WaitAsync(TestDeadline);
        AssertComplete(resources, completed, outputHealthy: true);
        Assert.False(first.Complete); // The first failed attempt remains immutable.
        Assert.Equal(OwnedOutputState.Pending, first.Stdout.State);
        Assert.Equal(1, resources.StopCalls);
        Assert.Same(retry, owner.RetryAsync());
        Assert.Equal(1, resources.DeleteCalls);
        Assert.Equal(1, resources.ReleaseCalls);
    }

    [Fact]
    public async Task PendingExitRetainsEverythingEvenWhenBothOutputsHaveSettled()
    {
        var exit = Gate();
        var resources = new Resources(Task.CompletedTask, Task.CompletedTask, exit.Task);
        var owner = Owner(resources);

        var first = await owner.RetryAsync().WaitAsync(TestDeadline);

        Assert.Equal("PROCESS_STOP_FAILED", first.Phase);
        Assert.Equal("TIMEOUT", first.ExceptionKind);
        Assert.False(first.ProcessExitConfirmed);
        Assert.False(first.ReadersJoined);
        Assert.Equal(OwnedOutputState.Eof, first.Stdout.State);
        Assert.Equal(OwnedOutputState.Eof, first.Stderr.State);
        AssertRetained(resources, first);

        exit.SetResult();
        await exit.Task.WaitAsync(TestDeadline);
        Assert.Same(first, owner.LastReport);
        AssertRetained(resources, first);

        var completed = await owner.RetryAsync().WaitAsync(TestDeadline);
        AssertComplete(resources, completed, outputHealthy: true);
        Assert.Equal(2, resources.StopCalls);
        Assert.False(first.ProcessExitConfirmed);
    }

    [Theory]
    [InlineData("io", OwnedOutputState.Failed, "IO")]
    [InlineData("canceled", OwnedOutputState.Canceled, "CANCELED")]
    [InlineData("timeout", OwnedOutputState.Failed, "TIMEOUT")]
    public async Task TerminalOutputFailurePermitsReleaseButNeverBecomesHealthyEof(
        string failure, OwnedOutputState expectedState, string expectedKind)
    {
        Exception? error = failure switch
        {
            "io" => new IOException(PrivateText),
            "timeout" => new TimeoutException(PrivateText),
            _ => null,
        };
        var output = error is null
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.FromException(error);
        var resources = new Resources(output, Task.CompletedTask);
        var owner = Owner(resources);

        var attempt = owner.RetryAsync();
        var report = await attempt.WaitAsync(TestDeadline);

        AssertComplete(resources, report, outputHealthy: false);
        Assert.Equal("COMPLETED", report.Phase);
        Assert.Equal("NONE", report.ExceptionKind);
        Assert.Equal(0, report.HResult);
        Assert.Equal(expectedState, report.Stdout.State);
        Assert.Equal(expectedKind, report.Stdout.ExceptionKind);
        Assert.Equal(error?.HResult ?? 0, report.Stdout.HResult);
        Assert.Equal(OwnedOutputState.Eof, report.Stderr.State);
        Assert.DoesNotContain(PrivateText, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.Same(attempt, owner.RetryAsync());
        Assert.Same(report, owner.LastReport);
        Assert.False(owner.LastReport!.OutputHealthy);
        Assert.Equal(1, resources.ReleaseCalls);
    }

    [Fact]
    public async Task AReadFaultDoesNotPermitReleaseWhileItsSiblingIsStillPending()
    {
        var sibling = Gate();
        var resources = new Resources(Task.FromException(new IOException(PrivateText)), sibling.Task);
        var owner = Owner(resources);

        var first = await owner.RetryAsync().WaitAsync(TestDeadline);

        Assert.Equal("OUTPUT_JOIN_FAILED", first.Phase);
        Assert.Equal("TIMEOUT", first.ExceptionKind);
        Assert.Equal(OwnedOutputState.Failed, first.Stdout.State);
        Assert.Equal("IO", first.Stdout.ExceptionKind);
        Assert.Equal(OwnedOutputState.Pending, first.Stderr.State);
        Assert.False(first.OutputHealthy);
        AssertRetained(resources, first);

        sibling.SetResult();
        var completed = await owner.RetryAsync().WaitAsync(TestDeadline);
        AssertComplete(resources, completed, outputHealthy: false);
        Assert.Equal(OwnedOutputState.Failed, completed.Stdout.State);
        Assert.Equal("IO", completed.Stdout.ExceptionKind);
        Assert.Equal(OwnedOutputState.Eof, completed.Stderr.State);
        Assert.False(first.Complete);
    }

    [Theory]
    [InlineData(false, "IO")]
    [InlineData(true, "ACCESS")]
    public async Task FailedDirectoryRemovalRetainsOwnershipAndSanitizedMetadataForRetry(
        bool denied, string expectedKind)
    {
        Exception error = denied ? new UnauthorizedAccessException(PrivateText) : new IOException(PrivateText);
        var resources = new Resources(Task.CompletedTask, Task.CompletedTask);
        resources.DeleteFailures.Enqueue(error);
        var owner = Owner(resources);

        var first = await owner.RetryAsync().WaitAsync(TestDeadline);

        Assert.Equal("DIRECTORY_CLEANUP_FAILED", first.Phase);
        Assert.Equal(expectedKind, first.ExceptionKind);
        Assert.Equal(error.HResult, first.HResult);
        Assert.True(first.ProcessExitConfirmed);
        Assert.True(first.ReadersJoined);
        Assert.False(first.Complete);
        Assert.False(first.DirectoryRemoved);
        Assert.False(first.ResourcesReleased);
        Assert.True(resources.DirectoryPresent);
        Assert.True(resources.ResourcesHeld);
        Assert.Equal(1, resources.DeleteCalls);
        Assert.Equal(0, resources.ReleaseCalls);
        var obligation = new OwnedProcessCleanupException(owner, first);
        Assert.Same(owner, obligation.PendingCleanup);
        Assert.Same(first, obligation.Report);
        Assert.Null(obligation.InnerException);
        foreach (var metadata in new[] { JsonSerializer.Serialize(first), first.Summary,
            obligation.ToString(), owner.ToString() })
            Assert.DoesNotContain(PrivateText, metadata, StringComparison.Ordinal);

        var completed = await obligation.PendingCleanup.RetryAsync().WaitAsync(TestDeadline);
        AssertComplete(resources, completed, outputHealthy: true);
        Assert.Equal(1, resources.StopCalls);
        Assert.Equal(2, resources.DeleteCalls);
        Assert.Equal(1, resources.ReleaseCalls);
        Assert.False(first.Complete);
        Assert.Equal(expectedKind, first.ExceptionKind);
    }

    [Fact]
    public async Task ConcurrentWaitersShareOneAttemptAndCancelingOneWaitDoesNotCancelOwnership()
    {
        var exit = Gate();
        var resources = new Resources(Task.CompletedTask, Task.CompletedTask, exit.Task);
        var owner = new OwnedProcessCleanup(resources, GateBudget, GateBudget);
        using var cancellation = new CancellationTokenSource();

        var first = owner.RetryAsync();
        var second = owner.RetryAsync();
        var canceledWait = owner.RetryAsync(cancellation.Token);
        Assert.Same(first, second);
        Assert.Equal(1, resources.StopCalls);
        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledWait.WaitAsync(TestDeadline));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Null(owner.LastReport);
        Assert.True(resources.DirectoryPresent);
        Assert.True(resources.ResourcesHeld);
        Assert.Equal(0, resources.DeleteCalls);
        Assert.Equal(0, resources.ReleaseCalls);

        exit.SetResult();
        var completed = await first.WaitAsync(TestDeadline);
        Assert.Same(completed, await second.WaitAsync(TestDeadline));
        AssertComplete(resources, completed, outputHealthy: true);
        Assert.Equal(1, resources.StopCalls);
        Assert.True(canceledWait.IsCanceled);
    }

    [Fact]
    public async Task ReentrantStopCallbackJoinsTheAlreadyPublishedAttempt()
    {
        var exit = Gate();
        var resources = new Resources(Task.CompletedTask, Task.CompletedTask, exit.Task);
        var owner = new OwnedProcessCleanup(resources, GateBudget, GateBudget);
        Task<OwnedProcessCleanupReport>? rejoined = null;
        var completedInsideCallback = true;
        resources.OnStop = () =>
        {
            rejoined = owner.RetryAsync();
            completedInsideCallback = rejoined.IsCompleted;
        };

        var attempt = owner.RetryAsync();
        Assert.Same(attempt, rejoined);
        Assert.False(completedInsideCallback);
        Assert.Equal(1, resources.StopCalls);
        exit.SetResult();
        AssertComplete(resources, await attempt.WaitAsync(TestDeadline), outputHealthy: true);
        Assert.Equal(1, resources.StopCalls);
    }

    private static OwnedProcessCleanup Owner(Resources resources) =>
        new(resources, ShortBudget, ShortBudget);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertRetained(Resources resources, OwnedProcessCleanupReport report)
    {
        Assert.False(report.Complete);
        Assert.False(report.DirectoryRemoved);
        Assert.False(report.ResourcesReleased);
        Assert.True(resources.DirectoryPresent);
        Assert.True(resources.ResourcesHeld);
        Assert.Equal(0, resources.DeleteCalls);
        Assert.Equal(0, resources.ReleaseCalls);
    }

    private static void AssertComplete(Resources resources, OwnedProcessCleanupReport report, bool outputHealthy)
    {
        Assert.True(report.Complete);
        Assert.True(report.ProcessExitConfirmed);
        Assert.True(report.ReadersJoined);
        Assert.True(report.DirectoryRemoved);
        Assert.True(report.ResourcesReleased);
        Assert.Equal(outputHealthy, report.OutputHealthy);
        Assert.False(resources.DirectoryPresent);
        Assert.False(resources.ResourcesHeld);
        Assert.Equal(1, resources.ReleaseCalls);
    }

    private sealed class Resources(Task stdout, Task stderr, Task? exit = null) : IOwnedProcessCleanupResources
    {
        private readonly Task _exit = exit ?? Task.CompletedTask;
        public bool Started => true;
        public Task? Stdout { get; } = stdout;
        public Task? Stderr { get; } = stderr;
        internal int StopCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        internal bool DirectoryPresent { get; private set; } = true;
        internal bool ResourcesHeld { get; private set; } = true;
        internal Queue<Exception> DeleteFailures { get; } = new();
        internal Action? OnStop { get; set; }

        public Task StopAndWaitAsync()
        {
            StopCalls++;
            OnStop?.Invoke();
            return _exit;
        }

        public void DeleteDirectory()
        {
            DeleteCalls++;
            if (!_exit.IsCompletedSuccessfully || !Stdout!.IsCompleted || !Stderr!.IsCompleted)
                throw new InvalidOperationException("DELETE_BEFORE_PROCESS_AND_READERS_FINISHED");
            if (DeleteFailures.TryDequeue(out var failure)) throw failure;
            DirectoryPresent = false;
        }

        public void Release()
        {
            ReleaseCalls++;
            if (DirectoryPresent) throw new InvalidOperationException("RELEASE_BEFORE_DIRECTORY_REMOVAL");
            ResourcesHeld = false;
        }
    }
}
