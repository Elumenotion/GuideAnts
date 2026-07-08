using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.EntityFrameworkCore;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireToolResultContinuation
{
internal sealed record AnthropicToolResult(
        string ToolCallId,
        string? Name,
        string Content);

internal static List<AnthropicToolResult> ParseOpenAiChatToolResults(JsonElement messages)
{
    var results = new List<AnthropicToolResult>();
    if (messages.ValueKind != JsonValueKind.Array)
    {
        return results;
    }

    // Only treat trailing tool messages as new tool outputs for this request.
    // Historical tool messages can be replayed in stateless clients and should not
    // trigger a resume attempt.
    foreach (var message in EnumerateTrailingArrayItems(messages, IsOpenAiChatToolMessage))
    {
        var toolCallId = message.TryGetProperty("tool_call_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            continue;
        }

        var name = message.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        var content = message.TryGetProperty("content", out var contentElement)
            ? WireJson.ExtractTextContent(contentElement) ?? string.Empty
            : string.Empty;

        results.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
    }

    return results;
}

internal static List<AnthropicToolResult> ParseOpenAiResponsesToolResults(JsonElement input)
{
    var results = new List<AnthropicToolResult>();
    if (input.ValueKind == JsonValueKind.Array)
    {
        // Like chat/messages, only trailing tool output items represent the active
        // callback payload for this request.
        foreach (var item in EnumerateTrailingArrayItems(input, IsOpenAiResponsesToolResultItem))
        {
            TryAddOpenAiResponsesToolResult(item, results);
        }
    }
    else
    {
        TryAddOpenAiResponsesToolResult(input, results);
    }

    return results;
}

internal static void TryAddOpenAiResponsesToolResult(JsonElement item, ICollection<AnthropicToolResult> destination)
{
    if (item.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
        ? typeElement.GetString()
        : null;
    if (!string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var toolCallId = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
        ? callIdElement.GetString()
        : item.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
            ? toolCallIdElement.GetString()
            : item.TryGetProperty("tool_use_id", out var toolUseIdElement) && toolUseIdElement.ValueKind == JsonValueKind.String
                ? toolUseIdElement.GetString()
                : null;
    if (string.IsNullOrWhiteSpace(toolCallId))
    {
        return;
    }

    var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
        ? nameElement.GetString()
        : null;
    var content = item.TryGetProperty("output", out var outputElement)
        ? WireJson.ExtractTextContent(outputElement) ?? string.Empty
        : item.TryGetProperty("content", out var contentElement)
            ? WireJson.ExtractTextContent(contentElement) ?? string.Empty
            : string.Empty;

    destination.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
}


internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolvePendingToolResultConversationAsync(
    PublishedApiExecutionContext context,
    ApplicationDbContext db,
    IReadOnlyList<AnthropicToolResult> toolResults,
    CancellationToken ct) =>
    await ResolvePendingToolResultConversationAsync(
        new PublishedWireExecutionContextAdapter(context),
        db,
        toolResults,
        ct);

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolvePendingToolResultConversationAsync(
    IWireExecutionContext context,
    ApplicationDbContext db,
    IReadOnlyList<AnthropicToolResult> toolResults,
    CancellationToken ct)
{
    var toolCallIds = toolResults
        .Select(r => r.ToolCallId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (toolCallIds.Length == 0)
    {
        return (null, OpenAiWireErrorResults.Create(
            StatusCodes.Status400BadRequest,
            "Tool results must include at least one tool call id.",
            type: "invalid_request_error",
            code: "invalid_tool_results",
            param: "input"));
    }

    var assistantQuery = db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m =>
            m.NotebookConversation.NotebookId == context.NotebookId &&
            m.Role == DataModelChatRole.Assistant &&
            m.ToolCalls != null);
    foreach (var toolCallId in toolCallIds)
    {
        assistantQuery = assistantQuery.Where(m => m.ToolCalls!.Contains(toolCallId));
    }

    var pendingAssistant = await assistantQuery
        .OrderByDescending(m => m.Created)
        .Select(m => new
        {
            m.NotebookConversationId,
            m.TurnIndex
        })
        .FirstOrDefaultAsync(ct);

    if (pendingAssistant == null)
    {
        return (null, OpenAiWireErrorResults.Create(
            StatusCodes.Status400BadRequest,
            "No pending tool invocation matches the supplied tool call ids.",
            type: "invalid_request_error",
            code: "tool_results_not_found",
            param: "input"));
    }

    var turn = await db.ConversationTurns
        .AsNoTracking()
        .Where(t =>
            t.NotebookConversationId == pendingAssistant.NotebookConversationId &&
            t.TurnIndex == pendingAssistant.TurnIndex)
        .Select(t => new { t.Status })
        .FirstOrDefaultAsync(ct);

    if (turn == null || !string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
    {
        return (null, OpenAiWireErrorResults.Create(
            StatusCodes.Status400BadRequest,
            "The referenced tool invocation is no longer pending.",
            type: "invalid_request_error",
            code: "tool_results_not_pending",
            param: "input"));
    }

    if (context.InternalUserId.HasValue)
    {
        var matchesUser = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                m.TurnIndex == pendingAssistant.TurnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.UserId == context.InternalUserId, ct);
        if (!matchesUser)
        {
            return (null, OpenAiWireErrorResults.Create(
                StatusCodes.Status404NotFound,
                "No matching pending tool invocation was found for this user.",
                type: "not_found_error",
                code: "tool_results_not_found",
                param: "input"));
        }
    }
    else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
    {
        var matchesIdentity = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                m.TurnIndex == pendingAssistant.TurnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.ExternalUserIdentity == context.ExternalUserIdentity, ct);
        if (!matchesIdentity)
        {
            return (null, OpenAiWireErrorResults.Create(
                StatusCodes.Status404NotFound,
                "No matching pending tool invocation was found for this user.",
                type: "not_found_error",
                code: "tool_results_not_found",
                param: "input"));
        }
    }

    return (pendingAssistant.NotebookConversationId, null);
}

internal static List<AnthropicToolResult> ParseAnthropicToolResults(JsonElement messages)
{
    var results = new List<AnthropicToolResult>();
    if (messages.ValueKind != JsonValueKind.Array)
    {
        return results;
    }

    // Anthropic tool results arrive in the latest user message. Ignore older history
    // blocks so a subsequent normal user turn does not get mistaken for a stale
    // tool callback.
    var trailingUserMessage = EnumerateTrailingArrayItems(messages, IsAnthropicUserMessage)
        .LastOrDefault();
    if (trailingUserMessage.ValueKind != JsonValueKind.Object ||
        !trailingUserMessage.TryGetProperty("content", out var contentElement))
    {
        return results;
    }

    if (contentElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var block in contentElement.EnumerateArray())
        {
            TryAddAnthropicToolResult(block, results);
        }
    }
    else
    {
        TryAddAnthropicToolResult(contentElement, results);
    }

    return results;
}

internal static bool IsOpenAiChatToolMessage(JsonElement message)
{
    if (message.ValueKind != JsonValueKind.Object ||
        !message.TryGetProperty("role", out var roleElement) ||
        roleElement.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    return string.Equals(roleElement.GetString(), "tool", StringComparison.OrdinalIgnoreCase);
}

internal static bool IsOpenAiResponsesToolResultItem(JsonElement item)
{
    if (item.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
        ? typeElement.GetString()
        : null;

    return string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase);
}

internal static bool IsAnthropicUserMessage(JsonElement message)
{
    if (message.ValueKind != JsonValueKind.Object ||
        !message.TryGetProperty("role", out var roleElement) ||
        roleElement.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    return string.Equals(roleElement.GetString(), "user", StringComparison.OrdinalIgnoreCase);
}

internal static IReadOnlyList<JsonElement> EnumerateTrailingArrayItems(
    JsonElement array,
    Func<JsonElement, bool> predicate)
{
    if (array.ValueKind != JsonValueKind.Array)
    {
        return [];
    }

    var items = array.EnumerateArray().ToList();
    if (items.Count == 0)
    {
        return [];
    }

    var trailing = new List<JsonElement>();
    for (var index = items.Count - 1; index >= 0; index--)
    {
        var item = items[index];
        if (!predicate(item))
        {
            break;
        }

        trailing.Add(item.Clone());
    }

    if (trailing.Count == 0)
    {
        return [];
    }

    trailing.Reverse();
    return trailing;
}

internal static void TryAddAnthropicToolResult(JsonElement block, ICollection<AnthropicToolResult> destination)
{
    if (block.ValueKind != JsonValueKind.Object ||
        !block.TryGetProperty("type", out var typeElement) ||
        typeElement.ValueKind != JsonValueKind.String ||
        !string.Equals(typeElement.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var toolCallId = block.TryGetProperty("tool_use_id", out var toolUseIdElement) &&
                     toolUseIdElement.ValueKind == JsonValueKind.String
        ? toolUseIdElement.GetString()
        : null;
    if (string.IsNullOrWhiteSpace(toolCallId))
    {
        return;
    }

    var name = block.TryGetProperty("name", out var nameElement) &&
               nameElement.ValueKind == JsonValueKind.String
        ? nameElement.GetString()
        : null;

    var content = block.TryGetProperty("content", out var contentElement)
        ? WireJson.ExtractTextContent(contentElement) ?? string.Empty
        : string.Empty;

    destination.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
}

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveAnthropicToolResultConversationAsync(
    PublishedApiExecutionContext context,
    ApplicationDbContext db,
    IReadOnlyList<AnthropicToolResult> toolResults,
    CancellationToken ct) =>
    await ResolveAnthropicToolResultConversationAsync(
        new PublishedWireExecutionContextAdapter(context),
        db,
        toolResults,
        ct);

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveAnthropicToolResultConversationAsync(
    IWireExecutionContext context,
    ApplicationDbContext db,
    IReadOnlyList<AnthropicToolResult> toolResults,
    CancellationToken ct)
{
    var toolCallIds = toolResults
        .Select(r => r.ToolCallId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (toolCallIds.Length == 0)
    {
        return (null, WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status400BadRequest,
            errorType: "invalid_request_error",
            message: "Tool results must include at least one 'tool_use_id'."));
    }

    var assistantQuery = db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m =>
            m.NotebookConversation.NotebookId == context.NotebookId &&
            m.Role == DataModelChatRole.Assistant &&
            m.ToolCalls != null);
    foreach (var toolCallId in toolCallIds)
    {
        assistantQuery = assistantQuery.Where(m => m.ToolCalls!.Contains(toolCallId));
    }

    var pendingAssistant = await assistantQuery
        .OrderByDescending(m => m.Created)
        .Select(m => new
        {
            m.NotebookConversationId,
            m.TurnIndex
        })
        .FirstOrDefaultAsync(ct);

    if (pendingAssistant == null)
    {
        return (null, WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status400BadRequest,
            errorType: "invalid_request_error",
            message: "No pending tool invocation matches the supplied tool_use_id values."));
    }

    var turn = await db.ConversationTurns
        .AsNoTracking()
        .Where(t =>
            t.NotebookConversationId == pendingAssistant.NotebookConversationId &&
            t.TurnIndex == pendingAssistant.TurnIndex)
        .Select(t => new { t.Status })
        .FirstOrDefaultAsync(ct);

    if (turn == null || !string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
    {
        return (null, WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status400BadRequest,
            errorType: "invalid_request_error",
            message: "The referenced tool invocation is no longer pending."));
    }

    if (context.InternalUserId.HasValue)
    {
        var matchesUser = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                m.TurnIndex == pendingAssistant.TurnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.UserId == context.InternalUserId, ct);
        if (!matchesUser)
        {
            return (null, WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status404NotFound,
                errorType: "not_found_error",
                message: "No matching pending tool invocation was found for this user."));
        }
    }
    else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
    {
        var matchesIdentity = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                m.TurnIndex == pendingAssistant.TurnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.ExternalUserIdentity == context.ExternalUserIdentity, ct);
        if (!matchesIdentity)
        {
            return (null, WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status404NotFound,
                errorType: "not_found_error",
                message: "No matching pending tool invocation was found for this user."));
        }
    }

    return (pendingAssistant.NotebookConversationId, null);
}

