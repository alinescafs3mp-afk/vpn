using System.Diagnostics;

namespace AutoVpn.Domain;

public readonly record struct TransferRead(int PayloadBytes, TimeSpan Elapsed, bool Truncated);

public readonly record struct SpeedSample(int PayloadBytes, TimeSpan Elapsed, double MegabitsPerSecond)
{
    public static SpeedSample? From(TransferRead read)
    {
        if (read.PayloadBytes <= 0 || read.Elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var megabits = (read.PayloadBytes * 8d) / read.Elapsed.TotalSeconds / 1_000_000d;
        return new SpeedSample(read.PayloadBytes, read.Elapsed, megabits);
    }
}

/// <summary>
/// Reads at most the supplied byte and time budgets. A short or empty read stays empty:
/// the caller must not invent a throughput figure from it.
/// </summary>
public static class BoundedTransfer
{
    public static async Task<TransferRead> ReadAsync(Stream stream, int maxBytes, TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0 || maxDuration <= TimeSpan.Zero)
        {
            return new TransferRead(0, TimeSpan.Zero, true);
        }

        var buffer = new byte[Math.Min(8192, maxBytes)];
        var read = 0;
        var watch = Stopwatch.StartNew();
        while (read < maxBytes && watch.Elapsed < maxDuration)
        {
            var slice = Math.Min(buffer.Length, maxBytes - read);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = maxDuration - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            linked.CancelAfter(remaining);
            int count;
            try
            {
                count = await stream.ReadAsync(buffer.AsMemory(0, slice), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (count == 0)
            {
                return new TransferRead(read, watch.Elapsed, false);
            }

            read += count;
        }

        var truncated = read >= maxBytes || watch.Elapsed >= maxDuration;
        return new TransferRead(read, watch.Elapsed, truncated);
    }
}
