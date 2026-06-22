using System.Text.Json;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedWireApi;
using UsageCategory = GuideAnts.Usage.UsageCategory;
using PublishedGuide = GuideAntsApi.DataModel.Models.PublishedGuide;

namespace GuideAntsApi.Tests.Services.PublishedWireApi;

[TestClass]
public sealed class PublishedWireUsageRecorderTests
{
    [TestMethod]
    public async Task RecordAsync_uses_execution_context_for_required_attribution()
    {
        var fake = new CapturingUsageRecorder();
        var sut = new PublishedWireUsageRecorder(fake);
        var context = CreateContext();

        await sut.RecordAsync(
            context: context,
            category: UsageCategory.Embeddings,
            service: "Embeddings.OpenAI.Embedding",
            operation: "embeddings",
            metrics: new UsageMetrics(ValueInput: 42, ValueOutput: 42),
            endpoint: "embeddings",
            alias: "embeddings",
            providerModel: "text-embedding-3-large",
            providerServiceMode: "default",
            requestBytes: 128,
            inputCount: 42,
            outputCount: 42,
            ct: CancellationToken.None);

        fake.PublishedGuideId.Should().Be(context.PubId);
        fake.SourceChannel.Should().Be("wire_api");
        fake.ExternalRequestId.Should().Be(context.ExternalRequestId);
        fake.ExternalUserIdentity.Should().Be(context.ExternalUserIdentity);
        fake.Category.Should().Be(UsageCategory.Embeddings);
        fake.Operation.Should().Be("embeddings");
        fake.Service.Should().Be("Embeddings.OpenAI.Embedding");

        using var metadata = JsonDocument.Parse(fake.MetadataJson!);
        metadata.RootElement.GetProperty("endpoint").GetString().Should().Be("embeddings");
        metadata.RootElement.GetProperty("alias").GetString().Should().Be("embeddings");
        metadata.RootElement.GetProperty("providerModel").GetString().Should().Be("text-embedding-3-large");
        metadata.RootElement.GetProperty("status").GetString().Should().Be("success");
    }

    [TestMethod]
    public async Task RecordAsync_does_not_swallow_usage_recorder_failures()
    {
        var sut = new PublishedWireUsageRecorder(new ThrowingUsageRecorder());

        var act = async () => await sut.RecordAsync(
            context: CreateContext(),
            category: UsageCategory.SpeechTranscription,
            service: "SpeechTranscription",
            operation: "audio.transcriptions",
            metrics: new UsageMetrics(ValueOther: 12),
            endpoint: "audio.transcriptions",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*usage write failed*");
    }

    private static PublishedApiExecutionContext CreateContext() =>
        new(
            PubId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            NotebookId: Guid.NewGuid(),
            GuideId: Guid.NewGuid(),
            PublishedGuide: new PublishedGuide(),
            WireApiConfig: new PublishedWireApiConfigDto(),
            AuthMode: PublishedApiAuthMode.ApiKey,
            ExternalUserIdentity: "ext-user",
            InternalUserId: null,
            SourceChannel: "wire_api",
            ExternalRequestId: "req_abc123",
            EndpointName: "embeddings");

    private sealed class CapturingUsageRecorder : IUsageRecorder
    {
        public UsageCategory? Category { get; private set; }
        public string? Service { get; private set; }
        public string? Operation { get; private set; }
        public string? MetadataJson { get; private set; }
        public Guid? PublishedGuideId { get; private set; }
        public string? SourceChannel { get; private set; }
        public string? ExternalRequestId { get; private set; }
        public string? ExternalUserIdentity { get; private set; }

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
            MetadataJson = metadataJson;
            PublishedGuideId = publishedGuideId;
            SourceChannel = sourceChannel;
            ExternalRequestId = externalRequestId;
            ExternalUserIdentity = externalUserIdentity;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingUsageRecorder : IUsageRecorder
    {
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
            string? externalUserIdentity = null) =>
            throw new InvalidOperationException("usage write failed");
    }
}
