using AntRunner.Chat.Abstractions;
using AntRunner.Chat;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Attachments;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Mapping;

/// <summary>
/// Builds OpenAI chat message history from notebook conversations, including assistant-switch handoff semantics.
/// </summary>
public class ConversationHistoryBuilder : IConversationHistoryBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IContextOptionsService _contextOptionsService;
    private readonly IAttachmentContentService _attachmentContentService;
    private readonly ILogger<ConversationHistoryBuilder> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ConversationHistoryBuilder(
        IServiceScopeFactory scopeFactory,
        IContextOptionsService contextOptionsService,
        IAttachmentContentService attachmentContentService,
        ILogger<ConversationHistoryBuilder> logger)
    {
        _scopeFactory = scopeFactory;
        _contextOptionsService = contextOptionsService;
        _attachmentContentService = attachmentContentService;
        _logger = logger;
    }

    public bool IsNewConversation(NotebookConversation conv) => conv.Messages.Count == 0;

    public bool IsAssistantSwitch(NotebookConversation conv, string assistantName)
    {
        var lastTurn = conv.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault();
        return lastTurn != null
               && !string.Equals(lastTurn.AssistantName, assistantName, StringComparison.OrdinalIgnoreCase);
    }

    public string HandoffSystemMessage =>
        "The previous messages between the user and assistant above are from a conversation with a different assistant. " +
        "Use them to understand the conversation context, but follow the system messages that were provided at the start of this message sequence.";

    public IReadOnlyList<NotebookConversationMessage> FilterMessages(
        NotebookConversation conv,
        string assistantName,
        bool isAssistantSwitch)
    {
        if (IsNewConversation(conv))
            return [];

        var deduped = ConversationMessageMapper.FilterDuplicateAssistantMessages(conv.Messages);

        var validToolCallIds = CollectValidToolCallIds(deduped, assistantName);

        var filtered = new List<NotebookConversationMessage>();
        foreach (var m in deduped.OrderBy(x => x.TurnIndex).ThenBy(x => x.MessageSequence))
        {
            if (ConversationMessageMapper.IsOmittedFromModelPrompt(m))
            {
                continue;
            }
            if (m.Role == DataModelChatRole.Tool)
            {
                if (string.IsNullOrEmpty(m.ToolCallId) || !validToolCallIds.Contains(m.ToolCallId))
                    continue;
            }
            else if (m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls))
            {
                if (!string.Equals(m.AssistantName, assistantName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (m.Role != DataModelChatRole.User && m.Role != DataModelChatRole.Assistant)
            {
                continue;
            }

            filtered.Add(m);
        }

        return filtered;
    }

    public async Task<List<ChatMessage>> PrepareMessagesForAssistantAsync(
        NotebookConversation conv,
        string assistantName,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!conv.Turns.Any() && conv.Messages.Any())
        {
            _logger.LogWarning("Turns collection not loaded for conversation {ConversationId}", conv.Id);
        }

        var isAssistantSwitch = IsAssistantSwitch(conv, assistantName);
        var isNewConversation = IsNewConversation(conv);

        var messages = new List<ChatMessage>();
        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
        if (assistantDef != null)
        {
            if (!string.IsNullOrWhiteSpace(assistantDef.Instructions))
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, assistantDef.Instructions));
            }

            var ctxMsg = await _contextOptionsService.BuildContextMessageAsync(
                assistantDef,
                conv.Notebook?.ProjectId ?? Guid.Empty,
                conv.Notebook?.Id ?? Guid.Empty,
                conv.Id);
            if (!string.IsNullOrEmpty(ctxMsg))
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, ctxMsg));
            }

            AddSkillsDiscoveryMessage(messages, assistantDef);
        }

        if (isNewConversation)
        {
            return messages;
        }

        if (isAssistantSwitch)
        {
            var switchMessages = await ApplyAssistantSwitchLogicAsync(conv, assistantName, cancellationToken);
            string? ctxContent = null;
            var ctxIndex = messages.FindIndex(m => m.Role == ChatMessageRole.System && m.GetText().StartsWith("{\"contextOptions\""));
            if (ctxIndex >= 0)
            {
                ctxContent = messages[ctxIndex].GetText();
                messages.RemoveAt(ctxIndex);
            }

            messages.AddRange(switchMessages);

            if (ctxContent != null)
            {
                messages.Add(new ChatMessage(ChatMessageRole.System, ctxContent));
            }

            return messages;
        }

        var conversationMessages = await BuildOpenAiMessagesAsync(conv, assistantName, cancellationToken);
        messages.AddRange(conversationMessages);
        return messages;
    }

    public async Task<List<ChatMessage>> ApplyAssistantSwitchLogicAsync(
        NotebookConversation conv,
        string newAssistantName,
        CancellationToken cancellationToken = default)
    {
        var dedupedMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        var assistantDef = await AssistantUtility.GetAssistantCreateRequest(newAssistantName);
        if (assistantDef == null)
        {
            return dedupedMessages
                .OrderBy(m => m.TurnIndex)
                .ThenBy(m => m.MessageSequence)
                .Where(m => !ConversationMessageMapper.IsOmittedFromModelPrompt(m))
                .Select(ConversationMessageMapper.ToChatMessage)
                .ToList();
        }

        var validToolCallIds = new HashSet<string>();
        foreach (var m in dedupedMessages.Where(m =>
            m.Role == DataModelChatRole.Assistant
            && !string.IsNullOrEmpty(m.ToolCalls)
            && !ConversationMessageMapper.IsOmittedFromModelPrompt(m)))
        {
            if (m.AssistantName == newAssistantName)
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls!, JsonOptions);
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            validToolCallIds.Add(tc.Id);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize tool calls for message {MessageId}", m.Id);
                }
            }
        }

        var filteredMessages = new List<ChatMessage>();
        foreach (var m in dedupedMessages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
        {
            if (ConversationMessageMapper.IsOmittedFromModelPrompt(m))
            {
                continue;
            }
            if (m.Role == DataModelChatRole.Tool)
            {
                if (string.IsNullOrEmpty(m.ToolCallId) || !validToolCallIds.Contains(m.ToolCallId))
                {
                    continue;
                }
            }
            else if (m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls))
            {
                if (m.AssistantName != newAssistantName)
                {
                    continue;
                }
            }
            else if (!(m.Role == DataModelChatRole.User || m.Role == DataModelChatRole.Assistant))
            {
                continue;
            }

            List<MessageAttachment> attachments;
            using (var attachmentScope = _scopeFactory.CreateScope())
            {
                var attachmentDb = attachmentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                attachments = await attachmentDb.MessageAttachments
                    .Include(ma => ma.NotebookFile)
                    .Where(ma => ma.MessageId == m.Id)
                    .OrderBy(ma => ma.OrderIndex)
                    .ToListAsync(cancellationToken);
            }

            if (attachments.Count > 0)
            {
                var contents = new List<ChatContent>();

                if (!string.IsNullOrEmpty(m.Content))
                {
                    contents.Add(new ChatContent(m.Content));
                }

                foreach (var attachment in attachments)
                {
                    var fileContents = await _attachmentContentService.CreateOpenAiContentFromNotebookFileAsync(
                        attachment.NotebookFileId, cancellationToken);
                    contents.AddRange(fileContents);
                }

                if (contents.Count > 0)
                {
                    var role = m.Role switch
                    {
                        DataModelChatRole.User => ChatMessageRole.User,
                        DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                        DataModelChatRole.Tool => ChatMessageRole.Tool,
                        _ => ChatMessageRole.System
                    };
                    var thinkingBlocks = role == ChatMessageRole.Assistant
                        ? ConversationMessageMapper.ThinkingBlocksForModelPrompt(m)
                        : null;

                    filteredMessages.Add(new ChatMessage(role, contents, null, thinkingBlocks));
                    continue;
                }
            }

            filteredMessages.Add(ConversationMessageMapper.ToChatMessage(m));
        }

        var newMessages = new List<ChatMessage>();
        newMessages.AddRange(filteredMessages);

        if (conv.Messages.Any())
        {
            newMessages.Add(new ChatMessage(ChatMessageRole.System, HandoffSystemMessage));
        }

        return newMessages;
    }

    public async Task<List<ChatMessage>> BuildOpenAiMessagesAsync(
        NotebookConversation conv,
        string assistantName,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ChatMessage>();

        var filteredMessages = ConversationMessageMapper.FilterDuplicateAssistantMessages(
            conv.Messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls)
        );

        var validToolCallIds = new HashSet<string>();
        foreach (var dbMsg in filteredMessages.Where(m =>
            m.Role == DataModelChatRole.Assistant
            && !string.IsNullOrEmpty(m.ToolCalls)
            && !ConversationMessageMapper.IsOmittedFromModelPrompt(m)))
        {
            if (dbMsg.AssistantName == assistantName)
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(dbMsg.ToolCalls!, JsonOptions);
                    if (toolCalls != null)
                    {
                        foreach (var tc in toolCalls)
                        {
                            validToolCallIds.Add(tc.Id);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize tool calls for message {MessageId}", dbMsg.Id);
                }
            }
        }

        foreach (var dbMsg in filteredMessages.OrderBy(m => m.TurnIndex).ThenBy(m => m.MessageSequence))
        {
            if (ConversationMessageMapper.IsOmittedFromModelPrompt(dbMsg))
            {
                continue;
            }
            if (dbMsg.Role == DataModelChatRole.Tool)
            {
                if (string.IsNullOrEmpty(dbMsg.ToolCallId) || !validToolCallIds.Contains(dbMsg.ToolCallId))
                {
                    continue;
                }
            }

            if (dbMsg.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(dbMsg.ToolCalls))
            {
                if (dbMsg.AssistantName != assistantName)
                {
                    continue;
                }
            }

            List<MessageAttachment> attachments;
            using (var attachmentScope = _scopeFactory.CreateScope())
            {
                var attachmentDb = attachmentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                attachments = await attachmentDb.MessageAttachments
                    .Include(ma => ma.NotebookFile)
                    .Where(ma => ma.MessageId == dbMsg.Id)
                    .OrderBy(ma => ma.OrderIndex)
                    .ToListAsync(cancellationToken);
            }

            if (attachments.Count > 0)
            {
                var contents = new List<ChatContent>();

                if (!string.IsNullOrEmpty(dbMsg.Content))
                {
                    contents.Add(new ChatContent(dbMsg.Content));
                }

                foreach (var attachment in attachments)
                {
                    var fileContents = await _attachmentContentService.CreateOpenAiContentFromNotebookFileAsync(
                        attachment.NotebookFileId, cancellationToken);
                    contents.AddRange(fileContents);
                }

                if (contents.Count > 0)
                {
                    var role = dbMsg.Role switch
                    {
                        DataModelChatRole.User => ChatMessageRole.User,
                        DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                        DataModelChatRole.Tool => ChatMessageRole.Tool,
                        _ => ChatMessageRole.System
                    };

                    list.Add(new ChatMessage(role, contents));
                    continue;
                }
            }

            list.Add(ConversationMessageMapper.ToChatMessage(dbMsg));
        }

        return list;
    }

    public async Task<List<ChatMessage>> BuildPublishedMessagesForAssistantAsync(
        NotebookConversation conv,
        string assistantName,
        string? clientContext,
        IReadOnlyList<ChatMessage>? clientMessages = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<ChatMessage>();
        AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition? assistantDef = null;

        SplitPublishedClientPrefix(clientMessages, out var leadingDeveloperMessages, out var conversationalClientPrefix);
        list.AddRange(leadingDeveloperMessages);

        try
        {
            assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
            if (assistantDef != null)
            {
                if (!string.IsNullOrWhiteSpace(assistantDef.Instructions))
                {
                    list.Add(new ChatMessage(ChatMessageRole.System, assistantDef.Instructions));
                }

                if (!string.IsNullOrWhiteSpace(clientContext))
                {
                    list.Add(new ChatMessage(ChatMessageRole.System, clientContext));
                }

                AddSkillsDiscoveryMessage(list, assistantDef);
            }
        }
        catch { /* ignore */ }

        if (conversationalClientPrefix.Count > 0)
        {
            list.AddRange(conversationalClientPrefix);
        }

        var isNewConversation = IsNewConversation(conv);
        var isAssistantSwitch = !isNewConversation && IsAssistantSwitch(conv, assistantName);
        var historyMessages = isNewConversation
            ? Array.Empty<NotebookConversationMessage>()
            : FilterMessages(conv, assistantName, isAssistantSwitch);

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var toolById = IndexToolMessagesByCallId(historyMessages);
            var retainedToolMessageIds = new HashSet<Guid>(toolById.Values.Select(x => x.Id));
            historyMessages = historyMessages
                .Where(m =>
                    m.Role != DataModelChatRole.Tool ||
                    m.ToolCallId == null ||
                    retainedToolMessageIds.Contains(m.Id))
                .ToList();
            var emittedToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in historyMessages)
            {
                if (ConversationMessageMapper.IsOmittedFromModelPrompt(m))
                {
                    continue;
                }
                List<MessageAttachment> attachments = await db.MessageAttachments
                    .Include(ma => ma.NotebookFile)
                    .Where(ma => ma.MessageId == m.Id)
                    .OrderBy(ma => ma.OrderIndex)
                    .ToListAsync(cancellationToken);

                if (attachments.Count > 0)
                {
                    var contents = new List<ChatContent>();
                    if (!string.IsNullOrEmpty(m.Content)) contents.Add(new ChatContent(m.Content));
                    foreach (var a in attachments)
                    {
                        var fileContents = await _attachmentContentService.CreateOpenAiContentFromNotebookFileAsync(
                            db, a.NotebookFileId, cancellationToken);
                        contents.AddRange(fileContents);
                    }
                    var role = m.Role switch
                    {
                        DataModelChatRole.User => ChatMessageRole.User,
                        DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                        DataModelChatRole.Tool => ChatMessageRole.Tool,
                        _ => ChatMessageRole.System
                    };
                    var thinkingBlocks = role == ChatMessageRole.Assistant
                        ? ConversationMessageMapper.ThinkingBlocksForModelPrompt(m)
                        : null;
                    list.Add(new ChatMessage(role, contents, null, thinkingBlocks));
                    continue;
                }

                if (m.Role == DataModelChatRole.Assistant)
                {
                    var thinkingBlocks = ConversationMessageMapper.ThinkingBlocksForModelPrompt(m);
                    var assistantContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
                    ChatMessage? msg = new ChatMessage(
                        ChatMessageRole.Assistant,
                        assistantContent,
                        null,
                        thinkingBlocks);
                    if (!string.IsNullOrEmpty(m.ToolCalls))
                    {
                        try
                        {
                            var tc = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, JsonOptions);
                            if (tc != null)
                            {
                                msg = new ChatMessage(
                                    ChatMessageRole.Assistant,
                                    assistantContent,
                                    tc,
                                    thinkingBlocks);

                                foreach (var call in tc)
                                {
                                    if (string.IsNullOrEmpty(call.Id)) continue;
                                    if (emittedToolCallIds.Contains(call.Id)) continue;
                                    if (toolById.TryGetValue(call.Id, out var toolMsg))
                                    {
                                        var toolContent = string.IsNullOrEmpty(toolMsg.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(toolMsg.Content) };
                                        if (msg != null)
                                        {
                                            list.Add(msg);
                                            msg = null;
                                        }
                                        list.Add(new ChatMessage(toolMsg.ToolCallId!, toolMsg.FunctionName!, toolContent));
                                        emittedToolCallIds.Add(call.Id);
                                    }
                                }
                            }
                        }
                        catch { /* ignore */ }
                    }
                    if (msg != null) list.Add(msg);
                    continue;
                }

                if (m.Role == DataModelChatRole.Tool && m.ToolCallId != null && m.FunctionName != null)
                {
                    if (emittedToolCallIds.Contains(m.ToolCallId)) continue;
                    var toolMsgContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
                    list.Add(new ChatMessage(m.ToolCallId, m.FunctionName, toolMsgContent));
                    emittedToolCallIds.Add(m.ToolCallId);
                    continue;
                }

                var basicRole = m.Role switch
                {
                    DataModelChatRole.User => ChatMessageRole.User,
                    DataModelChatRole.Assistant => ChatMessageRole.Assistant,
                    DataModelChatRole.Tool => ChatMessageRole.Tool,
                    _ => ChatMessageRole.System
                };
                var basicThinkingBlocks = basicRole == ChatMessageRole.Assistant
                    ? ConversationMessageMapper.ThinkingBlocksForModelPrompt(m)
                    : null;
                var basicContent = string.IsNullOrEmpty(m.Content) ? Array.Empty<ChatContent>() : new[] { new ChatContent(m.Content) };
                list.Add(new ChatMessage(basicRole, basicContent, null, basicThinkingBlocks));
            }
        }

        if (isAssistantSwitch && conv.Messages.Count > 0)
        {
            list.Add(new ChatMessage(ChatMessageRole.System, HandoffSystemMessage));
        }

        if (assistantDef != null)
        {
            var serverContextMsg = _contextOptionsService.BuildPublishedContextMessage(
                assistantDef,
                conv.Notebook?.ProjectId ?? Guid.Empty,
                conv.Notebook?.Id ?? Guid.Empty);
            if (!string.IsNullOrWhiteSpace(serverContextMsg))
            {
                list.Add(new ChatMessage(ChatMessageRole.System, serverContextMsg));
            }
        }

        return list;
    }

    /// <summary>
    /// Splits a published-wire client prefix so guide system instructions can be injected
    /// before replayed user/assistant history. Leading <c>developer</c> messages stay at the
    /// front (Cursor sandbox permissions); conversational replay follows guide instructions.
    /// </summary>
    internal static void SplitPublishedClientPrefix(
        IReadOnlyList<ChatMessage>? clientMessages,
        out List<ChatMessage> leadingDeveloperMessages,
        out List<ChatMessage> conversationalClientPrefix)
    {
        leadingDeveloperMessages = [];
        conversationalClientPrefix = [];

        if (clientMessages == null || clientMessages.Count == 0)
        {
            return;
        }

        var index = 0;
        while (index < clientMessages.Count && clientMessages[index].Role == ChatMessageRole.Developer)
        {
            leadingDeveloperMessages.Add(clientMessages[index]);
            index++;
        }

        for (; index < clientMessages.Count; index++)
        {
            conversationalClientPrefix.Add(clientMessages[index]);
        }
    }

    /// <summary>
    /// One tool result per call id. When duplicates exist (e.g. pre-fix overflow unwind inserts),
    /// keep the latest sequence so the model sees the replacement notice, not the oversized payload.
    /// </summary>
    internal static Dictionary<string, NotebookConversationMessage> IndexToolMessagesByCallId(
        IEnumerable<NotebookConversationMessage> historyMessages)
    {
        return historyMessages
            .Where(x => x.Role == DataModelChatRole.Tool && x.ToolCallId != null && x.FunctionName != null)
            .GroupBy(x => x.ToolCallId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.MessageSequence).ThenByDescending(x => x.Created).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> CollectValidToolCallIds(
        IEnumerable<NotebookConversationMessage> messages,
        string assistantName)
    {
        var validToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in messages.Where(m =>
            m.Role == DataModelChatRole.Assistant
            && !string.IsNullOrEmpty(m.ToolCalls)
            && !ConversationMessageMapper.IsOmittedFromModelPrompt(m)))
        {
            if (!string.Equals(m.AssistantName, assistantName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls!, JsonOptions);
                if (toolCalls == null)
                    continue;

                foreach (var tc in toolCalls)
                {
                    if (!string.IsNullOrEmpty(tc.Id))
                        validToolCallIds.Add(tc.Id);
                }
            }
            catch (JsonException)
            {
                // ignore malformed tool call payloads
            }
        }

        return validToolCallIds;
    }

    private static void AddSkillsDiscoveryMessage(
        List<ChatMessage> messages,
        AssistantDefinition? assistantDef)
    {
        if (assistantDef?.Skills is not { Count: > 0 })
        {
            return;
        }

        var visibleSkills = SkillVisibilityFilter.FilterVisibleSkills(assistantDef);
        var discoveryBlock = SkillDiscoveryBlockBuilder.BuildDiscoveryBlock(visibleSkills);
        if (string.IsNullOrWhiteSpace(discoveryBlock))
        {
            return;
        }

        messages.Add(new ChatMessage(ChatMessageRole.System, discoveryBlock));
    }
}
