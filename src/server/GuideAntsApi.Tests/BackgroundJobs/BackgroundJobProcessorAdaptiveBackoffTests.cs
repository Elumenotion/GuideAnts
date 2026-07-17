using FluentAssertions;
using GuideAntsApi.BackgroundJobs;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class BackgroundJobProcessorAdaptiveBackoffTests
{
    [TestMethod]
    public void NextDelayWithJitter_EscalatesAndCaps()
    {
        var backoff = new AdaptiveLoopBackoff([5, 10, 20, 40, 60]);
        var random = new Random(7);

        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(4.5, 5.5);
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(9.0, 11.0);
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(18.0, 22.0);
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(36.0, 44.0);
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(54.0, 66.0);
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(54.0, 66.0);
    }

    [TestMethod]
    public void Reset_RestartsEscalationSequence()
    {
        var backoff = new AdaptiveLoopBackoff([5, 10, 20]);
        var random = new Random(1);

        backoff.NextDelayWithJitter(random);
        backoff.NextDelayWithJitter(random);
        backoff.Reset();
        backoff.NextDelayWithJitter(random).TotalSeconds.Should().BeInRange(4.5, 5.5);
    }
}
