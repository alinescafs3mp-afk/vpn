using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Application;

/// <summary>
/// Per-catalogue, in-process authority for ALL probe publications. Lives outside node DTOs,
/// so SQLite copy-on-write, favorites and snapshot replacement cannot reuse an attempt.
/// A new catalogue instance has no live attempts; old completions cannot survive restart.
/// </summary>
public sealed class ProbeAuthority
{
    private static readonly ConditionalWeakTable<ICatalogue, ProbeAuthority> Authorities = new();
    private readonly ICatalogue _catalogue;
    private readonly Dictionary<string, Reservation> _live = new(StringComparer.Ordinal);
    private ProbeAuthority(ICatalogue catalogue) => _catalogue = catalogue;
    public static ProbeAuthority For(ICatalogue catalogue) => Authorities.GetValue(catalogue, c => new ProbeAuthority(c));

    public ProbeReservation? Begin(string nodeId, Uri target, SelectionPurpose purpose)
    {
        lock (_catalogue.SyncRoot)
        {
            var node = _catalogue.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            var settings = _catalogue.Settings;
            if (node is null || !Allows(node, settings, purpose)) return null;
            var semantics = node.Semantics with { Alpn = node.Semantics.Alpn?.ToArray() };
            if (CanonicalIdentity.Digest(semantics) != node.Digest) return null;
            var context = new ProbeAttemptContext(Guid.NewGuid().ToString("N"), nodeId, node.Digest,
                _catalogue.NetworkEpoch, settings.Revision, target.AbsoluteUri, purpose);
            var policy = PolicyKey(settings);
            _live[nodeId] = new Reservation(context, policy);
            return new ProbeReservation(context, semantics, settings.AllowInsecureCertificates, node.Assessment);
        }
    }

    public bool IsCurrent(ProbeAttemptContext context)
    {
        lock (_catalogue.SyncRoot)
        {
            var node = _catalogue.Nodes.FirstOrDefault(n => n.NodeId == context.NodeId);
            return _catalogue.Settings.DisclosureAccepted && node is not null &&
                _live.TryGetValue(context.NodeId, out var live) && live.Context == context &&
                _catalogue.NetworkEpoch == context.NetworkEpoch && node.Digest == context.Digest &&
                PolicyKey(_catalogue.Settings) == live.Policy &&
                Allows(node, _catalogue.Settings, context.Purpose) &&
                CanonicalIdentity.Digest(node.Semantics) == context.Digest;
        }
    }

    public bool Commit(ProbeAttemptContext requested, ProbeAttemptContext? observed,
        AssessmentSnapshot assessment, CancellationToken cancellationToken)
    {
        lock (_catalogue.SyncRoot)
        {
            if (!_live.TryGetValue(requested.NodeId, out var live) || live.Context != requested) return false;
            // Every completed attempt is consumed, including rejected proof and failed storage.
            // A newer Begin replaces it; no unbounded blacklist or probabilistic history is needed.
            _live.Remove(requested.NodeId);
            if (cancellationToken.IsCancellationRequested || observed != requested) return false;
            var node = _catalogue.Nodes.FirstOrDefault(n => n.NodeId == requested.NodeId);
            if (node is null || node.Digest != requested.Digest ||
                _catalogue.NetworkEpoch != requested.NetworkEpoch ||
                PolicyKey(_catalogue.Settings) != live.Policy ||
                !Allows(node, _catalogue.Settings, requested.Purpose) ||
                CanonicalIdentity.Digest(node.Semantics) != requested.Digest ||
                assessment.Digest != requested.Digest || assessment.NetworkEpoch != requested.NetworkEpoch)
                return false;
            _catalogue.ApplyAssessment(requested.NodeId, assessment);
            return true;
        }
    }

    public void Cancel(ProbeAttemptContext context)
    {
        lock (_catalogue.SyncRoot)
        {
            if (_live.TryGetValue(context.NodeId, out var live) && live.Context == context)
                _live.Remove(context.NodeId);
        }
    }

    public static bool Allows(CatalogueNode node, ProductSettings settings, SelectionPurpose purpose)
    {
        if ((node.Excluded && purpose != SelectionPurpose.Manual) || node.PolicyReason is not null ||
            node.Semantics.Classify(settings.AllowInsecureCertificates) != SecurityPosture.Accepted) return false;
        var families = node.CurrentFamilies.Concat(node.HistoricalFamilies).ToArray();
        if (families.Length != 0 && families.All(f => settings.DisabledFamilyIds.Contains(f))) return false;
        return settings.CountryMode != CountryConstraint.Strict ||
            string.Equals(node.AdvertisedCountry, settings.Country, StringComparison.OrdinalIgnoreCase);
    }

    private static string PolicyKey(ProductSettings settings) => JsonSerializer.Serialize(settings);
    private sealed record Reservation(ProbeAttemptContext Context, string Policy);
}

public sealed record ProbeReservation(ProbeAttemptContext Context, NodeSemantics Semantics,
    bool AllowInsecureCertificates, AssessmentSnapshot? PriorAssessment);
