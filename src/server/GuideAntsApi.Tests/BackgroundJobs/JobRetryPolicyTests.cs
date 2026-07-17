using FluentAssertions;
using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class JobRetryPolicyTests
{
    private static JobRetryPolicy CreatePolicy() => new(new JobRetryOptions());

    [TestMethod]
    public void ComputeDelay_ProgressesThroughConfiguredScheduleThenCapsAtSixHours()
    {
        var policy = CreatePolicy();

        policy.GetBaseDelayMinutes(0).Should().Be(2);
        policy.GetBaseDelayMinutes(1).Should().Be(5);
        policy.GetBaseDelayMinutes(2).Should().Be(10);
        policy.GetBaseDelayMinutes(3).Should().Be(20);
        policy.GetBaseDelayMinutes(4).Should().Be(40);
        policy.GetBaseDelayMinutes(5).Should().Be(80);
        policy.GetBaseDelayMinutes(6).Should().Be(160);
        policy.GetBaseDelayMinutes(7).Should().Be(360);
        policy.GetBaseDelayMinutes(99).Should().Be(360);
    }

    [TestMethod]
    public void ApplyJitter_StaysWithinPlusMinusTwentyPercent()
    {
        var baseDelay = TimeSpan.FromMinutes(10);
        var random = new Random(42);

        for (var i = 0; i < 50; i++)
        {
            var jittered = JobRetryPolicy.ApplyJitter(baseDelay, random, jitterFraction: 0.20);
            jittered.TotalMinutes.Should().BeInRange(8.0, 12.0);
        }
    }

    [TestMethod]
    public void DefaultMaxAttempts_IsForty()
    {
        CreatePolicy().DefaultMaxAttempts.Should().Be(40);
    }

    [TestMethod]
    public void CanRetry_StopsAfterSevenDayHorizon()
    {
        var policy = CreatePolicy();
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = created.AddDays(6).AddHours(23);
        var delay = TimeSpan.FromMinutes(30);

        policy.CanRetry(JobFailureClass.RetryableTransient, attemptsAfterFailure: 1, maxAttempts: 40, created, now, delay)
            .Should().BeTrue();

        var pastHorizon = created.AddDays(7).AddHours(1);
        policy.CanRetry(JobFailureClass.RetryableTransient, attemptsAfterFailure: 1, maxAttempts: 40, created, pastHorizon, delay)
            .Should().BeFalse();
    }

    [TestMethod]
    public void CanRetry_PermanentFailureClassNeverRetries()
    {
        var policy = CreatePolicy();
        var created = DateTime.UtcNow;
        var now = created.AddMinutes(1);

        policy.CanRetry(
                JobFailureClass.PermanentMissingInput,
                attemptsAfterFailure: 1,
                maxAttempts: 40,
                created,
                now,
                TimeSpan.FromMinutes(2))
            .Should().BeFalse();
    }
}
