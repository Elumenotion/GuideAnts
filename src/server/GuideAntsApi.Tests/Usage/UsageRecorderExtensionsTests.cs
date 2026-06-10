using FluentAssertions;
using GuideAnts.Usage;

namespace GuideAntsApi.Tests.Usage;

[TestClass]
public sealed class UsageRecorderExtensionsTests
{
    [TestMethod]
    public async Task Extension_methods_delegate_to_record_async_with_expected_category()
    {
        var recorder = new CapturingUsageRecorder();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        await recorder.RecordChatAsync(projectId, notebookId, "AzureOpenAI", "gpt-4.1", new UsageMetrics(ValueInput: 10));
        await recorder.RecordToolCallAsync(projectId, notebookId, conversationId, "search");
        await recorder.RecordImageAsync(projectId, notebookId, null, imageCount: 2, bytes: 1024);
        await recorder.RecordSttAsync(projectId, notebookId, null, null, durationSeconds: 60);
        await recorder.RecordTtsAsync(projectId, notebookId, null, characterCount: 500, conversationId: conversationId);
        await recorder.RecordDocExtractionAsync(projectId, notebookId, null, null, pages: 3);

        recorder.Calls.Select(c => c.Category).Should().Equal(
            UsageCategory.ChatCompletion,
            UsageCategory.ToolCall,
            UsageCategory.ImageGeneration,
            UsageCategory.SpeechTranscription,
            UsageCategory.SpeechSynthesis,
            UsageCategory.DocumentExtraction);
        recorder.Calls[0].Operation.Should().Be("chat");
        recorder.Calls[1].Operation.Should().Be("search");
        recorder.Calls[5].Metrics.ValueOther.Should().Be(3);
    }

    private sealed class CapturingUsageRecorder : IUsageRecorder
    {
        public List<(UsageCategory Category, string Operation, UsageMetrics Metrics)> Calls { get; } = [];

        public Task RecordAsync(
            Guid projectId,
            Guid notebookId,
            UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            decimal costUsd = 0,
            Guid? conversationId = null,
            Guid? contentFileId = null,
            Guid? notebookFileId = null,
            string? modelDeploymentId = null,
            string? metadataJson = null,
            Guid? assistantId = null,
            Guid? agentInvocationId = null,
            Guid? notebookConversationMessageId = null,
            CancellationToken ct = default)
        {
            Calls.Add((category, operation, metrics));
            return Task.CompletedTask;
        }
    }
}
