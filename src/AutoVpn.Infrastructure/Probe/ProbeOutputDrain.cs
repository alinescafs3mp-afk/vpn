using System.Diagnostics;
using System.Text;

namespace AutoVpn.Infrastructure.Probe;

public enum ProbeOutputEnd { NotStarted, Eof, Canceled, Failed }
public enum ProbeOutputError { None, Io, UnexpectedCancellation, Disposed, InvalidState, Decoding }

/// <summary>Counts decoded characters, not wire bytes. Contains no child output or exception message.</summary>
public sealed record ProbeOutputResult(int Characters, ProbeOutputEnd End,
    ProbeOutputError Error = ProbeOutputError.None, int HResult = 0);

public static class ProbeOutputDrain
{
    // Call only once for each fresh Process stream, before any reads on the original reader.
    public static async Task<ProbeOutputResult> ReadAsync(StreamReader reader, CancellationToken token,
        StringBuilder? retainedPrefix = null)
    {
        using var adapted = AdaptOwnedProcessReader(reader);
        return await ReadCoreAsync(adapted ?? reader, token, retainedPrefix).ConfigureAwait(false);
    }

    // The returned reader owns only its adapter; the original Process reader and
    // handle must remain alive until this reader has finished. Never mix reads.
    internal static StreamReader? AdaptOwnedProcessReader(StreamReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return OperatingSystem.IsWindows() && reader.BaseStream is FileStream { IsAsync: false } file
            ? new StreamReader(AvailablePipeReadStream.ForOwnedHandle(file.SafeFileHandle),
                reader.CurrentEncoding, true, 1024)
            : null;
    }

    internal static async Task<ProbeOutputResult> ReadCoreAsync(StreamReader reader, CancellationToken token,
        StringBuilder? retainedPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var buffer = new char[1024];
        var total = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) return new(total, ProbeOutputEnd.Eof);
                total = total > int.MaxValue - count ? int.MaxValue : total + count;
                if (retainedPrefix is not null)
                {
                    lock (retainedPrefix)
                    {
                        var room = 2000 - retainedPrefix.Length;
                        if (room > 0) retainedPrefix.Append(buffer, 0, Math.Min(count, room));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(total, ProbeOutputEnd.Canceled); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
            or InvalidOperationException or DecoderFallbackException)
        {
            var kind = ex switch
            {
                IOException => ProbeOutputError.Io,
                OperationCanceledException => ProbeOutputError.UnexpectedCancellation,
                ObjectDisposedException => ProbeOutputError.Disposed,
                DecoderFallbackException => ProbeOutputError.Decoding,
                _ => ProbeOutputError.InvalidState,
            };
            // An unexpected read failure is not EOF or a successful requested cancellation.
            return new(total, ProbeOutputEnd.Failed, kind, ex.HResult);
        }
    }
}

/// <summary>A bounded snapshot. Never includes paths, credentials, native output or exception text.</summary>
public sealed record ProbeCleanupReport(string Phase, bool ProcessExitConfirmed, bool ResourcesReleased,
    bool DirectoryRemoved, long ElapsedMilliseconds, string ExceptionKind, int HResult,
    TaskStatus StdoutTask, TaskStatus StderrTask, ProbeOutputResult? Stdout, ProbeOutputResult? Stderr,
    int ThreadPoolThreads, long PendingWorkItems)
{
    public string Summary => $"{Phase};EXIT={ProcessExitConfirmed};RELEASED={ResourcesReleased};DIR={DirectoryRemoved};" +
        $"MS={ElapsedMilliseconds};ERROR={ExceptionKind}:{HResult:X8};" +
        $"OUT={StdoutTask}/{Stdout?.End}/{Stdout?.Error}/{Stdout?.HResult:X8};" +
        $"ERR={StderrTask}/{Stderr?.End}/{Stderr?.Error}/{Stderr?.HResult:X8};" +
        $"THREADS={ThreadPoolThreads};PENDING={PendingWorkItems}";
}
