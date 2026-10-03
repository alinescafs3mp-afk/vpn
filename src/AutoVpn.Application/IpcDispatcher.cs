using System.Globalization;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Application;

public sealed class IpcDispatcher
{
    private const int ReadCacheEntries = 128;
    private const int SafetyCacheEntries = 64;
    private const int InflightLimit = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _mutations = new(StringComparer.Ordinal);
    private readonly Queue<string> _mutationOrder = new();
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Queue<string> _retiredOrder = new();
    private readonly Dictionary<string, CacheEntry> _reads = new(StringComparer.Ordinal);
    private readonly Queue<string> _readOrder = new();
    private readonly Dictionary<string, CacheEntry> _safety = new(StringComparer.Ordinal);
    private readonly Queue<string> _safetyOrder = new();
    private readonly Dictionary<string, Inflight> _inflight = new(StringComparer.Ordinal);
    private readonly uint[] _seen = new uint[1 << 15];
    private string? _ownerSid;
    private int? _ownerSession;
    private long _authority;
    private int _busyInflight;

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

        var fingerprint = Fingerprint(request);
        var safety = IsSafety(request.Operation);
        var read = request.Operation == IpcOperations.GetSnapshot;
        Inflight? wait = null;
        Inflight? created = null;
        long epoch;
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

            if (_retired.Contains(request.RequestId))
            {
                return Fail(request, ReasonCodes.ReplayExpired, "Идентификатор запроса уже вышел из окна повтора и не выполняется снова.");
            }

            if (TryCached(request.RequestId, fingerprint, out var cached, out var conflict))
            {
                return conflict
                    ? Fail(request, ReasonCodes.RequestConflict, "Идентификатор запроса уже использован с другим содержимым.")
                    : cached!;
            }

            if (_inflight.TryGetValue(request.RequestId, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Fail(request, ReasonCodes.RequestConflict, "Идентификатор запроса уже выполняется с другим содержимым.");
                }

                wait = existing;
            }
            else if (!safety && !read && _busyInflight >= InflightLimit)
            {
                return Fail(request, "BUSY", "Слишком много одновременных команд. Повторите запрос.");
            }
            else if (!read && !safety && ProbablySeen(request.RequestId))
            {
                return Fail(request, ReasonCodes.ReplayExpired, "Идентификатор запроса уже вышел из окна повтора и не выполняется снова.");
            }
            else
            {
                if (!read)
                {
                    MarkSeen(request.RequestId);
                }

                created = new Inflight(fingerprint, new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
                _inflight[request.RequestId] = created;
                if (!safety && !read)
                {
                    _busyInflight++;
                }
            }

            epoch = _authority;
        }

        if (wait is not null)
        {
            return wait.Done.Task.GetAwaiter().GetResult();
        }

