using FluentAssertions;
using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class JobQueueServiceRetryTests
{
    private static JobRetryPolicy CreatePolicy() => new(new JobRetryOptions());

    [TestMethod]
    public void PlanFailure_RetryableFailure_SchedulesFutureRetry()
    {
        var policy = CreatePolicy();
        var created = DateTime.UtcNow;
        var now = created.AddMinutes(1);

        var plan = policy.PlanFailure(
            JobFailureClass.RetryableTransient,
            currentAttempts: 0,
            maxAttempts: 40,
            jobCreatedUtc: created,
            nowUtc: now);

        plan.WillRetry.Should().BeTrue();
        plan.AttemptsNext.Should().Be(1);
        plan.NextAvailableAt.Should().NotBeNull();
        plan.NextAvailableAt!.Value.Should().BeAfter(now);
    }

    [TestMethod]
    public void PlanFailure_PermanentFailure_DoesNotScheduleRetry()
    {
        var policy = CreatePolicy();
        var created = DateTime.UtcNow;
        var now = created.AddMinutes(1);

        var plan = policy.PlanFailure(
            JobFailureClass.PermanentMissingInput,
            currentAttempts: 0,
            maxAttempts: 40,
            jobCreatedUtc: created,
            nowUtc: now);

        plan.WillRetry.Should().BeFalse();
        plan.AttemptsNext.Should().Be(1);
        plan.NextAvailableAt.Should().BeNull();
    }

    [TestMethod]
    public void PlanFailure_RespectsMaxAttemptsAndHorizon()
    {
        var policy = CreatePolicy();
        var created = DateTime.UtcNow.AddDays(-8);
        var now = DateTime.UtcNow;

        var plan = policy.PlanFailure(
            JobFailureClass.RetryableTransient,
            currentAttempts: 2,
            maxAttempts: 3,
            jobCreatedUtc: created,
            nowUtc: now);

        plan.WillRetry.Should().BeFalse();
        plan.AttemptsNext.Should().Be(3);
        plan.NextAvailableAt.Should().BeNull();
    }
}
