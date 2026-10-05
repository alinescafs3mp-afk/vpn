using System.ComponentModel;
using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.WindowsService;

internal readonly record struct ServiceProcessIdentity(uint ServiceType, uint State, uint ProcessId);

// The native session owns every handle until the exchange and final validation end.
// This internal seam lets tests assert ordering and fail-closed behavior without
// pretending to execute SCM or to authenticate a real Windows process on Linux.
internal interface IInstalledServiceSession : IDisposable
{
    void OpenManager();
    void OpenService();
    void ValidateConfiguration();
    ServiceProcessIdentity ReadStatus();
    Task ConnectAsync(CancellationToken token);
    uint GetPipeProcessId();
    void OpenProcess(uint processId);
    void ValidateProcessImage();
    bool IsProcessRunning();
    Stream Pipe { get; }
}

internal sealed class ServiceIdentityException(ServiceCheckFailure failure, int? nativeErrorCode = null)
    : Exception("SERVICE_IDENTITY_NOT_CONFIRMED")
{
    internal ServiceCheckFailure Failure { get; } = failure;
    internal int? NativeErrorCode { get; } = nativeErrorCode;
}

internal static class InstalledServiceQuery
{
    internal static async Task<InstalledServiceCheck> RunAsync(Func<IInstalledServiceSession> factory,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(InstalledServiceProtocol.TimeoutMs);
        var stage = ServiceCheckStage.None;
        var requestMayHaveBeenSent = false;
        InstalledServiceCheck Result(string state, ServiceCheckFailure failure = ServiceCheckFailure.None,
            int? nativeErrorCode = null, ServiceStatusReply? reply = null)
        {
            token.ThrowIfCancellationRequested();
            return new(state, reply)
            {
                Diagnostic = new(stage, failure, nativeErrorCode, requestMayHaveBeenSent),
                DiagnosticCode = failure == ServiceCheckFailure.None ? null : LegacyCode(stage, failure),
                NativeErrorCode = nativeErrorCode,
            };
        }
        void Enter(ServiceCheckStage next) { stage = next; budget.Token.ThrowIfCancellationRequested(); }
        try
        {
            using var session = factory();
            Enter(ServiceCheckStage.OpenManager); session.OpenManager();
            Enter(ServiceCheckStage.OpenService); session.OpenService();
            Enter(ServiceCheckStage.ConfigurationBefore); session.ValidateConfiguration();
            Enter(ServiceCheckStage.StatusBefore);
            var before = session.ReadStatus();
            if (before.ServiceType != 0x10) throw new ServiceIdentityException(ServiceCheckFailure.ServiceType);
            if (before.State != 4) return Result(InstalledServiceProtocol.NonRunningCheck(before.State).State);
            if (before.ProcessId is 0 or > int.MaxValue) return Result("Unavailable", ServiceCheckFailure.InvalidProcessId);
            Enter(ServiceCheckStage.ConnectPipe); await session.ConnectAsync(budget.Token).ConfigureAwait(false);
            Enter(ServiceCheckStage.PipeProcess);
            var pipePid = session.GetPipeProcessId();
            if (pipePid != before.ProcessId) throw new ServiceIdentityException(ServiceCheckFailure.PipeProcessMismatch);
            Enter(ServiceCheckStage.OpenProcess); session.OpenProcess(pipePid);
            Enter(ServiceCheckStage.ProcessImage); session.ValidateProcessImage();
            Enter(ServiceCheckStage.ProcessBefore); DemandLive(session, pipePid);
            var id = Guid.NewGuid().ToString("N");
            var frame = ServiceStatusFrames.Encode(new ServiceStatusRequest
                { ProtocolVersion = InstalledServiceProtocol.Version, RequestId = id, Operation = "GetStatus" });
            Enter(ServiceCheckStage.SendRequest);
            // A failed write can still have sent a prefix. Never claim "nothing sent" afterwards.
            requestMayHaveBeenSent = true;
            await session.Pipe.WriteAsync(frame, budget.Token).ConfigureAwait(false);
            Enter(ServiceCheckStage.ReceiveReply);
            var reply = await ServiceStatusFrames.ReadAsync<ServiceStatusReply>(session.Pipe, budget.Token).ConfigureAwait(false);
            Enter(ServiceCheckStage.ProcessAfter); DemandLive(session, pipePid);
            Enter(ServiceCheckStage.ConfigurationAfter); session.ValidateConfiguration();
            Enter(ServiceCheckStage.ValidateReply);
            if (!InstalledServiceProtocol.ValidReply(reply, id, checked((int)pipePid)))
                return Result("ProtocolError", ServiceCheckFailure.ReplyMismatch);
            Enter(ServiceCheckStage.Complete);
            return Result("Ready", reply: reply);
        }
        catch (ServiceIdentityException ex)
        {
            return Result(requestMayHaveBeenSent ? "Unavailable" : "AuthenticationFailed", ex.Failure, ex.NativeErrorCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { return Result("Timeout", ServiceCheckFailure.DeadlineExceeded); }
        catch (OperationCanceledException) { return Result("Unavailable", ServiceCheckFailure.UnexpectedCancellation); }
        catch (TimeoutException) { return Result("Timeout", ServiceCheckFailure.DeadlineExceeded); }
        catch (UnauthorizedAccessException) { return Result("AccessDenied", ServiceCheckFailure.AccessDenied); }
        catch (Win32Exception ex)
        {
            return Result(ex.NativeErrorCode == 1060 && stage == ServiceCheckStage.OpenService ? "NotInstalled" :
                ex.NativeErrorCode == 5 ? "AccessDenied" : "Unavailable", ServiceCheckFailure.NativeCall, ex.NativeErrorCode);
        }
        catch (InvalidDataException) { return Result("ProtocolError", ServiceCheckFailure.InvalidFrame); }
        catch (IOException) { return Result("Unavailable", ServiceCheckFailure.TransportIo); }
        catch (InvalidOperationException) { return Result("Unavailable", ServiceCheckFailure.InvalidState); }
    }

    private static string LegacyCode(ServiceCheckStage stage, ServiceCheckFailure failure) => stage switch
    {
        ServiceCheckStage.ConfigurationBefore or ServiceCheckStage.ConfigurationAfter => "SCM_CONFIGURATION",
        ServiceCheckStage.PipeProcess => failure == ServiceCheckFailure.PipeProcessMismatch ? "PIPE_PROCESS_MISMATCH" : "PIPE_PROCESS_QUERY",
        ServiceCheckStage.OpenProcess => "PROCESS_OPEN",
        ServiceCheckStage.ProcessImage => failure == ServiceCheckFailure.ProcessImageMismatch ? "PROCESS_IMAGE_MISMATCH" : "PROCESS_IMAGE_QUERY",
        ServiceCheckStage.ProcessBefore or ServiceCheckStage.ProcessAfter => "PROCESS_LIVENESS",
        _ => stage.ToString() + "_" + failure.ToString(),
    };

    private static void DemandLive(IInstalledServiceSession session, uint pid)
    {
        var now = session.ReadStatus();
        if (now.ServiceType != 0x10 || now.State != 4 || now.ProcessId != pid)
            throw new ServiceIdentityException(ServiceCheckFailure.ProcessChanged);
        if (!session.IsProcessRunning()) throw new ServiceIdentityException(ServiceCheckFailure.ProcessNotLive);
    }
}
