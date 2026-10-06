using System.Runtime.ExceptionServices;

namespace AutoVpn.UnitTests;

// Keep failures from the test body and its one existing cleanup attempt.
// Neither an eventual cleanup success nor a cleanup failure replaces the body.
internal static class OwnedFixtureExecution
{
    internal static async Task RunAsync(IAsyncDisposable fixture, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ExceptionDispatchInfo? bodyFailure = null;
        ExceptionDispatchInfo? cleanupFailure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(body);
            await body().ConfigureAwait(false);
        }
        catch (Exception error) { bodyFailure = ExceptionDispatchInfo.Capture(error); }

        try { await fixture.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { cleanupFailure = ExceptionDispatchInfo.Capture(error); }

        if (bodyFailure is not null && cleanupFailure is not null)
            throw new AggregateException("Owned fixture body and cleanup both failed.",
                bodyFailure.SourceException, cleanupFailure.SourceException);
        bodyFailure?.Throw();
        cleanupFailure?.Throw();
    }
}
