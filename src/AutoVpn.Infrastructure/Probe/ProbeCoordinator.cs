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

public readonly record struct ProbeAdmission(bool AllowInsecureProxyCertificates);

public interface IProbeTransport
{
    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken);

    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, ProbeAdmission admission, CancellationToken cancellationToken)
        => ProbeAsync(node, target, cancellationToken);
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
        long? byteBudget = null,
        ProbeByteBudget? spent = null)
    {
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var day = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        long bytes = spent?.SpentOn(day) ?? 0;
        var limit = budget ?? TimeSpan.FromSeconds(ProductLimits.NewCandidateBudgetSeconds);
        var perAttempt = attemptTimeout ?? TimeSpan.FromSeconds(ProductLimits.ProbeRequestTimeoutSeconds);
        var byteLimit = spent?.Limit ?? byteBudget ?? ProductLimits.DailyHealthBudgetBytes;
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

            if (bytes >= byteLimit || (spent is not null && spent.Exhausted(day)))
            {
                break;
            }

            if (!Scheduled(node, catalogue.Settings))
            {
                continue;
            }

            var epoch = catalogue.NetworkEpoch;
            var digest = node.Digest;
            var nodeId = node.NodeId;
            var settingsRevision = catalogue.Settings.Revision;
            var allowInsecure = catalogue.Settings.AllowInsecureCertificates;
            var stamp = Interlocked.Increment(ref node.ProbePublication);
            attempted++;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ProbeObservation observation;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(perAttempt);
                try
                {
                    observation = await transport.ProbeAsync(node.Semantics, target, new ProbeAdmission(allowInsecure), attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    spent?.Charge(1, day);
                    if (!PolicyHeld(catalogue, nodeId, settingsRevision, allowInsecure))
                    {
                        continue;
                    }

                    failed++;
                    TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
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

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var payloadBytes = Math.Max(0, observation.PayloadBytes);
            bytes += payloadBytes;
            if (spent is not null)
            {
                spent.Charge(Math.Max(payloadBytes, 1), day);
                bytes = spent.SpentOn(day);
            }

            if (!PolicyHeld(catalogue, nodeId, settingsRevision, allowInsecure))
            {
                continue;
            }

            if (catalogue.NetworkEpoch != epoch || catalogue.Nodes.All(item => item.NodeId != nodeId || item.Digest != digest))
            {
                continue;
            }

            if (observation.Class == ProbeClass.Canceled || observation.ReasonCode == ReasonCodes.Canceled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    attempted--;
                    break;
                }

                failed++;
                TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
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

            if (observation.Class == ProbeClass.Unsupported)
            {
                failed++;
                TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
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

            if (observation.Class == ProbeClass.CoreFailure)
            {
                return new ProbeReport(attempted, succeeded, failed, false);
            }

            if (observation.UplinkOffline || observation.Class == ProbeClass.Environment)
            {
                TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
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

            if (observation.Success && observation.Class == ProbeClass.Success && observation.LatencyMs is int latency && latency >= 0 && ProofAccepts(observation, digest, target) && !cancellationToken.IsCancellationRequested && !ConsumedOnAnotherEpoch(catalogue, nodeId, observation, epoch))
            {
                var published = TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
                {
                    Digest = digest,
                    NetworkEpoch = epoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = nowUtc,
                    MedianLatencyMs = latency,
                    ConsecutiveFailures = 0,
                    ProofToken = ProofToken(observation),
                });
                if (published)
                {
                    succeeded++;
                }

                continue;
            }

            TryApply(catalogue, nodeId, digest, epoch, stamp, new AssessmentSnapshot
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
        if (node.Assessment?.RetryAfterUtc is DateTimeOffset retry && retry > nowUtc)
        {
            return false;
        }

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
        CancellationToken cancellationToken,
        SelectionPurpose purpose = SelectionPurpose.PreConnect)
    {
        var node = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node is null || !NeedsOnDemandAdmission(node, nowUtc, catalogue.NetworkEpoch))
        {
            return false;
        }

        if (!Scheduled(node, catalogue.Settings, purpose))
        {
            return false;
        }

        var epoch = catalogue.NetworkEpoch;
        var digest = node.Digest;
        var settingsRevision = catalogue.Settings.Revision;
        var allowInsecure = catalogue.Settings.AllowInsecureCertificates;
        ProbeObservation observation;
        try
        {
            observation = await transport.ProbeAsync(node.Semantics, target, new ProbeAdmission(allowInsecure), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (cancellationToken.IsCancellationRequested
            || observation.Class is ProbeClass.Canceled or ProbeClass.Environment or ProbeClass.CoreFailure or ProbeClass.Unsupported
            || (observation.Success && observation.Class != ProbeClass.Success)
            || observation.ReasonCode == ReasonCodes.Canceled
            || observation.UplinkOffline)
        {
            return false;
        }

        if (!observation.Success || observation.LatencyMs is not int latency || latency < 0 || !ProofAccepts(observation, digest, target) || ConsumedOnAnotherEpoch(catalogue, nodeId, observation, epoch))
        {
            if (catalogue.NetworkEpoch != epoch || !PolicyHeld(catalogue, nodeId, settingsRevision, allowInsecure, purpose))
            {
                return false;
            }

            catalogue.ApplyAssessment(nodeId, new AssessmentSnapshot
            {
                Digest = digest,
                NetworkEpoch = epoch,
                Health = HealthState.Failed,
                LastSuccessUtc = node.Assessment?.LastSuccessUtc,
                LastFailureUtc = nowUtc,
                MedianLatencyMs = node.Assessment?.MedianLatencyMs,
                ConsecutiveFailures = (node.Assessment?.ConsecutiveFailures ?? 0) + 1,
            });
            return false;
        }

        if (catalogue.NetworkEpoch != epoch || !PolicyHeld(catalogue, nodeId, settingsRevision, allowInsecure, purpose))
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
            ProofToken = ProofToken(observation),
        });
        return true;
    }

    private static bool TryApply(ICatalogue catalogue, string nodeId, string digest, long epoch, long stamp, AssessmentSnapshot assessment)
    {
        if (catalogue.NetworkEpoch != epoch)
        {
            return false;
        }

        var current = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (current is null || current.Digest != digest || current.ProbePublication != stamp)
        {
            return false;
        }

        catalogue.ApplyAssessment(nodeId, assessment);
        return true;
    }

    private static bool Scheduled(CatalogueNode node, ProductSettings settings, SelectionPurpose purpose = SelectionPurpose.PreConnect)
    {
        if ((node.Excluded && purpose != SelectionPurpose.Manual) || node.PolicyReason is not null)
        {
            return false;
        }

        if (node.Semantics.SkipCertVerify && !settings.AllowInsecureCertificates)
        {
            return false;
        }

        var families = node.CurrentFamilies.Concat(node.HistoricalFamilies).ToArray();
        if (families.Length > 0 && families.All(family => settings.DisabledFamilyIds.Contains(family)))
        {
            return false;
        }

        if (settings.CountryMode == CountryConstraint.Strict
            && !string.Equals(node.AdvertisedCountry, settings.Country, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool ProofAccepts(ProbeObservation observation, string digest, Uri target)
    {
        return observation.Class == ProbeClass.Success
            && !string.IsNullOrEmpty(observation.CandidateDigest)
            && string.Equals(observation.CandidateDigest, digest, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(observation.TargetUri)
            && string.Equals(observation.TargetUri, target.AbsoluteUri, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(observation.WorkerId);
    }

    private static string ProofToken(ProbeObservation observation)
    {
        return string.Join('\n',
            observation.CandidateDigest,
            observation.TargetUri,
            observation.WorkerId,
            observation.Class.ToString(),
            observation.LatencyMs?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            observation.PayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool ConsumedOnAnotherEpoch(ICatalogue catalogue, string nodeId, ProbeObservation observation, long epoch)
    {
        var current = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        var prior = current?.Assessment;
        return prior?.ProofToken is string token
            && string.Equals(token, ProofToken(observation), StringComparison.Ordinal)
            && prior.NetworkEpoch != epoch;
    }

    private static bool PolicyHeld(ICatalogue catalogue, string nodeId, int revision, bool allowInsecure, SelectionPurpose purpose = SelectionPurpose.PreConnect)
    {
        if (catalogue.Settings.Revision != revision || catalogue.Settings.AllowInsecureCertificates != allowInsecure)
        {
            return false;
        }

        var node = catalogue.Nodes.FirstOrDefault(item => item.NodeId == nodeId);
        return node is not null && Scheduled(node, catalogue.Settings, purpose);
    }
}
