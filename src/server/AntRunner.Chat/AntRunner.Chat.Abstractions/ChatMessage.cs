using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public ChatRole Role { get; }

    [JsonPropertyName("content")]
    public IReadOnlyList<ChatContent> Content { get; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ChatToolCall>? ToolCalls { get; }

    [JsonPropertyName("thinking_blocks")]
    public IReadOnlyList<ChatThinkingBlock>? ThinkingBlocks { get; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; }

    [JsonPropertyName("name")]
    public string? FunctionName { get; }

    public ChatMessage(ChatRole role, string content)
        : this(role, new List<ChatContent> { new(content) }, null, null, null, null) { }

    public ChatMessage(ChatRole role, IReadOnlyList<ChatContent> content)
        : this(role, content, null, null, null, null) { }

    public ChatMessage(string toolCallId, string functionName, IReadOnlyList<ChatContent> content)
        : this(ChatRole.Tool, content, null, null, toolCallId, functionName) { }

    public ChatMessage(ChatRole role, IReadOnlyList<ChatContent> content, IReadOnlyList<ChatToolCall>? toolCalls)
        : this(role, content, toolCalls, null, null, null) { }

    public ChatMessage(
        ChatRole role,
        IReadOnlyList<ChatContent> content,
        IReadOnlyList<ChatToolCall>? toolCalls,
        IReadOnlyList<ChatThinkingBlock>? thinkingBlocks)
        : this(role, content, toolCalls, thinkingBlocks, null, null) { }

    public ChatMessage(
        ChatRole role,
        IReadOnlyList<ChatContent> content,
        string? toolCallId,
        string? functionName)
        : this(role, content, null, null, toolCallId, functionName) { }

    private ChatMessage(ChatRole role,
        IReadOnlyList<ChatContent> content,
        IReadOnlyList<ChatToolCall>? toolCalls,
        IReadOnlyList<ChatThinkingBlock>? thinkingBlocks,
        string? toolCallId,
        string? functionName)
    {
        Role = role;
        Content = content ?? [];
        ToolCalls = toolCalls;
        ThinkingBlocks = thinkingBlocks;
        ToolCallId = toolCallId;
        FunctionName = functionName;
    }
}

public static class ChatMessageExtensions
{
    public static string GetText(this ChatMessage message)
    {
        if (message.Content == null || message.Content.Count == 0)
        {
            return string.Empty;
        }

        var textParts = message.Content
            .Where(c => !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text!)
            .ToList();

        if (textParts.Count == 0)
        {
            return string.Empty;
        }

        return string.Concat(textParts);
    }
}
