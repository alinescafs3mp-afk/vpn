namespace AutoVpn.Domain;

/// <summary>
/// Single home for product defaults from the v1 directive. These are policy
/// choices, not measured network facts.
/// </summary>
public static class ProductLimits
{
    public const int CanonicalizerVersion = 3;
    public const int SettingsSchemaVersion = 1;
    public const int IpcProtocolVersion = 2;
    public const int MaxIpcFrameBytes = 256 * 1024;
    public const int IpcPipeInstances = 4;
    public const int IpcPipeCreateAttempts = 3;
    public const int IpcFrameTimeoutMs = 2000;
    public const int IpcWriteTimeoutMs = 2000;
    public const int IpcRoundTripTimeoutMs = 15000;
    public const int IpcIdempotencyEntries = 256;
    public const int IpcRetiredEntries = 8192;
    public const int MaxRedirects = 3;
    public const int MaxBase64Depth = 2;
    public const int MaxYamlDepth = 32;
    public const int MaxYamlAnchors = 8;
    public const int MaxJsonDepth = 32;
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxArtifactBytes = 8 * 1024 * 1024;
    public const int MaxRefreshBytes = 64 * 1024 * 1024;
    public const int MaxCandidatesPerCycle = 50_000;
    public const int MaxRetainedCandidates = 10_000;
    public const int ProbeSamplesPerNode = 20;
    public const int SourceDownloadConcurrency = 3;
    public const int FetchAttemptTimeoutSeconds = 20;
    public const int FetchRetries = 2;
    public const int LightweightProbeConcurrency = 8;
    public const int MaxProbesPerEndpoint = 2;
    public const int ProbeWorkerProcesses = 2;
    public const int ProbeRequestTimeoutSeconds = 5;
    public const int NewCandidateBudgetSeconds = 20;
    public const int HealthPayloadBytes = 8 * 1024;
    public const int RefreshIntervalMinutes = 120;
    public const int RefreshJitterMinutes = 10;
    public const int MinimumRefreshIntervalMinutes = 15;
    public const int RediscoveryHours = 12;
    public const int CatalogueFreshnessMinutes = 30;
    public const int PreConnectFreshnessSeconds = 60;
    public const int MaxAcceptableLatencyMs = 1_500;
    public const int ActiveHealthIntervalSeconds = 30;
    public const int HealthFailuresBeforeOutage = 3;
    public const int WarmStandbys = 5;
    public const int MaxSwitchesPerMinute = 3;
    public const int SwitchCooldownSeconds = 60;
    public const int OptimizationDwellMinutes = 10;
    public const int OptimizationSamples = 3;
    public const double OptimizationImprovementRatio = 0.25;
    public const int OptimizationImprovementMs = 100;
    public const int FailedCredentialRetentionDays = 7;
    public const int DailyHealthBudgetBytes = 128 * 1024 * 1024;
    public const int ManualDownloadBytes = 16 * 1024 * 1024;
    public const int ManualDownloadSeconds = 8;
    public const int ManualUploadBytes = 4 * 1024 * 1024;
    public const int ManualUploadSeconds = 5;
    public const string CoreVersion = "v1.19.32";
    public const string CoreTag = "v1.19.32";
    public const string CoreCommit = "88dcbf7f1614a67c3b36b848ee3592dfa92ada36";
}
