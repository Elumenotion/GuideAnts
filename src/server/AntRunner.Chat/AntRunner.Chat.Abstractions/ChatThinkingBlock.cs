using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatThinkingBlock
{
    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; }

    [JsonPropertyName("signature")]
    public string? Signature { get; }

    [JsonPropertyName("data")]
    public string? Data { get; }

    [JsonIgnore]
    public bool IsThinking => string.Equals(Type, "thinking", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsRedactedThinking => string.Equals(Type, "redacted_thinking", StringComparison.Ordinal);

    [JsonConstructor]
    public ChatThinkingBlock(string type, string? thinking = null, string? signature = null, string? data = null)
    {
        Type = type;
        Thinking = thinking;
        Signature = signature;
        Data = data;
    }

    public static ChatThinkingBlock ForThinking(string thinking, string signature) =>
        new("thinking", thinking, signature);

    public static ChatThinkingBlock ForRedacted(string data) =>
        new("redacted_thinking", data: data);
}
