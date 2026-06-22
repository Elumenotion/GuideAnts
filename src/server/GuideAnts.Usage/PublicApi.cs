namespace GuideAnts.Usage;

/// <summary>
/// Categories of usage events. Mirrors the database enum but intentionally duplicated
/// here so callers do not need EF/DataModel references.
/// </summary>
public enum UsageCategory
{
    /// <summary>ChatCompletion usage such as LLM chat token counts.</summary>
    ChatCompletion = 0,
    /// <summary>Image generation usage, bytes billed in ValueOther.</summary>
    ImageGeneration = 1,
    /// <summary>Markdown extraction page counts.</summary>
    DocumentExtraction = 2,
    /// <summary>Speech-to-text transcription seconds charged.</summary>
    SpeechTranscription = 3,
    /// <summary>Text-to-speech synthesis seconds charged.</summary>
    SpeechSynthesis = 4,
    /// <summary>Search API usage counts.</summary>
    Search = 5,
    /// <summary>Uploaded storage bytes.</summary>
    StorageUploaded = 6,
    /// <summary>System-generated storage bytes.</summary>
    StorageSystemGenerated = 7,
    /// <summary>Function/tool call counts.</summary>
    ToolCall = 8,
    /// <summary>Embedding generation usage events.</summary>
    Embeddings = 9
}

/// <summary>
/// Collection of numeric metrics associated with a usage event.
/// </summary>
/// <param name="ValueInput">Tokens or bytes provided as input.</param>
/// <param name="ValueCachedInput">Cached input tokens.</param>
/// <param name="ValueReasoning">Reasoning or intermediate step tokens.</param>
/// <param name="ValueOutput">Tokens or bytes produced as output.</param>
/// <param name="ValueOther">Any other numeric value (bytes, images, etc.).</param>
public sealed record UsageMetrics(
    long ValueInput = 0,
    long ValueCachedInput = 0,
    long ValueReasoning = 0,
    long ValueOutput = 0,
    long ValueOther = 0);

/// <summary>
/// Abstraction for recording usage events. Implementations may write directly to the database,
/// send over HTTP, etc.
/// </summary>
public interface IUsageRecorder
{
    /// <summary>
    /// Records a usage event.
    /// </summary>
    /// <param name="projectId">Project owning the notebook.</param>
    /// <param name="notebookId">Notebook where the event occurred.</param>
    /// <param name="category">Category of usage.</param>
    /// <param name="service">Provider/service name (AzureOpenAI, etc.).</param>
    /// <param name="operation">Specific operation (chat, image-generation, etc.).</param>
    /// <param name="metrics">Numeric metrics captured.</param>
    /// <param name="costUsd">Optional cost in USD.</param>
    /// <param name="conversationId">Optional conversation context.</param>
    /// <param name="contentFileId">Optional content file GUID.</param>
    /// <param name="notebookFileId">Optional notebook file GUID.</param>
    /// <param name="modelDeploymentId">Optional model deployment identifier.</param>
    /// <param name="metadataJson">Free-form JSON metadata.</param>
    /// <param name="assistantId">Optional assistant/guide that generated this usage.</param>
    /// <param name="agentInvocationId">Optional agent invocation if this usage came from an invocation.</param>
    /// <param name="notebookConversationMessageId">Optional notebook conversation message ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="publishedGuideId">Optional published guide ID attribution.</param>
    /// <param name="sourceChannel">Optional source channel attribution (for example, wire_api).</param>
    /// <param name="externalRequestId">Optional external request identifier.</param>
    /// <param name="externalUserIdentity">Optional external user identity attribution.</param>
    Task RecordAsync(
        Guid           projectId,
        Guid           notebookId,
        UsageCategory  category,
        string         service,
        string         operation,
        UsageMetrics   metrics,
        decimal        costUsd = 0m,
        Guid?          conversationId    = null,
        Guid?          contentFileId     = null,
        Guid?          notebookFileId    = null,
        string?        modelDeploymentId = null,
        string?        metadataJson      = null,
        Guid?          assistantId       = null,
        Guid?          agentInvocationId = null,
        Guid?          notebookConversationMessageId = null,
        CancellationToken ct             = default,
        Guid?          publishedGuideId  = null,
        string?        sourceChannel     = null,
        string?        externalRequestId = null,
        string?        externalUserIdentity = null);
}

/// <summary>
/// Blocking wrapper for environments that cannot flow async easily (e.g., console tools).
/// </summary>
public interface IUsageRecorderSync
{
    /// <summary>
    /// Synchronously records a usage event by blocking on the underlying async operation.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    void RecordBlocking(
        Guid projectId,
        Guid notebookId,
        UsageCategory category,
        string service,
        string operation,
        UsageMetrics metrics,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        Guid? contentFileId = null,
        Guid? notebookFileId = null,
        string? modelDeploymentId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        Guid? publishedGuideId = null,
        string? sourceChannel = null,
        string? externalRequestId = null,
        string? externalUserIdentity = null);
}

