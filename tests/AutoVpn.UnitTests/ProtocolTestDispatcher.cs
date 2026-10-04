using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.UnitTests;

// Adapts legacy in-process scenarios to the explicit v2 client contract. The map belongs
// only to this finite test fixture. Production requests carry their own lease and sequence.
// Reusing a test request ID deliberately reuses the SAME sequence, including after eviction.
internal sealed class ProtocolTestDispatcher : IpcDispatcher
{
    private readonly object _clientGate = new();
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private string? _lease;
    private long _next;
    private IpcRequest Seal(IpcRequest request, CallerIdentity caller)
    {
        lock (_clientGate)
        {
            _lease ??= base.Dispatch(new IpcRequest
            { ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = Guid.NewGuid().ToString("N"), Operation = IpcOperations.OpenSession },
                caller, _ => throw new InvalidOperationException("Handshake must not invoke effects")).SessionToken;
            if (!_sequences.TryGetValue(request.RequestId, out var sequence))
                _sequences[request.RequestId] = sequence = ++_next;
            return request with { SessionToken = _lease, CommandSequence = sequence };
        }
    }
    public new void ResetOwner()
    {
        lock (_clientGate) { base.ResetOwner(); _lease = null; _next = 0; _sequences.Clear(); }
    }
    public new IpcResponse Dispatch(IpcRequest request, CallerIdentity caller, Func<IpcRequest, IpcResponse> handler)
        => base.Dispatch(Seal(request, caller), caller, handler);
    public new Task<IpcResponse> DispatchAsync(IpcRequest request, CallerIdentity caller,
        Func<IpcRequest, Task<IpcResponse>> handler, CancellationToken cancellationToken = default)
        => base.DispatchAsync(Seal(request, caller), caller, handler, cancellationToken);
}
