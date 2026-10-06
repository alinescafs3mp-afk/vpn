using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AutoVpn.Infrastructure.Core;

/// <summary>A capability for the same owned resources; no caller-selected PID or path.</summary>
public interface IOwnedProcessCleanup
{
    OwnedProcessCleanupReport? LastReport { get; }
    Task<OwnedProcessCleanupReport> RetryAsync(CancellationToken cancellationToken = default);
}

public enum OwnedOutputState { NotStarted, Pending, Eof, Canceled, Failed }

public sealed record OwnedOutputReport(OwnedOutputState State, string ExceptionKind, int HResult);

/// <summary>Immutable, bounded metadata. Never includes child output or native exception text.</summary>
public sealed record OwnedProcessCleanupReport(string Phase, bool ProcessExitConfirmed,
    bool ReadersJoined, bool DirectoryRemoved, bool ResourcesReleased, bool OutputHealthy,
    OwnedOutputReport Stdout, OwnedOutputReport Stderr, string ExceptionKind, int HResult,
    long ElapsedMilliseconds)
{
    public bool Complete => ProcessExitConfirmed && ReadersJoined && DirectoryRemoved && ResourcesReleased;
    public string Summary => $"{Phase};EXIT={ProcessExitConfirmed};JOINED={ReadersJoined};" +
        $"DIR={DirectoryRemoved};RELEASED={ResourcesReleased};OUTPUT_OK={OutputHealthy};" +
        $"OUT={Stdout.State}/{Stdout.ExceptionKind}/{Stdout.HResult:X8};" +
        $"ERR={Stderr.State}/{Stderr.ExceptionKind}/{Stderr.HResult:X8};" +
        $"ERROR={ExceptionKind}:{HResult:X8};MS={ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}";
}

public sealed class OwnedProcessCleanupException : IOException
{
    [JsonIgnore] public IOwnedProcessCleanup PendingCleanup { get; }
    public OwnedProcessCleanupReport Report { get; }
    internal OwnedProcessCleanupException(IOwnedProcessCleanup cleanup, OwnedProcessCleanupReport report)
        : base("OWNED_PROCESS_CLEANUP_REQUIRED:" + report.Summary)
    { PendingCleanup = cleanup; Report = report; }
}

internal interface IOwnedProcessCleanupResources
{
    bool Started { get; }
    Task? Stdout { get; }
    Task? Stderr { get; }
    Task StopAndWaitAsync();
    void DeleteDirectory();
    void Release();
}

/// <summary>One bounded attempt at a time, explicit retry only, no background reaper.</summary>
internal sealed class OwnedProcessCleanup : IOwnedProcessCleanup
{
    private readonly IOwnedProcessCleanupResources _resources;
    private readonly Task? _stdout;
    private readonly Task? _stderr;
    private readonly Task _settled;
    private readonly TimeSpan _outputTimeout;
    private readonly TimeSpan _exitTimeout;
    private readonly object _gate = new();
    private Task<OwnedProcessCleanupReport>? _attempt;
    private OwnedProcessCleanupReport? _report;
    private bool _exitConfirmed;
    private bool _readersJoined;
    private bool _directoryRemoved;
    private bool _resourcesReleased;

    internal OwnedProcessCleanup(IOwnedProcessCleanupResources resources, TimeSpan outputTimeout,
        TimeSpan? exitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _outputTimeout = Bounded(outputTimeout, nameof(outputTimeout));
        _exitTimeout = Bounded(exitTimeout ?? TimeSpan.FromSeconds(5), nameof(exitTimeout));
        _resources = resources;
        _stdout = resources.Stdout; _stderr = resources.Stderr;
        // Observe failures without mistaking them for EOF. The join itself only
        // measures settlement, so a reader's TimeoutException is not a wait timeout.
        _settled = Task.WhenAll(SettleAsync(_stdout), SettleAsync(_stderr));
    }

    public OwnedProcessCleanupReport? LastReport => Volatile.Read(ref _report);

