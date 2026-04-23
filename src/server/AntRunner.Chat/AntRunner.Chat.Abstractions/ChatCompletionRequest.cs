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

    [JsonPropertyName("temperature")]
    public double? Temperature { get; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; }

    /// <summary>
    /// Data-driven sampling parameter overrides (e.g., "temperature", "top_k", "min_p").
    /// When set, takes precedence over Temperature/TopP typed properties in llama.cpp client.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, double>? SamplingParameters { get; }

    public ChatCompletionRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools = null,
        string? model = null,
        double? temperature = null,
        double? topP = null,
        string? reasoningEffort = null,
        IReadOnlyDictionary<string, double>? samplingParameters = null)
    {
        Messages = messages ?? [];
        Tools = tools;
        Model = model;
        Temperature = temperature;
        TopP = topP;
        ReasoningEffort = reasoningEffort;
        SamplingParameters = samplingParameters;
    }
}
