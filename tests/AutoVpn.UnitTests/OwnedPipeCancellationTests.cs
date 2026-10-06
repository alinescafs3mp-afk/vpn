using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Probe;
using Xunit.Abstractions;

namespace AutoVpn.UnitTests;

public sealed class OwnedPipeCancellationTests(ITestOutputHelper trace)
{
    [Fact]
    public Task LegacyDelayCancellationNeedsAnAvailableWorker() => RunControlAsync("pool-cancel-delay-control");

    [Fact]
    public Task IdleStreamCancellationCompletesWhileAllWorkersRemainBlocked() => RunControlAsync("pool-cancel-stream");

    [Fact]
    public Task IdleDrainCancellationCompletesWhileAllWorkersRemainBlocked() => RunControlAsync("pool-cancel-drain");

    [WindowsHandleFact]
    public Task OwnedStdoutAndStderrCancelWhileAllWorkersRemainBlocked() => RunControlAsync("pool-cancel-native");

    private async Task RunControlAsync(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.dll"));
        start.ArgumentList.Add(mode);
        using var child = Process.Start(start)!;
        using var outputStop = new CancellationTokenSource();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var stdout = ProbeOutputDrain.ReadAsync(child.StandardOutput, outputStop.Token, output);
        var stderr = ProbeOutputDrain.ReadAsync(child.StandardError, outputStop.Token, error);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync(deadline.Token);
            var results = await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            var text = output.ToString();
            trace.WriteLine(text);
            if (error.Length > 0) trace.WriteLine(error.ToString());
            Assert.All(results, result =>
            {
                Assert.Equal(ProbeOutputEnd.Eof, result.End);
                Assert.Equal(ProbeOutputError.None, result.Error);
            });
            Assert.InRange(output.Length, 1, 2000);
            Assert.Equal(output.Length, results[0].Characters);
            Assert.Equal(0, results[1].Characters);
            Assert.True(child.ExitCode == 0, text + error);
            using var json = JsonDocument.Parse(text);
            var report = json.RootElement;
            Assert.Equal(mode, report.GetProperty("mode").GetString());
            foreach (var key in new[] { "passed", "poolLimited", "workersOccupiedBeforeRead", "pendingBeforeCancel",
                "workersBlockedAtObservation", "workersJoined", "readsJoined", "canceledAfterJoin" })
                Assert.True(report.GetProperty(key).GetBoolean(), key);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("failure").ValueKind);
            Assert.Equal(JsonValueKind.Null, report.GetProperty("cleanupFailure").ValueKind);
            var legacy = mode == "pool-cancel-delay-control";
            Assert.Equal(!legacy, report.GetProperty("completedBeforeWorkerRelease").GetBoolean());
            Assert.Equal(!legacy, report.GetProperty("canceledBeforeWorkerRelease").GetBoolean());
            if (!legacy) Assert.InRange(report.GetProperty("cancellationMilliseconds").GetInt64(), 0, 1000);
            var native = mode == "pool-cancel-native";
            Assert.Equal(native ? 2 : 1, report.GetProperty("readCount").GetInt32());
            if (native)
            {
                Assert.True(report.GetProperty("childAliveBeforeWorkerRelease").GetBoolean());
                Assert.True(report.GetProperty("childExited").GetBoolean());
            }
            else if (!legacy) Assert.True(report.GetProperty("nextReadAllowed").GetBoolean());
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            outputStop.Cancel();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
