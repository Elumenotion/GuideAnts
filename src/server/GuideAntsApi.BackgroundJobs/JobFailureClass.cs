namespace GuideAntsApi.BackgroundJobs;

public enum JobFailureClass
{
    RetryableTransient,
    PermanentMissingInput,
    ShutdownCancellation,
    DependencyNotReady,
    LeaseOwnershipLost,
    PermanentInvalidInput,
}
