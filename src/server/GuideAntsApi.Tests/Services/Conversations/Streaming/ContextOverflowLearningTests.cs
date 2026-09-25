using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Routing;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations.Streaming;

[TestClass]
public sealed class ContextOverflowLearningTests
{
    private static ChatContextOverflowException Overflow(int? contextSize) =>
        new("too big", promptTokens: 9_000, contextSize: contextSize);

    [TestMethod]
    public void Overflow_RecordsReportedWindow()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, "model-a", Overflow(8_192));

        cache.Verify(c => c.Record("model-a", 8_192), Times.Once);
    }

    [TestMethod]
    public void Overflow_WrappedInChatConversationException_IsUnwrapped()
    {
        var cache = new Mock<ILearnedContextWindowCache>();
        var wrapped = new ChatConversationException(Overflow(4_096), null);

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, "model-a", wrapped);

        cache.Verify(c => c.Record("model-a", 4_096), Times.Once);
    }

    [TestMethod]
    public void NonOverflowException_RecordsNothing()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(
            cache.Object, "model-a", new InvalidOperationException("boom"));

        cache.Verify(c => c.Record(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [TestMethod]
    public void MissingModelIdOrCache_IsANoOp()
    {
        var cache = new Mock<ILearnedContextWindowCache>();

        ConversationStreamEngine.RecordLearnedContextWindow(cache.Object, null, Overflow(8_192));
        ConversationStreamEngine.RecordLearnedContextWindow(null, "model-a", Overflow(8_192));

        cache.Verify(c => c.Record(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }
}
