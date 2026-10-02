using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed class IpcDispatcher
{
    private readonly int _capacity;
    private readonly Queue<string> _seen = new();
    private readonly HashSet<string> _seenSet = new(StringComparer.Ordinal);
    private string? _ownerSid;
    private int? _ownerSession;

    public IpcDispatcher(int replayCapacity = 64)
    {
        _capacity = replayCapacity;
    }

    public IpcResponse Dispatch(IpcRequest request, CallerIdentity caller, Func<IpcRequest, IpcResponse> handler)
    {
        if (caller.IsRemotePipe)
        {
            return Fail(request, "REMOTE_PIPE", "Удалённый канал отклонён.");
        }

        if (request.ProtocolVersion != ProductLimits.IpcProtocolVersion)
        {
            return Fail(request, "PROTOCOL", "Версия протокола не поддерживается.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80)
        {
            return Fail(request, "REQUEST_ID", "Некорректный идентификатор запроса.");
        }

        if (!IpcOperations.All.Contains(request.Operation))
        {
            return Fail(request, "UNKNOWN_OPERATION", "Операция не разрешена.");
        }

        if (ForbiddenPayload.ContainsForbidden(request.Payload, out _))
        {
            return Fail(request, "FORBIDDEN_FIELD", "Запрос содержит запрещённое поле.");
        }

        if (_seenSet.Contains(request.RequestId))
        {
            return Fail(request, "REPLAY", "Повтор запроса отклонён.");
        }

        if (_ownerSid is null)
        {
            _ownerSid = caller.Sid;
            _ownerSession = caller.SessionId;
        }
        else if (!string.Equals(_ownerSid, caller.Sid, StringComparison.Ordinal) || _ownerSession != caller.SessionId)
        {
            return Fail(request, "OWNER", "Сессия не владеет службой.");
        }

        Remember(request.RequestId);
        return handler(request);
    }

    public void ResetOwner()
    {
        _ownerSid = null;
        _ownerSession = null;
    }

    private void Remember(string id)
    {
        _seen.Enqueue(id);
        _seenSet.Add(id);
        while (_seen.Count > _capacity)
        {
            _seenSet.Remove(_seen.Dequeue());
        }
    }

    private static IpcResponse Fail(IpcRequest request, string code, string message)
    {
        return new IpcResponse
        {
            RequestId = request.RequestId ?? "",
            Ok = false,
            ErrorCode = code,
            Message = message,
        };
    }
}
