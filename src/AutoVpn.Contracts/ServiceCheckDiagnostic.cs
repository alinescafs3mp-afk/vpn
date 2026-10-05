using System.Text.Json.Serialization;

namespace AutoVpn.Contracts;

// Local diagnostics only. These fields are never accepted from the pipe server.
[JsonConverter(typeof(JsonStringEnumConverter<ServiceCheckStage>))]
public enum ServiceCheckStage
{
    None, OpenManager, OpenService, ConfigurationBefore, StatusBefore, ConnectPipe,
    PipeProcess, OpenProcess, ProcessImage, ProcessBefore, SendRequest, ReceiveReply,
    ProcessAfter, ConfigurationAfter, ValidateReply, Complete,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceCheckFailure>))]
public enum ServiceCheckFailure
{
    None, NativeCall, ConfigurationSize, ServiceType, ServiceAccount, ServiceExecutable,
    InvalidProcessId, PipeProcessMismatch, ProcessAccess, ProcessImageQuery,
    ProcessImageMismatch, ProcessChanged, ProcessNotLive, ReplyMismatch,
    DeadlineExceeded, AccessDenied, InvalidFrame, TransportIo, InvalidState, UnexpectedCancellation,
}

public sealed record ServiceCheckDiagnostic(ServiceCheckStage Stage, ServiceCheckFailure Failure,
    int? NativeErrorCode = null, bool RequestMayHaveBeenSent = false);
