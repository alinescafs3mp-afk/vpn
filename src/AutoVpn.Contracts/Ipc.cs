using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoVpn.Contracts;

public static class IpcOperations
{
    public const string GetSnapshot = "GetSnapshot";
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    public const string ApplyRuntimeSet = "ApplyRuntimeSet";
    public const string ReportHealth = "ReportHealth";
    public const string RecoverOwned = "RecoverOwned";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        GetSnapshot, Connect, Disconnect, ApplyRuntimeSet, ReportHealth, RecoverOwned,
    };

    public static bool ChangesState(string operation)
    {
        return operation is Connect or Disconnect or ApplyRuntimeSet or ReportHealth or RecoverOwned;
    }
}

public sealed class IpcRequest
{
    public int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public long ExpectedStateRevision { get; init; }
    public required string Operation { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed class IpcResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public required string RequestId { get; init; }
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public long StateRevision { get; init; }
    public long StateSequence { get; init; }
    public BrokerSnapshot? Snapshot { get; init; }
}

public sealed class BrokerSnapshot
{
    public long Revision { get; init; }
    public long Sequence { get; init; }
    public long Generation { get; init; }
    public string Phase { get; init; } = "Disconnected";
    public string? ActiveNodeId { get; init; }
    public bool ProtectionArmed { get; init; }
    public string? BlockReason { get; init; }
    public bool CoreRunning { get; init; }
    public string? CoreVersion { get; init; }
    public long SessionBytesDown { get; init; }
    public long SessionBytesUp { get; init; }
    public double? LiveDownBytesPerSecond { get; init; }
    public double? LiveUpBytesPerSecond { get; init; }
    public string? CountryLabel { get; init; }
    public string? ServerLabel { get; init; }
    public int? LatencyMs { get; init; }
    public DateTimeOffset? LastCheckUtc { get; init; }
    public int StandbyCount { get; init; }
    public string? OwnerSid { get; init; }
}

public sealed class NodeWire
{
    public required string NodeId { get; init; }
    public required string Digest { get; init; }
    public required string Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? UserId { get; init; }
    public string? Password { get; init; }
    public string? Encryption { get; init; }
    public int? AlterId { get; init; }
    public string? Flow { get; init; }
    public string? Security { get; init; }
    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? SpiderX { get; init; }
    public string[]? Alpn { get; init; }
    public string? Transport { get; init; }
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? ServiceName { get; init; }
    public string? HeaderType { get; init; }
    public string? Plugin { get; init; }
    public string? PluginOpts { get; init; }
    public bool? Udp { get; init; }
    public bool SkipCertVerify { get; init; }
    public string? Congestion { get; init; }
    public string? UdpRelayMode { get; init; }
    public string? Obfs { get; init; }
    public string? ObfsPassword { get; init; }
    public string? PacketEncoding { get; init; }
    public string? Up { get; init; }
    public string? Down { get; init; }
    public string? HopPorts { get; init; }
    public string DisplayLabel { get; init; } = "";
    public string? AdvertisedCountry { get; init; }
    public string? EndpointKey { get; init; }
}

public sealed class ConnectPayload
{
    public required string NodeId { get; init; }
    public required string Digest { get; init; }
    public long NetworkEpoch { get; init; }
    public bool ProtectionRequired { get; init; } = true;
    public bool LanAccess { get; init; } = true;
    public NodeWire? Node { get; init; }
    public NodeWire[] Standbys { get; init; } = [];
}

public sealed class DisconnectPayload
{
    public long Generation { get; init; }
}

public sealed class HealthPayload
{
    public required string FailureKind { get; init; }
    public int ConsecutiveFailures { get; init; }
    public long NetworkEpoch { get; init; }
}

public sealed class CallerIdentity
{
    public required string Sid { get; init; }
    public int SessionId { get; init; }
    public bool IsRemotePipe { get; init; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
