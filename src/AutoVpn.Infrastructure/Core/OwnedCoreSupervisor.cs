using System.Security.Cryptography;
using System.Text;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;

namespace AutoVpn.Infrastructure.Core;

/// <summary>A process backend. Stop must throw unless exit and owned-file cleanup are confirmed.</summary>
public interface IOwnedCoreProcess
{
    bool IsRunning { get; }
    Task<CoreStartResult> StartAsync(string yaml, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ICoreLiveness
{
    bool IsRunning(long generation, string operationId);
}

public sealed record CoreSupervisorSnapshot(long? Generation, string? OperationId, string Phase,
    bool ProcessRunning, bool CleanupPending);

/// <summary>
/// One owned runtime, no I/O under the state lock. Cancellation of a stop waiter
/// never drops the actual stop task, its process handle, or its cleanup obligation.
/// </summary>
public sealed class OwnedCoreSupervisor : ICoreController, ICoreLiveness, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<IOwnedCoreProcess> _factory;
    private Entry? _entry;
    private long _latestGeneration;
    private readonly HashSet<string> _retiredOwners = new(StringComparer.Ordinal);
    private bool _generationExhausted;
    private const int MaxRetiredOwners = 4096;
    private bool _closing;

    public OwnedCoreSupervisor(Func<IOwnedCoreProcess> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public CoreSupervisorSnapshot Snapshot()
    {
        lock (_gate)
        {
            var entry = _entry;
            if (entry is null) return new(null, null, _closing ? "Closed" : "Idle", false, false);
            var running = entry.Process.IsRunning;
            var phase = entry.Phase == "Ready" && !running ? "Exited" : entry.Phase;
            return new(entry.Generation, entry.OperationId, phase, running,
                entry.StopRequested || phase is "Failed" or "CleanupPending" or "Exited");
        }
    }

    public bool IsRunning(long generation, string operationId)
    {
        lock (_gate)
            return _entry is { } entry && entry.Generation == generation &&
                entry.OperationId == operationId && !entry.StopRequested && entry.Process.IsRunning;
    }

    public async Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (generation <= 0 || string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 ||
            operationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            return new(false, "CORE_OWNER_INVALID");
        if (Encoding.UTF8.GetByteCount(yaml) > 512 * 1024) return new(false, "CORE_PROFILE_TOO_LARGE");
        cancellationToken.ThrowIfCancellationRequested();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
        Task<CoreStartResult> work;
        lock (_gate)
        {
            if (_closing) return new(false, "CORE_CLOSING");
            if (generation < _latestGeneration) return new(false, ReasonCodes.Canceled);
            if (generation > _latestGeneration)
            {
                if (_entry is not null) return new(false, "CORE_BUSY");
                _latestGeneration = generation;
                _retiredOwners.Clear();
                _generationExhausted = false;
            }
            if (_generationExhausted) return new(false, "CORE_GENERATION_EXHAUSTED");
            if (_retiredOwners.Contains(operationId)) return new(false, ReasonCodes.Canceled);
            if (_entry is { } existing)
            {
                if (existing.Generation != generation || existing.OperationId != operationId)
                    return new(false, "CORE_BUSY");
                if (existing.ProfileDigest != digest) return new(false, "CORE_OWNER_REBOUND");
                if (existing.StopRequested) return new(false, ReasonCodes.Canceled);
                if (existing.StartTask.IsCompletedSuccessfully && existing.StartTask.Result.Started &&
                    !existing.Process.IsRunning) return new(false, "CORE_EXITED");
                work = existing.StartTask;
            }
            else
            {
                var entry = new Entry(generation, operationId, digest, _factory(),
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                _entry = entry;
                // Publish ownership and the task before the backend can start or call back.
                entry.StartTask = Task.Run(() => StartOwnedAsync(entry, yaml), CancellationToken.None);
                work = entry.StartTask;
            }
        }
        // A duplicate waiter's cancellation does not revoke another caller's operation.
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
    {
        Task? work = null;
        lock (_gate)
        {
            if (generation <= 0 || string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 ||
                operationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
                return Task.CompletedTask;
            if (_entry is { } other && (other.Generation != generation || other.OperationId != operationId))
                return Task.CompletedTask; // Never retire or alter another active owner.
            if (generation < _latestGeneration) return Task.CompletedTask;
            if (generation > _latestGeneration)
            {
                _latestGeneration = generation;
                _retiredOwners.Clear();
                _generationExhausted = false;
            }
            // Fence this exact operation even when its Stop arrives before its Start.
            // A distinct failover operation may legitimately reuse the same generation.
            if (_retiredOwners.Count < MaxRetiredOwners) _retiredOwners.Add(operationId);
            else _generationExhausted = true; // Retain all fences; require a new generation.
            if (_entry is { } entry)
            {
                entry.StopRequested = true;
                if (entry.StopTask is null || entry.StopTask.IsFaulted || entry.StopTask.IsCanceled)
                    entry.StopTask = Task.Run(() => StopOwnedAsync(entry), CancellationToken.None);
                work = entry.StopTask;
            }
        }
        return work is null ? Task.CompletedTask : work.WaitAsync(cancellationToken);
    }

    private async Task<CoreStartResult> StartOwnedAsync(Entry entry, string yaml)
    {
        CoreStartResult result;
        try
        {
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            result = await entry.Process.StartAsync(yaml, entry.Cancellation.Token).ConfigureAwait(false);
            if (entry.Cancellation.IsCancellationRequested) result = new(false, ReasonCodes.Canceled);
            else if (result.Started && !entry.Process.IsRunning) result = new(false, "CORE_EXITED_DURING_START");
        }
        catch (OperationCanceledException) { result = new(false, ReasonCodes.Canceled); }
        catch (Exception)
        {
            // Exception messages and process output can contain imported secrets.
            result = new(false, "CORE_START_FAILED");
        }
        lock (_gate)
        {
            if (ReferenceEquals(_entry, entry))
                entry.Phase = entry.StopRequested ? "Stopping" : result.Started ? "Ready" : "Failed";
        }
        return result;
    }

    private async Task StopOwnedAsync(Entry entry)
    {
        lock (_gate) entry.Phase = "Stopping";
        try
        {
            try { await entry.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { /* A throwing cancellation callback does not waive process cleanup. */ }
            // Never dispose a process while its own start is still using its handles.
            try { await entry.StartTask.ConfigureAwait(false); }
            catch (Exception) { /* Cleanup is still obligatory after an unexpected start fault. */ }
            await entry.Process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (entry.Process.IsRunning) throw new IOException("CORE_CLEANUP_UNCERTAIN");
            lock (_gate)
            {
                if (ReferenceEquals(_entry, entry)) _entry = null;
                entry.Phase = "Stopped";
            }
            entry.Cancellation.Dispose();
        }
        catch
        {
            lock (_gate) entry.Phase = "CleanupPending";
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry? entry;
        lock (_gate) { _closing = true; entry = _entry; }
        if (entry is not null)
            await StopAsync(entry.Generation, entry.OperationId, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class Entry(long generation, string operationId, string digest,
        IOwnedCoreProcess process, CancellationTokenSource cancellation)
    {
        public long Generation { get; } = generation;
        public string OperationId { get; } = operationId;
        public string ProfileDigest { get; } = digest;
        public IOwnedCoreProcess Process { get; } = process;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<CoreStartResult> StartTask { get; set; } = null!;
        public Task? StopTask { get; set; }
        public string Phase { get; set; } = "Starting";
        public bool StopRequested { get; set; }
    }
}