internal static async Task AppendAnthropicToolResultsAsync(
    ApplicationDbContext db,
    Guid conversationId,
    IReadOnlyList<AnthropicToolResult> toolResults,
    CancellationToken ct)
{
    var conversation = await db.NotebookConversations
        .Include(c => c.Turns)
        .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
        ?? throw new InvalidOperationException("Conversation not found.");

    var turn = conversation.Turns
        .OrderByDescending(t => t.TurnIndex)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("No active turn to append tool results.");

    var existingToolCallIds = await db.NotebookConversationMessages
        .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turn.TurnIndex && m.ToolCallId != null)
        .Select(m => m.ToolCallId!)
        .ToListAsync(ct);

    var nextSequence = (await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turn.TurnIndex)
            .MaxAsync(m => (int?)m.MessageSequence, ct) ?? 1) + 1;

    var now = DateTime.UtcNow;
    foreach (var result in toolResults)
    {
        if (existingToolCallIds.Any(id => string.Equals(id, result.ToolCallId, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        db.NotebookConversationMessages.Add(new GuideAntsApi.DataModel.Models.NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = turn.TurnIndex,
            MessageSequence = nextSequence++,
            Role = DataModelChatRole.Tool,
            Content = result.Content ?? string.Empty,
            ToolCallId = result.ToolCallId,
            FunctionName = result.Name ?? "external_tool",
            Created = now
        });
    }

    turn.Status = "streaming";
    turn.LastUpdated = now;
    await db.SaveChangesAsync(ct);
}
}
