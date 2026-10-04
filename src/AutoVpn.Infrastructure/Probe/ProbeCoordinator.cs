using System.Diagnostics;
using AutoVpn.Application;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Probe;

public enum ProbeClass
{
    CandidateFailure = 0,
    Success = 1,
    Canceled = 2,
    Environment = 3,
    CoreFailure = 4,
    Unsupported = 5,
}

public sealed record ProbeObservation(
    bool Success,
    int? LatencyMs,
    bool UplinkOffline,
    string? ReasonCode,
    int PayloadBytes = 0,
    ProbeClass Class = ProbeClass.CandidateFailure,
    string? TargetUri = null,
    string? CandidateDigest = null,
    string? WorkerId = null)
{
    public ProbeAttemptContext? Attempt { get; init; }
}

public readonly record struct ProbeAdmission(bool AllowInsecureProxyCertificates)
{
    public ProbeAttemptContext? Attempt { get; init; }
}

public interface IProbeTransport
{
    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken);

    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken cancellationToken)
        => ProbeAsync(node, target, cancellationToken);
}

public sealed record ProbeReport(int Attempted, int Succeeded, int Failed, bool StoppedForUplink);

/// <summary>
/// Common routine/on-demand admission boundary. Attempt ownership is reserved in the catalogue,
/// not on a copied node. A transport must return the exact context supplied in ProbeAdmission.
/// Production callers pass a live clock; explicit nowUtc remains useful for deterministic tests.
/// </summary>
public static class ProbeCoordinator
{
    public static async Task<ProbeReport> RunAsync(
        ICatalogue catalogue, IProbeTransport transport, Uri target, DateTimeOffset nowUtc,
        CancellationToken cancellationToken, TimeSpan? budget = null, TimeSpan? attemptTimeout = null,
        long? byteBudget = null, ProbeByteBudget? spent = null, IClock? clock = null)
    {
        var attempted = 0; var succeeded = 0; var failed = 0;
        var elapsed = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds);
        var day = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        long bytes = spent?.SpentOn(day) ?? 0;
        var byteLimit = spent?.Limit ?? byteBudget ?? ProductLimits.DailyHealthBudgetBytes;
        CatalogueNode[] pending;
        lock (catalogue.SyncRoot)
        {
            pending = catalogue.Nodes.Where(n => NeedsProbe(n, nowUtc, catalogue.NetworkEpoch))
                .OrderBy(n => n.Assessment?.LastFailureUtc ?? n.Assessment?.LastSuccessUtc ?? DateTimeOffset.MinValue)
                .ThenBy(n => n.NodeId, StringComparer.Ordinal).ToArray();
        }
        foreach (var node in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((attempted > 0 && elapsed.Elapsed >= limit) || bytes >= byteLimit || spent?.Exhausted(day) == true) break;
            var result = await CheckAsync(catalogue, transport, target, node.NodeId, nowUtc,
                SelectionPurpose.PreConnect, cancellationToken, attemptTimeout, clock).ConfigureAwait(false);
            if (!result.Attempted) continue;
            attempted++;
            bytes = bytes > long.MaxValue - result.Bytes ? long.MaxValue : bytes + result.Bytes;
            spent?.Charge(Math.Max(result.Bytes, 1), day);
            if (result.Canceled) break;
            if (result.Published && result.Success) succeeded++;
            else if (result.Published && !result.Environment) failed++;
            if (result.Environment) return new ProbeReport(attempted, succeeded, failed, true);
            if (result.CoreFailure) break;
        }
        return new ProbeReport(attempted, succeeded, failed, false);
    }

    public static bool NeedsProbe(CatalogueNode node, DateTimeOffset nowUtc, long epoch)
    {
        if (node.Assessment?.RetryAfterUtc is DateTimeOffset retry && retry > nowUtc) return false;
        if (node.PolicyReason is not null) return false;
        var a = node.Assessment;
        return a is null || a.Health is not (HealthState.Healthy or HealthState.Degraded) ||
            a.NetworkEpoch != epoch || a.LastSuccessUtc is not DateTimeOffset success ||
            TimePolicy.ConservativeAge(success, nowUtc) > TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes);
    }

    public static bool NeedsOnDemandAdmission(CatalogueNode node, DateTimeOffset nowUtc, long epoch)
    {
        if (node.PolicyReason is not null) return false;
        var a = node.Assessment;
        return a is not null && a.Health is (HealthState.Healthy or HealthState.Degraded) &&
            (a.NetworkEpoch != epoch || a.LastSuccessUtc is not DateTimeOffset success ||
            TimePolicy.ConservativeAge(success, nowUtc) > TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds));
    }

    public static async Task<bool> AdmitIfStaleAsync(ICatalogue catalogue, IProbeTransport transport,
        Uri target, string nodeId, DateTimeOffset nowUtc, CancellationToken cancellationToken,
        SelectionPurpose purpose = SelectionPurpose.PreConnect, IClock? clock = null)
    {
        lock (catalogue.SyncRoot)
        {
            var node = catalogue.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node is null || !NeedsOnDemandAdmission(node, nowUtc, catalogue.NetworkEpoch) ||
                node.Assessment?.RetryAfterUtc > nowUtc) return false;
        }
        var result = await CheckAsync(catalogue, transport, target, nodeId, nowUtc, purpose,
            cancellationToken, clock: clock).ConfigureAwait(false);
        return result.Published && result.Success;
    }

    public static async Task<ProbeCheckResult> CheckAsync(ICatalogue catalogue, IProbeTransport transport,
        Uri target, string nodeId, DateTimeOffset nowUtc, SelectionPurpose purpose,
        CancellationToken cancellationToken, TimeSpan? timeout = null, IClock? clock = null)
    {
        if (cancellationToken.IsCancellationRequested) return new(Canceled: true);
        var authority = ProbeAuthority.For(catalogue);
        var reservation = authority.Begin(nodeId, target, purpose);
        if (reservation is null) return new();
        var requested = reservation.Context;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds));
        try
        {
            ProbeObservation observed;
            try
            {
                observed = await transport.ProbeAsync(reservation.Semantics, target,
                    new ProbeAdmission(reservation.AllowInsecureCertificates) { Attempt = requested },
                    deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return new(Attempted: true, Canceled: true);
                observed = new ProbeObservation(false, null, false, "PROBE_TIMEOUT") { Attempt = requested };
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                observed = new ProbeObservation(false, null, false, ReasonCodes.ProbeFailed) { Attempt = requested };
            }
            var bytes = Math.Max(observed.PayloadBytes, 0);
            if (cancellationToken.IsCancellationRequested) return new(Attempted: true, Canceled: true, Bytes: bytes);
            if (observed.Class == ProbeClass.CoreFailure)
                return new(Attempted: true, CoreFailure: true, Bytes: bytes);
            var environment = observed.UplinkOffline || observed.Class == ProbeClass.Environment;
            var success = observed.Success && observed.Class == ProbeClass.Success && observed.ReasonCode is null &&
                observed.LatencyMs is >= 0 && observed.CandidateDigest == requested.Digest &&
                observed.TargetUri == target.AbsoluteUri && !string.IsNullOrWhiteSpace(observed.WorkerId) &&
                observed.Attempt == requested;
            var prior = reservation.PriorAssessment;
            var completed = clock?.UtcNow ?? nowUtc;
            var assessment = new AssessmentSnapshot
            {
                Digest = requested.Digest, NetworkEpoch = requested.NetworkEpoch,
                Health = environment ? HealthState.EnvironmentUnknown : success ? HealthState.Healthy : HealthState.Failed,
                EnvironmentFailure = environment,
                LastSuccessUtc = success ? completed : prior?.LastSuccessUtc,
                LastFailureUtc = success ? null : environment ? prior?.LastFailureUtc : completed,
                MedianLatencyMs = success ? observed.LatencyMs : prior?.MedianLatencyMs,
                ConsecutiveFailures = success ? 0 : (int)Math.Min((long)(prior?.ConsecutiveFailures ?? 0) + 1, int.MaxValue),
                ProofToken = success ? requested.AttemptId : prior?.ProofToken,
            };
            var published = authority.Commit(requested, observed.Attempt, assessment, cancellationToken);
            return new(Attempted: true, Published: published, Success: success, Environment: environment, Bytes: bytes);
        }
        finally { authority.Cancel(requested); }
    }
}

public readonly record struct ProbeCheckResult(bool Attempted = false, bool Published = false,
    bool Success = false, bool Environment = false, bool CoreFailure = false, bool Canceled = false, int Bytes = 0);
