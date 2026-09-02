using FluentAssertions;
using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class JobQueueClaimSqlCharacterizationTests
{
    [TestMethod]
    public void TryClaimSql_RechecksPendingStatusAndEmptyClaimTokenOnOuterUpdate()
    {
        var claimSql = JobQueueClaimSql.Claim;

        claimSql.Should().Contain("WITH Candidate AS");
        claimSql.Should().Contain("TOP (1)");
        claimSql.Should().Contain("WHERE j.Status = {0}");
        claimSql.Should().Contain("AND j.ClaimToken = {2}");
        claimSql.Should().MatchRegex("UPDATE j[\\s\\S]*WHERE j\\.Status = \\{0\\}[\\s\\S]*AND j\\.ClaimToken = \\{2\\}");
    }

    [TestMethod]
    public void LeaseOwnershipLost_DoesNotBurnAttemptBudget()
    {
        JobRetryPolicy.BurnsAttemptBudget(JobFailureClass.LeaseOwnershipLost).Should().BeFalse();
    }

    [TestMethod]
    public void PlanFailure_LeaseOwnershipLost_RemainsRetryable()
    {
        var policy = new JobRetryPolicy(new JobRetryOptions());
        var created = DateTime.UtcNow.AddHours(-1);
        var now = DateTime.UtcNow;

        var plan = policy.PlanFailure(
            JobFailureClass.LeaseOwnershipLost,
            currentAttempts: 3,
            maxAttempts: 40,
            jobCreatedUtc: created,
            nowUtc: now);

        plan.WillRetry.Should().BeTrue();
    }
}
