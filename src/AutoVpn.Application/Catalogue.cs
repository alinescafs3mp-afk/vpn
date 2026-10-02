using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed class CatalogueNode
{
    public required string NodeId { get; init; }
    public required string Digest { get; init; }
    public required NodeSemantics Semantics { get; init; }
    public required string Label { get; set; }
    public string? AdvertisedCountry { get; set; }
    public bool Favorite { get; set; }
    public bool Excluded { get; set; }
    public bool ActiveSession { get; set; }
    public string? PolicyReason { get; set; }
    public Dictionary<string, string> ArtifactFamilies { get; } = new(StringComparer.Ordinal);
    public HashSet<string> CurrentFamilies { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HistoricalFamilies { get; } = new(StringComparer.Ordinal);
    public AssessmentSnapshot? Assessment { get; set; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class SnapshotNode
{
    public required string Digest { get; init; }
    public required NodeSemantics Semantics { get; init; }
    public required string Label { get; init; }
    public string? AdvertisedCountry { get; init; }
    public required string FamilyId { get; init; }
    public required string ArtifactId { get; init; }
    public bool PolicyBlocked { get; init; }
    public string? ReasonCode { get; init; }
}

public sealed class SnapshotCommit
{
    public required string ArtifactId { get; init; }
    public required string FamilyId { get; init; }
    public required string ContentHash { get; init; }
    public required bool Complete { get; init; }
    public required DateTimeOffset NowUtc { get; init; }
    public required IReadOnlyList<SnapshotNode> Nodes { get; init; }
}

public interface ICatalogue
{
    long NetworkEpoch { get; }
    void SetNetworkEpoch(long epoch);
    ProductSettings Settings { get; set; }
    IReadOnlyList<CatalogueNode> Nodes { get; }
    void ApplySnapshot(SnapshotCommit commit);
    void ApplyAssessment(string nodeId, AssessmentSnapshot assessment);
    int EvictOverflow(DateTimeOffset nowUtc);
    bool TrySetFavorite(string nodeId, bool favorite);
    bool TrySetExcluded(string nodeId, bool excluded);
    void SetActiveNode(string? nodeId);
    IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context);
}

public sealed class MemoryCatalogue : ICatalogue
{
    private readonly List<CatalogueNode> _nodes = [];
    private long _epoch = 1;

    public long NetworkEpoch => _epoch;
    public ProductSettings Settings { get; set; } = new();
    public IReadOnlyList<CatalogueNode> Nodes => _nodes;

    public void SetNetworkEpoch(long epoch)
    {
        _epoch = epoch;
    }

    public void ApplySnapshot(SnapshotCommit commit)
    {
        if (!commit.Complete)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in commit.Nodes)
        {
            seen.Add(item.Digest);
            var existing = _nodes.FirstOrDefault(node => node.Digest == item.Digest);
            if (existing is null)
            {
                existing = new CatalogueNode
                {
                    NodeId = Guid.NewGuid().ToString("N"),
                    Digest = item.Digest,
                    Semantics = item.Semantics,
                    Label = item.Label,
                    AdvertisedCountry = item.AdvertisedCountry,
                    FirstSeenUtc = commit.NowUtc,
                    LastSeenUtc = commit.NowUtc,
                    Assessment = new AssessmentSnapshot
                    {
                        Digest = item.Digest,
                        NetworkEpoch = _epoch,
                        Health = HealthState.Pending,
                    },
                };
                _nodes.Add(existing);
            }
            else
            {
                existing.Label = item.Label;
                existing.AdvertisedCountry = item.AdvertisedCountry;
                existing.LastSeenUtc = commit.NowUtc;
                if (existing.Assessment is not null && existing.Assessment.Digest != item.Digest)
                {
                    existing.Assessment = new AssessmentSnapshot
                    {
                        Digest = item.Digest,
                        NetworkEpoch = _epoch,
                        Health = HealthState.Pending,
                    };
                }
            }

            existing.ArtifactFamilies[commit.ArtifactId] = commit.FamilyId;
            if (item.PolicyBlocked)
            {
                existing.PolicyReason = item.ReasonCode;
            }

            RebuildFamilies(existing);
        }

        foreach (var node in _nodes)
        {
            if (node.ArtifactFamilies.TryGetValue(commit.ArtifactId, out var family) &&
                family == commit.FamilyId &&
                !seen.Contains(node.Digest))
            {
                node.ArtifactFamilies.Remove(commit.ArtifactId);
                RebuildFamilies(node);
            }
        }
    }

    public void Restore(long epoch, ProductSettings settings, IReadOnlyList<CatalogueNode> nodes)
    {
        _epoch = epoch;
        Settings = settings;
        _nodes.Clear();
        _nodes.AddRange(nodes);
    }

    public bool TrySetFavorite(string nodeId, bool favorite)
    {
        var node = _nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node is null)
        {
            return false;
        }

        node.Favorite = favorite;
        return true;
    }

    public bool TrySetExcluded(string nodeId, bool excluded)
    {
        var node = _nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node is null)
        {
            return false;
        }

        node.Excluded = excluded;
        return true;
    }

    public void SetActiveNode(string? nodeId)
    {
        foreach (var node in _nodes)
        {
            node.ActiveSession = nodeId is not null && node.NodeId == nodeId;
        }
    }

    public static void RebuildFamilies(CatalogueNode node)
    {
        var next = node.ArtifactFamilies.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var family in node.CurrentFamilies)
        {
            if (!next.Contains(family))
            {
                node.HistoricalFamilies.Add(family);
            }
        }

        node.CurrentFamilies.Clear();
        foreach (var family in next)
        {
            node.CurrentFamilies.Add(family);
            node.HistoricalFamilies.Remove(family);
        }
    }

    public void ApplyAssessment(string nodeId, AssessmentSnapshot assessment)
    {
        var node = _nodes.FirstOrDefault(item => item.NodeId == nodeId);
        if (node is null || !string.Equals(node.Digest, assessment.Digest, StringComparison.Ordinal))
        {
            return;
        }

        if (assessment.NetworkEpoch != _epoch)
        {
            return;
        }

        node.Assessment = assessment;
    }

    public int EvictOverflow(DateTimeOffset nowUtc)
    {
        var overflow = _nodes.Count - ProductLimits.MaxRetainedCandidates;
        var victims = RetentionPolicy.EvictOrder(_nodes.Select(node => new EvictionCandidate
        {
            NodeId = node.NodeId,
            Active = node.ActiveSession,
            Favorite = node.Favorite,
            Failed = node.Assessment?.Health == HealthState.Failed,
            CurrentUpstream = node.CurrentFamilies.Count > 0,
            LastSuccessUtc = node.Assessment?.LastSuccessUtc,
        }), overflow);
        _nodes.RemoveAll(node => victims.Contains(node.NodeId));
        return victims.Count;
    }

    public IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context)
    {
        var disabled = context.DisabledFamilies.Count > 0
            ? context.DisabledFamilies
            : Settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal);
        return _nodes.Where(node => IsEligible(node, context with { DisabledFamilies = disabled })).ToArray();
    }

    public static bool IsEligible(CatalogueNode node, EligibilityContext context)
    {
        var allowedCurrent = node.CurrentFamilies.Any(family => !context.DisabledFamilies.Contains(family));
        var allowedHistorical = node.HistoricalFamilies.Any(family => !context.DisabledFamilies.Contains(family));
        var decision = Eligibility.Evaluate(node.Semantics, node.Assessment, context with
        {
            OwnerExcluded = node.Excluded,
            SourceEnabled = context.SourceEnabled && (allowedCurrent || allowedHistorical),
        });
        return decision.Eligible;
    }
}