/// <summary>
/// Convenience extension helpers to reduce call-site boilerplate for common operations.
/// </summary>
public static class UsageRecorderExtensions
{
    /// <summary>
    /// Convenience wrapper for chat completion events.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordChatAsync(
        this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        string service,
        string modelDeploymentId,
        UsageMetrics metrics,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        CancellationToken ct = default,
        Guid? publishedGuideId = null,
        string? sourceChannel = null,
        string? externalRequestId = null,
        string? externalUserIdentity = null) =>
        recorder.RecordAsync(projectId, notebookId, UsageCategory.ChatCompletion, service, "chat", metrics, costUsd, conversationId, null, null, modelDeploymentId, metadataJson, assistantId, agentInvocationId, notebookConversationMessageId, ct, publishedGuideId, sourceChannel, externalRequestId, externalUserIdentity);

    /// <summary>
    /// Convenience wrapper for tool call events.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordToolCallAsync(
        this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        string functionName,
        string? metadataJson = null,
        decimal costUsd = 0m,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        CancellationToken ct = default,
        Guid? publishedGuideId = null,
        string? sourceChannel = null,
        string? externalRequestId = null,
        string? externalUserIdentity = null) =>
        recorder.RecordAsync(projectId, notebookId, UsageCategory.ToolCall, "ToolCall", functionName, new UsageMetrics(ValueOther: 1), costUsd, conversationId, null, null, null, metadataJson, assistantId, agentInvocationId, notebookConversationMessageId, ct, publishedGuideId, sourceChannel, externalRequestId, externalUserIdentity);

    /// <summary>
    /// Records an image generation event.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordImageAsync(this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        Guid? notebookFileId,
        int imageCount,
        long bytes,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        CancellationToken ct = default,
        string service = "AzureOpenAI",
        string operation = "image-generation")
        => recorder.RecordAsync(projectId, notebookId, UsageCategory.ImageGeneration,
            service, operation,
            new UsageMetrics(ValueOther: bytes), costUsd, conversationId, null, notebookFileId,
            null,
            metadataJson ?? (imageCount > 0 ? $"{{\"imageCount\":{imageCount}}}" : null),
            assistantId, agentInvocationId, notebookConversationMessageId, ct);

    /// <summary>
    /// Records a speech transcription (STT) event.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordSttAsync(this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        Guid? contentFileId,
        Guid? notebookFileId,
        long durationSeconds,
        decimal costUsd = 0m,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        CancellationToken ct = default)
        => recorder.RecordAsync(projectId, notebookId, UsageCategory.SpeechTranscription,
            "AzureSpeech", "STT",
            new UsageMetrics(ValueOther: durationSeconds), costUsd, null, contentFileId, notebookFileId,
            null, metadataJson, assistantId, agentInvocationId, null, ct);

    /// <summary>
    /// Records a text-to-speech (TTS) synthesis event.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordTtsAsync(this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        Guid? notebookFileId,
        long characterCount,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        CancellationToken ct = default,
        string service = "AzureSpeech",
        string operation = "TTS",
        Guid? notebookConversationMessageId = null)
        => recorder.RecordAsync(projectId, notebookId, UsageCategory.SpeechSynthesis,
            service, operation,
            new UsageMetrics(ValueOther: characterCount), costUsd, conversationId, null, notebookFileId,
            null, metadataJson, assistantId, agentInvocationId, notebookConversationMessageId, ct);

    /// <summary>
    /// Records a document extraction event.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordDocExtractionAsync(this IUsageRecorder recorder,
        Guid projectId,
        Guid? notebookId,
        Guid? contentFileId,
        Guid? notebookFileId,
        int pages,
        string service = "AzureDocumentIntelligence",
        string operation = "prebuilt-read",
        decimal costUsd = 0m,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        CancellationToken ct = default) =>
        recorder.RecordAsync(projectId, notebookId ?? Guid.Empty, UsageCategory.DocumentExtraction,
            service, operation,
            new UsageMetrics(ValueOther: pages), costUsd, null, contentFileId, notebookFileId,
            null, metadataJson, assistantId, agentInvocationId, null, ct);

    /// <summary>
    /// Records an embeddings event.
    /// </summary>
    /// <inheritdoc cref="IUsageRecorder.RecordAsync"/>
    public static Task RecordEmbeddingsAsync(
        this IUsageRecorder recorder,
        Guid projectId,
        Guid notebookId,
        string service,
        long inputCount,
        long outputCount = 0,
        decimal costUsd = 0m,
        Guid? conversationId = null,
        string? modelDeploymentId = null,
        string? metadataJson = null,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        CancellationToken ct = default)
        => recorder.RecordAsync(
            projectId: projectId,
            notebookId: notebookId,
            category: UsageCategory.Embeddings,
            service: service,
            operation: "embeddings",
            metrics: new UsageMetrics(ValueInput: inputCount, ValueOutput: outputCount),
            costUsd: costUsd,
            conversationId: conversationId,
            contentFileId: null,
            notebookFileId: null,
            modelDeploymentId: modelDeploymentId,
            metadataJson: metadataJson,
            assistantId: assistantId,
            agentInvocationId: agentInvocationId,
            notebookConversationMessageId: notebookConversationMessageId,
            ct: ct);
}
