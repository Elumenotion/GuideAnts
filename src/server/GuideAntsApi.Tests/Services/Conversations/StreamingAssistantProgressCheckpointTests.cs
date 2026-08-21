using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class StreamingAssistantProgressCheckpointTests
{
    [TestMethod]
    public void ShouldCheckpoint_returns_false_until_flush_interval()
    {
        var scheduler = new StreamingAssistantProgressCheckpoint(flushInterval: 5);

        for (var i = 0; i < 4; i++)
        {
            scheduler.ShouldCheckpoint().Should().BeFalse();
        }

        scheduler.ShouldCheckpoint().Should().BeTrue();
        scheduler.FlushCounter.Should().Be(5);
    }

    [TestMethod]
    public void ShouldCheckpoint_repeats_every_flush_interval()
    {
        var scheduler = new StreamingAssistantProgressCheckpoint(flushInterval: 3);

        scheduler.ShouldCheckpoint().Should().BeFalse();
        scheduler.ShouldCheckpoint().Should().BeFalse();
        scheduler.ShouldCheckpoint().Should().BeTrue();
        scheduler.ShouldCheckpoint().Should().BeFalse();
        scheduler.ShouldCheckpoint().Should().BeFalse();
        scheduler.ShouldCheckpoint().Should().BeTrue();
        scheduler.FlushCounter.Should().Be(6);
    }

    [TestMethod]
    public void Constructor_rejects_non_positive_interval()
    {
        var act = () => new StreamingAssistantProgressCheckpoint(flushInterval: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
