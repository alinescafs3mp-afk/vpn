namespace AutoVpn.Infrastructure.Broker;

public sealed record CoreStartResult(bool Started, string? ReasonCode);

public interface ICoreController
{
    Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken);

    Task StopAsync(long generation, string operationId, CancellationToken cancellationToken);
}

/// <summary>
/// Never starts Mihomo. Used until a Windows host has passed the TUN gate.
/// </summary>
public sealed class RefusingCoreController : ICoreController
{
    public Task<CoreStartResult> StartAsync(string yaml, long generation, string operationId, CancellationToken cancellationToken)
    {
        _ = yaml;
        _ = generation;
        _ = operationId;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CoreStartResult(false, UnavailableNetworkGuard.PlatformReason()));
    }

    public Task StopAsync(long generation, string operationId, CancellationToken cancellationToken)
    {
        _ = generation;
        _ = operationId;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
