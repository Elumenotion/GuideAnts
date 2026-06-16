using System.Text.Json;
using System.Threading.Channels;
using AntRunner.Chat;
using AntRunner.ToolCalling;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Streaming;

/// <summary>
/// Shared SSE event type mapping and channel emission helpers.
/// </summary>
public static class StreamingEvents
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DetermineEventType(string role, string message)
    {
        return role.ToLowerInvariant() switch
        {
            "user" => StreamingEventTypes.UserMessage,
            "assistant" => StreamingEventTypes.AssistantMessage,
            "tool" => StreamingEventTypes.ToolResult,
            "system" => StreamingEventTypes.SystemMessage,
            _ => StreamingEventTypes.Message
        };
    }

    public static void EmitThinkingMessages(
        ChatRunOutput? output,
        IReadOnlyList<Guid> assistantMessageIds,
        ChannelWriter<StreamingEvent> writer)
    {
        if (output?.Messages == null || assistantMessageIds.Count == 0)
        {
            return;
        }

        var assistantMessages = output.Messages
            .Where(m => m.Role == AntRunner.Chat.Abstractions.ChatRole.Assistant)
            .ToList();

        if (assistantMessages.Count < assistantMessageIds.Count)
        {
            return;
        }

        var recentAssistantMessages = assistantMessages
            .Skip(assistantMessages.Count - assistantMessageIds.Count)
            .ToList();

        foreach (var message in recentAssistantMessages)
        {
            if (message.ThinkingBlocks is not { Count: > 0 })
            {
                continue;
            }

            foreach (var block in message.ThinkingBlocks)
            {
                var content = Mapping.ConversationMessageMapper.FormatThinkingDisplay(block);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var payload = new
                {
                    role = "assistant",
                    content,
                    timestamp = DateTime.UtcNow
                };
                writer.TryWrite(new StreamingEvent("assistant_message", JsonSerializer.Serialize(payload, JsonOptions)));
            }
        }
    }

    public static StreamingEvent BuildToolActivityProgress(ToolActivityUpdate activity)
    {
        var payload = new
        {
            toolActivity = new
            {
                name = activity.Name,
                status = activity.Status,
                toolCallId = activity.ToolCallId,
                invocationId = activity.InvocationId,
                invocationDepth = activity.InvocationDepth,
                source = activity.Source,
                timestamp = activity.Timestamp
            }
        };

        return new StreamingEvent(
            StreamingEventTypes.StreamingProgress,
            JsonSerializer.Serialize(payload, JsonOptions));
    }
}
