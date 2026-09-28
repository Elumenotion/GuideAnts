using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations.Streaming;

[TestClass]
public sealed class CompactionBoundaryMarkerTests
{
    [TestMethod]
    public void NoBoundary_NeverEmits()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(null, currentTurnIndex: 5)
            .Should().BeFalse();
    }

    [TestMethod]
    public void FirstTurnAfterBoundary_Emits()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 4)
            .Should().BeTrue();
    }

    [TestMethod]
    public void LaterTurnAfterBoundary_DoesNotReEmit()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 5)
            .Should().BeFalse();
    }

    [TestMethod]
    public void TheBoundaryTurnItself_DoesNotEmit()
    {
        ConversationStreamEngine.ShouldEmitCompactionBoundaryMarker(3, currentTurnIndex: 3)
            .Should().BeFalse();
    }
}
