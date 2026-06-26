using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.PublishedWireApi;
using Microsoft.EntityFrameworkCore;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireConversationResolver
{
internal static readonly TimeSpan TranscriptContinuationWindow = TimeSpan.FromMinutes(60);

    internal sealed record AnthropicConversationResolution(
        Guid? ConversationId,
        IResult? ErrorResult);

    internal sealed record WireTranscriptMessage(
        string Role,
        string Text,
        string? ToolCallId,
        IReadOnlyList<string> ToolCallIds);

    internal sealed record PersistedHistoryRow(
        DataModelChatRole Role,
        string? Content,
        string? ToolCallId,
        string? ToolCallsJson);

internal static async Task<AnthropicConversationResolution> ResolveConversationFromMessageIdsAsync(
    PublishedApiExecutionContext context,
    JsonElement messages,
    ApplicationDbContext db,
    CancellationToken ct)
{
    if (!TryFindLatestAnthropicAssistantMessageId(messages, out var previousMessageId))
    {
        return new AnthropicConversationResolution(null, null);
    }

    var previousAssistant = await db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m => m.Id == previousMessageId && m.Role == DataModelChatRole.Assistant)
        .Select(m => new
        {
            m.Id,
            m.NotebookConversationId,
            m.TurnIndex,
            NotebookId = m.NotebookConversation.NotebookId
        })
        .FirstOrDefaultAsync(ct);

    if (previousAssistant == null || previousAssistant.NotebookId != context.NotebookId)
    {
        return new AnthropicConversationResolution(
            null,
            WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status404NotFound,
                errorType: "not_found_error",
                message: "The referenced message id was not found for this published guide."));
    }

    if (!await TurnMatchesCallerScopeAsync(
            context,
            db,
            previousAssistant.NotebookConversationId,
            previousAssistant.TurnIndex,
            ct))
    {
        return new AnthropicConversationResolution(
            null,
            WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status404NotFound,
                errorType: "not_found_error",
                message: "The referenced message id is not accessible for this caller."));
    }

    var latestAssistantMessageId = await ResolveLatestAssistantMessageIdAsync(
        db,
        previousAssistant.NotebookConversationId,
        ct);
    if (latestAssistantMessageId != previousAssistant.Id)
    {
        return new AnthropicConversationResolution(
            null,
            WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: "The referenced message id is not the latest assistant message in this conversation."));
    }

    return new AnthropicConversationResolution(previousAssistant.NotebookConversationId, null);
}

internal static async Task<Guid?> ResolveConversationFromTranscriptAsync(
    PublishedApiExecutionContext context,
    IReadOnlyList<ChatMessage> prefixMessages,
    ApplicationDbContext db,
    CancellationToken ct)
{
    var expectedHistory = BuildWireTranscriptHistory(prefixMessages);
    if (expectedHistory.Count == 0)
    {
        return null;
    }

    var latestExpectedAssistant = expectedHistory
        .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.Ordinal));
    if (latestExpectedAssistant == null)
    {
        return null;
    }

    var windowStart = DateTime.UtcNow - TranscriptContinuationWindow;
    var candidateSummaries = await db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m =>
            m.NotebookConversation.NotebookId == context.NotebookId &&
            m.IsStreaming != true)
        .GroupBy(m => m.NotebookConversationId)
        .Where(g => g.Max(m => m.Created) >= windowStart)
        .Select(g => new
        {
            ConversationId = g.Key,
            LastTurnIndex = g.Max(m => m.TurnIndex),
            LastActivity = g.Max(m => m.Created)
        })
        .OrderByDescending(x => x.LastTurnIndex)
        .ThenByDescending(x => x.LastActivity)
        .Take(50)
        .ToListAsync(ct);

    Guid? matchedConversationId = null;
    var matchCount = 0;

    foreach (var candidate in candidateSummaries)
    {
        if (!await ConversationMatchesCallerIdentityAsync(context, db, candidate.ConversationId, ct))
        {
            continue;
        }

        var latestAssistantMessageId = await ResolveLatestAssistantMessageIdAsync(
            db,
            candidate.ConversationId,
            ct);
        if (!latestAssistantMessageId.HasValue)
        {
            continue;
        }

        var latestAssistant = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.Id == latestAssistantMessageId.Value)
            .Select(m => new { m.Content, m.ToolCalls, m.TurnIndex })
            .FirstOrDefaultAsync(ct);
        if (latestAssistant == null ||
            !WireTranscriptMessageMatchesAssistant(
                latestExpectedAssistant,
                latestAssistant.Content,
                latestAssistant.ToolCalls))
        {
            continue;
        }

        if (!await TurnMatchesCallerScopeAsync(
                context,
                db,
                candidate.ConversationId,
                latestAssistant.TurnIndex,
                ct))
        {
            continue;
        }

        var persistedRows = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == candidate.ConversationId &&
                m.IsStreaming != true)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .Select(m => new PersistedHistoryRow(
                m.Role,
                m.Content,
                m.ToolCallId,
                m.ToolCalls))
            .ToListAsync(ct);
        var persistedHistory = BuildPersistedTranscriptHistory(persistedRows);
        if (!TranscriptEndsWith(persistedHistory, expectedHistory))
        {
            continue;
        }

        matchCount++;
        matchedConversationId = candidate.ConversationId;
    }

    return matchCount == 1 ? matchedConversationId : null;
}

