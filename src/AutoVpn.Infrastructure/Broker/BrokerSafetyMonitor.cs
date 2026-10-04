namespace AutoVpn.Infrastructure.Broker;

/// <summary>One retained local-observation loop. No network probes or reconnect attempts.</summary>
public sealed class BrokerSafetyMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _work;
    public Task Completion => _work;

    public BrokerSafetyMonitor(BrokerEngine engine, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var period = interval ?? TimeSpan.FromSeconds(1);
        if (period < TimeSpan.FromMilliseconds(20) || period > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _work = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                do { await engine.EnforceSafetyAsync(_stop.Token).ConfigureAwait(false); }
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _work.ConfigureAwait(false);
        _stop.Dispose();
    }
}
