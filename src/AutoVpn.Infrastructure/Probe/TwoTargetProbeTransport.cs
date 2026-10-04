using System.Security.Cryptography;
using System.Text;
using AutoVpn.Domain;
using AutoVpn.Application;

namespace AutoVpn.Infrastructure.Probe;

/// <summary>
/// One atomic admission consists of two separately authenticated HTTPS exchanges.
/// This adapter never publishes the first half, never grants trust to a target certificate,
/// and keeps its worker slot until the inner transport has completed its cleanup.
/// </summary>
public sealed class TwoTargetProbeTransport : IProbeTransport
{
    private readonly IProbeTransport _inner;
    private readonly ProbeAuthority _authority;
    private readonly Uri[] _targets;
    private readonly SemaphoreSlim _slots;
    private readonly int _workers;
    private readonly TimeSpan _timeout;
    public Uri PrimaryTarget => _targets[0];
    public string TargetSetId { get; }

    public TwoTargetProbeTransport(ICatalogue catalogue, IProbeTransport inner, IReadOnlyList<Uri> targets,
        int workers = ProductLimits.ProbeWorkerProcesses, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        _authority = ProbeAuthority.For(catalogue);
        ArgumentNullException.ThrowIfNull(inner);
        var identity = TargetIdentity(targets);
        if (workers is < 1 or > ProductLimits.ProbeWorkerProcesses) throw new ArgumentOutOfRangeException(nameof(workers));
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _inner = inner; _targets = targets.ToArray(); _workers = workers; _slots = new(workers, workers);
        TargetSetId = identity;
    }

    /// <summary>Validate and identify the reviewed pair without creating workers or performing I/O.</summary>
    public static string TargetIdentity(IReadOnlyList<Uri> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count != 2 || targets.Any(t => t is null || !t.IsAbsoluteUri ||
            t.Scheme != Uri.UriSchemeHttps || t.UserInfo.Length != 0 || t.Fragment.Length != 0) ||
            string.Equals(targets[0].IdnHost, targets[1].IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Two HTTPS targets on distinct reviewed hosts are required.", nameof(targets));
        var contract = "https-204-empty-v2\n" + string.Join("\n", targets.Select(t => t.AbsoluteUri).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    public Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken)
        => ProbeAsync(node, target, new ProbeAdmission(false), cancellationToken);

    public async Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission,
        CancellationToken cancellationToken)
    {
        var context = admission.Attempt;
        if (context is null || target != PrimaryTarget || context.TargetUri != target.AbsoluteUri ||
            context.Digest != CanonicalIdentity.Digest(node))
            return new(false, null, false, "ADMISSION_CONTEXT_REQUIRED", Class: ProbeClass.Unsupported);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        long bytes = 0;
        var good = new List<ProbeObservation>(2);
        var failures = new List<ProbeObservation>(2);
        try
        {
            foreach (var endpoint in _targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_authority.IsCurrent(context)) return Combined(false, null, ProbeClass.Canceled, "ATTEMPT_SUPERSEDED");
                // A distinct child context prevents using the first response as second-target proof.
                var child = context with { TargetUri = endpoint.AbsoluteUri };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_timeout);
                ProbeObservation result;
                try
                {
                    result = await _inner.ProbeAsync(node, endpoint, admission with { Attempt = child },
                        deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new(false, null, false, "PROBE_TIMEOUT");
                }
                catch (IOException)
                {
                    result = new(false, null, false, "PROBE_IO");
                }
                bytes = Math.Min(int.MaxValue, bytes + Math.Max(0, result.PayloadBytes));
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Class == ProbeClass.CoreFailure)
                    return Combined(false, null, ProbeClass.CoreFailure, result.ReasonCode ?? "CORE_UNAVAILABLE");
                if (result.UplinkOffline || result.Class == ProbeClass.Environment)
                    return Combined(false, null, ProbeClass.Environment, result.ReasonCode ?? "ENVIRONMENT_UNKNOWN");
                var matches = result.Success && result.Class == ProbeClass.Success && result.ReasonCode is null &&
                    result.LatencyMs is >= 0 && result.Attempt == child && result.TargetUri == endpoint.AbsoluteUri &&
                    result.CandidateDigest == child.Digest && !string.IsNullOrWhiteSpace(result.WorkerId);
                if (matches) good.Add(result); else failures.Add(result);
            }
            if (good.Count == 2)
            {
                // Median of the two measured exchanges, not ICMP or a historical latency estimate.
                var latency = (int)(((long)good[0].LatencyMs!.Value + good[1].LatencyMs!.Value) / 2);
                return Combined(true, latency, ProbeClass.Success, null) with { VerifiedTargetSetId = TargetSetId };
            }
            if (good.Count == 1)
                return Combined(false, null, ProbeClass.Environment, "TARGET_SET_INCONCLUSIVE");
            return Combined(false, null, ProbeClass.CandidateFailure,
                failures.Any(f => f.Class == ProbeClass.Unsupported) ? "CANDIDATE_UNSUPPORTED" : "TWO_TARGETS_FAILED");
        }
        finally { _slots.Release(); }

        ProbeObservation Combined(bool success, int? latency, ProbeClass kind, string? reason)
            => new(success, latency, false, reason, (int)bytes, kind, target.AbsoluteUri,
                context.Digest, "target-set:" + context.AttemptId) { Attempt = context };
    }

    /// <summary>Wait for owned workers to finish. Caller supplies a bounded shutdown deadline.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var acquired = 0;
        try
        {
            for (; acquired < _workers; acquired++) await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { if (acquired > 0) _slots.Release(acquired); }
    }
}
