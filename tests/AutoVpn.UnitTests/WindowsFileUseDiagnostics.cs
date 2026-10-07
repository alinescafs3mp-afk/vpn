using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.TestSupport;
using Microsoft.Win32.SafeHandles;

namespace AutoVpn.UnitTests;

// Immutable identity from a retained handle, never a later PID lookup or report field.
internal sealed class FileUseProcessIdentity(uint processId, ulong creationFileTime)
{
    private readonly uint _processId = processId;
    private readonly ulong _creationFileTime = creationFileTime;
    internal void Read(out uint processId, out ulong creationFileTime)
    { processId = _processId; creationFileTime = _creationFileTime; }
}

internal sealed record FileUseDiagnosticReport
{
    public string State { get; init; } = "DIAGNOSTIC_ERROR";
    public uint? StartCode { get; init; }
    public uint? RegisterCode { get; init; }
    public uint? QueryCode { get; init; }
    public uint? EndCode { get; init; }
    public int QueryCalls { get; init; }
    public int TotalOwners { get; init; }
    public int TestHostOwners { get; init; }
    public int OwnedChildOwners { get; init; }
    public int OtherOwners { get; init; }
    public bool Truncated { get; init; }
    public bool Incomplete { get; init; } = true;
    public OwnedProcessCleanupReport? Cleanup { get; init; }
    public string ControlMode { get; init; } = "Normal";
    public FileUseAttemptReport? Attempt { get; init; }
    public OwnedWriterReleaseReport? InputBeforeCleanup { get; init; }
    public FileUseProgressReport? HelperAfterCleanup { get; init; }
    [JsonIgnore] public OwnedProcessCleanupException? RetainedCleanupFailure { get; init; }
}

internal static class WindowsFileUseDiagnostics
{
    internal static Task<FileUseDiagnosticReport> CaptureAsync(string ownedBinaryPath, Process? ownedChild,
        FileUseProcessIdentity? knownChildIdentity = null) => CaptureCoreAsync(ownedBinaryPath, ownedChild, knownChildIdentity, FileUseControlMode.Normal);

    internal static FileUseProcessIdentity? CaptureIdentity(Process? process)
    {
        if (!OperatingSystem.IsWindows() || process is null) return null;
        try { return new((uint)process.Id, Creation(process)); }
        catch (Exception) { return null; }
    }

    internal static Task<FileUseDiagnosticReport> CaptureTimeoutControlAsync(string ownedBinaryPath) =>
        CaptureCoreAsync(ownedBinaryPath, null, null, FileUseControlMode.TimeoutAfterInput);

    internal static Task<FileUseDiagnosticReport> CaptureControlAsync(string ownedBinaryPath, FileUseControlMode mode) =>
        CaptureCoreAsync(ownedBinaryPath, null, null, mode);

