using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpPublishedGuideInvokeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IPublishedConversationService _conversations;
    private readonly ApplicationDbContext _db;

    public McpPublishedGuideInvokeService(
        IPublishedConversationService conversations,
        ApplicationDbContext db)
    {
        _conversations = conversations;
        _db = db;
    }

    public async Task<McpAssistantInvokeResult> InvokeAssistantAsync(
        McpAddressableAssistant assistant,
        string instructions,
        string? conversationId,
        string? title,
        McpPublishedGuideContext mcpContext,
        CancellationToken cancellationToken)
    {
        if (!mcpContext.IsValid)
            return Failure(JsonError("unauthorized", "MCP context is not valid."));

        if (string.IsNullOrWhiteSpace(instructions))
            return Failure(JsonError("missing_instructions", "The instructions parameter is required."));

        Guid convoId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            var convoTitle = string.IsNullOrWhiteSpace(title)
                ? DateTime.UtcNow.ToString("f", System.Globalization.CultureInfo.InvariantCulture)
                : title.Trim();

            var conversation = await _conversations.CreateConversationAsync(mcpContext.NotebookId, convoTitle);
            convoId = conversation.Id;
        }
        else
        {
            if (!Guid.TryParse(conversationId, out convoId))
                return Failure(JsonError("invalid_conversation_id", "The conversationId is not a valid GUID."));

            var belongs = await _db.NotebookConversations
                .AsNoTracking()
                .AnyAsync(c => c.Id == convoId && c.NotebookId == mcpContext.NotebookId, cancellationToken);
            if (!belongs)
                return Failure(JsonError("not_found", "Conversation not found in this published guide notebook."));
        }

        var request = new SendMessageRequest
        {
            Instructions = instructions.Trim(),
            AssistantName = assistant.Name
        };

        string? errorMessage = null;
        var pendingClientTool = false;

        await foreach (var ev in _conversations.SendMessageStreamAsync(
                           convoId,
                           request,
                           mcpContext.PubId.ToString(),
                           mcpContext.UserIdentity,
                           internalUserId: null,
                           cancellationToken))
        {
            switch (ev.EventType)
            {
                case StreamingEventTypes.PendingClientTool:
                case StreamingEventTypes.ExternalToolCall:
                    pendingClientTool = true;
                    break;
                case StreamingEventTypes.Error:
                    errorMessage = ev.Payload;
                    break;
            }
        }

        if (pendingClientTool)
        {
            return Failure(JsonError(
                "client_tools_not_supported",
                $"Assistant '{assistant.Name}' requires client-side tools, which are not available over MCP."));
        }

        if (errorMessage != null)
            return Failure(JsonError("invoke_failed", errorMessage));

        var assistantMessage = await _db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == convoId
                        && m.Role == ChatRole.Assistant
                        && m.AssistantName == assistant.Name
                        && m.IsStreaming != true)
            .OrderByDescending(m => m.TurnIndex)
            .ThenByDescending(m => m.MessageSequence)
            .Select(m => new { m.Content })
            .FirstOrDefaultAsync(cancellationToken);

        // The turn (with its tracked output files) is persisted by the time the stream completes.
        var latestTurnIndex = await _db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == convoId)
            .MaxAsync(t => (int?)t.TurnIndex, cancellationToken);

        var json = JsonSerialize(new
        {
            assistantId = assistant.AssistantId,
            assistantName = assistant.Name,
            conversationId = convoId,
            response = RewriteForMcpClient(assistantMessage?.Content, mcpContext)
        });

        return new McpAssistantInvokeResult(json, convoId, latestTurnIndex);
    }

    public async Task<string> GetConversationAsync(
        string conversationId,
        McpPublishedGuideContext mcpContext,
        CancellationToken cancellationToken)
    {
        if (!mcpContext.IsValid)
            return JsonError("unauthorized", "MCP context is not valid.");

        if (!Guid.TryParse(conversationId, out var convoId))
            return JsonError("invalid_conversation_id", "The conversationId is not a valid GUID.");

        var belongs = await _db.NotebookConversations
            .AsNoTracking()
            .AnyAsync(c => c.Id == convoId && c.NotebookId == mcpContext.NotebookId, cancellationToken);
        if (!belongs)
            return JsonError("not_found", "Conversation not found.");

        var convo = await _conversations.GetConversationWithMessagesAsync(convoId);
        if (convo == null)
            return JsonError("not_found", "Conversation not found.");

        return JsonSerialize(new
        {
            conversationId = convo.Id,
            title = convo.Title,
            currentAssistant = convo.AssistantName,
            created = convo.Created,
            messages = convo.Messages.Select(m => new
            {
                role = m.Role.ToString().ToLowerInvariant(),
                assistantName = m.AssistantName,
                content = RewriteForMcpClient(m.Content, mcpContext),
                created = m.Created,
                turnIndex = m.TurnIndex
            })
        });
    }

    private static string RewriteForMcpClient(string? content, McpPublishedGuideContext mcpContext) =>
        McpPublishedContentUrlRewriter.Rewrite(content, mcpContext.PublicApiOrigin);

    private static string JsonSerialize(object value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static McpAssistantInvokeResult Failure(string json) =>
        new(json, null, null);

    public static string JsonError(string code, string message) =>
        JsonSerialize(new { error = code, message });
}
