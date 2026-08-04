using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.PublishedWireApi;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireConversationExecutor
{
internal const string DefaultConversationTitle = "New Conversation";

    internal sealed record WireConversationStreamHandle(
        Guid ConversationId,
        bool CreatedConversation,
        IAsyncEnumerable<StreamingEvent> Events);

    internal sealed record WireConversationResult(
        Guid ConversationId,
        string Text,
        long PromptTokens,
        long CompletionTokens,
        bool PendingClientTool,
        string? ErrorPayload,
        string? ResponseId,
        Guid? AssistantMessageId,
        IReadOnlyList<ChatToolCall> ExternalToolCalls);

internal static async Task<WireConversationStreamHandle> StartConversationStreamAsync(
    IPublishedConversationService publishedConversationService,
    PublishedApiExecutionContext context,
    string instructions,
    CancellationToken ct,
    Guid? existingConversationId = null,
    IReadOnlyList<ChatMessage>? clientMessages = null,
    IReadOnlyList<ChatToolDefinition>? clientToolDefinitions = null,
    IReadOnlyList<AttachmentDto>? attachments = null)
{
    var createdConversation = !existingConversationId.HasValue;
    var conversationId = existingConversationId ??
        (await publishedConversationService.CreateConversationAsync(
            context.NotebookId,
            DefaultConversationTitle)).Id;
    var request = new SendMessageRequest
    {
        Instructions = instructions,
        Attachments = attachments == null ? null : [.. attachments],
        ClientMessages = clientMessages == null ? null : [.. clientMessages],
        ClientToolDefinitions = clientToolDefinitions == null ? null : [.. clientToolDefinitions]
    };

    var stream = publishedConversationService.SendMessageStreamAsync(
        conversationId,
        request,
        context.PubId.ToString(),
        context.ExternalUserIdentity,
        context.InternalUserId,
        ct);

    return new WireConversationStreamHandle(conversationId, createdConversation, stream);
}

internal static WireConversationStreamHandle StartResumeConversationStream(
    IPublishedConversationService publishedConversationService,
    PublishedApiExecutionContext context,
    Guid conversationId,
    IReadOnlyList<ChatToolDefinition>? clientToolDefinitions,
    CancellationToken ct)
{
    var stream = publishedConversationService.ResumeAfterExternalToolResultsStreamAsync(
        conversationId,
        context.PubId.ToString(),
        context.ExternalUserIdentity,
        context.InternalUserId,
        clientToolDefinitions,
        ct);

    return new WireConversationStreamHandle(conversationId, CreatedConversation: false, stream);
}

internal static async Task<WireConversationResult> ExecuteConversationAsync(
    IPublishedConversationService publishedConversationService,
    ApplicationDbContext db,
    PublishedApiExecutionContext context,
    string instructions,
    CancellationToken ct,
    Guid? existingConversationId = null,
    IReadOnlyList<ChatMessage>? clientMessages = null,
    IReadOnlyList<ChatToolDefinition>? clientToolDefinitions = null,
    IReadOnlyList<AttachmentDto>? attachments = null)
{
    var streamHandle = await StartConversationStreamAsync(
        publishedConversationService,
        context,
        instructions,
        ct,
        existingConversationId,
        clientMessages,
        clientToolDefinitions,
        attachments);

    var result = await CollectWireConversationResultAsync(streamHandle.Events, db, streamHandle.ConversationId, ct);
    if (streamHandle.CreatedConversation)
    {
        await TryGenerateWireConversationTitleAsync(db, streamHandle.ConversationId, ct);
    }

    return result;
}

internal static async Task<WireConversationResult> ResumeConversationAfterToolResultsAsync(
    IPublishedConversationService publishedConversationService,
    ApplicationDbContext db,
    PublishedApiExecutionContext context,
    Guid conversationId,
    IReadOnlyList<ChatToolDefinition>? clientToolDefinitions,
    CancellationToken ct)
{
    var streamHandle = StartResumeConversationStream(
        publishedConversationService,
        context,
        conversationId,
        clientToolDefinitions,
        ct);

    return await CollectWireConversationResultAsync(streamHandle.Events, db, conversationId, ct);
}

internal static async Task<WireConversationResult> CollectWireConversationResultAsync(
    IAsyncEnumerable<StreamingEvent> stream,
    ApplicationDbContext db,
    Guid conversationId,
    CancellationToken ct)
{
    var assistantText = new StringBuilder();
    long promptTokens = 0;
    long completionTokens = 0;
    string? errorPayload = null;
    var pendingClientTool = false;
    var externalToolCalls = new List<ChatToolCall>();

    await foreach (var ev in stream)
    {
        if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
        {
            pendingClientTool = true;
            continue;
        }

        if (string.Equals(ev.EventType, StreamingEventTypes.ExternalToolCall, StringComparison.Ordinal))
        {
            externalToolCalls.AddRange(ParseExternalToolCallsFromPayload(ev.Payload));
            continue;
        }

        if (string.Equals(ev.EventType, StreamingEventTypes.Error, StringComparison.Ordinal))
        {
            errorPayload = ev.Payload;
            continue;
        }

        if (string.Equals(ev.EventType, StreamingEventTypes.Usage, StringComparison.Ordinal))
        {
            WireStreamPayloadReader.ReadUsagePayload(ev.Payload, out promptTokens, out completionTokens);
            continue;
        }

        if (string.Equals(ev.EventType, StreamingEventTypes.AssistantMessage, StringComparison.Ordinal) ||
            string.Equals(ev.EventType, StreamingEventTypes.Message, StringComparison.Ordinal))
        {
            var content = WireStreamPayloadReader.ReadContentPayload(ev.Payload);
            if (!string.IsNullOrWhiteSpace(content))
            {
                assistantText.Clear();
                assistantText.Append(content);
            }
        }
        else if (string.Equals(ev.EventType, StreamingEventTypes.Token, StringComparison.Ordinal))
        {
            var delta = WireStreamPayloadReader.ReadContentDeltaPayload(ev.Payload);
            if (!string.IsNullOrWhiteSpace(delta))
            {
                assistantText.Append(delta);
            }
        }
    }

    var latestAssistantMessageId = await WireConversationResolver.ResolveLatestAssistantMessageIdAsync(db, conversationId, ct);
    var responseId = latestAssistantMessageId.HasValue
        ? WireIdCodec.FormatResponsesId(latestAssistantMessageId.Value)
        : null;

    return new WireConversationResult(
        ConversationId: conversationId,
        Text: assistantText.ToString(),
        PromptTokens: promptTokens,
        CompletionTokens: completionTokens,
        PendingClientTool: pendingClientTool,
        ErrorPayload: errorPayload,
        ResponseId: responseId,
        AssistantMessageId: latestAssistantMessageId,
        ExternalToolCalls: externalToolCalls);
}

internal static async Task TryGenerateWireConversationTitleAsync(
    ApplicationDbContext db,
    Guid conversationId,
    CancellationToken ct)
{
    try
    {
        await ConversationTitleGenerator.GenerateAndApplyAsync(db, conversationId, ct);
    }
    catch
    {
        // best effort
    }
}

internal static IReadOnlyList<ChatToolCall> ParseExternalToolCallsFromPayload(string payload)
{
    if (string.IsNullOrWhiteSpace(payload))
    {
        return [];
    }

    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("toolCalls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<ChatToolCall>>(toolCalls.GetRawText(), WireJson.SerializationOptions) ?? [];
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<ChatToolCall>>(root.GetRawText(), WireJson.SerializationOptions) ?? [];
        }
    }
    catch
    {
        // ignore malformed payloads
    }

    return [];
}
}
