using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed class IpcDispatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IpcResponse> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<IpcResponse>> _inflight = new(StringComparer.Ordinal);
    private string? _ownerSid;
    private int? _ownerSession;

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

        TaskCompletionSource<IpcResponse>? wait = null;
        TaskCompletionSource<IpcResponse>? created = null;
        lock (_gate)
        {
            if (_ownerSid is null)
            {
                _ownerSid = caller.Sid;
                _ownerSession = caller.SessionId;
            }
            else if (!string.Equals(_ownerSid, caller.Sid, StringComparison.Ordinal) || _ownerSession != caller.SessionId)
            {
                return Fail(request, "OWNER", "Сессия не владеет службой.");
            }

            if (_results.TryGetValue(request.RequestId, out var prior))
            {
                return prior;
            }

            if (_inflight.TryGetValue(request.RequestId, out wait))
            {
            }
            else if (_results.Count >= ProductLimits.IpcIdempotencyEntries)
            {
                return Fail(request, "REPLAY_WINDOW", "Журнал идемпотентности заполнен. Повтор не выполняется заново.");
            }
            else
            {
                created = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight[request.RequestId] = created;
            }
        }

        if (wait is not null)
        {
            return wait.Task.GetAwaiter().GetResult();
        }

        try
        {
            var response = handler(request);
            lock (_gate)
            {
                _results[request.RequestId] = response;
                _inflight.Remove(request.RequestId);
            }

            created!.TrySetResult(response);
            return response;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _inflight.Remove(request.RequestId);
            }

            created!.TrySetException(ex);
            throw;
        }
    }

    public void ResetOwner()
    {
        lock (_gate)
        {
            _ownerSid = null;
            _ownerSession = null;
            _results.Clear();
            _inflight.Clear();
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
