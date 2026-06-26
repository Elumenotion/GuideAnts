using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Endpoints.PublishedWire;

public sealed class OpenAiChatCompletionsRequest
{
    public string? Model { get; set; }
    public JsonElement Messages { get; set; }
    public JsonElement Tools { get; set; }
    public bool? Stream { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class OpenAiResponsesRequest
{
    public string? Model { get; set; }
    public JsonElement Input { get; set; }
    public JsonElement Tools { get; set; }
    public bool? Stream { get; set; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("conversation")]
    public JsonElement Conversation { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class AnthropicMessagesRequest
{
    public string? Model { get; set; }
    public JsonElement Messages { get; set; }
    public JsonElement System { get; set; }
    public JsonElement Tools { get; set; }
    public bool? Stream { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement ToolChoice { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class OpenAiEmbeddingsRequest
{
    public string? Model { get; set; }
    public JsonElement Input { get; set; }
}

public sealed class OpenAiImageGenerationsRequest
{
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public int? N { get; set; }
    public string? Size { get; set; }
    public string? ResponseFormat { get; set; }
}

public sealed class OpenAiAudioSpeechRequest
{
    public string? Model { get; set; }
    public string? Input { get; set; }
    public string? Voice { get; set; }
    public string? ResponseFormat { get; set; }
}
