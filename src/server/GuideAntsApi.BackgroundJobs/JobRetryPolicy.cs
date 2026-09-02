namespace GuideAntsApi.BackgroundJobs;

public sealed class JobRetryPolicy
{
    private readonly JobRetryOptions _options;

    public JobRetryPolicy(JobRetryOptions options)
    {
        _options = options;
    }

    public TimeSpan MaxRetryWindow => TimeSpan.FromDays(_options.MaxRetryWindowDays);

    public int DefaultMaxAttempts => _options.DefaultMaxAttempts;

    public TimeSpan ComputeDelay(int failedAttemptIndex, Random? random = null)
    {
        var baseMinutes = GetBaseDelayMinutes(failedAttemptIndex);
        var baseDelay = TimeSpan.FromMinutes(baseMinutes);
        return ApplyJitter(baseDelay, random ?? Random.Shared);
    }

    public int GetBaseDelayMinutes(int failedAttemptIndex)
    {
        var schedule = _options.RetryDelayMinutes;
        if (schedule.Length == 0)
        {
            return _options.MaxRetryDelayMinutes;
        }

        if (failedAttemptIndex < schedule.Length)
        {
            return schedule[failedAttemptIndex];
        }

        return _options.MaxRetryDelayMinutes;
    }

    public static bool IsRetryableFailureClass(JobFailureClass failureClass) =>
        failureClass is JobFailureClass.RetryableTransient
            or JobFailureClass.DependencyNotReady
            or JobFailureClass.LeaseOwnershipLost;

    public static bool BurnsAttemptBudget(JobFailureClass failureClass) =>
        failureClass is JobFailureClass.RetryableTransient
            or JobFailureClass.DependencyNotReady
            or JobFailureClass.PermanentMissingInput
            or JobFailureClass.PermanentInvalidInput;

    public bool CanRetry(
        JobFailureClass failureClass,
        int attemptsAfterFailure,
        int maxAttempts,
        DateTime jobCreatedUtc,
        DateTime nowUtc,
        TimeSpan nextDelay)
    {
        if (!IsRetryableFailureClass(failureClass))
        {
            return false;
        }

        if (attemptsAfterFailure >= maxAttempts)
        {
            return false;
        }

        var deadline = jobCreatedUtc.Add(MaxRetryWindow);
        return nowUtc + nextDelay <= deadline;
    }

    public JobFailurePlan PlanFailure(
        JobFailureClass failureClass,
        int currentAttempts,
        int maxAttempts,
        DateTime jobCreatedUtc,
        DateTime nowUtc)
    {
        var attemptsNext = currentAttempts + 1;
        var delay = ComputeDelay(currentAttempts);
        var willRetry = CanRetry(failureClass, attemptsNext, maxAttempts, jobCreatedUtc, nowUtc, delay);
        return new JobFailurePlan(
            attemptsNext,
            willRetry,
            willRetry ? nowUtc.Add(delay) : null);
    }

    public static TimeSpan ApplyJitter(TimeSpan baseDelay, Random random, double jitterFraction)
    {
        if (baseDelay <= TimeSpan.Zero || jitterFraction <= 0)
        {
            return baseDelay;
        }

        var jitterRange = baseDelay.TotalMilliseconds * jitterFraction;
        var offsetMs = (random.NextDouble() * 2 - 1) * jitterRange;
        var adjustedMs = Math.Max(0, baseDelay.TotalMilliseconds + offsetMs);
        return TimeSpan.FromMilliseconds(adjustedMs);
    }

    private TimeSpan ApplyJitter(TimeSpan baseDelay, Random random) =>
        ApplyJitter(baseDelay, random, _options.JitterFraction);
}
