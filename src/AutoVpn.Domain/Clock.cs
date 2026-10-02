namespace AutoVpn.Domain;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeSpan Monotonic { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Monotonic => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    public TimeSpan Monotonic { get; set; }

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        Monotonic += by;
    }
}

public static class TimePolicy
{
    public static TimeSpan ConservativeAge(DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        if (observedUtc > nowUtc)
        {
            return TimeSpan.MaxValue;
        }

        return nowUtc - observedUtc;
    }
}
