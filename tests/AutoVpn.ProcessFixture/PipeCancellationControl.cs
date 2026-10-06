using System.Diagnostics;
using System.Text.Json;
using AutoVpn.Infrastructure.Probe;

// Deliberate worker starvation belongs only in this finite, isolated child.
// Its main thread cancels and observes the read without scheduling a worker.
internal static class PipeCancellationControl
{
    internal static int Run(string mode)
    {
        var report = new Report { Mode = mode };
        var legacy = mode == "pool-cancel-delay-control";
        var native = mode == "pool-cancel-native";
        var streamOnly = mode == "pool-cancel-stream";
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        using var exited = new CountdownEvent(2);
        var active = 0;
        var queued = 0;
        Process? child = null;
        AvailablePipeReadStream? stream = null;
        StreamReader? reader = null;
        var source = new IdleReader();
        Task? canceledTask = null;
        var drains = new List<Task<ProbeOutputResult>>();
        var reads = new List<Task>();
        try
        {
            Require(!native || OperatingSystem.IsWindows(), "WINDOWS_REQUIRED");
            Require(!Thread.CurrentThread.IsThreadPoolThread, "MAIN_THREAD_REQUIRED");
            if (native) child = StartQuietChild();
            Require(ThreadPool.SetMinThreads(2, 2) && ThreadPool.SetMaxThreads(2, 2), "POOL_LIMIT_FAILED");
            ThreadPool.GetMaxThreads(out var maximum, out _);
            report.PoolLimited = maximum == 2;
            Require(report.PoolLimited, "POOL_LIMIT_NOT_OBSERVED");
            for (var index = 0; index < 2; index++)
            {
                Require(ThreadPool.QueueUserWorkItem(_ =>
                {
                    Interlocked.Increment(ref active);
                    entered.Signal();
                    try { release.Wait(); }
                    finally { Interlocked.Decrement(ref active); exited.Signal(); }
                }), "BLOCKER_QUEUE_FAILED");
                queued++;
            }
            Require(entered.Wait(TimeSpan.FromSeconds(3)), "WORKERS_NOT_OCCUPIED");
            ThreadPool.GetAvailableThreads(out var available, out _);
            report.WorkersOccupiedBeforeRead = Volatile.Read(ref active) == 2 && available == 0 &&
                ThreadPool.ThreadCount == 2;
            Require(report.WorkersOccupiedBeforeRead, "WORKER_CAPACITY_REMAINED");

            // Both workers are already blocked: the 10 ms poll timer cannot win
            // and queue an ordinary continuation before main-thread cancellation.
            if (legacy)
            {
                canceledTask = LegacyDelayIdleReadAsync(stop.Token);
                reads.Add(canceledTask);
            }
            else if (native)
            {
                Require(child!.StandardOutput.BaseStream is FileStream { IsAsync: false } &&
                    child.StandardError.BaseStream is FileStream { IsAsync: false }, "OWNED_SYNC_PIPES_REQUIRED");
                drains.Add(ProbeOutputDrain.ReadAsync(child.StandardOutput, stop.Token));
                drains.Add(ProbeOutputDrain.ReadAsync(child.StandardError, stop.Token));
                reads.AddRange(drains);
            }
            else
            {
                stream = new AvailablePipeReadStream(source);
                if (streamOnly)
                {
                    canceledTask = stream.ReadAsync(new byte[16], stop.Token).AsTask();
                    reads.Add(canceledTask);
                }
                else
                {
                    reader = new StreamReader(stream);
                    drains.Add(ProbeOutputDrain.ReadAsync(reader, stop.Token));
                    reads.AddRange(drains);
                }
                Require(source.Calls == 1, "IDLE_READER_NOT_POLLED_ONCE");
            }
            report.ReadCount = reads.Count;
            report.PendingBeforeCancel = reads.All(read => !read.IsCompleted);
            Require(report.PendingBeforeCancel, "IDLE_READ_COMPLETED_EARLY");

            var clock = Stopwatch.StartNew();
            stop.Cancel();
            report.CompletedBeforeWorkerRelease = WaitForCompletion(reads, clock, TimeSpan.FromSeconds(1));
            report.CancellationMilliseconds = clock.ElapsedMilliseconds;
            ThreadPool.GetAvailableThreads(out available, out _);
            report.WorkersBlockedAtObservation = Volatile.Read(ref active) == 2 && available == 0 &&
                ThreadPool.ThreadCount == 2 && exited.CurrentCount == 2 && !release.IsSet;
            report.CanceledBeforeWorkerRelease = AreCanceled(canceledTask, drains, stop.Token);
            if (native)
                report.ChildAliveBeforeWorkerRelease = !child!.HasExited &&
                    child.StandardOutput.BaseStream.CanRead && child.StandardError.BaseStream.CanRead;
            Require(report.WorkersBlockedAtObservation, "WORKERS_RELEASED_BEFORE_OBSERVATION");
            if (legacy)
            {
                Require(!report.CompletedBeforeWorkerRelease && !report.CanceledBeforeWorkerRelease,
                    "DELAY_CONTROL_DID_NOT_REPRODUCE");
            }
            else
            {
                Require(report.CompletedBeforeWorkerRelease, "CANCELLATION_DEADLINE");
                Require(report.CanceledBeforeWorkerRelease, "CANCELLATION_BECAME_EOF_OR_FAILURE");
                if (native) Require(report.ChildAliveBeforeWorkerRelease == true, "CHILD_EXITED_BEFORE_CANCEL");
                else
                {
                    // A completed canceled read must release the exclusive-reader
                    // fence, including when StreamReader and drain are above it.
                    source.Eof = true;
                    if (streamOnly)
                    {
                        var next = stream!.ReadAsync(new byte[1]).AsTask();
                        report.NextReadAllowed = next.IsCompletedSuccessfully && next.Result == 0;
                    }
                    else
                    {
                        var next = ProbeOutputDrain.ReadAsync(reader!, CancellationToken.None);
                        report.NextReadAllowed = next.IsCompletedSuccessfully &&
                            next.Result is { End: ProbeOutputEnd.Eof, Characters: 0, Error: ProbeOutputError.None };
                    }
                    Require(report.NextReadAllowed == true && source.Calls == 2, "CANCELED_READER_FENCE_RETAINED");
                }
            }
        }
        catch (ControlFailure failure) { report.Failure = failure.Code; }
        catch (Exception failure) { report.Failure = "CONTROL_EXCEPTION:" + failure.GetType().Name; }
        finally
        {
            // The failed old implementation must also be allowed to unwind. The
            // observation above is immutable and is never retried after release.
            release.Set();
            if (queued < 2) exited.Signal(2 - queued);
            report.WorkersJoined = exited.Wait(TimeSpan.FromSeconds(2));
            try { stop.Cancel(); }
            catch (Exception) { report.CleanupFailure ??= "CANCEL_FAILED"; }
            report.ReadsJoined = WaitForCompletion(reads, Stopwatch.StartNew(), TimeSpan.FromSeconds(2));
            report.CanceledAfterJoin = reads.Count > 0 && AreCanceled(canceledTask, drains, stop.Token);
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    report.ChildExited = child.WaitForExit(2000);
                }
                catch (Exception) { report.CleanupFailure ??= "OWNED_CHILD_STOP_FAILED"; }
            }
            reader?.Dispose();
            stream?.Dispose();
            child?.Dispose();
            if (!report.WorkersJoined || !report.ReadsJoined || !report.CanceledAfterJoin ||
                (native && report.ChildExited != true)) report.CleanupFailure ??= "CONTROL_CLEANUP_INCOMPLETE";
        }
        report.Passed = report.Failure is null && report.CleanupFailure is null;
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return report.Passed ? 0 : 1;
    }

    private static bool WaitForCompletion(IReadOnlyCollection<Task> reads, Stopwatch clock, TimeSpan limit)
    {
        while (reads.Any(read => !read.IsCompleted) && clock.Elapsed < limit) Thread.Sleep(1);
        return reads.All(read => read.IsCompleted) && clock.Elapsed <= limit;
    }

    private static bool AreCanceled(Task? canceled, IReadOnlyCollection<Task<ProbeOutputResult>> drains,
        CancellationToken token)
    {
        if (canceled is not null)
        {
            if (!canceled.IsCanceled) return false;
            try { canceled.GetAwaiter().GetResult(); }
            catch (OperationCanceledException failure) { return failure.CancellationToken == token; }
            return false;
        }
        return drains.Count > 0 && drains.All(read => read.IsCompletedSuccessfully &&
            read.Result is { End: ProbeOutputEnd.Canceled, Characters: 0, Error: ProbeOutputError.None, HResult: 0 });
    }

    // Frozen control of the previous idle-wait mechanism. Task.Delay cancellation
    // queues its continuation; the production adapter is never swapped or patched.
    private static async Task LegacyDelayIdleReadAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private static Process StartQuietChild()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(PipeCancellationControl).Assembly.Location);
        start.ArgumentList.Add("sleep");
        return Process.Start(start) ?? throw new ControlFailure("OWNED_CHILD_START_FAILED");
    }

    private static void Require(bool condition, string code)
    { if (!condition) throw new ControlFailure(code); }

    private sealed class ControlFailure(string code) : Exception
    { internal string Code { get; } = code; }

    private sealed class IdleReader : IAvailablePipeReader
    {
        internal int Calls;
        internal bool Eof;
        public int? ReadAvailable(byte[] buffer, int count) { Calls++; return Eof ? 0 : null; }
    }

    private sealed class Report
    {
        public string Mode { get; init; } = "";
        public bool Passed { get; set; }
        public bool PoolLimited { get; set; }
        public bool WorkersOccupiedBeforeRead { get; set; }
        public int ReadCount { get; set; }
        public bool PendingBeforeCancel { get; set; }
        public bool CompletedBeforeWorkerRelease { get; set; }
        public long CancellationMilliseconds { get; set; }
        public bool WorkersBlockedAtObservation { get; set; }
        public bool CanceledBeforeWorkerRelease { get; set; }
        public bool? NextReadAllowed { get; set; }
        public bool? ChildAliveBeforeWorkerRelease { get; set; }
        public bool WorkersJoined { get; set; }
        public bool ReadsJoined { get; set; }
        public bool CanceledAfterJoin { get; set; }
        public bool? ChildExited { get; set; }
        public string? Failure { get; set; }
        public string? CleanupFailure { get; set; }
    }
}
