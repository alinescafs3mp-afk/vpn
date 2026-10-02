namespace AutoVpn.Domain;

public static class ReasonCodes
{
    public const string InvalidUri = "INVALID_URI";
    public const string InvalidPort = "INVALID_PORT";
    public const string InvalidUuid = "INVALID_UUID";
    public const string YamlLimit = "YAML_LIMIT";
    public const string AmbiguousField = "AMBIGUOUS_FIELD";
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";
    public const string UnsupportedTransport = "UNSUPPORTED_TRANSPORT";
    public const string UnsupportedSecurityOption = "UNSUPPORTED_SECURITY_OPTION";
    public const string CertVerificationDisabled = "CERT_VERIFICATION_DISABLED";
    public const string PlaintextTransport = "PLAINTEXT_TRANSPORT";
    public const string NonPublicEndpoint = "NON_PUBLIC_ENDPOINT";
    public const string CoreConfigRejected = "CORE_CONFIG_REJECTED";
    public const string MissingCredential = "MISSING_CREDENTIAL";
    public const string SizeLimit = "SIZE_LIMIT";
    public const string HtmlContent = "HTML_CONTENT";
    public const string DuplicateKey = "DUPLICATE_KEY";
    public const string NestedWrapper = "NESTED_WRAPPER";
    public const string ClientPolicyStripped = "CLIENT_POLICY_STRIPPED";
    public const string EmptyValidSource = "EMPTY_VALID_SOURCE";
    public const string NotModified = "NOT_MODIFIED";
    public const string FetchFailed = "FETCH_FAILED";
    public const string Canceled = "CANCELED";
    public const string OffRegistryRedirect = "OFF_REGISTRY_REDIRECT";
    public const string TargetUnhealthy = "TARGET_UNHEALTHY";
    public const string ProbeFailed = "PROBE_FAILED";
    public const string UplinkOffline = "UPLINK_OFFLINE";
    public const string CaptivePortal = "CAPTIVE_PORTAL";
    public const string Pinned = "PINNED";
    public const string CountryFiltered = "COUNTRY_FILTERED";
    public const string NoEligibleServer = "NO_ELIGIBLE_SERVER";
    public const string StaleRevision = "STALE_REVISION";
    public const string WindowsNotValidated = "WINDOWS_NETWORK_NOT_VALIDATED";
    public const string NotWindows = "NOT_WINDOWS";
}
