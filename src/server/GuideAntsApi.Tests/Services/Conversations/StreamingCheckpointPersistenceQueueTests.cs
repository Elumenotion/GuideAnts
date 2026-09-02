using FluentAssertions;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Streaming;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class StreamingCheckpointPersistenceQueueTests
{
    [TestMethod]
    public async Task Assistant_responses_are_separate_ordered_segments()
    {
        var firstMessageId = Guid.NewGuid();
        var secondMessageId = Guid.NewGuid();
        var persistedOperations = new List<string>();
        var startedRequests = new List<StartAssistantMessageRequest>();
        var persistence = new Mock<IConversationPersistence>();

        persistence
            .Setup(p => p.StartAssistantMessageAsync(
                It.IsAny<StartAssistantMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((StartAssistantMessageRequest request, CancellationToken _) =>
            {
                startedRequests.Add(request);
                var messageId = request.MessageSequence == 1 ? firstMessageId : secondMessageId;
                persistedOperations.Add($"start:{messageId}");
                return messageId;
            });
        persistence
            .Setup(p => p.AppendOrFinalizeAssistantMessageAsync(
                It.IsAny<AssistantMessageUpdateRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<AssistantMessageUpdateRequest, CancellationToken>((request, _) =>
                persistedOperations.Add($"finalize:{request.MessageId}"))
            .Returns(Task.CompletedTask);

        await using var queue = new StreamingCheckpointPersistenceQueue(persistence.Object);
        queue.EnsureAssistantMessage(CreateStartRequest(messageSequence: 1));
        queue.EnqueueAssistantResponse(
            () => CreateStartRequest(messageSequence: 2, isStreaming: false, content: "second"),
            messageId => new AssistantMessageUpdateRequest(
                messageId,
                Guid.Empty,
                "first",
                Finalize: true),
            onSucceeded: () => persistedOperations.Add("first-succeeded"),
            onFailed: ex => throw new InvalidOperationException("Unexpected persistence failure.", ex));
        queue.EnqueueAssistantResponse(
            () => CreateStartRequest(messageSequence: 3, isStreaming: false, content: "third"),
            messageId => new AssistantMessageUpdateRequest(
                messageId,
                Guid.Empty,
                "unused",
                Finalize: true),
            onSucceeded: () => persistedOperations.Add("second-succeeded"),
            onFailed: ex => throw new InvalidOperationException("Unexpected persistence failure.", ex));

        await queue.FlushAsync();

        queue.AssistantMessageIds.Should().Equal(firstMessageId, secondMessageId);
        queue.LastMessageId.Should().Be(secondMessageId);
        startedRequests.Should().HaveCount(2);
        startedRequests[0].IsStreaming.Should().BeTrue();
        startedRequests[1].IsStreaming.Should().BeFalse();
        persistedOperations.Should().Equal(
            $"start:{firstMessageId}",
            $"finalize:{firstMessageId}",
            "first-succeeded",
            $"start:{secondMessageId}",
            "second-succeeded");
    }

    private static StartAssistantMessageRequest CreateStartRequest(
        int messageSequence,
        bool isStreaming = true,
        string content = "")
    {
        return new StartAssistantMessageRequest(
            Guid.Empty,
            Guid.Empty,
            1,
            messageSequence,
            "assistant",
            "test-model",
            Guid.Empty,
            content,
            isStreaming);
    }
}
