namespace AutoVpn.Domain;

public enum HealthState
{
    Pending = 0,
    Checking = 1,
    Healthy = 2,
    Degraded = 3,
    Stale = 4,
    Failed = 5,
    EnvironmentUnknown = 6,
}

public enum SelectionPurpose
{
    Catalogue = 0,
    PreConnect = 1,
    Automatic = 2,
    Manual = 3,
}

public sealed record AssessmentSnapshot
{
    public required string Digest { get; init; }
    public long NetworkEpoch { get; init; }
    public HealthState Health { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    public int? MedianLatencyMs { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public bool EnvironmentFailure { get; init; }
    public string? ProofToken { get; init; }
    public string? VerifiedTargetSetId { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
}

public sealed record EligibilityContext
{
    public DateTimeOffset NowUtc { get; init; }
    public string? RequiredTargetSetId { get; init; }
    public long NetworkEpoch { get; init; }
    public TimeSpan AllowedAge { get; init; } = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes);
    public int MaxAcceptableLatencyMs { get; init; } = ProductLimits.MaxAcceptableLatencyMs;
    public bool AllowInsecureCertificates { get; init; }
    public bool OwnerExcluded { get; init; }
    public bool SourceEnabled { get; init; } = true;
    public bool CountryAllowed { get; init; } = true;
    public bool CapabilitiesMatch { get; init; } = true;
    public SelectionPurpose Purpose { get; init; } = SelectionPurpose.Catalogue;
    public IReadOnlySet<string> DisabledFamilies { get; init; } = EmptyFamilies;

    public static readonly IReadOnlySet<string> EmptyFamilies = new HashSet<string>(StringComparer.Ordinal);
}

public readonly record struct EligibilityDecision(bool Eligible, string? ReasonCode);

public static class Eligibility
{
    public static EligibilityDecision Evaluate(NodeSemantics node, AssessmentSnapshot? assessment, EligibilityContext context)
    {
        var posture = node.Classify(context.AllowInsecureCertificates);
        if (posture == SecurityPosture.Invalid)
        {
            return new(false, node.Port is < 1 or > 65535 ? ReasonCodes.InvalidPort : ReasonCodes.NonPublicEndpoint);
        }

        if (posture == SecurityPosture.Unsupported)
        {
            return new(false, ReasonCodes.UnsupportedProtocol);
        }

        if (posture == SecurityPosture.PolicyBlocked)
        {
            return new(false, node.SkipCertVerify ? ReasonCodes.CertVerificationDisabled : ReasonCodes.PlaintextTransport);
        }

        if (context.OwnerExcluded && context.Purpose != SelectionPurpose.Manual)
        {
            return new(false, "OWNER_EXCLUDED");
        }

        if (!context.SourceEnabled)
        {
            return new(false, "SOURCE_DISABLED");
        }

        if (!context.CountryAllowed)
        {
            return new(false, ReasonCodes.CountryFiltered);
        }

        if (!context.CapabilitiesMatch)
        {
            return new(false, "CAPABILITY_MISMATCH");
        }

        if (assessment is null || assessment.Health is HealthState.Pending or HealthState.Checking)
        {
            return new(false, "NOT_TESTED");
        }

        if (!string.Equals(assessment.Digest, CanonicalIdentity.Digest(node), StringComparison.Ordinal))
        {
            return new(false, "CONFIG_MISMATCH");
        }

        if (assessment.NetworkEpoch != context.NetworkEpoch)
        {
            return new(false, "EPOCH_MISMATCH");
        }

        if (assessment.EnvironmentFailure || assessment.Health == HealthState.EnvironmentUnknown)
        {
            return new(false, ReasonCodes.UplinkOffline);
        }

        if (assessment.LastFailureUtc is DateTimeOffset failure &&
            (assessment.LastSuccessUtc is null || failure > assessment.LastSuccessUtc))
        {
            return new(false, ReasonCodes.ProbeFailed);
        }

        if (assessment.RetryAfterUtc is DateTimeOffset retry && retry > context.NowUtc)
        {
            return new(false, "COOLDOWN");
        }

        if (assessment.LastSuccessUtc is not DateTimeOffset success)
        {
            return new(false, "NOT_TESTED");
        }

        if (context.RequiredTargetSetId is not null && assessment.VerifiedTargetSetId != context.RequiredTargetSetId)
        {
            return new(false, "TWO_TARGET_VERIFICATION_REQUIRED");
        }

        var age = TimePolicy.ConservativeAge(success, context.NowUtc);
        if (age > context.AllowedAge || assessment.Health == HealthState.Stale)
        {
            return new(false, "STALE");
        }

        if (assessment.MedianLatencyMs is int latency && latency > context.MaxAcceptableLatencyMs)
        {
            return new(false, "LATENCY");
        }

        if (assessment.Health is not (HealthState.Healthy or HealthState.Degraded))
        {
            return new(false, ReasonCodes.ProbeFailed);
        }

        return new(true, null);
    }
}
