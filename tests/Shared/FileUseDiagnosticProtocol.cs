using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoVpn.TestSupport;

// Test-only protocol. A phase contains no path, PID, native handle or exception text.
internal static class FileUseDiagnosticProtocol
{
    internal const int MaximumOutputBytes = 4096;
    internal const int MaximumFrames = 16;

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<FileUseFrameKind>(allowIntegerValues: false),
            new JsonStringEnumConverter<FileUsePhase>(allowIntegerValues: false),
        },
    };
}

internal enum FileUseControlMode
{
    Normal,
    TimeoutAfterInput,
    TimeoutBeforeInput,
    TimeoutBeforeQuery,
    InputClosed,
}

internal enum FileUseFrameKind { PHASE, RESULT }

internal enum FileUsePhase
{
    HELPER_STARTED,
    INPUT_RECEIVED,
    INPUT_VALIDATED,
    INPUT_CLOSED,
    RM_START_BEGIN,
    RM_START_RETURNED,
    REGISTER_BEGIN,
    REGISTER_RETURNED,
    QUERY_BEGIN,
    QUERY_RETURNED,
    END_BEGIN,
    END_RETURNED,
    RESULT_READY,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FileUseDiagnosticFrame
{
    public required FileUseFrameKind Kind { get; init; }
    public required int Sequence { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileUsePhase? Phase { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? QueryOrdinal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileUseSnapshot? Snapshot { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FileUseSnapshot
{
    public string State { get; init; } = "QUERY_INCOMPLETE";
    public uint? StartCode { get; init; }
    public uint? RegisterCode { get; init; }
    public uint? QueryCode { get; init; }
    public uint? EndCode { get; init; }
    public int QueryCalls { get; init; }
    public int TotalOwners { get; init; }
    public int TestHostOwners { get; init; }
    public int OwnedChildOwners { get; init; }
    public int OtherOwners { get; init; }
    public bool Truncated { get; init; }
    public bool Incomplete { get; init; } = true;
}
