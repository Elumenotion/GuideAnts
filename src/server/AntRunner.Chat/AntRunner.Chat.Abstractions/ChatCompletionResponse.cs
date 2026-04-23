using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatChoice> Choices { get; }

    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; }

    [JsonIgnore]
    public ChatChoice? FirstChoice => Choices.FirstOrDefault();

    public ChatCompletionResponse(IReadOnlyList<ChatChoice> choices, ChatCompletionUsage? usage)
    {
        Choices = choices ?? [];
        Usage = usage;
    }
}

public sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage Message { get; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; }

    public ChatChoice(ChatMessage message, string? finishReason)
    {
        Message = message;
        FinishReason = finishReason;
    }
}

public sealed class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public ChatPromptTokensDetails? PromptTokensDetails { get; set; }
}

public sealed class ChatPromptTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int? CachedTokens { get; set; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatChoiceDelta> Choices { get; }

    [JsonIgnore]
    public ChatChoiceDelta? FirstChoice => Choices.FirstOrDefault();

    public ChatCompletionChunk(IReadOnlyList<ChatChoiceDelta> choices)
    {
        Choices = choices ?? [];
    }
}

public sealed class ChatChoiceDelta
{
    [JsonPropertyName("delta")]
    public ChatDelta Delta { get; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; }

    public ChatChoiceDelta(ChatDelta delta, string? finishReason)
    {
        Delta = delta;
        FinishReason = finishReason;
    }
}

public sealed class ChatDelta
{
    [JsonPropertyName("role")]
    public ChatRole? Role { get; }

    [JsonPropertyName("content")]
    public string? Content { get; }

    public ChatDelta(ChatRole? role, string? content)
    {
        Role = role;
        Content = content;
    }
}
