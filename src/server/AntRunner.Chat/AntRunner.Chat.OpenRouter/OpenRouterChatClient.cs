using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.OpenRouter;

public sealed class OpenRouterChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OpenRouterChatConfig _config;
    private readonly string? _defaultModel;
    private readonly ILogger<OpenRouterChatClient> _logger;

    public OpenRouterChatClient(
        HttpClient httpClient,
        OpenRouterChatConfig config,
        string? defaultModel,
        ILogger<OpenRouterChatClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _defaultModel = defaultModel;
        _logger = logger ?? NullLogger<OpenRouterChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = OpenRouterRequestMapper.ToRequest(request, ResolveModel(request), stream: false);
        using var message = CreateRequestMessage(payload);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenRouter chat request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<OpenRouterChatResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("OpenRouter chat response was empty.");
        return OpenRouterResponseMapper.ToChatCompletionResponse(parsed);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var payload = OpenRouterRequestMapper.ToRequest(request, ResolveModel(request), stream: true);
        using var message = CreateRequestMessage(payload);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenRouter chat stream failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulator = new OpenRouterStreamingAccumulator();
        var eventData = new StringBuilder();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    if (ProcessServerSentEvent(eventData.ToString(), accumulator, onChunk))
                    {
                        break;
                    }

                    eventData.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                eventData.AppendLine(line[5..].TrimStart());
            }
        }

        if (eventData.Length > 0)
        {
            ProcessServerSentEvent(eventData.ToString(), accumulator, onChunk);
        }

        return accumulator.ToResponse();
    }

    private HttpRequestMessage CreateRequestMessage(OpenRouterChatRequest payload)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException("OpenRouter:ApiKey is required.");
        }

        var endpoint = $"{(_config.BaseUrl ?? "https://openrouter.ai/api/v1").TrimEnd('/')}/chat/completions";
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        if (!string.IsNullOrWhiteSpace(_config.HttpReferer))
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", _config.HttpReferer);
        }

        if (!string.IsNullOrWhiteSpace(_config.AppTitle))
        {
            message.Headers.TryAddWithoutValidation("X-Title", _config.AppTitle);
        }

        return message;
    }

    private string ResolveModel(ChatCompletionRequest request)
    {
        var model = request.Model ?? _defaultModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("OpenRouter chat requires a model identifier.");
        }

        return model;
    }

    private static bool ProcessServerSentEvent(
        string payload,
        OpenRouterStreamingAccumulator accumulator,
        Action<ChatCompletionChunk> onChunk)
    {
        var trimmed = payload.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (string.Equals(trimmed, "[DONE]", StringComparison.Ordinal))
        {
            return true;
        }

        var parsed = JsonSerializer.Deserialize<OpenRouterChatStreamResponse>(trimmed, SerializerOptions);
        if (parsed != null)
        {
            accumulator.Apply(parsed, onChunk);
        }

        return false;
    }

    private static class OpenRouterRequestMapper
    {
        public static OpenRouterChatRequest ToRequest(ChatCompletionRequest request, string model, bool stream)
        {
            var messages = request.Messages.Select(ToMessage).ToList();
            var tools = request.Tools?.Select(ToTool).ToList();
            var (temperature, topP, extensions) = ResolveSampling(request);
            return new OpenRouterChatRequest(
                Model: model,
                Messages: messages,
                Tools: tools,
                Temperature: temperature,
                TopP: topP,
                ReasoningEffort: request.ReasoningEffort,
                Stream: stream)
            {
                Extensions = extensions
            };
        }

        private static (double? Temperature, double? TopP, Dictionary<string, JsonElement>? Extensions)
            ResolveSampling(ChatCompletionRequest request)
        {
            double? temperature = null;
            double? topP = null;
            Dictionary<string, JsonElement>? extensions = null;
            if (request.SamplingParameters == null)
            {
                return (temperature, topP, extensions);
            }

            foreach (var (key, value) in request.SamplingParameters)
            {
                if (string.Equals(key, "temperature", StringComparison.Ordinal))
                {
                    temperature = value;
                    continue;
                }

                if (string.Equals(key, "top_p", StringComparison.Ordinal))
                {
                    topP = value;
                    continue;
                }

                extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                extensions[key] = JsonSerializer.SerializeToElement(value);
            }

            return (temperature, topP, extensions);
        }

        private static OpenRouterChatMessage ToMessage(ChatMessage message)
        {
            if (message.Role == ChatRole.Tool)
            {
                return OpenRouterChatMessage.Create(
                    role: "tool",
                    contentText: message.GetText(),
                    contentParts: null,
                    toolCalls: null,
                    toolCallId: message.ToolCallId,
                    name: message.FunctionName);
            }

            var toolCalls = message.ToolCalls?.Count > 0
                ? message.ToolCalls.Select(ToToolCall).ToList()
                : null;
            var contentText = CanUseStringContent(message.Content)
                ? message.Content[0].Text
                : null;
            var contentParts = contentText == null
                ? message.Content.Select(ToContentPart).ToList()
                : null;

            return OpenRouterChatMessage.Create(
                role: ToRole(message.Role),
                contentText: contentText,
                contentParts: contentParts,
                toolCalls: toolCalls,
                toolCallId: null,
                name: null);
        }

        private static bool CanUseStringContent(IReadOnlyList<ChatContent> content) =>
            content.Count == 1 && content[0].IsText;

        private static OpenRouterChatTool ToTool(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("OpenRouter function tools require a function definition.");
            }

            return new OpenRouterChatTool(
                Type: tool.Type,
                Function: new OpenRouterChatFunctionDefinition(
                    Name: tool.Function.Name,
                    Description: tool.Function.Description,
                    Parameters: tool.Function.Parameters));
        }

        private static OpenRouterChatToolCall ToToolCall(ChatToolCall toolCall) =>
            new(
                Id: toolCall.Id,
                Type: toolCall.Type,
                Function: new OpenRouterChatToolCallFunction(
                    Name: toolCall.Function.Name,
                    Arguments: toolCall.Function.Arguments.ValueKind == JsonValueKind.String
                        ? toolCall.Function.Arguments.GetString() ?? "{}"
                        : toolCall.Function.Arguments.GetRawText()));

        private static OpenRouterChatContentPart ToContentPart(ChatContent content)
        {
            if (content.IsText)
            {
                return new OpenRouterChatContentPart("text", content.Text, null);
            }

            if (content.IsImage && content.ImageUrl != null)
            {
                return new OpenRouterChatContentPart(
                    "image_url",
                    null,
                    new OpenRouterChatImageUrl(content.ImageUrl.Url));
            }

            throw new InvalidOperationException("Unsupported OpenRouter content item.");
        }

        private static string ToRole(ChatRole role) => role switch
        {
            ChatRole.System => "system",
            ChatRole.Developer => "developer",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new InvalidOperationException($"Unsupported OpenRouter role '{role}'.")
        };
    }

    private static class OpenRouterResponseMapper
    {
        public static ChatCompletionResponse ToChatCompletionResponse(OpenRouterChatResponse response)
        {
            var choices = response.Choices?.Select(ToChoice).ToList()
                ?? throw new InvalidOperationException("OpenRouter response did not contain choices.");
            return new ChatCompletionResponse(choices, ToUsage(response.Usage));
        }

        private static ChatChoice ToChoice(OpenRouterChatChoice choice)
        {
            var message = ToMessage(choice.Message);
            var finishReason = NormalizeFinishReason(choice.FinishReason, message.ToolCalls);
            return new ChatChoice(message, finishReason);
        }

        private static ChatMessage ToMessage(OpenRouterChatResponseMessage? message)
        {
            if (message == null)
            {
                throw new InvalidOperationException("OpenRouter choice message is required.");
            }

            var content = ExtractContent(message.Content);
            var toolCalls = message.ToolCalls?.Count > 0
                ? message.ToolCalls.Select(ToToolCall).ToList()
                : null;
            return toolCalls is { Count: > 0 }
                ? new ChatMessage(ChatRole.Assistant, content, toolCalls)
                : new ChatMessage(ChatRole.Assistant, content);
        }

        private static IReadOnlyList<ChatContent> ExtractContent(JsonElement? contentElement)
        {
            if (contentElement == null || contentElement.Value.ValueKind == JsonValueKind.Null || contentElement.Value.ValueKind == JsonValueKind.Undefined)
            {
                return [];
            }

            var content = contentElement.Value;
            return content.ValueKind switch
            {
                JsonValueKind.String => [new ChatContent(content.GetString() ?? string.Empty)],
                JsonValueKind.Array => ExtractContentParts(content),
                JsonValueKind.Object => ExtractContentObject(content),
                _ => []
            };
        }

        private static IReadOnlyList<ChatContent> ExtractContentParts(JsonElement content)
        {
            var results = new List<ChatContent>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("text", out var textProp))
                {
                    results.Add(new ChatContent(textProp.GetString() ?? string.Empty));
                    continue;
                }

                if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase)
                    && item.TryGetProperty("image_url", out var imageProp))
                {
                    var url = imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp)
                        ? urlProp.GetString()
                        : imageProp.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        results.Add(new ChatContent(new ChatImageUrl(url)));
                    }
                }
            }

            return results;
        }

        private static IReadOnlyList<ChatContent> ExtractContentObject(JsonElement content)
        {
            if (content.TryGetProperty("text", out var textProp))
            {
                return [new ChatContent(textProp.GetString() ?? string.Empty)];
            }

            if (content.TryGetProperty("image_url", out var imageProp))
            {
                var url = imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp)
                    ? urlProp.GetString()
                    : imageProp.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return [new ChatContent(new ChatImageUrl(url))];
                }
            }

            return [];
        }

        private static ChatToolCall ToToolCall(OpenRouterChatToolCall toolCall)
        {
            return new ChatToolCall
            {
                Id = toolCall.Id ?? string.Empty,
                Type = toolCall.Type ?? "function",
                Function = new ChatToolCallFunction
                {
                    Name = toolCall.Function?.Name ?? string.Empty,
                    Arguments = ParseArguments(toolCall.Function?.Arguments)
                }
            };
        }

        internal static JsonElement ParseArguments(string? arguments)
        {
            var raw = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }

        internal static ChatCompletionUsage? ToUsage(OpenRouterUsage? usage)
        {
            if (usage == null)
            {
                return null;
            }

            return new ChatCompletionUsage
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                PromptTokensDetails = usage.PromptTokensDetails == null
                    ? null
                    : new ChatPromptTokensDetails
                    {
                        CachedTokens = usage.PromptTokensDetails.CachedTokens
                    }
            };
        }

        internal static string? NormalizeFinishReason(string? finishReason, IReadOnlyList<ChatToolCall>? toolCalls)
        {
            if (toolCalls is { Count: > 0 })
            {
                return "tool_calls";
            }

            return finishReason?.ToLowerInvariant() switch
            {
                null => null,
                "stop" => "stop",
                "length" => "length",
                "max_tokens" => "length",
                "tool_calls" => "tool_calls",
                "tool_use" => "tool_calls",
                _ => "stop"
            };
        }
    }

    private sealed class OpenRouterStreamingAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly Dictionary<int, MutableToolCall> _toolCalls = [];
        private string? _finishReason;
        private ChatCompletionUsage? _usage;

        public void Apply(OpenRouterChatStreamResponse response, Action<ChatCompletionChunk> onChunk)
        {
            _usage = OpenRouterResponseMapper.ToUsage(response.Usage) ?? _usage;
            var choice = response.Choices?.FirstOrDefault();
            if (choice == null)
            {
                return;
            }

            var delta = choice.Delta;
            if (delta == null)
            {
                if (!string.IsNullOrWhiteSpace(choice.FinishReason))
                {
                    _finishReason = OpenRouterResponseMapper.NormalizeFinishReason(choice.FinishReason, null);
                }

                return;
            }

            var textDelta = ExtractDeltaText(delta.Content);
            if (!string.IsNullOrEmpty(textDelta))
            {
                _content.Append(textDelta);
                onChunk(new ChatCompletionChunk(
                [
                    new ChatChoiceDelta(
                        new ChatDelta(MapRole(delta.Role), textDelta),
                        null)
                ]));
            }

            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in delta.ToolCalls)
                {
                    var index = toolCall.Index ?? _toolCalls.Count;
                    if (!_toolCalls.TryGetValue(index, out var mutable))
                    {
                        mutable = new MutableToolCall();
                        _toolCalls[index] = mutable;
                    }

                    if (!string.IsNullOrWhiteSpace(toolCall.Id))
                    {
                        mutable.Id = toolCall.Id;
                    }

                    if (!string.IsNullOrWhiteSpace(toolCall.Type))
                    {
                        mutable.Type = toolCall.Type;
                    }

                    if (!string.IsNullOrWhiteSpace(toolCall.Function?.Name))
                    {
                        mutable.Name = toolCall.Function.Name;
                    }

                    if (!string.IsNullOrWhiteSpace(toolCall.Function?.Arguments))
                    {
                        mutable.Arguments.Append(toolCall.Function.Arguments);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(choice.FinishReason))
            {
                _finishReason = OpenRouterResponseMapper.NormalizeFinishReason(
                    choice.FinishReason,
                    _toolCalls.Count > 0 ? BuildToolCalls() : null);
            }
        }

        public ChatCompletionResponse ToResponse()
        {
            var content = _content.Length == 0 ? [] : new[] { new ChatContent(_content.ToString()) };
            var toolCalls = BuildToolCalls();
            var message = toolCalls.Count > 0
                ? new ChatMessage(ChatRole.Assistant, content, toolCalls)
                : new ChatMessage(ChatRole.Assistant, content);
            var finishReason = _finishReason ?? (toolCalls.Count > 0 ? "tool_calls" : "stop");
            return new ChatCompletionResponse([new ChatChoice(message, finishReason)], _usage);
        }

        private List<ChatToolCall> BuildToolCalls()
        {
            return _toolCalls
                .OrderBy(pair => pair.Key)
                .Select(pair => new ChatToolCall
                {
                    Id = pair.Value.Id ?? string.Empty,
                    Type = pair.Value.Type ?? "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = pair.Value.Name ?? string.Empty,
                        Arguments = OpenRouterResponseMapper.ParseArguments(pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString())
                    }
                })
                .ToList();
        }

        private static ChatRole? MapRole(string? role) => role?.ToLowerInvariant() switch
        {
            "assistant" => ChatRole.Assistant,
            "user" => ChatRole.User,
            "system" => ChatRole.System,
            "developer" => ChatRole.Developer,
            "tool" => ChatRole.Tool,
            _ => null
        };

        private static string? ExtractDeltaText(JsonElement? contentElement)
        {
            if (contentElement == null)
            {
                return null;
            }

            var content = contentElement.Value;
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())),
                JsonValueKind.Object when content.TryGetProperty("text", out var textProp) => textProp.GetString(),
                _ => null
            };
        }

        private sealed class MutableToolCall
        {
            public string? Id { get; set; }
            public string? Type { get; set; }
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}