internal static async Task<bool> ConversationMatchesCallerIdentityAsync(
    PublishedApiExecutionContext context,
    ApplicationDbContext db,
    Guid conversationId,
    CancellationToken ct)
{
    if (context.InternalUserId.HasValue)
    {
        return await db.NotebookConversationMessages
            .AsNoTracking()
            .AnyAsync(
                m =>
                    m.NotebookConversationId == conversationId &&
                    m.Role == DataModelChatRole.User &&
                    m.UserId == context.InternalUserId,
                ct);
    }

    if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
    {
        return await db.NotebookConversationMessages
            .AsNoTracking()
            .AnyAsync(
                m =>
                    m.NotebookConversationId == conversationId &&
                    m.Role == DataModelChatRole.User &&
                    m.ExternalUserIdentity == context.ExternalUserIdentity,
                ct);
    }

    return true;
}

internal static bool WireTranscriptMessageMatchesAssistant(
    WireTranscriptMessage expected,
    string? persistedContent,
    string? persistedToolCallsJson)
{
    if (!string.IsNullOrWhiteSpace(expected.Text))
    {
        var persistedText = NormalizeTranscriptText(persistedContent);
        if (!string.Equals(persistedText, expected.Text, StringComparison.Ordinal))
        {
            return false;
        }
    }

    var persistedToolCallIds = ExtractToolCallIds(persistedToolCallsJson);
    return ToolCallIdsMatch(persistedToolCallIds, expected.ToolCallIds);
}

internal static IReadOnlyList<WireTranscriptMessage> BuildWireTranscriptHistory(
    IReadOnlyList<ChatMessage> messages)
{
    var history = new List<WireTranscriptMessage>();
    foreach (var message in messages)
    {
        var role = message.Role switch
        {
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => null
        };
        if (role == null)
        {
            continue;
        }

        var text = NormalizeTranscriptText(message.GetText());
        var toolCallId = NormalizeTranscriptId(message.ToolCallId);
        var toolCallIds = message.ToolCalls?
            .Select(t => NormalizeTranscriptId(t.Id))
            .Where(id => id != null)
            .Select(id => id!)
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(text) &&
            string.IsNullOrWhiteSpace(toolCallId) &&
            toolCallIds.Count == 0)
        {
            continue;
        }

        history.Add(new WireTranscriptMessage(role, text, toolCallId, toolCallIds));
    }

    return history;
}

internal static IReadOnlyList<WireTranscriptMessage> BuildPersistedTranscriptHistory(
    IReadOnlyList<PersistedHistoryRow> rows)
{
    var history = new List<WireTranscriptMessage>();
    foreach (var row in rows)
    {
        var role = row.Role switch
        {
            DataModelChatRole.User => "user",
            DataModelChatRole.Assistant => "assistant",
            DataModelChatRole.Tool => "tool",
            _ => null
        };
        if (role == null)
        {
            continue;
        }

        var text = NormalizeTranscriptText(row.Content);
        var toolCallId = NormalizeTranscriptId(row.ToolCallId);
        var toolCallIds = ExtractToolCallIds(row.ToolCallsJson);
        if (string.IsNullOrWhiteSpace(text) &&
            string.IsNullOrWhiteSpace(toolCallId) &&
            toolCallIds.Count == 0)
        {
            continue;
        }

        history.Add(new WireTranscriptMessage(role, text, toolCallId, toolCallIds));
    }

    return history;
}

internal static bool TranscriptEndsWith(
    IReadOnlyList<WireTranscriptMessage> persistedHistory,
    IReadOnlyList<WireTranscriptMessage> expectedHistory)
{
    if (expectedHistory.Count == 0)
    {
        return false;
    }

    var persistedIndex = persistedHistory.Count - 1;
    for (var expectedIndex = expectedHistory.Count - 1; expectedIndex >= 0; expectedIndex--)
    {
        // If we run out of persisted history but still have expected history,
        // we assume the remaining expected messages are client-injected unpersisted prefixes
        // (e.g. <environment_context>). Since we matched from the end backwards, this is a valid continuation.
        if (persistedIndex < 0)
        {
            break;
        }

        var matched = false;
        while (persistedIndex >= 0)
        {
            if (TranscriptMessagesEqual(persistedHistory[persistedIndex], expectedHistory[expectedIndex]))
            {
                matched = true;
                persistedIndex--;
                break;
            }

            persistedIndex--;
        }

        if (!matched)
        {
            return false;
        }
    }

    return true;
}

