namespace GuideAntsApi.BackgroundJobs;

public class JobProcessorOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>
    /// How often to poll for new jobs (in seconds)
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Requeue any in-flight jobs on startup (safe for single-instance deployments).
    /// </summary>
    public bool RequeueProcessingOnStartup { get; set; } = true;

    /// <summary>
    /// Configuration for each job type
    /// </summary>
    public Dictionary<string, JobTypeOptions> JobTypes { get; set; } = new();
}

public class JobTypeOptions
{
    /// <summary>
    /// Maximum number of concurrent jobs of this type
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Lease duration in seconds
    /// </summary>
    public int LeaseSeconds { get; set; } = 300; // 5 minutes default
}

