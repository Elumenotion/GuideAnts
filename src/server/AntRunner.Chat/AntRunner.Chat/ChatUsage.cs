using Microsoft.Extensions.Logging;
using GuideAnts.Usage;

namespace AntRunner.Chat;

public static class ChatUsage
{
    private static readonly IUsageRecorderSync Recorder;

    static ChatUsage()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        Recorder = UsageRecorderFactory.CreateFromEnv(loggerFactory);
    }

    public static void RecordChatCompletion(Guid projectId, Guid notebookId, Guid conversationId,
        string service, string operation, string? modelDeploymentId,
        long inputTokens, long cachedInputTokens, long reasoningTokens, long outputTokens,
        Guid? assistantId = null, Guid? agentInvocationId = null,
        object? metadata = null)
    {
        var metrics = new UsageMetrics(inputTokens, cachedInputTokens, reasoningTokens, outputTokens);
        Recorder.RecordBlocking(
            projectId,
            notebookId,
            UsageCategory.ChatCompletion,
            service,
            operation,
            metrics,
            0m,
            conversationId,
            null,
            null,
            modelDeploymentId,
            metadata?.ToString(),
            assistantId,
            agentInvocationId);
    }

    public static void RecordToolCall(Guid projectId, Guid notebookId, Guid conversationId,
        string functionName, Guid? assistantId = null, Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null, object? metadata = null)
    {
        Recorder.RecordBlocking(
            projectId,
            notebookId,
            UsageCategory.ToolCall,
            "ToolCall",
            functionName,
            new UsageMetrics(ValueOther: 1),
            0m,
            conversationId,
            null,
            null,
            null,
            metadata?.ToString(),
            assistantId,
            agentInvocationId,
            notebookConversationMessageId);
    }

    /// <summary>
    /// Records an image generation event using the invocation context.
    /// This is used for agent invocations so that image usage is attributed
    /// to the correct <see cref="AgentInvocation"/> instead of only the
    /// notebook conversation message.
    /// </summary>
    public static void RecordImageGeneration(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        int imageCount = 1,
        long bytes = 0,
        Guid? assistantId = null,
        Guid? agentInvocationId = null,
        Guid? notebookConversationMessageId = null,
        object? metadata = null)
    {
        var metrics = new UsageMetrics(ValueOther: bytes);

        // Preserve the existing PublicApi default of encoding imageCount into
        // metadata when no explicit metadata is provided.
        var metadataJson = metadata?.ToString()
                           ?? (imageCount > 0 ? $"{{\"imageCount\":{imageCount}}}" : null);

        Recorder.RecordBlocking(
            projectId,
            notebookId,
            UsageCategory.ImageGeneration,
            "AzureOpenAI",
            "image-generation",
            metrics,
            0m,
            conversationId,
            null,
            null,
            null,
            metadataJson,
            assistantId,
            agentInvocationId,
            notebookConversationMessageId);
    }
}
