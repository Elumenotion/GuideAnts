namespace GuideAntsApi.BackgroundJobs.Options;

/// <summary>
/// Configuration for retention cleanup scheduler and behavior.
/// </summary>
public class RetentionCleanupOptions
{
    /// <summary>
    /// Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "BackgroundJobs:JobTypes:RetentionCleanup";

    /// <summary>
    /// Enables retention cleanup job scheduler when true.
    /// </summary>
    public bool Enabled { get; set; } = false;
}