internal static bool TranscriptMessagesEqual(WireTranscriptMessage left, WireTranscriptMessage right)
{
    return string.Equals(left.Role, right.Role, StringComparison.Ordinal) &&
           string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
           string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal) &&
           ToolCallIdsMatch(left.ToolCallIds, right.ToolCallIds);
}

internal static bool ToolCallIdsMatch(IReadOnlyList<string> persistedIds, IReadOnlyList<string> expectedIds)
{
    if (expectedIds.Count == 0)
    {
        return persistedIds.Count == 0;
    }

    if (persistedIds.Count < expectedIds.Count)
    {
        return false;
    }

    var persistedIndex = 0;
    foreach (var expectedId in expectedIds)
    {
        var found = false;
        while (persistedIndex < persistedIds.Count)
        {
            if (string.Equals(persistedIds[persistedIndex], expectedId, StringComparison.Ordinal))
            {
                found = true;
                persistedIndex++;
                break;
            }

            persistedIndex++;
        }

        if (!found)
        {
            return false;
        }
    }

    return true;
}

internal static string NormalizeTranscriptText(string? text) =>
    string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

internal static string? NormalizeTranscriptId(string? id) =>
    string.IsNullOrWhiteSpace(id) ? null : id.Trim();

internal static IReadOnlyList<string> ExtractToolCallIds(string? toolCallsJson)
{
    if (string.IsNullOrWhiteSpace(toolCallsJson))
    {
        return [];
    }

    try
    {
        using var document = JsonDocument.Parse(toolCallsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : item.TryGetProperty("Id", out var pascalIdElement) && pascalIdElement.ValueKind == JsonValueKind.String
                    ? pascalIdElement.GetString()
                    : null;
            id = NormalizeTranscriptId(id);
            if (id != null)
            {
                ids.Add(id);
            }
        }

        return ids;
    }
    catch (JsonException)
    {
        return [];
    }
}

internal static bool TryFindLatestAnthropicAssistantMessageId(JsonElement messages, out Guid messageId)
{
    messageId = Guid.Empty;
    if (messages.ValueKind != JsonValueKind.Array)
    {
        return false;
    }

    foreach (var message in messages.EnumerateArray())
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("role", out var roleElement) ||
            roleElement.ValueKind != JsonValueKind.String ||
            !string.Equals(roleElement.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!message.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        var id = idElement.GetString();
        if (WireIdCodec.TryParseAnthropicMessageId(id, out var parsedMessageId))
        {
            messageId = parsedMessageId;
        }
    }

    return messageId != Guid.Empty;
}

internal static async Task<bool> TurnMatchesCallerScopeAsync(
    PublishedApiExecutionContext context,
    ApplicationDbContext db,
    Guid conversationId,
    int turnIndex,
    CancellationToken ct)
{
    if (context.InternalUserId.HasValue)
    {
        return await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == conversationId &&
                m.TurnIndex == turnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.UserId == context.InternalUserId, ct);
    }

    if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
    {
        return await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == conversationId &&
                m.TurnIndex == turnIndex &&
                m.Role == DataModelChatRole.User)
            .AnyAsync(m => m.ExternalUserIdentity == context.ExternalUserIdentity, ct);
    }

    return true;
}

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveResponsesStateAsync(
    PublishedApiExecutionContext context,
    OpenAiResponsesRequest request,
    IReadOnlyList<ChatMessage> prefixMessages,
    ApplicationDbContext db,
    CancellationToken ct)
{
    var conversationWireId = WireIdCodec.ParseResponsesConversationWireId(request.Conversation);
    var hasConversation = !string.IsNullOrWhiteSpace(conversationWireId);
    var hasPreviousResponse = !string.IsNullOrWhiteSpace(request.PreviousResponseId);

    Guid? conversationFromConversation = null;
    if (hasConversation)
    {
        var conversationResolution = await ResolveConversationFromWireIdAsync(
            context,
            conversationWireId!,
            db,
            ct);
        if (conversationResolution.ErrorResult != null)
        {
            return conversationResolution;
        }

        conversationFromConversation = conversationResolution.ConversationId;
    }

    var previousResponseResolution = await ResolveResponsesConversationAsync(
        context,
        request.PreviousResponseId,
        db,
        ct);
    if (previousResponseResolution.ErrorResult != null)
    {
        return previousResponseResolution;
    }

    if (hasConversation && hasPreviousResponse &&
        conversationFromConversation != previousResponseResolution.ConversationId)
    {
        return (null, OpenAiWireErrorResults.ConversationPreviousResponseMismatch());
    }

    if (conversationFromConversation.HasValue)
    {
        return (conversationFromConversation, null);
    }

    if (previousResponseResolution.ConversationId.HasValue)
    {
        return previousResponseResolution;
    }

    if (hasConversation || hasPreviousResponse)
    {
        return (null, null);
    }

    var transcriptConversationId = await ResolveConversationFromTranscriptAsync(
        context,
        prefixMessages,
        db,
        ct);
    return (transcriptConversationId, null);
}

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveConversationFromWireIdAsync(
    PublishedApiExecutionContext context,
    string conversationWireId,
    ApplicationDbContext db,
    CancellationToken ct)
{
    if (!WireIdCodec.TryParseConversationId(conversationWireId, out var notebookConversationId))
    {
        return (null, OpenAiWireErrorResults.InvalidConversationId(conversationWireId));
    }

    var conversation = await db.NotebookConversations
        .AsNoTracking()
        .Where(c => c.Id == notebookConversationId)
        .Select(c => new { c.Id, c.NotebookId })
        .FirstOrDefaultAsync(ct);

    if (conversation == null || conversation.NotebookId != context.NotebookId)
    {
        return (null, OpenAiWireErrorResults.ConversationNotFound(conversationWireId));
    }

    if (!await ConversationMatchesCallerIdentityAsync(context, db, conversation.Id, ct))
    {
        return (null, OpenAiWireErrorResults.ConversationScopeMismatch());
    }

    return (conversation.Id, null);
}

