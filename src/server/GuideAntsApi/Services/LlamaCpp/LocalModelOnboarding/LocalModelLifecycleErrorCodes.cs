namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

public static class LocalModelLifecycleErrorCodes
{
    public const string InstallationNotFound = "INSTALLATION_NOT_FOUND";
    public const string OperationInFlight = "OPERATION_IN_FLIGHT";
    public const string AliasLockConflict = "ALIAS_LOCK_CONFLICT";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string AdoptionBlocked = "ADOPTION_BLOCKED";
    public const string ProvenanceUnknown = "PROVENANCE_UNKNOWN";
    public const string RuntimeCleanupFailed = "RUNTIME_CLEANUP_FAILED";
    public const string LoadRestoreFailed = "LOAD_RESTORE_FAILED";
    public const string ObsoleteCleanupFailed = "OBSOLETE_CLEANUP_FAILED";
    public const string IniPreservationFailed = "INI_PRESERVATION_FAILED";
}

public sealed class LocalModelLifecycleException : Exception
{
    public LocalModelLifecycleException(string code, string message, string remediation, int statusCode = 400)
        : base(message)
    {
        Code = code;
        Remediation = remediation;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public string Remediation { get; }
    public int StatusCode { get; }
}
