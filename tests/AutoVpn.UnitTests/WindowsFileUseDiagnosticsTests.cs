using System.Text.Json;
using AutoVpn.Infrastructure.Core;
using AutoVpn.TestSupport;
using Xunit.Abstractions;

namespace AutoVpn.UnitTests;

public sealed class WindowsFileUseDiagnosticsTests(ITestOutputHelper trace)
{
    [WindowsHandleFact]
    public async Task KnownHeldFileIsClassifiedByExactTestHostIdentity()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            using var held = new FileStream(copy.BinaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureAsync(copy.BinaryPath, null);
            Trace("known-held-file", report);
            RequireJoined(report);
            Assert.Equal("OBSERVED", report.State);
            Assert.False(report.Incomplete);
            Assert.Equal(0U, report.StartCode);
            Assert.Equal(0U, report.RegisterCode);
            Assert.Equal(0U, report.QueryCode);
            Assert.Equal(0U, report.EndCode);
            Assert.Equal(2, report.QueryCalls);
            Assert.Equal(1, report.TestHostOwners);
            Assert.Equal(0, report.OwnedChildOwners);
            Assert.InRange(report.TotalOwners, 1, 32);
            var safe = JsonSerializer.Serialize(report);
            Assert.DoesNotContain(copy.BinaryPath, safe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AutoVpn.ProcessFixture", safe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProcessId", safe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CreationFileTime", safe, StringComparison.OrdinalIgnoreCase);
        });
    }

    [WindowsHandleFact]
    public async Task NoHolderSnapshotRemainsExplicitlyIncomplete()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureAsync(copy.BinaryPath, null);
            Trace("no-holder-or-incomplete", report);
            RequireJoined(report);
            Assert.Equal(0U, report.StartCode);
            Assert.Equal(0U, report.RegisterCode);
            Assert.Equal(0U, report.EndCode);
            Assert.InRange(report.QueryCalls, 1, 2);
            Assert.Equal(0, report.TestHostOwners);
            Assert.Equal(0, report.OwnedChildOwners);
            // An external owner may appear between copy creation and the RM snapshot.
            // An empty result must never assert that no holder existed at a past failure.
            if (report.TotalOwners == 0)
            {
                Assert.Contains(report.State, new[] { "NO_HOLDER_OR_INCOMPLETE", "QUERY_INCOMPLETE" });
                Assert.True(report.Incomplete);
            }
            else
            {
                Assert.Equal(report.TotalOwners, report.OtherOwners);
                Assert.InRange(report.TotalOwners, 1, 32);
                Assert.Contains(report.State, new[] { "OBSERVED", "NO_HOLDER_OR_INCOMPLETE" });
            }
        });
    }

    [WindowsHandleFact]
    public async Task TimedOutQueryKillsAndJoinsOnlyItsOwnedHelper()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureTimeoutControlAsync(copy.BinaryPath);
            Trace("query-timeout", report);
            Assert.Equal("QUERY_TIMEOUT", report.State);
            Assert.True(report.Incomplete);
            Assert.Equal(0, report.QueryCalls);
            RequireJoined(report);
        });
    }

    [WindowsHandleFact]
    public async Task TimeoutBeforeInputPreservesStartedPhaseAndJoinsOwnedHelper()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureControlAsync(
                copy.BinaryPath, FileUseControlMode.TimeoutBeforeInput);
            Trace("timeout-before-input", report);
            Assert.Equal("QUERY_TIMEOUT", report.State);
            Assert.Equal("TimeoutBeforeInput", report.ControlMode);
            Assert.True(report.Incomplete);
            Assert.Equal(0, report.QueryCalls);
            Assert.Null(report.QueryCode);
            var attempt = Assert.IsType<FileUseAttemptReport>(report.Attempt);
            Assert.True(attempt.BudgetExpired);
            Assert.Equal("CANCELED", attempt.ExceptionKind);
            Assert.Equal("PREFIX", attempt.HelperBeforeDeadline.State);
            Assert.Equal("HELPER_STARTED", attempt.HelperBeforeDeadline.LastPhase);
            Assert.Null(attempt.HelperBeforeDeadline.QueryOrdinal);
            Assert.Equal(1, attempt.HelperBeforeDeadline.Frames);
            Assert.False(attempt.HelperBeforeDeadline.Eof);
            var after = Assert.IsType<FileUseProgressReport>(report.HelperAfterCleanup);
            Assert.Equal("HELPER_STARTED", after.LastPhase);
            Assert.Equal("PREFIX", after.State);
            Assert.True(after.Eof);
            RequireJoined(report);
        });
    }

    [WindowsHandleFact]
    public async Task TimeoutBeforeQueryPreservesQueryBoundaryAndJoinsOwnedHelper()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureControlAsync(
                copy.BinaryPath, FileUseControlMode.TimeoutBeforeQuery);
            Trace("timeout-before-query", report);
            Assert.Equal("QUERY_TIMEOUT", report.State);
            Assert.Equal("TimeoutBeforeQuery", report.ControlMode);
            Assert.True(report.Incomplete);
            // This deliberate stop is before the native RM call. A QUERY_BEGIN
            // observation never claims that the native call itself has stalled.
            Assert.Equal(0, report.QueryCalls);
            Assert.Null(report.QueryCode);
            var attempt = Assert.IsType<FileUseAttemptReport>(report.Attempt);
            Assert.True(attempt.BudgetExpired);
            Assert.Equal("CANCELED", attempt.ExceptionKind);
            Assert.Equal("PREFIX", attempt.HelperBeforeDeadline.State);
            Assert.Equal("QUERY_BEGIN", attempt.HelperBeforeDeadline.LastPhase);
            Assert.Equal(1, attempt.HelperBeforeDeadline.QueryOrdinal);
            Assert.False(attempt.HelperBeforeDeadline.Eof);
            var after = Assert.IsType<FileUseProgressReport>(report.HelperAfterCleanup);
            Assert.Equal("QUERY_BEGIN", after.LastPhase);
            Assert.Equal(1, after.QueryOrdinal);
            Assert.Equal("PREFIX", after.State);
            Assert.True(after.Eof);
            RequireJoined(report);
        });
    }

    [WindowsHandleFact]
    public async Task ClosedChildInputPreservesBrokenPipeAndRetainsCleanupUntilExplicitRetry()
    {
        var copy = new OwnedCopy();
        await OwnedFixtureExecution.RunAsync(copy, async () =>
        {
            var report = copy.Report = await WindowsFileUseDiagnostics.CaptureControlAsync(
                copy.BinaryPath, FileUseControlMode.InputClosed);
            Trace("input-closed", report);
            Assert.Equal("DIAGNOSTIC_ERROR", report.State);
            Assert.Equal("InputClosed", report.ControlMode);
            Assert.True(report.Incomplete);
            Assert.Equal(0, report.QueryCalls);
            var attempt = Assert.IsType<FileUseAttemptReport>(report.Attempt);
            Assert.Contains(attempt.ParentPhase, new[] { "INPUT_WRITE", "INPUT_FLUSH" });
            Assert.Equal("IO", attempt.ExceptionKind);
            Assert.Contains(attempt.HResult, new[] { unchecked((int)0x8007006D), unchecked((int)0x800700E8) });
            Assert.Equal("INPUT_CLOSED", attempt.HelperBeforeDeadline.LastPhase);
            var inputBefore = Assert.IsType<OwnedWriterReleaseReport>(report.InputBeforeCleanup);
            Assert.True(inputBefore.Present);
            Assert.False(inputBefore.DisposeReturned);
            var initial = Assert.IsType<OwnedProcessCleanupReport>(report.Cleanup);
            Assert.True(initial.ProcessExitConfirmed, initial.Summary);
            Assert.True(initial.InputJoined, initial.Summary);
            Assert.True(initial.ReadersJoined, initial.Summary);
            Assert.True(initial.OutputHealthy, initial.Summary);
            Assert.Equal(OwnedInputState.Failed, initial.Stdin.State);
            Assert.Equal("IO", initial.Stdin.ExceptionKind);
            Assert.Equal(attempt.HResult, initial.Stdin.HResult);
            Assert.Equal(OwnedOutputState.Eof, initial.Stdout.State);
            Assert.Equal(OwnedOutputState.Eof, initial.Stderr.State);
            RequireNativeExit(initial);
            var originalJson = JsonSerializer.Serialize(report);
            var settled = initial;
            if (!initial.Complete)
            {
                // FileStream can retry its buffered flush during Dispose and
                // still close its handle in finally. Preserve that first failure.
                Assert.Equal("RESOURCE_RELEASE_FAILED", initial.Phase);
                Assert.Equal("IO", initial.ExceptionKind);
                Assert.False(initial.ResourcesReleased);
                var partial = Assert.IsType<OwnedResourceReleaseReport>(initial.ReleaseObservation);
                Assert.False(partial.StdinWriter.DisposeReturned);
                Assert.True(partial.StdinWriter.SafeHandleClosed);
                Assert.False(partial.ProcessDisposeReturned);
                var retained = Assert.IsType<OwnedProcessCleanupException>(report.RetainedCleanupFailure);
                Assert.Same(initial, retained.Report);
                settled = await retained.PendingCleanup.RetryAsync();
                trace.WriteLine(JsonSerializer.Serialize(new
                {
                    evidence = "windows-file-use-input-retry-v1", scenario = "input-closed",
                    initial, retry = settled,
                }));
            }
            else Assert.Null(report.RetainedCleanupFailure);
            Assert.True(settled.Complete, settled.Summary);
            Assert.True(settled.InputJoined, settled.Summary);
            Assert.Equal(OwnedInputState.Failed, settled.Stdin.State);
            Assert.Equal(attempt.HResult, settled.Stdin.HResult);
            var released = Assert.IsType<OwnedResourceReleaseReport>(settled.ReleaseObservation).StdinWriter;
            Assert.True(released.Present);
            Assert.True(released.DisposeReturned);
            Assert.True(released.SafeHandleClosed);
            Assert.Equal(originalJson, JsonSerializer.Serialize(report));
            Assert.Same(report, copy.Report);
        });
    }

    [LinuxOnlyFact]
    public async Task NonWindowsPlatformRefusesWithoutStartingHelper()
    {
        var report = await WindowsFileUseDiagnostics.CaptureAsync("not-an-owned-binary", null);
        Trace("platform-refusal", report);
        Assert.Equal("PLATFORM_REFUSED", report.State);
        Assert.True(report.Incomplete);
        Assert.Null(report.StartCode);
        Assert.Equal(0, report.QueryCalls);
        Assert.Null(report.Cleanup);
        Assert.Null(report.RetainedCleanupFailure);
    }

    private void Trace(string scenario, FileUseDiagnosticReport report) =>
        trace.WriteLine(JsonSerializer.Serialize(new { evidence = "windows-file-use-control-v1", scenario, report }));

    private static void RequireJoined(FileUseDiagnosticReport report)
    {
        var cleanup = Assert.IsType<OwnedProcessCleanupReport>(report.Cleanup);
        Assert.True(cleanup.Complete, cleanup.Summary);
        Assert.True(cleanup.InputJoined, cleanup.Summary);
        Assert.Equal(OwnedInputState.Completed, cleanup.Stdin.State);
        Assert.True(cleanup.OutputHealthy, cleanup.Summary);
        Assert.Equal(OwnedOutputState.Eof, cleanup.Stdout.State);
        Assert.Equal(OwnedOutputState.Eof, cleanup.Stderr.State);
        var input = Assert.IsType<OwnedResourceReleaseReport>(cleanup.ReleaseObservation).StdinWriter;
        Assert.True(input.Present);
        Assert.True(input.DisposeReturned);
        Assert.True(input.SafeHandleClosed);
        RequireNativeExit(cleanup);
        Assert.Null(report.RetainedCleanupFailure);
    }

    private static void RequireNativeExit(OwnedProcessCleanupReport cleanup)
    {
        var exit = Assert.IsType<OwnedProcessExitReport>(cleanup.ExitObservation);
        Assert.True(exit.NativeWaitRequired);
        Assert.True(exit.NativeSignalConfirmed);
    }

    private sealed class OwnedCopy : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "autovpn-file-use-" + Guid.NewGuid().ToString("N")));
        internal string BinaryPath { get; }
        internal FileUseDiagnosticReport? Report { get; set; }
        internal OwnedCopy()
        {
            BinaryPath = Path.Combine(_directory.FullName, "AutoVpn.ProcessFixture.exe");
            try { File.Copy(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.exe"), BinaryPath); }
            catch (Exception original)
            {
                try { _directory.Delete(true); }
                catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Report?.RetainedCleanupFailure is { } retained)
            {
                OwnedProcessCleanupReport retried;
                try { retried = await retained.PendingCleanup.RetryAsync(); }
                catch (Exception retry) { throw new AggregateException(retained, retry); }
                if (!retried.Complete) throw new OwnedProcessCleanupException(retained.PendingCleanup, retried);
            }
            _directory.Delete(true);
        }
    }
}
