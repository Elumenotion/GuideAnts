using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations.Mapping;

/// <summary>
/// Shared DTO and chat-message mapping for private and published conversation flows.
/// </summary>
public static class ConversationMessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Filters out duplicate assistant messages that have the same content but no ToolCalls
    /// when another message in the same turn has the same content WITH ToolCalls.
    /// </summary>
    public static List<T> FilterDuplicateAssistantMessages<T>(
        IEnumerable<T> messages,
        Func<T, DataModelChatRole> getRole,
        Func<T, int> getTurnIndex,
        Func<T, string?> getContent,
        Func<T, bool> hasToolCalls)
    {
        var messageList = messages.ToList();

        var turnContentWithToolCalls = new HashSet<(int turn, string content)>();
        foreach (var m in messageList.Where(m => getRole(m) == DataModelChatRole.Assistant && hasToolCalls(m)))
        {
            turnContentWithToolCalls.Add((getTurnIndex(m), getContent(m)?.Trim() ?? string.Empty));
        }

        var result = new List<T>();
        foreach (var m in messageList)
        {
            if (getRole(m) == DataModelChatRole.Assistant && !hasToolCalls(m))
            {
                var key = (getTurnIndex(m), getContent(m)?.Trim() ?? string.Empty);
                if (turnContentWithToolCalls.Contains(key))
                {
                    continue;
                }
            }

            result.Add(m);
        }

        return result;
    }

    public static List<NotebookConversationMessage> FilterDuplicateAssistantMessages(
        ICollection<NotebookConversationMessage> messages) =>
        FilterDuplicateAssistantMessages(
            messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls));

    public static string DetermineFileTypeString(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tiff" or "webp" => "image",
            "wav" or "mp3" or "flac" or "aac" or "ogg" or "m4a" => "audio",
            "txt" or "md" or "json" or "xml" or "csv" => "text",
            _ => "other"
        };
    }

    public static MessageDto ToMessageDto(NotebookConversationMessage m)
    {
        IReadOnlyList<ToolCallDto>? toolCalls = null;
        if (!string.IsNullOrEmpty(m.ToolCalls))
        {
            try
            {
                var openAiToolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, JsonOptions);
                if (openAiToolCalls != null)
                {
                    toolCalls = openAiToolCalls.Select(tc => new ToolCallDto(
                        tc.Id,
                        tc.Type.ToString(),
                        new ToolCallFunctionDto(
                            tc.Function.Name,
                            tc.Function.Arguments.ToString()
                        )
                    )).ToList();
                }
            }
            catch (JsonException)
            {
                // leave toolCalls null
            }
        }

        var attachments = m.Attachments?
            .OrderBy(a => a.OrderIndex)
            .Select(a => new AttachedFileDto(
                a.NotebookFileId,
                Path.GetFileName(a.NotebookFile?.RelativePath ?? "unknown"),
                DetermineFileTypeString(a.NotebookFile?.RelativePath ?? string.Empty),
                a.NotebookFile?.FileSize ?? 0,
                null,
                a.Type
            ))
            .ToList() ?? [];

        return new MessageDto(
            m.Id,
            m.Role,
            m.Content,
            m.UserId ?? m.LastEditedByUserId,
            m.AssistantName,
            m.IsEdited,
            m.LastEditedAt,
            m.Created,
            m.EditHistory?.OriginalContent,
            toolCalls,
            m.ToolCallId,
            m.FunctionName,
            attachments,
            m.MessageContentType,
            UserName: m.User?.Name ?? m.LastEditedByUser?.Name,
            UserEmail: m.User?.Email ?? m.LastEditedByUser?.Email
        );
    }

    public static ConversationDto ToConversationDto(NotebookConversation c)
    {
        var messages = c.Messages.Where(m => m.IsStreaming != true).ToList();
        var filteredMessages = FilterDuplicateAssistantMessages(
            messages,
            m => m.Role,
            m => m.TurnIndex,
            m => m.Content,
            m => !string.IsNullOrEmpty(m.ToolCalls));

        var orderedMessages = filteredMessages
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToList();

        var messageDtos = new List<MessageDto>();
        foreach (var message in orderedMessages)
        {
            messageDtos.AddRange(BuildThinkingMessageDtos(message));
            if (HasVisibleAssistantBody(message.Role, message.Content, !string.IsNullOrEmpty(message.ToolCalls), message.Attachments?.Count ?? 0))
            {
                messageDtos.Add(ToMessageDto(message));
            }
        }

        return new ConversationDto(c.NotebookId, c.Created, messageDtos);
    }

    public static ChatMessage ToChatMessage(NotebookConversationMessage m)
    {
        var role = m.Role switch
        {
            DataModelChatRole.User => ChatMessageRole.User,
            DataModelChatRole.Assistant => ChatMessageRole.Assistant,
            DataModelChatRole.Tool => ChatMessageRole.Tool,
            _ => ChatMessageRole.System
        };

        if (role == ChatMessageRole.Assistant)
        {
            var thinkingBlocks = DeserializeThinkingBlocks(m.ThinkingBlocksJson);

            if (!string.IsNullOrEmpty(m.ToolCalls))
            {
                try
                {
                    var toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, JsonOptions);
                    if (toolCalls is { Count: > 0 })
                    {
                        var content = string.IsNullOrEmpty(m.Content)
                            ? Array.Empty<ChatContent>()
                            : new[] { new ChatContent(m.Content) };
                        return new ChatMessage(role, content, toolCalls, thinkingBlocks);
                    }
                }
                catch (JsonException)
                {
                    // include message without tool calls
                }
            }

            var assistantContent = string.IsNullOrEmpty(m.Content)
                ? Array.Empty<ChatContent>()
                : new[] { new ChatContent(m.Content) };
            return new ChatMessage(role, assistantContent, null, thinkingBlocks);
        }

        if (role == ChatMessageRole.Tool && m.ToolCallId != null && m.FunctionName != null)
        {
            var toolContent = string.IsNullOrEmpty(m.Content)
                ? Array.Empty<ChatContent>()
                : new[] { new ChatContent(m.Content) };
            return new ChatMessage(m.ToolCallId, m.FunctionName, toolContent);
        }

        return new ChatMessage(role, m.Content);
    }

    public static bool HasVisibleAssistantBody(
        DataModelChatRole role,
        string? content,
        bool hasToolCalls,
        int attachmentCount)
    {
        if (role != DataModelChatRole.Assistant)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(content) || hasToolCalls || attachmentCount > 0;
    }

    public static IReadOnlyList<MessageDto> BuildThinkingMessageDtos(NotebookConversationMessage message)
    {
        if (message.Role != DataModelChatRole.Assistant)
        {
            return [];
        }

        var thinkingBlocks = DeserializeThinkingBlocks(message.ThinkingBlocksJson);
        if (thinkingBlocks is not { Count: > 0 })
        {
            return [];
        }

        var results = new List<MessageDto>(thinkingBlocks.Count);
        for (var i = 0; i < thinkingBlocks.Count; i++)
        {
            var content = FormatThinkingDisplay(thinkingBlocks[i]);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            results.Add(new MessageDto(
                BuildThinkingMessageId(message.Id, i),
                DataModelChatRole.Assistant,
                content,
                null,
                message.AssistantName,
                false,
                null,
                message.Created,
                null,
                null,
                null,
                null,
                [],
                MessageContentType.Text,
                null,
                message.TurnIndex,
                null,
                null
            ));
        }

        return results;
    }

    public static IReadOnlyList<MessageDto> BuildThinkingMessageDtos(
        Guid messageId,
        DataModelChatRole role,
        string? assistantName,
        DateTime created,
        string? thinkingBlocksJson,
        int? turnIndex)
    {
        if (role != DataModelChatRole.Assistant)
        {
            return [];
        }

        var thinkingBlocks = DeserializeThinkingBlocks(thinkingBlocksJson);
        if (thinkingBlocks is not { Count: > 0 })
        {
            return [];
        }

        var results = new List<MessageDto>(thinkingBlocks.Count);
        for (var i = 0; i < thinkingBlocks.Count; i++)
        {
            var content = FormatThinkingDisplay(thinkingBlocks[i]);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            results.Add(new MessageDto(
                BuildThinkingMessageId(messageId, i),
                DataModelChatRole.Assistant,
                content,
                null,
                assistantName,
                false,
                null,
                created,
                null,
                null,
                null,
                null,
                [],
                MessageContentType.Text,
                null,
                turnIndex,
                null,
                null
            ));
        }

        return results;
    }

    public static Guid BuildThinkingMessageId(Guid sourceId, int index)
    {
        var sourceBytes = sourceId.ToByteArray();
        var indexBytes = BitConverter.GetBytes(index);
        var input = new byte[sourceBytes.Length + indexBytes.Length];
        Buffer.BlockCopy(sourceBytes, 0, input, 0, sourceBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, input, sourceBytes.Length, indexBytes.Length);
        var hash = System.Security.Cryptography.MD5.HashData(input);
        return new Guid(hash);
    }

    public static string FormatThinkingDisplay(ChatThinkingBlock block)
    {
        if (block.IsThinking)
        {
            return block.Thinking ?? string.Empty;
        }

        if (block.IsRedactedThinking)
        {
            return "Thinking (redacted)";
        }

        return string.Empty;
    }

    public static IReadOnlyList<ChatThinkingBlock>? DeserializeThinkingBlocks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var blocks = JsonSerializer.Deserialize<List<ChatThinkingBlock>>(json, JsonOptions);
            return blocks is { Count: > 0 } ? blocks : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
