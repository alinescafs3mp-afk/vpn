namespace AutoVpn.Domain;

public static class RefreshSchedule
{
    public static TimeSpan Interval(int minutes, int jitterMinutes, int seed)
    {
        var bounded = Math.Clamp(minutes, ProductLimits.MinimumRefreshIntervalMinutes, 7 * 24 * 60);
        var jitterAbs = jitterMinutes == int.MinValue ? int.MaxValue : Math.Abs(jitterMinutes);
        var jitter = Math.Min(jitterAbs, bounded / 2);
        if (jitter == 0)
        {
            return TimeSpan.FromMinutes(bounded);
        }

        var span = (jitter * 2) + 1;
        var seedAbs = seed == int.MinValue ? 0 : Math.Abs(seed);
        var offset = (seedAbs % span) - jitter;
        var lower = Math.Max(ProductLimits.MinimumRefreshIntervalMinutes, bounded - jitter);
        var upper = bounded + jitter;
        return TimeSpan.FromMinutes(Math.Clamp(bounded + offset, lower, upper));
    }

    public static bool IsDue(DateTimeOffset? lastSuccessUtc, DateTimeOffset nowUtc, TimeSpan interval)
    {
        if (lastSuccessUtc is null)
        {
            return true;
        }

        var age = TimePolicy.ConservativeAge(lastSuccessUtc.Value, nowUtc);
        return age >= interval;
    }

    public static int MissedIntervalsToReplay(TimeSpan gap, TimeSpan interval)
    {
        _ = gap;
        _ = interval;
        return 1;
    }
}

public static class QuarantineSchedule
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    public static DateTimeOffset NextRetry(DateTimeOffset nowUtc, int failureCount, int jitterSeconds)
    {
        var index = Math.Clamp(failureCount - 1, 0, Steps.Length - 1);
        var jitter = TimeSpan.FromSeconds(Math.Clamp(jitterSeconds, 0, 60));
        return nowUtc + Steps[index] + jitter;
    }
}

public enum MembershipKind
{
    Current = 0,
    Retained = 1,
    Disabled = 2,
}

public static class RetentionPolicy
{
    public static bool KeepMissingNode(bool stillPasses, bool activeSession, bool favorite, DateTimeOffset? lastSuccessUtc, DateTimeOffset nowUtc)
    {
        if (activeSession || favorite)
        {
            return true;
        }

        if (stillPasses)
        {
            return true;
        }

        if (lastSuccessUtc is null)
        {
            return false;
        }

        return TimePolicy.ConservativeAge(lastSuccessUtc.Value, nowUtc) <= TimeSpan.FromDays(ProductLimits.FailedCredentialRetentionDays);
    }

    public static IReadOnlyList<string> EvictOrder(IEnumerable<EvictionCandidate> candidates, int overflow)
    {
        if (overflow <= 0)
        {
            return [];
        }

        return candidates
            .Where(item => !item.Active && !item.Favorite && item.Failed && !item.CurrentUpstream)
            .OrderBy(item => item.LastSuccessUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.NodeId, StringComparer.Ordinal)
            .Take(overflow)
            .Select(item => item.NodeId)
            .ToArray();
    }
}

public sealed record EvictionCandidate
{
    public required string NodeId { get; init; }
    public bool Active { get; init; }
    public bool Favorite { get; init; }
    public bool Failed { get; init; }
    public bool CurrentUpstream { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
}

public sealed record NetworkObservation
{
    public IReadOnlySet<string> ChangedInterfaceIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> OwnedTunInterfaceIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool DefaultGatewayChanged { get; init; }
    public bool DnsChanged { get; init; }
    public bool SleepResume { get; init; }
    public bool CompetingVpn { get; init; }
}

public static class NetworkEpochPolicy
{
    public static bool ShouldIncrement(NetworkObservation observation)
    {
        if (observation.SleepResume || observation.DefaultGatewayChanged || observation.DnsChanged || observation.CompetingVpn)
        {
            return true;
        }

        if (observation.ChangedInterfaceIds.Count == 0)
        {
            return false;
        }

        return observation.ChangedInterfaceIds.Any(id => !observation.OwnedTunInterfaceIds.Contains(id));
    }
}
