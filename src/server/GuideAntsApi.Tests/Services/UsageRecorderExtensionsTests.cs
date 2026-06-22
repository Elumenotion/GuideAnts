using FluentAssertions;
using GuideAnts.Usage;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class UsageRecorderExtensionsTests
{
    [TestMethod]
    public async Task RecordImageAsync_UsesProvidedServiceAndOperation()
    {
        var recorder = new CapturingUsageRecorder();

        await recorder.RecordImageAsync(
            projectId: Guid.NewGuid(),
            notebookId: Guid.NewGuid(),
            notebookFileId: null,
            imageCount: 1,
            bytes: 42,
            service: "ImageGeneration.Google.Imagen",
            operation: "image-generation");

        recorder.Service.Should().Be("ImageGeneration.Google.Imagen");
        recorder.Operation.Should().Be("image-generation");
        recorder.Category.Should().Be(UsageCategory.ImageGeneration);
    }

    [TestMethod]
    public async Task RecordTtsAsync_UsesProvidedServiceAndOperation()
    {
        var recorder = new CapturingUsageRecorder();

        await recorder.RecordTtsAsync(
            projectId: Guid.NewGuid(),
            notebookId: Guid.NewGuid(),
            notebookFileId: null,
            characterCount: 123,
            service: "SpeechSynthesis.Google.TextToSpeech",
            operation: "TTS");

        recorder.Service.Should().Be("SpeechSynthesis.Google.TextToSpeech");
        recorder.Operation.Should().Be("TTS");
        recorder.Category.Should().Be(UsageCategory.SpeechSynthesis);
    }

    private sealed class CapturingUsageRecorder : IUsageRecorder
    {
        public UsageCategory? Category { get; private set; }
        public string? Service { get; private set; }
        public string? Operation { get; private set; }

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
            CancellationToken ct = default,
            Guid? publishedGuideId = null,
            string? sourceChannel = null,
            string? externalRequestId = null,
            string? externalUserIdentity = null)
        {
            Category = category;
            Service = service;
            Operation = operation;
            return Task.CompletedTask;
        }
    }
}
