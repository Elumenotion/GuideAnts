namespace GuideAntsApi.BackgroundJobs;

public sealed class AdaptiveLoopBackoff
{
    private readonly int[] _delaySeconds;
    private readonly double _jitterFraction;
    private int _consecutiveFailures;

    public AdaptiveLoopBackoff(int[] delaySeconds, double jitterFraction = 0.10)
    {
        _delaySeconds = delaySeconds.Length > 0 ? delaySeconds : [5, 10, 20, 40, 60];
        _jitterFraction = jitterFraction;
    }

    public TimeSpan NextDelayWithJitter(Random? random = null)
    {
        var index = Math.Min(_consecutiveFailures, _delaySeconds.Length - 1);
        _consecutiveFailures++;
        var baseDelay = TimeSpan.FromSeconds(_delaySeconds[index]);
        return JobRetryPolicy.ApplyJitter(baseDelay, random ?? Random.Shared, _jitterFraction);
    }

    public void Reset() => _consecutiveFailures = 0;
}
