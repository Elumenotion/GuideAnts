using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatToolCallFunction Function { get; set; } = new();

    [JsonIgnore]
    public bool IsFunction =>
        string.Equals(Type, "function", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChatToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}
