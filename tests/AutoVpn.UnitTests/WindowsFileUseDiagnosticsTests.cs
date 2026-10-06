using System.Text.Json;
using AutoVpn.Infrastructure.Core;
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
        Assert.True(cleanup.OutputHealthy, cleanup.Summary);
        Assert.Equal(OwnedOutputState.Eof, cleanup.Stdout.State);
        Assert.Equal(OwnedOutputState.Eof, cleanup.Stderr.State);
        Assert.Null(report.RetainedCleanupFailure);
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
