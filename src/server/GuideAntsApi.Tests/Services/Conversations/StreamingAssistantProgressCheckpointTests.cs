using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class StreamingAssistantProgressCheckpointTests
{
    [TestMethod]
    public void ShouldCheckpoint_returns_true_on_first_delta()
    {
        var scheduler = new StreamingAssistantProgressCheckpoint();

        scheduler.ShouldCheckpoint(1).Should().BeTrue();
        scheduler.FlushCounter.Should().Be(1);
    }

    [TestMethod]
    public void ShouldCheckpoint_returns_true_after_byte_threshold()
    {
        var scheduler = new StreamingAssistantProgressCheckpoint();

        scheduler.ShouldCheckpoint(1).Should().BeTrue();
        scheduler.ShouldCheckpoint(600).Should().BeTrue();
        scheduler.FlushCounter.Should().Be(2);
    }

    [TestMethod]
    public void ShouldCheckpoint_rate_limits_to_four_per_second()
    {
        var scheduler = new StreamingAssistantProgressCheckpoint();

        Enumerable.Range(0, 4).Select(_ => scheduler.ShouldCheckpoint(600)).Should().OnlyContain(x => x);
        scheduler.ShouldCheckpoint(600).Should().BeFalse();
        scheduler.FlushCounter.Should().Be(5);
    }
}
