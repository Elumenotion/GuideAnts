namespace AntRunner.Chat.Compaction;

internal enum CompactionBlockKind
{
    UserMessage,
    AssistantMessage,
    SystemMessage,
    ToolCall,
    ToolResult
}

/// <summary>
/// One piece of conversation content, normalized to a shape every extractor can scan without
/// caring whether it came from a <c>ChatMessage</c>'s text, a tool call, or a tool result.
/// </summary>
internal sealed record CompactionBlock(
    CompactionBlockKind Kind,
    string Text,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    bool? ToolSucceeded = null);
