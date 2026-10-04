using System.Diagnostics;
using AutoVpn.Application;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Probe;

public sealed record MaintenanceSnapshot(string Phase = "WAITING", int Due = 0, int Attempted = 0,
    int Succeeded = 0, int Failed = 0, string? Reason = null);

/// <summary>
/// One unelevated owner per desktop catalogue. Source downloads do not own or reset this queue.
/// Durable assessment timestamps/backoff reconstruct the queue after restart; copied DTOs never
/// own an attempt. Pulses coalesce, foreground admission takes the next available turn, and the
/// two-target transport owns the process limit across every entry point.
/// </summary>
public sealed class CatalogueMaintenance
{
    private readonly ICatalogue _catalogue;
    private readonly TwoTargetProbeTransport _transport;
    private readonly ProbeByteBudget _budget;
    private readonly IClock _clock;
    private readonly string? _budgetPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, DateTimeOffset> _deferred = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cycle;
    private Task? _loop;
    private int _paused;
    private int _foreground;
    private TimeSpan _blockedUntil;
    private MaintenanceSnapshot _snapshot = new();
    public string TargetSetId => _transport.TargetSetId;
    public MaintenanceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public CatalogueMaintenance(ICatalogue catalogue, TwoTargetProbeTransport transport, ProbeByteBudget budget,
        IClock? clock = null, string? budgetPath = null)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _clock = clock ?? new SystemClock(); _budgetPath = budgetPath;
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_lifetime.IsCancellationRequested) throw new InvalidOperationException("Maintenance is stopped.");
            _loop ??= Task.Run(RunAsync);
        }
    }

    public void Pause()
    {
        Interlocked.Exchange(ref _paused, 1);
        lock (_lifecycleGate) _cycle?.Cancel();
        Volatile.Write(ref _snapshot, Snapshot with { Phase = "PAUSED" });
    }

    public void Resume()
    {
        if (!_lifetime.IsCancellationRequested) Interlocked.Exchange(ref _paused, 0);
    }

    public void RequestStop()
    {
        _lifetime.Cancel(); Pause();
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var taken = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); taken = true;
            await _transport.DrainAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { if (taken) _gate.Release(); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do { await PulseAsync(_lifetime.Token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task<MaintenanceSnapshot> PulseAsync(CancellationToken cancellationToken = default,
        int maxNodes = 8, TimeSpan? cycleBudget = null)
    {
        if (maxNodes is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        var limit = cycleBudget ?? TimeSpan.FromSeconds(20);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(cycleBudget));
        if (Volatile.Read(ref _foreground) != 0 || !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Snapshot;
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_lifecycleGate) _cycle = cycle;
        try
        {
            if (Volatile.Read(ref _paused) != 0) return Set(new("PAUSED"));
            lock (_catalogue.SyncRoot)
                if (!_catalogue.Settings.DisclosureAccepted) return Set(new("CONSENT_REQUIRED"));
            if (_lifetime.IsCancellationRequested) return Set(new("STOPPED"));
            if (_clock.Monotonic < _blockedUntil) return Snapshot;
            var watch = Stopwatch.StartNew();
            var attempted = 0; var successes = 0; var failures = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (attempted < maxNodes && watch.Elapsed < limit && !cycle.IsCancellationRequested)
            {
                var now = _clock.UtcNow;
                foreach (var key in _deferred.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _deferred.Remove(key);
                string[] ids; int due;
                lock (_catalogue.SyncRoot)
                {
                    if (!_catalogue.Settings.DisclosureAccepted) return Set(new("CONSENT_REQUIRED"));
                    var pending = _catalogue.Nodes.Where(n => !visited.Contains(n.NodeId) &&
                            !_deferred.ContainsKey(n.NodeId) && Due(n, now) &&
                            ProbeAuthority.Allows(n, _catalogue.Settings, SelectionPurpose.Automatic))
                        .OrderBy(n => n.Assessment?.LastAttemptUtc ?? DateTimeOffset.MinValue)
                        .ThenBy(n => n.NodeId, StringComparer.Ordinal).ToList();
                    due = pending.Count;
                    // At most one priority slot; bulk/new work always retains a slot.
                    var active = pending.FirstOrDefault(n => n.ActiveSession);
                    if (active is not null) { pending.Remove(active); pending.Insert(0, active); }
                    ids = pending.Take(Math.Min(2, maxNodes - attempted)).Select(n => n.NodeId).ToArray();
                }
                if (ids.Length == 0) return Set(new("IDLE", 0, attempted, successes, failures));
                Set(new("CHECKING", due, attempted, successes, failures));
                var tasks = new List<Task<ProbeCheckResult>>();
                Exception? reservationError = null;
                foreach (var id in ids)
                {
                    visited.Add(id);
                    try { if (!ReserveBudget()) break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { reservationError = ex; break; }
                    tasks.Add(ProbeCoordinator.CheckAsync(_catalogue, _transport, _transport.PrimaryTarget,
                        id, _clock.UtcNow, SelectionPurpose.Automatic, cycle.Token,
                        TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds), _clock));
                }
                if (tasks.Count == 0 && reservationError is not null) throw reservationError;
                if (tasks.Count == 0) return Set(new("BUDGET_EXHAUSTED", due, attempted, successes, failures));
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                for (var i = 0; i < results.Length; i++)
                {
                    var r = results[i];
                    attempted++;
                    if (r.Published && r.Success) successes++;
                    else if (r.Published && !r.Environment) failures++;
                    if (!r.Published) _deferred[ids[i]] = _clock.UtcNow.AddMinutes(1);
                }
                if (reservationError is not null) throw reservationError;
                if (results.Any(r => r.CoreFailure || r.Environment))
                {
                    _blockedUntil = _clock.Monotonic + TimeSpan.FromMinutes(1);
                    return Set(new("BACKOFF", due, attempted, successes, failures,
                        results.Any(r => r.CoreFailure) ? "CORE_UNAVAILABLE" : "TARGET_OR_UPLINK_UNCERTAIN"));
                }
                if (Volatile.Read(ref _foreground) != 0) break;
            }
            return Set(new(cycle.IsCancellationRequested ? "PAUSED" : "WAITING", 0, attempted, successes, failures));
        }
        catch (OperationCanceledException) { return Set(new("PAUSED")); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _blockedUntil = _clock.Monotonic + TimeSpan.FromMinutes(1);
            return Set(new("ERROR", Reason: "MAINTENANCE_" + ex.GetType().Name));
        }
        finally
        {
            lock (_lifecycleGate) { if (_cycle == cycle) _cycle = null; }
            _gate.Release();
        }
    }

    public async Task<bool> CheckNowAsync(string nodeId, CancellationToken cancellationToken,
        SelectionPurpose purpose = SelectionPurpose.PreConnect)
    {
        Interlocked.Increment(ref _foreground);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var taken = false;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false); taken = true;
            lock (_catalogue.SyncRoot)
            {
                var node = _catalogue.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
                if (node is null || !_catalogue.Settings.DisclosureAccepted ||
                    !ProbeAuthority.Allows(node, _catalogue.Settings, purpose) || node.Assessment?.RetryAfterUtc > _clock.UtcNow) return false;
                if (Eligibility.Evaluate(node.Semantics, node.Assessment, new EligibilityContext
                {
                    NowUtc = _clock.UtcNow, NetworkEpoch = _catalogue.NetworkEpoch, Purpose = purpose,
                    AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
                    AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                    RequiredTargetSetId = TargetSetId, MaxAcceptableLatencyMs = _catalogue.Settings.MaxAcceptableLatencyMs,
                }).Eligible) return true;
            }
            if (!ReserveBudget()) return false;
            var result = await ProbeCoordinator.CheckAsync(_catalogue, _transport, _transport.PrimaryTarget,
                nodeId, _clock.UtcNow, purpose, linked.Token,
                TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds), _clock).ConfigureAwait(false);
            return result.Published && result.Success;
        }
        finally { if (taken) _gate.Release(); Interlocked.Decrement(ref _foreground); }
    }

    private bool Due(CatalogueNode node, DateTimeOffset now)
    {
        if (node.Assessment?.RetryAfterUtc > now) return false;
        return ProbeCoordinator.NeedsProbe(node, now, _catalogue.NetworkEpoch) ||
            node.Assessment?.VerifiedTargetSetId != TargetSetId ||
            (node.Assessment?.LastSuccessUtc is DateTimeOffset stamp &&
                TimePolicy.ConservativeAge(stamp, now) >= TimeSpan.FromMinutes(25));
    }

    private bool ReserveBudget()
    {
        // Conservative health-payload reservation is persisted BEFORE I/O. No refund after a crash,
        // cancellation or midnight rollover. This does not claim to measure IP/TLS setup overhead.
        if (!_budget.TryReserve(2 * ProductLimits.HealthPayloadBytes, DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime))) return false;
        if (_budgetPath is not null) _budget.Save(_budgetPath);
        return true;
    }

    private MaintenanceSnapshot Set(MaintenanceSnapshot state)
    {
        Volatile.Write(ref _snapshot, state); return state;
    }
}
