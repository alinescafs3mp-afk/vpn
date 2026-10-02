namespace AutoVpn.Domain;

public enum RankMode
{
    Stability = 0,
    FasterDownload = 1,
}

public sealed record RankSample
{
    public required string NodeId { get; init; }
    public required string EndpointKey { get; init; }
    public int? MedianLatencyMs { get; init; }
    public int SampleCount { get; init; }
    public int FailureCount { get; init; }
    public int FlapsLastHour { get; init; }
    public double? DownloadMbps { get; init; }
    public bool Current { get; init; }
}

public static class Ranker
{
    public const int PriorLatencyMs = ProductLimits.MaxAcceptableLatencyMs;

    public static double Cost(RankSample sample)
    {
        var latency = sample.MedianLatencyMs ?? PriorLatencyMs;
        var observations = Math.Max(sample.SampleCount, 0);
        var failures = Math.Clamp(sample.FailureCount, 0, Math.Max(observations, sample.FailureCount));
        var fraction = observations == 0 ? 1d : failures / (double)observations;
        var confidence = 1d - (Math.Min(observations, 20) / 20d);
        var flaps = Math.Clamp(sample.FlapsLastHour, 0, 5);
        return latency + (300d * fraction) + (100d * flaps) + (100d * confidence);
    }

    public static IReadOnlyList<RankSample> Order(IEnumerable<RankSample> samples, RankMode mode)
    {
        return samples
            .OrderBy(sample => Cost(sample))
            .ThenBy(sample => mode == RankMode.FasterDownload ? sample.DownloadMbps is null ? 1 : 0 : 0)
            .ThenByDescending(sample => mode == RankMode.FasterDownload ? sample.DownloadMbps ?? double.NegativeInfinity : 0)
            .ThenBy(sample => sample.NodeId, StringComparer.Ordinal)
            .ToArray();
    }

    public static string SuccessPhrase(int successes, int attempts)
    {
        return $"{successes} успешных проверок из {attempts}";
    }

    public static bool ShouldOptimizeSwitch(
        int currentLatencyMs,
        int candidateLatencyMs,
        TimeSpan dwell,
        int consistentSamples,
        bool hardFailure)
    {
        if (hardFailure)
        {
            return false;
        }

        if (dwell < TimeSpan.FromMinutes(ProductLimits.OptimizationDwellMinutes))
        {
            return false;
        }

        if (consistentSamples < ProductLimits.OptimizationSamples)
        {
            return false;
        }

        var improvement = currentLatencyMs - candidateLatencyMs;
        if (improvement < ProductLimits.OptimizationImprovementMs)
        {
            return false;
        }

        return improvement >= currentLatencyMs * ProductLimits.OptimizationImprovementRatio;
    }
}
