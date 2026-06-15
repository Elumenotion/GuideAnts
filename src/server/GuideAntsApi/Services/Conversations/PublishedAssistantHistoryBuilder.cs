using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Filters notebook conversation messages when preparing history for a specific assistant,
/// including assistant-switch handoff semantics (parity with <see cref="ConversationService"/>).
/// </summary>
public static class PublishedAssistantHistoryBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsAssistantSwitch(NotebookConversation conv, string assistantName)
    {
        var lastTurn = conv.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault();
        return lastTurn != null
               && !string.Equals(lastTurn.AssistantName, assistantName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewConversation(NotebookConversation conv) => conv.Messages.Count == 0;

    /// <summary>
    /// Returns conversation messages in order, filtered for the target assistant.
    /// When <paramref name="isAssistantSwitch"/> is true, a handoff system line should be appended by the caller.
    /// </summary>
    public static IReadOnlyList<NotebookConversationMessage> FilterMessages(
        NotebookConversation conv,
        string assistantName,
        bool isAssistantSwitch)
    {
        if (IsNewConversation(conv))
            return [];

        var deduped = FilterDuplicateAssistantMessages(conv.Messages);

        var validToolCallIds = CollectValidToolCallIds(deduped, assistantName);

        var filtered = new List<NotebookConversationMessage>();
        foreach (var m in deduped.OrderBy(x => x.TurnIndex).ThenBy(x => x.MessageSequence))
        {
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

    public static string HandoffSystemMessage =>
        "The previous messages between the user and assistant above are from a conversation with a different assistant. " +
        "Use them to understand the conversation context, but follow the system messages that were provided at the start of this message sequence.";

    private static HashSet<string> CollectValidToolCallIds(
        IEnumerable<NotebookConversationMessage> messages,
        string assistantName)
    {
        var validToolCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in messages.Where(m => m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls)))
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

    private static List<NotebookConversationMessage> FilterDuplicateAssistantMessages(
        ICollection<NotebookConversationMessage> messages)
    {
        var turnContentWithToolCalls = new HashSet<(int turn, string content)>();
        foreach (var m in messages.Where(m =>
                     m.Role == DataModelChatRole.Assistant && !string.IsNullOrEmpty(m.ToolCalls)))
        {
            turnContentWithToolCalls.Add((m.TurnIndex, m.Content?.Trim() ?? string.Empty));
        }

        var result = new List<NotebookConversationMessage>();
        foreach (var m in messages)
        {
            if (m.Role == DataModelChatRole.Assistant && string.IsNullOrEmpty(m.ToolCalls))
            {
                var key = (m.TurnIndex, m.Content?.Trim() ?? string.Empty);
                if (turnContentWithToolCalls.Contains(key))
                    continue;
            }

            result.Add(m);
        }

        return result;
    }
}
