namespace AutoVpn.Application;

/// <summary>
/// Refreshes an external read authority before admission or status observation.
/// Failure must reject admission; it must never authorize a stale snapshot.
/// </summary>
public interface IRefreshableCatalogue
{
    void Refresh();
}