        try
        {
            var response = handler(request);
            lock (_gate)
            {
                Complete(request.RequestId, created!, epoch, fingerprint, read, safety, response);
            }

            created!.Done.TrySetResult(response);
            return response;
        }
        catch (Exception ex)
        {
            var uncertain = Fail(request, "EFFECT_UNCERTAIN", ex.GetType().Name);
            lock (_gate)
            {
                if (!read)
                {
                    Complete(request.RequestId, created!, epoch, fingerprint, false, safety, uncertain);
                }
                else
                {
                    Release(request.RequestId, created!, read: true, safety: false);
                }
            }

            created!.Done.TrySetException(ex);
            throw;
        }
    }

    public void ResetOwner()
    {
        lock (_gate)
        {
            _authority++;
            _ownerSid = null;
            _ownerSession = null;
            _mutations.Clear();
            _mutationOrder.Clear();
            _retired.Clear();
            _retiredOrder.Clear();
            _reads.Clear();
            _readOrder.Clear();
            _safety.Clear();
            _safetyOrder.Clear();
            _inflight.Clear();
            _busyInflight = 0;
            Array.Clear(_seen);
        }
    }

    private bool ProbablySeen(string id)
    {
        var (first, second) = Mix(id);
        var bits = (ulong)_seen.Length * 32;
        for (var index = 0; index < 8; index++)
        {
            var bit = (uint)((first + ((ulong)index * second)) % bits);
            if ((_seen[bit >> 5] & (1u << (int)(bit & 31))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private void MarkSeen(string id)
    {
        var (first, second) = Mix(id);
        var bits = (ulong)_seen.Length * 32;
        for (var index = 0; index < 8; index++)
        {
            var bit = (uint)((first + ((ulong)index * second)) % bits);
            _seen[bit >> 5] |= 1u << (int)(bit & 31);
        }
    }

    private static (ulong First, ulong Second) Mix(string id)
    {
        ulong first = 14695981039346656037;
        ulong second = 1099511628211;
        foreach (var character in id)
        {
            first ^= character;
            first *= 1099511628211;
            second ^= (ulong)character << 1;
            second *= 14695981039346656037;
        }

        if ((second & 1) == 0)
        {
            second |= 1;
        }

        return (first, second);
    }

    private void Complete(string id, Inflight created, long epoch, string fingerprint, bool read, bool safety, IpcResponse response)
    {
        if (epoch == _authority)
        {
            var entry = new CacheEntry(fingerprint, response);
            if (read)
            {
                StoreBounded(_reads, _readOrder, id, entry, ReadCacheEntries);
            }
            else if (safety)
            {
                StoreBounded(_safety, _safetyOrder, id, entry, SafetyCacheEntries);
            }
            else
            {
                RememberMutation(id, entry);
            }
        }

        Release(id, created, read, safety);
    }

    private void Release(string id, Inflight created, bool read, bool safety)
    {
        if (_inflight.TryGetValue(id, out var current) && ReferenceEquals(current, created))
        {
            _inflight.Remove(id);
            if (!safety && !read && _busyInflight > 0)
            {
                _busyInflight--;
            }
        }
    }

    private bool TryCached(string id, string fingerprint, out IpcResponse? response, out bool conflict)
    {
        response = null;
        conflict = false;
        foreach (var map in new[] { _mutations, _reads, _safety })
        {
            if (!map.TryGetValue(id, out var entry))
            {
                continue;
            }

            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                conflict = true;
                return true;
            }

            response = entry.Response;
            return true;
        }

        return false;
    }

    private void RememberMutation(string id, CacheEntry entry)
    {
        if (!_mutations.ContainsKey(id))
        {
            while (_mutations.Count >= ProductLimits.IpcIdempotencyEntries)
            {
                if (!RetireOldest())
                {
                    break;
                }
            }

            _mutationOrder.Enqueue(id);
        }

        _mutations[id] = entry;
    }

    private bool RetireOldest()
    {
        while (_mutationOrder.Count > 0)
        {
            var old = _mutationOrder.Dequeue();
            if (!_mutations.Remove(old))
            {
                continue;
            }

            if (_retired.Add(old))
            {
                _retiredOrder.Enqueue(old);
                while (_retired.Count > ProductLimits.IpcRetiredEntries && _retiredOrder.Count > 0)
                {
                    _retired.Remove(_retiredOrder.Dequeue());
                }
            }

            return true;
        }

        return false;
    }

    private static void StoreBounded(Dictionary<string, CacheEntry> map, Queue<string> order, string id, CacheEntry entry, int cap)
    {
        if (!map.ContainsKey(id))
        {
            while (map.Count >= cap && order.Count > 0)
            {
                var old = order.Dequeue();
                map.Remove(old);
            }

            order.Enqueue(id);
        }

        map[id] = entry;
    }

    private static bool IsSafety(string operation)
    {
        return operation is IpcOperations.Disconnect or IpcOperations.RecoverOwned;
    }

    private static string Fingerprint(IpcRequest request)
    {
        var payload = request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? ""
            : request.Payload.GetRawText();
        return request.Operation + "\n" + request.ExpectedStateRevision.ToString(CultureInfo.InvariantCulture) + "\n" + payload;
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

    private sealed record CacheEntry(string Fingerprint, IpcResponse Response);

    private sealed record Inflight(string Fingerprint, TaskCompletionSource<IpcResponse> Done);
}
