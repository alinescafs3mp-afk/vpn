using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

/// <summary>Only a rejected, side-effect-free stale revision may be retried automatically.</summary>
public static class DisconnectRetry
{
    public const int MaximumAttempts = 3;

    public static bool ShouldRetry(IpcRequest request, IpcResponse response, bool snapshotAccepted, int attempts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        return attempts is > 0 and < MaximumAttempts && request.Operation == IpcOperations.Disconnect &&
            snapshotAccepted && !response.Ok && response.ErrorCode == ReasonCodes.StaleRevision &&
            response.ProtocolVersion == request.ProtocolVersion && response.RequestId == request.RequestId &&
            response.Snapshot is { } snapshot && response.StateRevision == snapshot.Revision &&
            response.StateSequence == snapshot.Sequence && snapshot.Revision > request.ExpectedStateRevision;
    }
}