    private static async Task<FileUseDiagnosticReport> CaptureCoreAsync(string ownedBinaryPath, Process? ownedChild,
        FileUseProcessIdentity? knownChildIdentity, FileUseControlMode mode)
    {
        if (!OperatingSystem.IsWindows()) return new() { State = "PLATFORM_REFUSED" };
        var resources = new NativeProcessCleanupResources();
        var report = new FileUseDiagnosticReport();
        Task<Output>? stdout = null; Task<Output>? stderr = null;
        Stopwatch? clock = null;
        FileUseProgressCapture? progress = null;
        FileUseParentProgress? parent = null;
        CancellationTokenSource? budget = null;
        Exception? failure = null;
        try
        {
            var original = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "process-fixture", "AutoVpn.ProcessFixture.exe"));
            var target = Path.GetFullPath(ownedBinaryPath);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(target), "AutoVpn.ProcessFixture.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, original, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                return new() { State = "INPUT_REFUSED" };
            using var host = Process.GetCurrentProcess();
            var hostTime = Creation(host);
            uint childPid = 0; ulong childTime = 0;
            (knownChildIdentity ?? CaptureIdentity(ownedChild))?.Read(out childPid, out childTime);
            var input = JsonSerializer.SerializeToUtf8Bytes(new { path = target, hostPid = (uint)host.Id,
                hostCreated = hostTime, childPid, childCreated = childTime });
            if (input.Length > 4096) return new() { State = "INPUT_REFUSED" };
            var start = new ProcessStartInfo(original)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(mode switch
            {
                FileUseControlMode.Normal => "windows-file-use",
                FileUseControlMode.TimeoutAfterInput => "windows-file-use-timeout",
                FileUseControlMode.TimeoutBeforeInput => "windows-file-use-input-timeout",
                FileUseControlMode.TimeoutBeforeQuery => "windows-file-use-query-timeout",
                FileUseControlMode.InputClosed => "windows-file-use-input-closed",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            });
            resources.Process = new Process { StartInfo = start };
            clock = Stopwatch.StartNew();
            progress = new(() => clock.ElapsedMilliseconds);
            parent = new(() => clock.ElapsedMilliseconds);
            budget = new(TimeSpan.FromSeconds(2));
            parent.Advance(FileUseParentPhase.PROCESS_START);
            resources.Started = resources.Process.Start();
            if (!resources.Started) throw new InvalidOperationException();
            CheckBudget(clock, budget.Token);
            parent.Advance(FileUseParentPhase.OUTPUT_CAPTURE);
            var stdoutReader = resources.Process.StandardOutput;
            resources.StdoutReader = stdoutReader;
            var stderrReader = resources.Process.StandardError;
            resources.StderrReader = stderrReader;
            resources.StdinWriter = resources.Process.StandardInput;
            resources.Stdout = stdout = DrainAsync(stdoutReader, progress);
            resources.Stderr = stderr = DrainAsync(stderrReader);
            if (mode == FileUseControlMode.InputClosed)
            {
                parent.Advance(FileUseParentPhase.INPUT_CONTROL_WAIT);
                await progress.InputClosed.WaitAsync(budget.Token).ConfigureAwait(false);
                CheckBudget(clock, budget.Token);
            }
            await FileUseInputTransfer.WriteAndCloseAsync(resources, input, parent.Advance, budget.Token).ConfigureAwait(false);
            CheckBudget(clock, budget.Token);
            parent.Advance(FileUseParentPhase.PROCESS_WAIT);
            await resources.Process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            CheckBudget(clock, budget.Token);
            parent.Advance(FileUseParentPhase.OUTPUT_JOIN);
            await Task.WhenAll(stdout, stderr).WaitAsync(budget.Token).ConfigureAwait(false);
            CheckBudget(clock, budget.Token);
            parent.Advance(FileUseParentPhase.RESULT_PARSE);
            if (resources.Process.ExitCode != 0 || stdout.Result.Truncated || stderr.Result.Total != 0)
                report = new() { State = "OUTPUT_INVALID", Truncated = stdout.Result.Truncated };
            else
            {
                var parsed = progress.RequireResult();
                report = new()
                {
                    State = parsed.State, StartCode = parsed.StartCode, RegisterCode = parsed.RegisterCode,
                    QueryCode = parsed.QueryCode, EndCode = parsed.EndCode, QueryCalls = parsed.QueryCalls,
                    TotalOwners = parsed.TotalOwners, TestHostOwners = parsed.TestHostOwners,
                    OwnedChildOwners = parsed.OwnedChildOwners, OtherOwners = parsed.OtherOwners,
                    Truncated = parsed.Truncated, Incomplete = parsed.Incomplete,
                };
            }
            CheckBudget(clock, budget.Token);
            parent.Advance(FileUseParentPhase.COMPLETED);
        }
        catch (OperationCanceledException error) { failure = error; report = new() { State = "QUERY_TIMEOUT" }; }
        catch (InvalidDataException error) { failure = error; report = new() { State = "OUTPUT_INVALID" }; }
        catch (Exception error) { failure = error; report = new() { State = "DIAGNOSTIC_ERROR" }; }
        finally { budget?.Dispose(); }

        // Freeze before cleanup. Later I/O observations never rewrite the original
        // state or the last phases actually observed before the two-second boundary.
        FileUseAttemptReport? attempt = null;
        if (clock is not null && parent is not null && progress is not null)
        {
            var parentBefore = parent.BeforeDeadline;
            var parentEnd = parent.Latest;
            var helperBefore = progress.BeforeDeadline;
            var helperEnd = progress.Latest;
            var elapsed = clock.ElapsedMilliseconds;
            var expired = elapsed >= FileUseProgressCapture.BudgetMilliseconds;
            attempt = new(expired ? parentBefore.Phase : parentEnd.Phase,
                parentEnd, OwnedProcessCleanup.Kind(failure), failure?.HResult ?? 0,
                elapsed, expired, helperBefore, helperEnd);
        }
        var inputBefore = resources.ReleaseObservation.StdinWriter;
        var cleanup = new OwnedProcessCleanup(resources, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var joined = await cleanup.RetryAsync().ConfigureAwait(false);
        return report with
        {
            Cleanup = joined, ControlMode = mode.ToString(), Attempt = attempt, InputBeforeCleanup = inputBefore,
            HelperAfterCleanup = progress?.Latest,
            RetainedCleanupFailure = joined.Complete ? null : new(cleanup, joined),
        };
    }

    // Timer callbacks may be delivered late. The observed monotonic boundary also
    // governs success; synchronous Process.Start/Dispose themselves are not preempted.
    private static void CheckBudget(Stopwatch clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.ElapsedMilliseconds >= FileUseProgressCapture.BudgetMilliseconds)
            throw new OperationCanceledException("FILE_USE_BUDGET_EXPIRED", cancellationToken);
    }

    // Fresh original reader only. Keep at most 4096 stdout bytes in the phase
    // collector; stderr is counted without retaining private text. Always drain EOF.
    [SupportedOSPlatform("windows")]
    private static async Task<Output> DrainAsync(StreamReader reader, FileUseProgressCapture? progress = null)
    {
        using var adapter = reader.BaseStream is FileStream { IsAsync: false } file
            ? AvailablePipeReadStream.ForOwnedHandle(file.SafeFileHandle) : null;
        Stream stream = adapter is null ? reader.BaseStream : adapter;
        var buffer = new byte[1024]; long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                progress?.Finish();
                return new(total, total > 4096);
            }
            total = total > long.MaxValue - count ? long.MaxValue : total + count;
            progress?.Append(buffer.AsSpan(0, count));
        }
    }

    [SupportedOSPlatform("windows")]
    private static ulong Creation(Process process)
    {
        if (!GetProcessTimes(process.SafeHandle, out var created, out _, out _, out _))
            throw new InvalidOperationException();
        var value = ((ulong)created.High << 32) | created.Low;
        if (value == 0) throw new InvalidOperationException();
        return value;
    }

    private sealed record Output(long Total, bool Truncated);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
}
