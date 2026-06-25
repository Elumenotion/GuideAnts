namespace GuideAntsApi.BackgroundJobs.Options;

public class ProjectScheduledJobOptions
{
    public const string SectionName = "BackgroundJobs:ProjectScheduledJobs";

    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 60;
}
