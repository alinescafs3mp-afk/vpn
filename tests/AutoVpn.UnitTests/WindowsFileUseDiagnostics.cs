using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
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
    [JsonIgnore] public OwnedProcessCleanupException? RetainedCleanupFailure { get; init; }
}

internal static class WindowsFileUseDiagnostics
{
    internal static Task<FileUseDiagnosticReport> CaptureAsync(string ownedBinaryPath, Process? ownedChild,
        FileUseProcessIdentity? knownChildIdentity = null) => CaptureCoreAsync(ownedBinaryPath, ownedChild, knownChildIdentity, false);

    internal static FileUseProcessIdentity? CaptureIdentity(Process? process)
    {
        if (!OperatingSystem.IsWindows() || process is null) return null;
        try { return new((uint)process.Id, Creation(process)); }
        catch (Exception) { return null; }
    }

    internal static Task<FileUseDiagnosticReport> CaptureTimeoutControlAsync(string ownedBinaryPath) =>
        CaptureCoreAsync(ownedBinaryPath, null, null, true);

    private static async Task<FileUseDiagnosticReport> CaptureCoreAsync(string ownedBinaryPath, Process? ownedChild,
        FileUseProcessIdentity? knownChildIdentity, bool timeoutControl)
    {
        if (!OperatingSystem.IsWindows()) return new() { State = "PLATFORM_REFUSED" };
        var resources = new NativeProcessCleanupResources();
        var report = new FileUseDiagnosticReport();
        Task<Output>? stdout = null; Task<Output>? stderr = null;
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
            start.ArgumentList.Add(timeoutControl ? "windows-file-use-timeout" : "windows-file-use");
            resources.Process = new Process { StartInfo = start };
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            resources.Started = resources.Process.Start();
            if (!resources.Started) throw new InvalidOperationException();
            resources.Stdout = stdout = DrainAsync(resources.Process.StandardOutput);
            resources.Stderr = stderr = DrainAsync(resources.Process.StandardError);
            await resources.Process.StandardInput.BaseStream.WriteAsync(input, budget.Token).ConfigureAwait(false);
            resources.Process.StandardInput.Close();
            await resources.Process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(budget.Token).ConfigureAwait(false);
            if (resources.Process.ExitCode != 0 || stdout.Result.Truncated || stderr.Result.Total != 0)
                report = new() { State = "OUTPUT_INVALID", Truncated = stdout.Result.Truncated };
            else
            {
                var parsed = JsonSerializer.Deserialize<FileUseDiagnosticReport>(stdout.Result.Bytes) ?? throw new InvalidDataException();
                if (parsed.State is not ("OBSERVED" or "NO_HOLDER_OR_INCOMPLETE" or "QUERY_INCOMPLETE" or "HELPER_ERROR" or "TIMEOUT_CONTROL_FINISHED") ||
                    parsed.QueryCalls is < 0 or > 2 || parsed.TotalOwners is < 0 or > 32 ||
                    parsed.TestHostOwners < 0 || parsed.OwnedChildOwners < 0 || parsed.OtherOwners < 0 ||
                    (long)parsed.TestHostOwners + parsed.OwnedChildOwners + parsed.OtherOwners != parsed.TotalOwners)
                    throw new InvalidDataException();
                report = parsed;
            }
        }
        catch (OperationCanceledException) { report = new() { State = "QUERY_TIMEOUT" }; }
        catch (Exception) { report = new() { State = "DIAGNOSTIC_ERROR" }; }
        var cleanup = new OwnedProcessCleanup(resources, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var joined = await cleanup.RetryAsync().ConfigureAwait(false);
        return report with { Cleanup = joined, RetainedCleanupFailure = joined.Complete ? null : new(cleanup, joined) };
    }

    // Fresh original reader only. Retain at most 4096 bytes, but always drain real EOF.
    [SupportedOSPlatform("windows")]
    private static async Task<Output> DrainAsync(StreamReader reader)
    {
        using var adapter = reader.BaseStream is FileStream { IsAsync: false } file
            ? AvailablePipeReadStream.ForOwnedHandle(file.SafeFileHandle) : null;
        Stream stream = adapter is null ? reader.BaseStream : adapter;
        var buffer = new byte[1024]; using var retained = new MemoryStream(); long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return new(retained.ToArray(), total, total > 4096);
            total = total > long.MaxValue - count ? long.MaxValue : total + count;
            var keep = Math.Min(count, 4096 - (int)retained.Length);
            if (keep > 0) retained.Write(buffer, 0, keep);
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

    private sealed record Output(byte[] Bytes, long Total, bool Truncated);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
}
