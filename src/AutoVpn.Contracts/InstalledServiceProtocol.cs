namespace AutoVpn.Contracts;

// Separate from the development broker: this endpoint cannot receive nodes or credentials.
public static class InstalledServiceProtocol
{
    public const string ServiceName = "AutoVPN.Broker";
    public const string PipeName = "AutoVPN.Broker.Status.v1";
    public const int Version = 1;
    public const int MaxFrameBytes = 4096;
    public const int TimeoutMs = 2000;
    public const string Mode = "ControlPlaneOnly";

    public static bool ValidId(string? value) => value is { Length: 32 } &&
        Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty &&
        value == id.ToString("N");

    public static ServiceStatusReply Answer(ServiceStatusRequest request, string instanceId, int processId, long uptimeSeconds)
    {
        var error = !ValidId(request.RequestId) ? "REQUEST_ID" :
            request.ProtocolVersion != Version ? "PROTOCOL_VERSION" :
            request.Operation != "GetStatus" ? "OPERATION_NOT_SUPPORTED" : null;
        return new ServiceStatusReply
        {
            ProtocolVersion = Version, ServiceName = ServiceName, Mode = Mode, Phase = "Ready",
            RequestId = ValidId(request.RequestId) ? request.RequestId : "",
            Ok = error is null, ErrorCode = error, InstanceId = instanceId,
            ProcessId = processId, UptimeSeconds = uptimeSeconds,
            CanConnect = false, ProtectionArmed = false, CoreRunning = false,
        };
    }

    public static bool ValidReply(ServiceStatusReply reply, string requestId, int processId) =>
        reply.ProtocolVersion == Version && reply.RequestId == requestId && reply.Ok && reply.ErrorCode is null &&
        reply.ServiceName == ServiceName && reply.Mode == Mode && reply.Phase == "Ready" &&
        ValidId(reply.InstanceId) && processId > 0 && reply.ProcessId == processId && reply.UptimeSeconds >= 0 &&
        !reply.CanConnect && !reply.ProtectionArmed && !reply.CoreRunning;
}

public sealed record ServiceStatusRequest
{
    public required int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string Operation { get; init; }
}

public sealed record ServiceStatusReply
{
    public required int ProtocolVersion { get; init; } = InstalledServiceProtocol.Version;
    public required string RequestId { get; init; }
    public required bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public required string ServiceName { get; init; } = InstalledServiceProtocol.ServiceName;
    public required string Mode { get; init; } = InstalledServiceProtocol.Mode;
    public required string Phase { get; init; } = "Ready";
    public required string InstanceId { get; init; }
    public required int ProcessId { get; init; }
    public required long UptimeSeconds { get; init; }
    public required bool CanConnect { get; init; } = false;
    public required bool ProtectionArmed { get; init; } = false;
    public required bool CoreRunning { get; init; } = false;
}

public sealed record InstalledServiceCheck(string State, ServiceStatusReply? Reply = null)
{
    public string Message => State switch
    {
        "Ready" => "Служба Windows работает, подлинность канала проверена. Подключение VPN ещё недоступно.",
        "NotInstalled" => "Служба Windows не установлена.",
        "Stopped" => "Служба Windows остановлена или ещё запускается.",
        "AccessDenied" => "Нет доступа к службе: проверьте назначенного при установке пользователя.",
        "AuthenticationFailed" => "Подлинность канала службы не подтверждена. Данные не отправлены.",
        "ProtocolError" => "Ответ службы не соответствует протоколу.",
        "Timeout" => "Служба не ответила в отведённое время.",
        "Unsupported" => "Установленная служба доступна только в Windows.",
        _ => "Служба Windows недоступна.",
    };
}