    public Task<OwnedProcessCleanupReport> RetryAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<OwnedProcessCleanupReport>? owner = null;
        Task<OwnedProcessCleanupReport> attempt;
        lock (_gate)
        {
            if (_attempt is null || (_attempt.IsCompleted && LastReport?.Complete != true))
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _attempt = owner.Task;
            }
            attempt = _attempt;
        }
        // Publish before callbacks and start outside the lock without scheduling
        // a worker. Caller cancellation affects only its wait, never ownership.
        if (owner is not null) _ = CompleteAttemptAsync(owner);
        return cancellationToken.CanBeCanceled ? attempt.WaitAsync(cancellationToken) : attempt;
    }

    private async Task CompleteAttemptAsync(TaskCompletionSource<OwnedProcessCleanupReport> owner)
    {
        var clock = Stopwatch.StartNew();
        var phase = "PROCESS_STOP";
        try
        {
            if (!_exitConfirmed)
            {
                await _resources.StopAndWaitAsync().WaitAsync(_exitTimeout).ConfigureAwait(false);
                _exitConfirmed = true;
            }
            phase = "OUTPUT_JOIN";
            if (!_readersJoined)
            {
                await _settled.WaitAsync(_outputTimeout).ConfigureAwait(false);
                _readersJoined = true;
            }
            phase = "DIRECTORY_CLEANUP";
            if (!_directoryRemoved)
            {
                _resources.DeleteDirectory();
                _directoryRemoved = true;
            }
            phase = "RESOURCE_RELEASE";
            if (!_resourcesReleased)
            {
                _resources.Release();
                _resourcesReleased = true;
            }
            var report = Snapshot("COMPLETED", null, clock.ElapsedMilliseconds);
            Volatile.Write(ref _report, report);
            owner.TrySetResult(report);
        }
        catch (Exception error)
        {
            // Preserve the obligation and metadata, never the native message.
            var report = Snapshot(phase + "_FAILED", error, clock.ElapsedMilliseconds);
            Volatile.Write(ref _report, report);
            owner.TrySetResult(report);
        }
    }

    private OwnedProcessCleanupReport Snapshot(string phase, Exception? error, long milliseconds)
    {
        var stdout = Output(_stdout); var stderr = Output(_stderr);
        var healthy = _resources.Started
            ? stdout.State == OwnedOutputState.Eof && stderr.State == OwnedOutputState.Eof
            : stdout.State == OwnedOutputState.NotStarted && stderr.State == OwnedOutputState.NotStarted;
        return new(phase, _exitConfirmed, _readersJoined, _directoryRemoved, _resourcesReleased,
            healthy, stdout, stderr, Kind(error), error?.HResult ?? 0, milliseconds);
    }

    private static OwnedOutputReport Output(Task? task)
    {
        if (task is null) return new(OwnedOutputState.NotStarted, "NONE", 0);
        if (!task.IsCompleted) return new(OwnedOutputState.Pending, "NONE", 0);
        if (task.IsCanceled) return new(OwnedOutputState.Canceled, "CANCELED", 0);
        if (!task.IsFaulted) return new(OwnedOutputState.Eof, "NONE", 0);
        var error = task.Exception!.GetBaseException();
        return new(OwnedOutputState.Failed, Kind(error), error.HResult);
    }

    internal static string Kind(Exception? error) => error switch
    {
        null => "NONE", TimeoutException => "TIMEOUT", OperationCanceledException => "CANCELED",
        UnauthorizedAccessException => "ACCESS", Win32Exception => "WIN32", IOException => "IO",
        DecoderFallbackException => "DECODING", ObjectDisposedException => "DISPOSED",
        InvalidOperationException => "STATE", _ => "OTHER",
    };

    private static async Task SettleAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Original task remains faulted/canceled and is reported separately. */ }
    }

    private static TimeSpan Bounded(TimeSpan timeout, string parameter)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(parameter);
        return timeout;
    }

    public override string ToString() => "OWNED_PROCESS_CLEANUP";
}

// Used only by trusted code that creates the resources. Populate before handing
// the completed preparation to OwnedProcessCleanup; never mutate after handoff.
internal sealed class NativeProcessCleanupResources : IOwnedProcessCleanupResources
{
    internal Process? Process;
    internal FileStream? Binary;
    internal DirectoryInfo? Directory;
    public bool Started { get; internal set; }
    public Task? Stdout { get; internal set; }
    public Task? Stderr { get; internal set; }
    private Task? _exit;

    public Task StopAndWaitAsync()
    {
        if (!Started) return Task.CompletedTask;
        var process = Process ?? throw new InvalidOperationException("OWNED_PROCESS_MISSING");
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited)
        { /* Natural exit raced the kill; still join the exact retained process. */ }
        // Keep a pending native wait across attempts. A terminally failed wait
        // must not permanently poison an explicit retry of the same process.
        if (_exit is null || _exit.IsFaulted || _exit.IsCanceled)
        {
            if (_exit?.IsFaulted == true) _ = _exit.Exception;
            _exit = process.WaitForExitAsync(CancellationToken.None);
        }
        return _exit;
    }

    public void DeleteDirectory()
    {
        if (Directory is null) return;
        try
        {
            if ((File.GetAttributes(Directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("OWNED_DIRECTORY_REPLACED");
            Directory.Delete(recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }

    public void Release()
    {
        Process?.Dispose();
        Binary?.Dispose();
    }
}
