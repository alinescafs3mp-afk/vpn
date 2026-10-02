namespace AutoVpn.Domain;

public enum SelectionMode
{
    Automatic = 0,
    ManualWithFallback = 1,
    Pinned = 2,
}

public enum CountryConstraint
{
    Any = 0,
    Prefer = 1,
    Strict = 2,
}

public enum FailureKind
{
    None = 0,
    HealthTimeout = 1,
    CoreExit = 2,
    UplinkOffline = 3,
    TargetOutage = 4,
    ExplicitDisconnect = 5,
}

public enum FailoverAction
{
    Stay = 0,
    Switch = 1,
    BlockNoServer = 2,
    BlockPinned = 3,
    BlockOffline = 4,
    IgnoreStale = 5,
    WaitCooldown = 6,
    DiagnoseTargets = 7,
}

public sealed record StandbyCandidate
{
    public required string NodeId { get; init; }
    public required string EndpointKey { get; init; }
    public required string Country { get; init; }
    public required string SourceFamilyId { get; init; }
    public double Cost { get; init; }
    public bool FreshOnEpoch { get; init; }
}

public sealed record FailoverContext
{
    public long Generation { get; init; }
    public long CommandGeneration { get; init; }
    public SelectionMode Mode { get; init; }
    public CountryConstraint CountryMode { get; init; }
    public string? StrictCountry { get; init; }
    public string? ActiveNodeId { get; init; }
    public FailureKind Failure { get; init; }
    public int ConsecutiveHealthFailures { get; init; }
    public int SwitchesInLastMinute { get; init; }
    public bool CooldownActive { get; init; }
    public bool OwnerRetry { get; init; }
    public IReadOnlyList<StandbyCandidate> Standbys { get; init; } = [];
}

public sealed record FailoverDecision(FailoverAction Action, string? NodeId, string ReasonCode);

public static class FailoverPolicy
{
    public static FailoverDecision Decide(FailoverContext context)
    {
        if (context.CommandGeneration != context.Generation)
        {
            return new(FailoverAction.IgnoreStale, null, ReasonCodes.StaleRevision);
        }

        if (context.Failure == FailureKind.ExplicitDisconnect)
        {
            return new(FailoverAction.IgnoreStale, null, ReasonCodes.Canceled);
        }

        if (context.Failure == FailureKind.UplinkOffline)
        {
            return new(FailoverAction.BlockOffline, null, ReasonCodes.UplinkOffline);
        }

        if (context.Failure == FailureKind.TargetOutage)
        {
            return new(FailoverAction.DiagnoseTargets, null, ReasonCodes.TargetUnhealthy);
        }

        var confirmed = context.Failure == FailureKind.CoreExit ||
                        context.ConsecutiveHealthFailures >= ProductLimits.HealthFailuresBeforeOutage;
        if (!confirmed)
        {
            return new(FailoverAction.Stay, context.ActiveNodeId, "HEALTH_PENDING");
        }

        if (context.Mode == SelectionMode.Pinned)
        {
            return new(FailoverAction.BlockPinned, context.ActiveNodeId, ReasonCodes.Pinned);
        }

        if (context.CooldownActive && !context.OwnerRetry)
        {
            return new(FailoverAction.WaitCooldown, null, "COOLDOWN");
        }

        if (context.SwitchesInLastMinute >= ProductLimits.MaxSwitchesPerMinute && !context.OwnerRetry)
        {
            return new(FailoverAction.WaitCooldown, null, "SWITCH_BUDGET");
        }

        var choice = context.Standbys
            .Where(item => item.FreshOnEpoch && item.NodeId != context.ActiveNodeId)
            .Where(item => CountryAllows(context, item.Country))
            .OrderBy(item => item.Cost)
            .ThenBy(item => item.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (choice is null)
        {
            return new(FailoverAction.BlockNoServer, null, ReasonCodes.NoEligibleServer);
        }

        return new(FailoverAction.Switch, choice.NodeId, "FAILOVER");
    }

    public static bool CountryAllows(FailoverContext context, string country)
    {
        if (context.CountryMode != CountryConstraint.Strict)
        {
            return true;
        }

        return string.Equals(context.StrictCountry, country, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<StandbyCandidate> PickDiverse(IEnumerable<StandbyCandidate> ranked, int limit)
    {
        var selected = new List<StandbyCandidate>();
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ranked.Where(item => item.FreshOnEpoch).OrderBy(item => item.Cost))
        {
            if (selected.Count >= limit)
            {
                break;
            }

            if (!endpoints.Add(item.EndpointKey))
            {
                continue;
            }

            selected.Add(item);
        }

        if (selected.Count < limit)
        {
            foreach (var item in ranked.Where(item => item.FreshOnEpoch).OrderBy(item => item.Cost))
            {
                if (selected.Count >= limit)
                {
                    break;
                }

                if (selected.Any(existing => existing.NodeId == item.NodeId))
                {
                    continue;
                }

                selected.Add(item);
            }
        }

        return selected;
    }
}
