using System.Runtime.CompilerServices;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.UnitTests;

// Explicit test-only adapter for existing synthetic factories. A reused observation INSTANCE
// retains its first request binding, so replay tests cannot accidentally mint fresh evidence.
// Production transports must construct their own context; the interface never fills it in.
public abstract class BoundTestProbeTransport : IProbeTransport
{
    private static readonly ConditionalWeakTable<ProbeObservation, Bound> Bindings = new();
    public abstract Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target, CancellationToken token);
    public virtual async Task<ProbeObservation> ProbeAsync(NodeSemantics node, Uri target,
        ProbeAdmission admission, CancellationToken token)
        => Bind(await ProbeAsync(node, target, token), admission);
    protected static ProbeObservation Bind(ProbeObservation observation, ProbeAdmission admission)
    {
        if (observation.Attempt is not null) return observation;
        return Bindings.GetValue(observation, o => new Bound(o with { Attempt = admission.Attempt })).Observation;
    }
    private sealed record Bound(ProbeObservation Observation);
}