public sealed record OpenRouterChatConfig
{
    public string ApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";
    public string? HttpReferer { get; init; }
    public string? AppTitle { get; init; }
}

internal sealed record OpenRouterChatRequest(
    string Model,
    IReadOnlyList<OpenRouterChatMessage> Messages,
    IReadOnlyList<OpenRouterChatTool>? Tools,
    double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort,
    bool Stream)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class OpenRouterChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public object? Content => (object?)ContentParts ?? ContentText;

    [JsonIgnore]
    public string? ContentText { get; init; }

    [JsonIgnore]
    public IReadOnlyList<OpenRouterChatContentPart>? ContentParts { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenRouterChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    public static OpenRouterChatMessage Create(
        string role,
        string? contentText,
        IReadOnlyList<OpenRouterChatContentPart>? contentParts,
        IReadOnlyList<OpenRouterChatToolCall>? toolCalls,
        string? toolCallId,
        string? name)
    {
        return new OpenRouterChatMessage
        {
            Role = role,
            ContentText = contentText,
            ContentParts = contentParts,
            ToolCalls = toolCalls,
            ToolCallId = toolCallId,
            Name = name
        };
    }
}

internal sealed record OpenRouterChatContentPart(
    string Type,
    string? Text,
    [property: JsonPropertyName("image_url")] OpenRouterChatImageUrl? ImageUrl);

