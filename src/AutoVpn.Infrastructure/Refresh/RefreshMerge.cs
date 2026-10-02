using AutoVpn.Application;
using AutoVpn.Infrastructure.Import;

namespace AutoVpn.Infrastructure.Refresh;

public sealed record IngestArtifact
{
    public required string ArtifactId { get; init; }
    public required string FamilyId { get; init; }
    public required bool Enabled { get; init; }
    public bool NotModified { get; init; }
    public bool FetchFailed { get; init; }
    public string? Text { get; init; }
    public string? ContentHash { get; init; }
}

public sealed record IngestReport
{
    public required IReadOnlyList<ImportBatch> Batches { get; init; }
    public int PublishedPending { get; init; }
    public int RetainedMissing { get; init; }
    public bool AnyFetchFailed { get; init; }
    public bool Balanced { get; init; }
    public bool RefetchRequired { get; init; }
}

public static class RefreshMerge
{
    public static IngestReport Ingest(ICatalogue catalogue, IEnumerable<IngestArtifact> artifacts, DateTimeOffset nowUtc, bool allowInsecure)
    {
        var batches = new List<ImportBatch>();
        var pending = 0;
        var retained = 0;
        var failed = false;
        var balanced = true;
        var refetch = false;
        foreach (var artifact in artifacts)
        {
            if (!artifact.Enabled)
            {
                continue;
            }

            if (artifact.NotModified)
            {
                var represented = catalogue.Nodes.Any(node => node.ArtifactFamilies.ContainsKey(artifact.ArtifactId));
                refetch |= !represented;
                continue;
            }

            if (artifact.FetchFailed || artifact.Text is null)
            {
                failed = true;
                continue;
            }

            var batch = SubscriptionImporter.Import(artifact.Text, new ImportOptions { AllowInsecureCertificates = allowInsecure });
            batches.Add(batch);
            var publishable = batch.DocumentValid && batch.Balanced && !batch.LimitExceeded &&
                              (batch.EmptyValidDocument || batch.Pending + batch.PolicyBlocked > 0);
            balanced &= publishable;
            if (!publishable)
            {
                continue;
            }

            var nodes = batch.Records
                .Where(record => record.Semantics is not null && record.Digest is not null &&
                                 record.Disposition is RecordDisposition.Pending or RecordDisposition.PolicyBlocked)
                .Select(record => new SnapshotNode
                {
                    Digest = record.Digest!,
                    Semantics = record.Semantics!,
                    Label = record.DisplayName,
                    AdvertisedCountry = record.AdvertisedCountry,
                    FamilyId = artifact.FamilyId,
                    ArtifactId = artifact.ArtifactId,
                    PolicyBlocked = record.Disposition == RecordDisposition.PolicyBlocked,
                    ReasonCode = record.ReasonCode,
                })
                .ToArray();
            var before = catalogue.Nodes.Where(node => node.CurrentFamilies.Contains(artifact.FamilyId)).Select(node => node.Digest).ToHashSet(StringComparer.Ordinal);
            catalogue.ApplySnapshot(new SnapshotCommit
            {
                ArtifactId = artifact.ArtifactId,
                FamilyId = artifact.FamilyId,
                ContentHash = artifact.ContentHash ?? "",
                Complete = true,
                NowUtc = nowUtc,
                Nodes = nodes,
            });
            retained += before.Count(digest => catalogue.Nodes.All(node => node.Digest != digest || !node.CurrentFamilies.Contains(artifact.FamilyId)));
            pending += nodes.Count(node => !node.PolicyBlocked);
        }

        return new IngestReport
        {
            Batches = batches,
            PublishedPending = pending,
            RetainedMissing = retained,
            AnyFetchFailed = failed,
            Balanced = balanced,
            RefetchRequired = refetch,
        };
    }
}
