using System.Text.Json;
using GuideAnts.Usage;

namespace GuideAntsApi.Services.PublishedWireApi;

public interface IPublishedWireUsageRecorder
{
    Task RecordAsync(
        PublishedApiExecutionContext context,
        UsageCategory category,
        string service,
        string operation,
        UsageMetrics metrics,
        string endpoint,
        string status = "success",
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        long? inputCount = null,
        long? outputCount = null,
        decimal costUsd = 0m,
        string? modelDeploymentId = null,
        CancellationToken ct = default);
}

public sealed class PublishedWireUsageRecorder : IPublishedWireUsageRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IUsageRecorder _usageRecorder;

    public PublishedWireUsageRecorder(IUsageRecorder usageRecorder)
    {
        _usageRecorder = usageRecorder;
    }

    public Task RecordAsync(
        PublishedApiExecutionContext context,
        UsageCategory category,
        string service,
        string operation,
        UsageMetrics metrics,
        string endpoint,
        string status = "success",
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        long? inputCount = null,
        long? outputCount = null,
        decimal costUsd = 0m,
        string? modelDeploymentId = null,
        CancellationToken ct = default)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var metadataJson = JsonSerializer.Serialize(
            new PublishedWireUsageMetadata(
                Endpoint: endpoint,
                Alias: alias,
                ProviderModel: providerModel,
                ProviderServiceMode: providerServiceMode,
                Status: status,
                RequestBytes: requestBytes,
                InputCount: inputCount,
                OutputCount: outputCount),
            JsonOptions);

        return _usageRecorder.RecordAsync(
            projectId: context.ProjectId,
            notebookId: context.NotebookId,
            category: category,
            service: service,
            operation: operation,
            metrics: metrics,
            costUsd: costUsd,
            conversationId: null,
            contentFileId: null,
            notebookFileId: null,
            modelDeploymentId: modelDeploymentId,
            metadataJson: metadataJson,
            assistantId: context.GuideId,
            agentInvocationId: null,
            notebookConversationMessageId: null,
            ct: ct,
            publishedGuideId: context.PubId,
            sourceChannel: context.SourceChannel,
            externalRequestId: context.ExternalRequestId,
            externalUserIdentity: context.ExternalUserIdentity);
    }

    private sealed record PublishedWireUsageMetadata(
        string Endpoint,
        string? Alias,
        string? ProviderModel,
        string? ProviderServiceMode,
        string Status,
        long? RequestBytes,
        long? InputCount,
        long? OutputCount);
}
