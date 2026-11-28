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

    /// <summary>
    /// True when this message was synthesized by the run loop rather than produced
    /// by the model or the user (e.g. the tool-attachment bridge and attachment
    /// messages composed by <c>MessageComposer</c>). Such messages are real model
    /// context and must replay identically, but clients must render them as tool
    /// artifacts, not as human turns.
    /// </summary>
    [JsonPropertyName("is_synthetic")]
    public bool IsSynthetic { get; }

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
        string? functionName,
        bool isSynthetic = false)
    {
        Role = role;
        Content = content ?? [];
        ToolCalls = toolCalls;
        ThinkingBlocks = thinkingBlocks;
        ToolCallId = toolCallId;
        FunctionName = functionName;
        IsSynthetic = isSynthetic;
    }

    /// <summary>
    /// Creates a synthetic message (run-loop composed, not model- or user-produced).
    /// See <see cref="IsSynthetic"/>.
    /// </summary>
    public static ChatMessage Synthetic(
        ChatRole role,
        IReadOnlyList<ChatContent> content)
    {
        return new ChatMessage(role, content, null, null, null, null, true);
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