internal static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveResponsesConversationAsync(
    PublishedApiExecutionContext context,
    string? previousResponseId,
    ApplicationDbContext db,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(previousResponseId))
    {
        return (null, null);
    }

    if (!WireIdCodec.TryParseResponsesMessageId(previousResponseId, out var previousMessageId))
    {
        return (null, OpenAiWireErrorResults.InvalidPreviousResponseId(previousResponseId));
    }

    var previousAssistant = await db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m => m.Id == previousMessageId && m.Role == DataModelChatRole.Assistant)
        .Select(m => new
        {
            m.Id,
            m.NotebookConversationId,
            m.TurnIndex,
            NotebookId = m.NotebookConversation.NotebookId
        })
        .FirstOrDefaultAsync(ct);

    if (previousAssistant == null || previousAssistant.NotebookId != context.NotebookId)
    {
        return (null, OpenAiWireErrorResults.PreviousResponseNotFound(previousResponseId));
    }

    var turnUserIdentity = await db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m =>
            m.NotebookConversationId == previousAssistant.NotebookConversationId &&
            m.TurnIndex == previousAssistant.TurnIndex &&
            m.Role == DataModelChatRole.User)
        .OrderBy(m => m.MessageSequence)
        .Select(m => new
        {
            m.UserId,
            m.ExternalUserIdentity
        })
        .FirstOrDefaultAsync(ct);

    if (context.InternalUserId.HasValue)
    {
        if (turnUserIdentity?.UserId != context.InternalUserId)
        {
            return (null, OpenAiWireErrorResults.PreviousResponseScopeMismatch());
        }
    }
    else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
    {
        if (!string.Equals(
                turnUserIdentity?.ExternalUserIdentity,
                context.ExternalUserIdentity,
                StringComparison.Ordinal))
        {
            return (null, OpenAiWireErrorResults.PreviousResponseScopeMismatch());
        }
    }

    var latestAssistantMessageId = await ResolveLatestAssistantMessageIdAsync(
        db,
        previousAssistant.NotebookConversationId,
        ct);
    if (!latestAssistantMessageId.HasValue)
    {
        return (null, OpenAiWireErrorResults.PreviousResponseNotFound(previousResponseId));
    }

    if (latestAssistantMessageId.Value != previousAssistant.Id)
    {
        return (null, OpenAiWireErrorResults.UnsupportedPreviousResponseBranch());
    }

    return (previousAssistant.NotebookConversationId, null);
}

internal static async Task<Guid?> ResolveLatestAssistantMessageIdAsync(
    ApplicationDbContext db,
    Guid conversationId,
    CancellationToken ct)
{
    return await db.NotebookConversationMessages
        .AsNoTracking()
        .Where(m =>
            m.NotebookConversationId == conversationId &&
            m.Role == DataModelChatRole.Assistant &&
            m.IsStreaming != true)
        .OrderByDescending(m => m.TurnIndex)
        .ThenByDescending(m => m.MessageSequence)
        .Select(m => (Guid?)m.Id)
        .FirstOrDefaultAsync(ct);
}
}
