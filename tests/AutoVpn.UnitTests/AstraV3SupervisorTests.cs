using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.UnitTests;

public sealed class AstraV3SupervisorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SameOwnerAndProfileShareOneProcessAndOwnedStop()
    {
        var process = new ProcessStub(); var factories = 0;
        await using var owner = new OwnedCoreSupervisor(() => { Interlocked.Increment(ref factories); return process; });
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => owner.StartAsync("profile", 1, "owner", default)));
        Assert.All(results, result => Assert.True(result.Started));
        Assert.Equal(1, factories); Assert.Equal(1, process.Starts);
        Assert.True(owner.IsRunning(1, "owner")); Assert.False(owner.IsRunning(1, "foreign"));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => owner.StopAsync(1, "owner", default)));
        Assert.Equal(1, process.Stops); Assert.Equal("Idle", owner.Snapshot().Phase);
    }

    [Fact]
    public async Task SameOwnerCannotRebindItsProfile()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        Assert.True((await owner.StartAsync("first", 1, "owner", default)).Started);
        Assert.Equal("CORE_OWNER_REBOUND", (await owner.StartAsync("other", 1, "owner", default)).ReasonCode);
        Assert.Equal(1, process.Starts);
    }

    [Fact]
    public async Task ForeignStopDoesNotRetireActiveOwner()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default);
        await owner.StopAsync(1, "foreign", default); await owner.StopAsync(2, "future", default);
        Assert.True(owner.IsRunning(1, "owner")); Assert.Equal(0, process.Stops);
        Assert.True((await owner.StartAsync("profile", 1, "owner", default)).Started);
    }

    [Fact]
    public async Task DifferentOwnerCannotStartUntilCleanupCompletes()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default);
        Assert.Equal("CORE_BUSY", (await owner.StartAsync("profile", 1, "next", default)).ReasonCode);
        Assert.Equal("CORE_BUSY", (await owner.StartAsync("profile", 2, "next", default)).ReasonCode);
        Assert.Equal(1, process.Starts);
    }

    [Fact]
    public async Task StopBeforeStartFencesOnlyThatOperation()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StopAsync(1, "canceled", default);
        Assert.Equal(ReasonCodes.Canceled, (await owner.StartAsync("profile", 1, "canceled", default)).ReasonCode);
        Assert.Equal(0, process.Starts);
        Assert.True((await owner.StartAsync("profile", 1, "replacement", default)).Started);
    }

    [Fact]
    public async Task CleanedOwnerCannotResurrectButSameGenerationReplacementCanStart()
    {
        var first = new ProcessStub(); var second = new ProcessStub(); var count = 0;
        await using var owner = new OwnedCoreSupervisor(() => ++count == 1 ? first : second);
        await owner.StartAsync("first", 7, "first", default); await owner.StopAsync(7, "first", default);
        Assert.Equal(ReasonCodes.Canceled, (await owner.StartAsync("first", 7, "first", default)).ReasonCode);
        Assert.True((await owner.StartAsync("second", 7, "second", default)).Started);
        await owner.StopAsync(7, "first", default);
        Assert.True(owner.IsRunning(7, "second")); Assert.Equal(0, second.Stops);
    }

    [Fact]
    public async Task NewGenerationRejectsAnOlderDelayedStart()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StopAsync(2, "already-canceled", default);
        Assert.Equal(ReasonCodes.Canceled, (await owner.StartAsync("profile", 1, "late", default)).ReasonCode);
        Assert.Equal(0, process.Starts);
    }

    [Fact]
    public async Task StopDuringStartCancelsAndJoinsTheOwnedStart()
    {
        var process = new ProcessStub { HoldStart = true };
        await using var owner = new OwnedCoreSupervisor(() => process);
        var start = owner.StartAsync("profile", 1, "owner", default);
        await process.StartEntered.Task.WaitAsync(Limit);
        await owner.StopAsync(1, "owner", default).WaitAsync(Limit);
        Assert.False((await start).Started); Assert.Equal(1, process.Stops);
        Assert.Equal("Idle", owner.Snapshot().Phase); Assert.False(process.IsRunning);
    }

    [Fact]
    public async Task CanceledDuplicateWaiterDoesNotCancelOriginalStart()
    {
        var process = new ProcessStub { HoldStart = true };
        await using var owner = new OwnedCoreSupervisor(() => process);
        var first = owner.StartAsync("profile", 1, "owner", default);
        await process.StartEntered.Task.WaitAsync(Limit);
        using var cancellation = new CancellationTokenSource();
        var second = owner.StartAsync("profile", 1, "owner", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        process.StartRelease.TrySetResult();
        Assert.True((await first.WaitAsync(Limit)).Started); Assert.Equal(1, process.Starts);
    }

    [Fact]
    public async Task CanceledStopWaiterDoesNotAbandonCleanup()
    {
        var process = new ProcessStub { HoldStop = true };
        await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default);
        using var cancellation = new CancellationTokenSource();
        var stop = owner.StopAsync(1, "owner", cancellation.Token);
        try
        {
            await process.StopEntered.Task.WaitAsync(Limit);
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.True(owner.Snapshot().CleanupPending);
            Assert.Equal("CORE_BUSY", (await owner.StartAsync("other", 2, "next", default)).ReasonCode);
        }
        finally { process.StopRelease.TrySetResult(); }
        await owner.StopAsync(1, "owner", default).WaitAsync(Limit);
        Assert.Equal(1, process.Stops); Assert.Equal("Idle", owner.Snapshot().Phase);
    }

    [Fact]
    public async Task CleanupFailureRetainsOwnerAndRetryIsReal()
    {
        var process = new ProcessStub { StopFailures = 1 };
        await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default);
        await Assert.ThrowsAsync<IOException>(() => owner.StopAsync(1, "owner", default));
        Assert.Equal("CleanupPending", owner.Snapshot().Phase); Assert.True(process.IsRunning);
        Assert.Equal("CORE_BUSY", (await owner.StartAsync("other", 2, "next", default)).ReasonCode);
        await owner.StopAsync(1, "owner", default);
        Assert.Equal(2, process.Stops); Assert.False(process.IsRunning); Assert.Equal("Idle", owner.Snapshot().Phase);
    }

    [Fact]
    public async Task BackendFalseSuccessCannotPublishReadiness()
    {
        var process = new ProcessStub { BecomeRunning = false };
        await using var owner = new OwnedCoreSupervisor(() => process);
        var result = await owner.StartAsync("profile", 1, "owner", default);
        Assert.False(result.Started); Assert.Equal("CORE_EXITED_DURING_START", result.ReasonCode);
        Assert.True(owner.Snapshot().CleanupPending);
    }

    [Fact]
    public async Task DuplicateAfterCrashDoesNotReplayAStaleSuccess()
    {
        var process = new ProcessStub(); await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default); process.Running = false;
        var result = await owner.StartAsync("profile", 1, "owner", default);
        Assert.False(result.Started); Assert.Equal("CORE_EXITED", result.ReasonCode); Assert.Equal(1, process.Starts);
        Assert.Equal("Exited", owner.Snapshot().Phase); Assert.True(owner.Snapshot().CleanupPending);
    }

    [Fact]
    public async Task ExceptionTextDoesNotEscapeAndFailedStartStillOwnsCleanup()
    {
        var process = new ProcessStub { StartFailure = new IOException("synthetic-private-password") };
        await using var owner = new OwnedCoreSupervisor(() => process);
        var result = await owner.StartAsync("profile", 1, "owner", default);
        Assert.False(result.Started); Assert.Equal("CORE_START_FAILED", result.ReasonCode);
        Assert.DoesNotContain("synthetic-private-password", owner.Snapshot().ToString(), StringComparison.Ordinal);
        await owner.StopAsync(1, "owner", default); Assert.Equal(1, process.Stops);
    }

    [Theory]
    [InlineData(0, "owner")]
    [InlineData(-1, "owner")]
    [InlineData(1, "")]
    [InlineData(1, "../other")]
    public async Task InvalidOwnersNeverReachBackend(long generation, string operation)
    {
        var calls = 0; await using var owner = new OwnedCoreSupervisor(() => { calls++; return new ProcessStub(); });
        Assert.Equal("CORE_OWNER_INVALID", (await owner.StartAsync("profile", generation, operation, default)).ReasonCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OversizedProfileNeverReachesBackend()
    {
        var calls = 0; await using var owner = new OwnedCoreSupervisor(() => { calls++; return new ProcessStub(); });
        Assert.Equal("CORE_PROFILE_TOO_LARGE", (await owner.StartAsync(new string('x', 512 * 1024 + 1), 1, "owner", default)).ReasonCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisposeClosesAdmissionAndStopsExactlyItsProcess()
    {
        var process = new ProcessStub(); var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default); await owner.DisposeAsync();
        Assert.Equal(1, process.Stops); Assert.Equal("Closed", owner.Snapshot().Phase);
        Assert.Equal("CORE_CLOSING", (await owner.StartAsync("other", 2, "next", default)).ReasonCode);
        await owner.DisposeAsync(); Assert.Equal(1, process.Stops);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotWaiveCleanup()
    {
        var process = new ProcessStub { ThrowOnCancellation = true };
        await using var owner = new OwnedCoreSupervisor(() => process);
        await owner.StartAsync("profile", 1, "owner", default);
        await owner.StopAsync(1, "owner", default).WaitAsync(Limit);
        Assert.Equal(1, process.Stops); Assert.False(process.IsRunning); Assert.Equal("Idle", owner.Snapshot().Phase);
    }

    [Fact]
    public async Task BoundedTombstonesFailClosedUntilANewGeneration()
    {
        var factories = 0;
        await using var owner = new OwnedCoreSupervisor(() => { factories++; return new ProcessStub(); });
        for (var index = 0; index <= 4096; index++) await owner.StopAsync(1, "canceled-" + index, default);
        Assert.Equal("CORE_GENERATION_EXHAUSTED", (await owner.StartAsync("profile", 1, "next", default)).ReasonCode);
        Assert.Equal(0, factories);
        Assert.True((await owner.StartAsync("profile", 2, "new-generation", default)).Started);
        Assert.Equal(1, factories);
    }

    private sealed class ProcessStub : IOwnedCoreProcess
    {
        private CancellationTokenRegistration _registration;
        public bool ThrowOnCancellation { get; init; }
        public bool HoldStart { get; init; }
        public bool HoldStop { get; init; }
        public bool BecomeRunning { get; init; } = true;
        public Exception? StartFailure { get; init; }
        public int StopFailures;
        public int Starts;
        public int Stops;
        public volatile bool Running;
        public bool IsRunning => Running;
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CoreStartResult> StartAsync(string yaml, CancellationToken token)
        {
            Interlocked.Increment(ref Starts); StartEntered.TrySetResult();
            if (HoldStart) await StartRelease.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (StartFailure is not null) throw StartFailure;
            if (ThrowOnCancellation) _registration = token.Register(() => throw new InvalidOperationException("synthetic callback failure"));
            Running = BecomeRunning; return new(true, null);
        }
        public async Task StopAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Stops); StopEntered.TrySetResult();
            if (HoldStop) await StopRelease.Task.WaitAsync(token);
            if (Interlocked.Decrement(ref StopFailures) >= 0) throw new IOException("synthetic cleanup failure");
            Running = false; _registration.Dispose();
        }
    }
}
