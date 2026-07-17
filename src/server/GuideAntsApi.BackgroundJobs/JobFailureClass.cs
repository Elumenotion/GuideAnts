namespace GuideAntsApi.BackgroundJobs;

public enum JobFailureClass
{
    RetryableTransient,
    PermanentMissingInput,
}
