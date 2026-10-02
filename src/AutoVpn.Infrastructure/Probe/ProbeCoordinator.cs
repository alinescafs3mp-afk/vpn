using AutoVpn.Application;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Probe;

public sealed record ProbeObservation(bool Success, int? LatencyMs, bool UplinkOffline, string? ReasonCode);

public interface IProbeTransport
{
    Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken cancellationToken);
}

public sealed record ProbeReport(int Attempted, int Succeeded, int Failed, bool StoppedForUplink);

/// <summary>
/// Publishes a node as healthy only after the transport reports success and a latency sample.
/// An uplink failure stops the cycle and does not mark the remaining nodes failed.
/// </summary>
public static class ProbeCoordinator
{
    public static async Task<ProbeReport> RunAsync(
        ICatalogue catalogue,
        IProbeTransport transport,
        Uri target,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var perEndpoint = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in catalogue.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.PolicyReason is not null)
            {
                continue;
            }

            if (node.Assessment?.Health is HealthState.Healthy or HealthState.Degraded)
            {
                continue;
            }

            var endpoint = node.Semantics.Host + ":" + node.Semantics.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            perEndpoint.TryGetValue(endpoint, out var used);
            if (used >= ProductLimits.MaxProbesPerEndpoint)
            {
                continue;
            }

            if (attempted >= ProductLimits.LightweightProbeConcurrency)
            {
                break;
            }

            perEndpoint[endpoint] = used + 1;
            attempted++;
            var observation = await transport.ProbeAsync(node.Semantics, target, cancellationToken).ConfigureAwait(false);
            if (observation.UplinkOffline)
            {
                catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
                {
                    Digest = node.Digest,
                    NetworkEpoch = catalogue.NetworkEpoch,
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
                catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
                {
                    Digest = node.Digest,
                    NetworkEpoch = catalogue.NetworkEpoch,
                    Health = HealthState.Healthy,
                    LastSuccessUtc = nowUtc,
                    MedianLatencyMs = latency,
                    ConsecutiveFailures = 0,
                });
                succeeded++;
                continue;
            }

            catalogue.ApplyAssessment(node.NodeId, new AssessmentSnapshot
            {
                Digest = node.Digest,
                NetworkEpoch = catalogue.NetworkEpoch,
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
}
