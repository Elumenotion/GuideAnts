using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("messages")]
    public IReadOnlyList<ChatMessage> Messages { get; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<ChatToolDefinition>? Tools { get; }

    [JsonPropertyName("model")]
    public string? Model { get; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; }

    /// <summary>
    /// Data-driven sampling parameter overrides (e.g., "temperature", "top_k", "min_p").
    /// This is the single sampling source-of-truth for provider request shaping.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, double>? SamplingParameters { get; }

    /// <summary>
    /// Optional tool-choice override for limit escalation (Tier 2). Only <c>"none"</c> is used today.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; }

    public ChatCompletionRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools = null,
        string? model = null,
        string? reasoningEffort = null,
        IReadOnlyDictionary<string, double>? samplingParameters = null,
        string? toolChoice = null)
    {
        Messages = messages ?? [];
        Tools = tools;
        Model = model;
        ReasoningEffort = reasoningEffort;
        SamplingParameters = samplingParameters;
        ToolChoice = toolChoice;
    }
}
