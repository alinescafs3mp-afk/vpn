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
    string? WorkerId = null);

public interface IProbeTransport
{
    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken);
}

public sealed record ProbeReport(int Attempted, int Succeeded, int Failed, bool StoppedForUplink);

/// <summary>
/// Publishes a node only after the transport returns and the captured epoch still matches.
/// An uplink failure stops the cycle and does not mark the remaining nodes failed.
/// Healthy rows are rechecked when they are stale or belong to another network epoch.
/// </summary>
public static class ProbeCoordinator
{
    public static async Task<ProbeReport> RunAsync(
        ICatalogue catalogue,
        IProbeTransport transport,
        Uri target,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        TimeSpan? budget = null,
        TimeSpan? attemptTimeout = null,
        long? byteBudget = null)
    {
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        long bytes = 0;
        var limit = budget ?? TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds);
        var perAttempt = attemptTimeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds);
        var byteLimit = byteBudget ?? ProductLimits.DailyHealthBudgetBytes;
        var elapsed = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(ProductLimits.MaxProbesPerEndpoint, ProductLimits.MaxProbesPerEndpoint);
        var pending = catalogue.Nodes
            .Where(node => NeedsProbe(node, nowUtc, catalogue.NetworkEpoch))
            .OrderBy(node => node.Assessment?.LastFailureUtc ?? node.Assessment?.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        foreach (var node in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempted > 0 && elapsed.Elapsed >= limit)
            {
                break;
            }

            if (bytes >= byteLimit)
            {
                break;
            }

            var epoch = catalogue.NetworkEpoch;
            var digest = node.Digest;
            var nodeId = node.NodeId;
            attempted++;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ProbeObservation observation;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(perAttempt);
                try
                {
                    observation = await transport.ProbeAsync(node.Semantics, target, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    TryApply(catalogue, nodeId, digest, epoch, new AssessmentSnapshot
                    {
                        Digest = digest,
                        NetworkEpoch = epoch,
                        Health = HealthState.Failed,
                        LastSuccessUtc = node.Assessment?.LastSuccessUtc,
                        LastFailureUtc = nowUtc,
                        MedianLatencyMs = node.Assessment?.MedianLatencyMs,
                        ConsecutiveFailures = (node.Assessment?.ConsecutiveFailures ?? 0) + 1,
                    });
                    continue;
                }
                catch (OperationCanceledException)
                {
                    attempted--;
                    break;
                }
            }
            finally
            {
                gate.Release();
            }

            bytes += Math.Max(0, observation.PayloadBytes);
            if (catalogue.NetworkEpoch != epoch || catalogue.Nodes.All(item => item.NodeId != nodeId || item.Digest != digest))
            {
                continue;
            }

            if (observation.Class == ProbeClass.Canceled || observation.ReasonCode == ReasonCodes.Canceled)
            {
                attempted--;
                break;
            }

            if (observation.Class is ProbeClass.CoreFailure or ProbeClass.Unsupported)
            {
                return new ProbeReport(attempted, succeeded, failed, false);
            }

            if (observation.UplinkOffline || observation.Class == ProbeClass.Environment)
            {
                TryApply(catalogue, nodeId, digest, epoch, new AssessmentSnapshot
                {
                    Digest = digest,
                    NetworkEpoch = epoch,
                    Health = HealthState.EnvironmentUnknown,
                    EnvironmentFailure = true,
                    LastSuccessUtc = node.Assessment?.LastSuccessUtc,
                    LastFailureUtc = node.Assessment?.LastFailureUtc,
                    MedianLatencyMs = node.Assessment?.MedianLatencyMs,
                });
                return new ProbeReport(attempted, succeeded, failed, true);
            }

            if (observation.Success && observation.LatencyMs is int latency && latency >= 0)
            {
                TryApply(catalogue, nodeId, digest, epoch, new AssessmentSnapshot
                {
                    Digest = digest,
                    NetworkEpoch = epoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = nowUtc,
                    MedianLatencyMs = latency,
                    ConsecutiveFailures = 0,
                });
                succeeded++;
                continue;
            }

            TryApply(catalogue, nodeId, digest, epoch, new AssessmentSnapshot
            {
                Digest = digest,
                NetworkEpoch = epoch,
                Health = HealthState.Failed,
                LastSuccessUtc = node.Assessment?.LastSuccessUtc,
                LastFailureUtc = nowUtc,
                MedianLatencyMs = node.Assessment?.MedianLatencyMs,
                ConsecutiveFailures = (node.Assessment?.ConsecutiveFailures ?? 0) + 1,
            });
            failed++;
        }

        return new ProbeReport(attempted, succeeded, failed, false);
    }

    public static bool NeedsProbe(CatalogueNode node, DateTimeOffset nowUtc, long epoch)
    {
        if (node.PolicyReason is not null)
        {
            return false;
        }

        var assessment = node.Assessment;
        if (assessment is null || assessment.Health is not (HealthState.Healthy or HealthState.Degraded))
        {
            return true;
        }

        if (assessment.NetworkEpoch != epoch || assessment.LastSuccessUtc is not DateTimeOffset success)
        {
            return true;
        }

        return TimePolicy.ConservativeAge(success, nowUtc) > TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes);
    }

    public static bool NeedsOnDemandAdmission(CatalogueNode node, DateTimeOffset nowUtc, long epoch)
    {
        if (node.PolicyReason is not null)
        {
            return false;
        }

        var assessment = node.Assessment;
        if (assessment is null || assessment.Health is not (HealthState.Healthy or HealthState.Degraded))
        {
            return false;
        }

        if (assessment.NetworkEpoch != epoch || assessment.LastSuccessUtc is not DateTimeOffset success)
        {
            return true;
        }

        return TimePolicy.ConservativeAge(success, nowUtc) > TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds);
    }

    public static async Task<bool> AdmitIfStaleAsync(
        ICatalogue catalogue,
        IProbeTransport transport,
        Uri target,
        string nodeId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var node = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node is null || !NeedsOnDemandAdmission(node, nowUtc, catalogue.NetworkEpoch))
        {
            return false;
        }

        var epoch = catalogue.NetworkEpoch;
        var digest = node.Digest;
        ProbeObservation observation;
        try
        {
            observation = await transport.ProbeAsync(node.Semantics, target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (observation.Class == ProbeClass.Canceled || observation.ReasonCode == ReasonCodes.Canceled)
        {
            return false;
        }

        if (!observation.Success || observation.LatencyMs is not int latency || latency < 0)
        {
            return false;
        }

        if (catalogue.NetworkEpoch != epoch)
        {
            return false;
        }

        catalogue.ApplyAssessment(nodeId, new AssessmentSnapshot
        {
            Digest = digest,
            NetworkEpoch = epoch,
            Health = HealthState.Healthy,
            LastSuccessUtc = nowUtc,
            MedianLatencyMs = latency,
            ConsecutiveFailures = 0,
        });
        return true;
    }

    private static void TryApply(ICatalogue catalogue, string nodeId, string digest, long epoch, AssessmentSnapshot assessment)
    {
        if (catalogue.NetworkEpoch != epoch)
        {
            return;
        }

        var current = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (current is null || current.Digest != digest)
        {
            return;
        }

        catalogue.ApplyAssessment(nodeId, assessment);
    }
}
