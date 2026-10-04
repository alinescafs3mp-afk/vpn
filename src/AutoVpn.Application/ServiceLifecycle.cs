namespace AutoVpn.Application;

/// <summary>Monotonic SCM lifecycle. Running means control-plane readiness, never VPN readiness.</summary>
public sealed class ServiceLifecycle
{
    private readonly object _gate = new();
    private int _state = 2; // START_PENDING
    public int State { get { lock (_gate) return _state; } }
    public bool TryMarkReady()
    {
        lock (_gate) { if (_state != 2) return false; _state = 4; return true; }
    }
    public bool RequestStop()
    {
        lock (_gate) { if (_state is 1 or 3) return false; _state = 3; return true; }
    }
    public void MarkStopped()
    {
        lock (_gate)
        {
            if (_state != 3) throw new InvalidOperationException("STOP_NOT_REQUESTED");
            _state = 1;
        }
    }
}
