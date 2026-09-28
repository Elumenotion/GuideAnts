using FluentAssertions;
using GuideAntsApi.Services.Conversations.Tracing;

namespace GuideAntsApi.Tests.Services.Conversations.Tracing;

[TestClass]
public sealed class TurnTraceCollectorTests
{
    [TestMethod]
    public void CaptureCompaction_SetsBoundaryOnTheFinalizedSegment()
    {
        var collector = new TurnTraceCollector("Claude", "gpt-4o-mini");

        collector.CaptureCompaction(3);
        var segment = collector.BuildFinalizedSegment("completed");

        segment.CompactionBoundaryTurnIndex.Should().Be(3);
    }

    [TestMethod]
    public void NoCompaction_LeavesBoundaryNull()
    {
        var collector = new TurnTraceCollector("Claude", "gpt-4o-mini");

        var segment = collector.BuildFinalizedSegment("completed");

        segment.CompactionBoundaryTurnIndex.Should().BeNull();
    }
}
