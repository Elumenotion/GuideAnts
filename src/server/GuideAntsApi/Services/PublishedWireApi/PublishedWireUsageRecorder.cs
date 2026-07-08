using System.Text.Json;
using GuideAnts.Usage;
using GuideAntsApi.Services.SandboxWireApi;

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

    Task RecordAsync(
        IWireExecutionContext context,
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

    Task RecordTranscriptionAsync(
        PublishedApiExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long durationSeconds,
        long transcriptLength,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default);

    Task RecordSpeechAsync(
        PublishedApiExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long characterCount,
        long durationSeconds,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default);

    Task RecordTranscriptionAsync(
        IWireExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long durationSeconds,
        long transcriptLength,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default);

    Task RecordSpeechAsync(
        IWireExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long characterCount,
        long durationSeconds,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
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
        CancellationToken ct = default) =>
        RecordAsync(
            new PublishedWireExecutionContextAdapter(context),
            category,
            service,
            operation,
            metrics,
            endpoint,
            status,
            alias,
            providerModel,
            providerServiceMode,
            requestBytes,
            inputCount,
            outputCount,
            costUsd,
            modelDeploymentId,
            ct);

    public Task RecordAsync(
        IWireExecutionContext context,
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
            conversationId: context.AttributionConversationId,
            contentFileId: null,
            notebookFileId: null,
            modelDeploymentId: modelDeploymentId,
            metadataJson: metadataJson,
            assistantId: context.OwnerAssistantId,
            agentInvocationId: null,
            notebookConversationMessageId: null,
            ct: ct,
            publishedGuideId: context is PublishedWireExecutionContextAdapter adapter
                ? adapter.Context.PubId
                : null,
            sourceChannel: context.SourceChannel,
            externalRequestId: context.ExternalRequestId,
            externalUserIdentity: context.ExternalUserIdentity);
    }

    public Task RecordTranscriptionAsync(
        PublishedApiExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long durationSeconds,
        long transcriptLength,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default) =>
        RecordTranscriptionAsync(
            new PublishedWireExecutionContextAdapter(context),
            service,
            operation,
            endpoint,
            durationSeconds,
            transcriptLength,
            alias,
            providerModel,
            providerServiceMode,
            requestBytes,
            costUsd,
            ct);

    public Task RecordTranscriptionAsync(
        IWireExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long durationSeconds,
        long transcriptLength,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default) =>
        RecordAsync(
            context,
            UsageCategory.SpeechTranscription,
            service,
            operation,
            SpeechUsageMetrics.ForTranscription(durationSeconds, transcriptLength),
            endpoint,
            alias: alias,
            providerModel: providerModel,
            providerServiceMode: providerServiceMode,
            requestBytes: requestBytes,
            inputCount: durationSeconds,
            outputCount: transcriptLength,
            costUsd: costUsd,
            ct: ct);

    public Task RecordSpeechAsync(
        PublishedApiExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long characterCount,
        long durationSeconds,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default) =>
        RecordSpeechAsync(
            new PublishedWireExecutionContextAdapter(context),
            service,
            operation,
            endpoint,
            characterCount,
            durationSeconds,
            alias,
            providerModel,
            providerServiceMode,
            requestBytes,
            costUsd,
            ct);

    public Task RecordSpeechAsync(
        IWireExecutionContext context,
        string service,
        string operation,
        string endpoint,
        long characterCount,
        long durationSeconds,
        string? alias = null,
        string? providerModel = null,
        string? providerServiceMode = null,
        long? requestBytes = null,
        decimal costUsd = 0m,
        CancellationToken ct = default) =>
        RecordAsync(
            context,
            UsageCategory.SpeechSynthesis,
            service,
            operation,
            SpeechUsageMetrics.ForSynthesis(characterCount, durationSeconds),
            endpoint,
            alias: alias,
            providerModel: providerModel,
            providerServiceMode: providerServiceMode,
            requestBytes: requestBytes,
            inputCount: characterCount,
            outputCount: durationSeconds,
            costUsd: costUsd,
            ct: ct);

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
