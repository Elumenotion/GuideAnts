namespace GuideAntsApi.BackgroundJobs;

public class JobRetryOptions
{
    public const string SectionName = "BackgroundJobs:Retry";

    public int DefaultMaxAttempts { get; set; } = 40;

    public int MaxRetryWindowDays { get; set; } = 7;

    public double JitterFraction { get; set; } = 0.20;

    public int[] RetryDelayMinutes { get; set; } = [2, 5, 10, 20, 40, 80, 160];

    public int MaxRetryDelayMinutes { get; set; } = 360;

    public int[] LoopBackoffSeconds { get; set; } = [5, 10, 20, 40, 60];
}
