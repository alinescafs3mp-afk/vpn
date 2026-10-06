using System.Runtime.CompilerServices;

namespace AutoVpn.UnitTests;

public sealed class OwnedFixtureExecutionTests
{
    [Fact]
    public async Task SuccessfulBodyAndCleanupAreAwaitedInOrderExactlyOnce()
    {
        var events = new List<string>();
        var fixture = new Fixture(async () =>
        {
            events.Add("cleanup-start");
            await Task.Yield();
            events.Add("cleanup-end");
        });

        await OwnedFixtureExecution.RunAsync(fixture, async () =>
        {
            events.Add("body-start");
            await Task.Yield();
            events.Add("body-end");
        });

        Assert.Equal(new[] { "body-start", "body-end", "cleanup-start", "cleanup-end" }, events);
        Assert.Equal(1, fixture.DisposeCalls);
    }

    [Fact]
    public async Task BodyFailureKeepsItsIdentityAndOriginAfterCleanupSucceeds()
    {
        var expected = new InvalidOperationException("SYNTHETIC_BODY_FAILURE");
        var fixture = new Fixture(() => Task.CompletedTask);

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OwnedFixtureExecution.RunAsync(fixture, () => ThrowAtBodyOriginAsync(expected)));

        Assert.Same(expected, observed);
        Assert.Contains(nameof(ThrowAtBodyOriginAsync), observed.StackTrace!, StringComparison.Ordinal);
        Assert.Equal(1, fixture.DisposeCalls);
    }

    [Fact]
    public async Task CleanupFailureKeepsItsIdentityAndOriginAfterBodySucceeds()
    {
        var expected = new UnauthorizedAccessException("SYNTHETIC_CLEANUP_FAILURE");
        var fixture = new Fixture(() => ThrowAtCleanupOriginAsync(expected));
        var bodyCalls = 0;

        var observed = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            OwnedFixtureExecution.RunAsync(fixture, () => { bodyCalls++; return Task.CompletedTask; }));

        Assert.Same(expected, observed);
        Assert.Contains(nameof(ThrowAtCleanupOriginAsync), observed.StackTrace!, StringComparison.Ordinal);
        Assert.Equal(1, bodyCalls);
        Assert.Equal(1, fixture.DisposeCalls);
    }

    [Fact]
    public async Task BothFailuresKeepOriginalObjectsStacksAndNestedAggregate()
    {
        var nested = new IOException("SYNTHETIC_RETAINED_FAILURE");
        var body = new AggregateException("SYNTHETIC_BODY_FAILURE", nested);
        var cleanup = new UnauthorizedAccessException("SYNTHETIC_CLEANUP_FAILURE");
        var fixture = new Fixture(() => ThrowAtCleanupOriginAsync(cleanup));

        var observed = await Assert.ThrowsAsync<AggregateException>(() =>
            OwnedFixtureExecution.RunAsync(fixture, () => ThrowAtBodyOriginAsync(body)));

        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Same(body, observed.InnerExceptions[0]);
        Assert.Same(cleanup, observed.InnerExceptions[1]);
        Assert.Same(nested, body.InnerException);
        Assert.Contains(nameof(ThrowAtBodyOriginAsync), body.StackTrace!, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowAtCleanupOriginAsync), cleanup.StackTrace!, StringComparison.Ordinal);
        Assert.Equal(1, fixture.DisposeCalls);
    }

    [Fact]
    public async Task BodyCancellationWaitsForCleanupAndRetainsItsExceptionAndToken()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var expected = new OperationCanceledException("SYNTHETIC_BODY_CANCELED", stop.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture(async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var run = OwnedFixtureExecution.RunAsync(fixture, () => ThrowAtBodyOriginAsync(expected));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            release.TrySetResult();
            var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => run);
            Assert.Same(expected, observed);
            Assert.Equal(stop.Token, observed.CancellationToken);
            Assert.Contains(nameof(ThrowAtBodyOriginAsync), observed.StackTrace!, StringComparison.Ordinal);
            Assert.True(run.IsCanceled);
            Assert.Equal(1, fixture.DisposeCalls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task SynchronousBodyDelegateFailureStillRunsCleanupExactlyOnce()
    {
        var expected = new IOException("SYNTHETIC_SYNCHRONOUS_BODY_FAILURE");
        var fixture = new Fixture(() => Task.CompletedTask);

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            OwnedFixtureExecution.RunAsync(fixture, () => ThrowAtSynchronousBodyOrigin(expected)));

        Assert.Same(expected, observed);
        Assert.Contains(nameof(ThrowAtSynchronousBodyOrigin), observed.StackTrace!, StringComparison.Ordinal);
        Assert.Equal(1, fixture.DisposeCalls);
    }

    private static async Task ThrowAtBodyOriginAsync(Exception error)
    {
        await Task.Yield();
        throw error;
    }

    private static async Task ThrowAtCleanupOriginAsync(Exception error)
    {
        await Task.Yield();
        throw error;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ThrowAtSynchronousBodyOrigin(Exception error) => throw error;

    private sealed class Fixture(Func<Task> cleanup) : IAsyncDisposable
    {
        internal int DisposeCalls { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return new ValueTask(cleanup());
        }
    }
}
