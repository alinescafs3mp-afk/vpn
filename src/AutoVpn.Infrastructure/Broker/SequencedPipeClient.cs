using System.Collections.Concurrent;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Broker;

/// <summary>
/// Creates a fresh exact sequence for each new command. An explicit sealed IpcRequest is
/// sent unchanged, so callers can retry an uncertain command without repeating its effects.
/// No automatic effect retry is performed after a failed/stale lease.
/// </summary>
internal sealed class SequencedPipeClient
{
    private static readonly ConcurrentDictionary<string, SequencedPipeClient> Clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lease;
    private long _sequence;
    public static SequencedPipeClient For(string pipe) => Clients.GetOrAdd(pipe, _ => new());
    internal static void Forget(string pipe) => Clients.TryRemove(pipe, out _);

    public async Task<IpcResponse?> SendAsync(string pipe, IpcRequest request, CancellationToken token)
    {
        if (request.SessionToken is not null || request.Operation is IpcOperations.GetSnapshot or IpcOperations.OpenSession)
            return await LocalIpcServer.RawRoundTripAsync(pipe, request, token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IpcRequest command;
        try
        {
            if (_lease is null || _sequence == long.MaxValue)
            {
                var opened = await LocalIpcServer.RawRoundTripAsync(pipe, new IpcRequest
                {
                    ProtocolVersion = ProductLimits.IpcProtocolVersion,
                    RequestId = Guid.NewGuid().ToString("N"), Operation = IpcOperations.OpenSession,
                    Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                }, token).ConfigureAwait(false);
                if (opened is not { Ok: true, SessionToken: not null }) return opened;
                _lease = opened.SessionToken; _sequence = 0;
            }
            command = request with { SessionToken = _lease, CommandSequence = ++_sequence };
        }
        finally { _gate.Release(); }
        var response = await LocalIpcServer.RawRoundTripAsync(pipe, command, token).ConfigureAwait(false);
        if (response?.ErrorCode == "SESSION_REQUIRED")
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { if (_lease == command.SessionToken) _lease = null; }
            finally { _gate.Release(); }
        }
        return response;
    }
}