internal sealed record OpenRouterChatImageUrl(string Url);

internal sealed record OpenRouterChatTool(
    string Type,
    OpenRouterChatFunctionDefinition Function);

internal sealed record OpenRouterChatFunctionDefinition(
    string Name,
    string? Description,
    JsonNode? Parameters);

internal sealed record OpenRouterChatToolCall(
    string? Id,
    string? Type,
    OpenRouterChatToolCallFunction? Function);

internal sealed record OpenRouterChatToolCallFunction(
    string? Name,
    string? Arguments);

internal sealed record OpenRouterChatResponse(
    IReadOnlyList<OpenRouterChatChoice>? Choices,
    OpenRouterUsage? Usage);

internal sealed record OpenRouterChatChoice(
    [property: JsonPropertyName("message")] OpenRouterChatResponseMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record OpenRouterChatResponseMessage(
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenRouterChatToolCall>? ToolCalls);

internal sealed record OpenRouterUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens,
    [property: JsonPropertyName("prompt_tokens_details")] OpenRouterPromptTokensDetails? PromptTokensDetails);

internal sealed record OpenRouterPromptTokensDetails(
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens);

internal sealed record OpenRouterChatStreamResponse(
    IReadOnlyList<OpenRouterChatStreamChoice>? Choices,
    OpenRouterUsage? Usage);

internal sealed record OpenRouterChatStreamChoice(
    OpenRouterChatStreamDelta? Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record OpenRouterChatStreamDelta(
    string? Role,
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenRouterChatStreamToolCall>? ToolCalls);

internal sealed record OpenRouterChatStreamToolCall(
    int? Index,
    string? Id,
    string? Type,
    OpenRouterChatToolCallFunction? Function);
