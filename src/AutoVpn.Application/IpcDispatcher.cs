using System.Globalization;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

/// <summary>
/// Authenticated-peer dispatch with an exact, bounded sequence window. Pipe identity is
/// checked by the transport before this boundary. Opening a lease changes no network state.
/// Completed results are bounded; eviction never makes a consumed sequence executable again.
/// </summary>
public class IpcDispatcher
{
    private const int WindowSize = 4096;
    private const int InflightLimit = 64;
    private readonly object _gate = new();
    private readonly long[] _seenSequences = new long[WindowSize];
    private readonly Dictionary<string, Cached> _completed = new(StringComparer.Ordinal);
    private readonly Queue<string> _readOrder = new();
    private readonly Queue<string> _mutationOrder = new();
    private readonly Queue<string> _safetyOrder = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private string _lease = Guid.NewGuid().ToString("N");
    private string? _ownerSid;
    private int _ownerSession;
    private long _highest;
    private int _busy;

    // Only compatibility for in-process callers. Live pipe sessions use DispatchAsync.
    public IpcResponse Dispatch(IpcRequest request, CallerIdentity caller, Func<IpcRequest, IpcResponse> handler)
        => DispatchAsync(request, caller, r => Task.FromResult(handler(r))).GetAwaiter().GetResult();

    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CallerIdentity caller,
        Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken cancellationToken = default)
    {
        if (caller.IsRemotePipe) return Fail(request, "REMOTE_PIPE", "Удалённый канал отклонён.");
        if (string.IsNullOrWhiteSpace(caller.Sid)) return Fail(request, "OWNER", "Клиент не подтверждён.");
        if (request.ProtocolVersion != ProductLimits.IpcProtocolVersion)
            return Fail(request, "PROTOCOL", "Требуется совместимая версия клиента и службы.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80)
            return Fail(request, "REQUEST_ID", "Некорректный идентификатор запроса.");
        if (!IpcOperations.All.Contains(request.Operation)) return Fail(request, "UNKNOWN_OPERATION", "Операция не разрешена.");
        if (ForbiddenPayload.ContainsForbidden(request.Payload, out _))
            return Fail(request, "FORBIDDEN_FIELD", "Запрос содержит запрещённое поле.");

        var read = request.Operation == IpcOperations.GetSnapshot;
        var safety = request.Operation is IpcOperations.Disconnect or IpcOperations.RecoverOwned;
        var fingerprint = Fingerprint(request);
        Pending pending;
        bool duplicate;
        string lease;
        string key;
        lock (_gate)
        {
            if (_ownerSid is null) { _ownerSid = caller.Sid; _ownerSession = caller.SessionId; }
            if (_ownerSid != caller.Sid || _ownerSession != caller.SessionId)
                return Fail(request, "OWNER", "Сессия не владеет службой.");
            if (request.Operation == IpcOperations.OpenSession)
            {
                RotateLease();
                return new IpcResponse { RequestId = request.RequestId, Ok = true, SessionToken = _lease };
            }
            lease = _lease;
            if (!read && (request.SessionToken != lease || request.CommandSequence <= 0))
                return Fail(request, "SESSION_REQUIRED", "Сначала откройте актуальную управляющую сессию.");
            key = lease + ":" + request.RequestId;
            if (_completed.TryGetValue(key, out var cached))
                return cached.Fingerprint == fingerprint ? cached.Response
                    : Fail(request, ReasonCodes.RequestConflict, "Идентификатор использован с другим содержимым.");
            duplicate = _pending.TryGetValue(key, out pending!);
            if (duplicate)
            {
                if (pending.Fingerprint != fingerprint)
                    return Fail(request, ReasonCodes.RequestConflict, "Ожидающий запрос имеет другое содержимое.");
            }
            else
            {
                if (!read && !safety && _busy >= InflightLimit)
                    return Fail(request, "BUSY", "Слишком много ожидающих операций. Команды безопасности доступны.");
                if (read && _pending.Count >= InflightLimit + 32)
                    return Fail(request, "BUSY", "Слишком много ожидающих запросов состояния.");
                // A separate safety reserve is bounded without a lifetime command cap.
                if (safety && _pending.Count >= InflightLimit + 48)
                    return Fail(request, "BUSY", "Команда безопасности уже ожидает обработки. Повторите запрос.");
                if (!read && !AcceptSequence(request.CommandSequence))
                    return Fail(request, ReasonCodes.ReplayExpired, "Номер команды уже исполнен или устарел.");
                pending = new Pending(fingerprint, !read && !safety,
                    new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
                _pending.Add(key, pending);
                if (pending.Busy) _busy++;
            }
        }
        if (duplicate)
        {
            try { return await pending.Done.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { return Fail(request, "OPERATION_PENDING", "Исходная операция ещё выполняется; повтор не запущен."); }
        }
        try
        {
            var response = await handler(request).ConfigureAwait(false);
            response = response with { SessionToken = lease };
            Finish(key, lease, pending, fingerprint, response, read, safety);
            return response;
        }
        catch (Exception ex)
        {
            // A lost/failed reply is not proof of no effects. Retries return this durable-in-lease outcome.
            var uncertain = Fail(request, "EFFECT_UNCERTAIN", ex.GetType().Name) with { SessionToken = lease };
            Finish(key, lease, pending, fingerprint, uncertain, read, safety);
            throw;
        }
    }

    public void ResetOwner()
    {
        lock (_gate) { _ownerSid = null; _ownerSession = 0; RotateLease(); }
    }

    private void RotateLease()
    {
        _lease = Guid.NewGuid().ToString("N");
        _highest = 0;
        Array.Clear(_seenSequences);
        _completed.Clear(); _readOrder.Clear(); _mutationOrder.Clear(); _safetyOrder.Clear();
        // Outstanding handlers keep their own completion and resource identity. Do not erase them.
    }

    private bool AcceptSequence(long sequence)
    {
        if (sequence <= 0 || (_highest >= WindowSize && sequence <= _highest - WindowSize)) return false;
        var slot = (int)(sequence % WindowSize);
        if (_seenSequences[slot] == sequence) return false;
        if (sequence > _highest) _highest = sequence;
        _seenSequences[slot] = sequence;
        return true;
    }

    private void Finish(string key, string lease, Pending pending, string fingerprint,
        IpcResponse response, bool read, bool safety)
    {
        lock (_gate)
        {
            if (_pending.Remove(key) && pending.Busy) _busy--;
            if (_lease == lease)
            {
                var order = read ? _readOrder : safety ? _safetyOrder : _mutationOrder;
                var cap = read ? 128 : safety ? 64 : ProductLimits.IpcIdempotencyEntries;
                while (order.Count >= cap) _completed.Remove(order.Dequeue());
                order.Enqueue(key);
                _completed[key] = new Cached(fingerprint, response);
            }
        }
        pending.Done.TrySetResult(response);
    }

    private static string Fingerprint(IpcRequest request)
        => request.Operation + "\n" + request.ExpectedStateRevision.ToString(CultureInfo.InvariantCulture)
        + "\n" + request.CommandSequence.ToString(CultureInfo.InvariantCulture)
        + "\n" + (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : request.Payload.GetRawText());

    private static IpcResponse Fail(IpcRequest request, string code, string message)
        => new() { RequestId = request.RequestId ?? "", Ok = false, ErrorCode = code, Message = message };
    private sealed record Cached(string Fingerprint, IpcResponse Response);
    private sealed record Pending(string Fingerprint, bool Busy, TaskCompletionSource<IpcResponse> Done);
}
