namespace AutoVpn.Infrastructure.Broker;

public sealed record CoreStartResult(bool Started, string? ReasonCode);

public interface ICoreController
{
    Task<CoreStartResult> StartAsync(string yaml, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Never starts Mihomo. Used until a Windows host has passed the TUN gate.
/// </summary>
public sealed class RefusingCoreController : ICoreController
{
    public Task<CoreStartResult> StartAsync(string yaml, CancellationToken cancellationToken)
    {
        _ = yaml;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CoreStartResult(false, UnavailableNetworkGuard.PlatformReason()));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
