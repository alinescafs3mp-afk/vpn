namespace AutoVpn.Domain;

/// <summary>An observation is valid only for the one locally reserved attempt that requested it.</summary>
public sealed record ProbeAttemptContext(
    string AttemptId,
    string NodeId,
    string Digest,
    long NetworkEpoch,
    int PolicyRevision,
    string TargetUri,
    SelectionPurpose Purpose);
