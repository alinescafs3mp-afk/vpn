using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed class CatalogueNode
{
    public required string NodeId { get; init; }
    public required string Digest { get; set; }
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
    public long ProbePublication;
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

public sealed record ArtifactSnapshot(string ArtifactId, string FamilyId, string ContentHash,
    IReadOnlyList<string> Digests, DateTimeOffset AcceptedUtc);

public interface ICatalogue
{
    object SyncRoot => this;
    IReadOnlyList<ArtifactSnapshot> ArtifactSnapshots => [];
    bool HasCommittedSnapshot(string artifactId, string? contentHash = null) => false;
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
    private readonly Dictionary<string, ArtifactSnapshot> _snapshots = new(StringComparer.Ordinal);
    public IReadOnlyList<ArtifactSnapshot> ArtifactSnapshots
    {
        get { lock (SyncRoot) return _snapshots.Values.ToArray(); }
    }

    public bool HasCommittedSnapshot(string artifactId, string? contentHash = null)
    {
        lock (SyncRoot)
        {
            if (!_snapshots.TryGetValue(artifactId, out var snapshot) ||
                (contentHash is not null && snapshot.ContentHash != contentHash)) return false;
            var current = _nodes.Where(n => n.ArtifactFamilies.ContainsKey(artifactId))
                .Select(n => n.Digest).ToHashSet(StringComparer.Ordinal);
            return current.SetEquals(snapshot.Digests);
        }
    }

    public void RestoreSnapshots(IEnumerable<ArtifactSnapshot> snapshots)
    {
        lock (SyncRoot)
        {
            _snapshots.Clear();
            foreach (var item in snapshots)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.ArtifactId) || item.Digests is null ||
                    item.ContentHash is null || item.FamilyId is null || item.Digests.Count > ProductLimits.MaxRetainedCandidates)
                    throw new InvalidDataException("Invalid stored artifact snapshot.");
                _snapshots[item.ArtifactId] = item with { Digests = Array.AsReadOnly(item.Digests.ToArray()) };
            }
        }
    }
    public object SyncRoot { get; } = new();
    private long _epoch = 1;
    private ProductSettings _settings = new();

    public long NetworkEpoch => _epoch;

    public ProductSettings Settings
    {
        get => _settings;
        set
        {
            lock (SyncRoot)
            {
                var error = value.Validate();
                if (error is not null)
                {
                    throw new InvalidOperationException(error);
                }

                var allowChanged = _settings.AllowInsecureCertificates != value.AllowInsecureCertificates;
                _settings = value;
                if (allowChanged)
                {
                    ReconcileCertificatePolicy();
                }

            }
        }
    }

    private void ReconcileCertificatePolicy()
    {
        foreach (var node in _nodes)
        {
            if (!node.Semantics.SkipCertVerify)
            {
                continue;
            }

            if (_settings.AllowInsecureCertificates)
            {
                if (node.PolicyReason == ReasonCodes.CertVerificationDisabled)
                {
                    node.PolicyReason = null;
                }
            }
            else if (node.PolicyReason is null)
            {
                node.PolicyReason = ReasonCodes.CertVerificationDisabled;
            }
        }
    }

    public IReadOnlyList<CatalogueNode> Nodes => _nodes;

    public void SetNetworkEpoch(long epoch)
    {
        lock (SyncRoot)
        {
            _epoch = epoch;

        }
    }

    public void ApplySnapshot(SnapshotCommit commit)
    {
        lock (SyncRoot)
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
                existing.PolicyReason = item.PolicyBlocked ? item.ReasonCode : null;
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
            _snapshots[commit.ArtifactId] = new ArtifactSnapshot(commit.ArtifactId, commit.FamilyId,
                commit.ContentHash, Array.AsReadOnly(commit.Nodes.Select(n => n.Digest)
                    .Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToArray()), commit.NowUtc);

        }
    }

    public void Restore(long epoch, ProductSettings settings, IReadOnlyList<CatalogueNode> nodes)
    {
        lock (SyncRoot)
        {
            _epoch = epoch;
            Settings = settings;
            _nodes.Clear();
            _nodes.AddRange(nodes);

        }
    }

    public MemoryCatalogue Copy()
    {
        lock (SyncRoot)
        {
            var copy = new MemoryCatalogue();
            copy._epoch = _epoch;
            copy._settings = _settings;
            foreach (var node in _nodes)
            {
                copy._nodes.Add(CloneNode(node));
            }

            copy.RestoreSnapshots(_snapshots.Values);
            return copy;

        }
    }

    public static bool ReconcileStoredDigest(CatalogueNode node)
    {
        var currentJson = CanonicalIdentity.CanonicalJson(node.Semantics);
        var current = CanonicalIdentity.Digest(node.Semantics);
        if (node.Digest == current && (node.Assessment is null || node.Assessment.Digest == current))
        {
            return false;
        }

        var previousVersion = ProductLimits.CanonicalizerVersion - 1;
        var previousJson = CanonicalIdentity.CanonicalJson(node.Semantics, previousVersion);
        var previous = CanonicalIdentity.Digest(node.Semantics, previousVersion);
        var sameEffective = string.Equals(currentJson, previousJson, StringComparison.Ordinal);
        var sameBytes = node.Digest == previous;
        node.Digest = current;
        if (sameBytes && sameEffective && node.Assessment is { } assessment && assessment.Digest == previous)
        {
            node.Assessment = assessment with { Digest = current };
        }
        else
        {
            node.Assessment = null;
        }

        return true;
    }

    private static CatalogueNode CloneNode(CatalogueNode node)
    {
        var clone = new CatalogueNode
        {
            NodeId = node.NodeId,
            Digest = node.Digest,
            Semantics = node.Semantics,
            Label = node.Label,
            AdvertisedCountry = node.AdvertisedCountry,
            Favorite = node.Favorite,
            Excluded = node.Excluded,
            ActiveSession = node.ActiveSession,
            PolicyReason = node.PolicyReason,
            Assessment = node.Assessment,
            ProbePublication = node.ProbePublication,
            FirstSeenUtc = node.FirstSeenUtc,
            LastSeenUtc = node.LastSeenUtc,
        };
        foreach (var pair in node.ArtifactFamilies)
        {
            clone.ArtifactFamilies[pair.Key] = pair.Value;
        }

        foreach (var family in node.CurrentFamilies)
        {
            clone.CurrentFamilies.Add(family);
        }

        foreach (var family in node.HistoricalFamilies)
        {
            clone.HistoricalFamilies.Add(family);
        }

        return clone;
    }

    public bool TrySetFavorite(string nodeId, bool favorite)
    {
        lock (SyncRoot)
        {
            var node = _nodes.FirstOrDefault(item => item.NodeId == nodeId);
            if (node is null)
            {
                return false;
            }

            node.Favorite = favorite;
            return true;

        }
    }

    public bool TrySetExcluded(string nodeId, bool excluded)
    {
        lock (SyncRoot)
        {
            var node = _nodes.FirstOrDefault(item => item.NodeId == nodeId);
            if (node is null)
            {
                return false;
            }

            node.Excluded = excluded;
            return true;

        }
    }

    public void SetActiveNode(string? nodeId)
    {
        lock (SyncRoot)
        {
            foreach (var node in _nodes)
            {
                node.ActiveSession = nodeId is not null && node.NodeId == nodeId;
            }

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
        lock (SyncRoot)
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
    }

    public int EvictOverflow(DateTimeOffset nowUtc)
    {
        lock (SyncRoot)
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
    }

    public IReadOnlyList<CatalogueNode> Eligible(EligibilityContext context)
    {
        lock (SyncRoot)
        {
            var disabled = context.DisabledFamilies.Count > 0
                ? context.DisabledFamilies
                : Settings.DisabledFamilyIds.ToHashSet(StringComparer.Ordinal);
            return _nodes.Where(node => IsEligible(node, context with { DisabledFamilies = disabled })).ToArray();

        }
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
