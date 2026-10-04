namespace AutoVpn.Contracts;

// This deliberately small protocol is private to one launched host and one desktop process.
// No arbitrary YAML, executable path, target URL, filesystem path or shell command crosses it.
public sealed record LiveSessionRequest
{
    public long Sequence { get; init; }
    public string Operation { get; init; } = "Status";
    public NodeWire? Node { get; init; }
    public bool Tun { get; init; }
    public bool AllowUnprotectedTun { get; init; }
    public bool AllowInsecureProxy { get; init; }
    public bool LanAccess { get; init; }
}

public sealed record LiveSessionSnapshot
{
    public string Phase { get; init; } = "Stopped";
    public string? Error { get; init; }
    public string? NodeId { get; init; }
    public bool Tun { get; init; }
    public bool KillSwitchArmed { get; init; } // Never true in the V3 preview.
    public int? ProcessId { get; init; }
    public int? ProxyPort { get; init; }
    public string? DeviceName { get; init; }
    public long DownloadBytes { get; init; }
    public long UploadBytes { get; init; }
    public DateTimeOffset? VerifiedUtc { get; init; }
    public int? HttpsLatencyMs { get; init; }
    public bool HasOwnedProcess { get; init; }
}
