namespace GuideAntsApi.BackgroundJobs;

public sealed record JobFailurePlan(int AttemptsNext, bool WillRetry, DateTime? NextAvailableAt);
