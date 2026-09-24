using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.OpenAiCompatible;

/// <summary>
/// Raw-HTTP client for the <c>openai-compatible</c> provider. Ported from
/// <see cref="AntRunner.Chat.OpenRouter.OpenRouterChatClient"/>: same request mapping,
/// SSE streaming, tool-call accumulation, and <see cref="ProviderChatBehavior"/>
/// support, minus the OpenRouter attribution headers and the required-key rule
/// (a local endpoint such as vLLM typically runs keyless). POSTs to
/// <c>{BaseUrl}/chat/completions</c> for both streaming and non-streaming.
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleChatConfig _config;
    private readonly string? _defaultModel;
    private readonly ProviderChatBehavior? _behavior;
    private readonly ILogger<OpenAiCompatibleChatClient> _logger;

    public bool SupportsToolChoiceNone => true;

    public OpenAiCompatibleChatClient(
        HttpClient httpClient,
        OpenAiCompatibleChatConfig config,
        string? defaultModel,
        ILogger<OpenAiCompatibleChatClient>? logger = null,
        ProviderChatBehavior? behavior = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _defaultModel = defaultModel;
        _behavior = behavior;
        _logger = logger ?? NullLogger<OpenAiCompatibleChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateRequestMessage(BuildRequestBody(request, stream: false));
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible chat request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<OpenAiCompatibleChatResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI-compatible chat response was empty.");
        return OpenAiCompatibleResponseMapper.ToChatCompletionResponse(parsed);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateRequestMessage(BuildRequestBody(request, stream: true));
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenAI-compatible chat stream failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulator = new OpenAiCompatibleStreamingAccumulator();
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

    /// <summary>
    /// Serializes the typed payload, then lets the model row's chat behavior override it.
    /// Row-owned thinking control replaces the built-in reasoning mapping entirely.
    /// </summary>
    private JsonObject BuildRequestBody(ChatCompletionRequest request, bool stream)
    {
        var messages = _behavior is { HasSystemMessageMerge: true }
            ? SystemMessageMerger.CombineSystemAndDeveloperMessages(request.Messages)
            : request.Messages.ToList();
        var payload = OpenAiCompatibleRequestMapper.ToRequest(request, ResolveModel(request), stream, messages);
        var body = JsonSerializer.SerializeToNode(payload, SerializerOptions)?.AsObject()
            ?? throw new InvalidOperationException("OpenAI-compatible chat request payload could not be serialized.");

        ProviderChatBehaviorApplier.ApplyExtraRequestFields(body, _behavior);

        var thinkingActions = ProviderChatBehaviorApplier.ResolveThinkingActions(_behavior, request.ReasoningEffort);
        if (thinkingActions != null)
        {
            body.Remove("reasoning");
            body.Remove("reasoning_effort");
            ProviderChatBehaviorApplier.ApplyThinkingActions(body, body["messages"] as JsonArray, thinkingActions);
        }

        return body;
    }

    private HttpRequestMessage CreateRequestMessage(JsonObject body)
    {
        var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        return message;
    }

    private string ResolveModel(ChatCompletionRequest request)
    {
        var model = request.Model ?? _defaultModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("OpenAI-compatible chat requires a model identifier.");
        }

        return model;
    }

    private static bool ProcessServerSentEvent(
        string payload,
        OpenAiCompatibleStreamingAccumulator accumulator,
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

        var parsed = JsonSerializer.Deserialize<OpenAiCompatibleChatStreamResponse>(trimmed, SerializerOptions);
        if (parsed != null)
        {
            accumulator.Apply(parsed, onChunk);
        }

        return false;
    }

    private static class OpenAiCompatibleRequestMapper
    {
        public static OpenAiCompatibleChatRequest ToRequest(
            ChatCompletionRequest request,
            string model,
            bool stream,
            IReadOnlyList<ChatMessage>? messages = null)
        {
            var mappedMessages = (messages ?? request.Messages).Select(ToMessage).ToList();
            var tools = request.Tools?.Select(ToTool).ToList();
            var (temperature, topP, extensions) = ResolveSampling(request);
            return new OpenAiCompatibleChatRequest(
                Model: model,
                Messages: mappedMessages,
                Tools: tools,
                Temperature: temperature,
                TopP: topP,
                ReasoningEffort: request.ReasoningEffort,
                Stream: stream,
                ToolChoice: request.ToolChoice)
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

        private static OpenAiCompatibleChatMessage ToMessage(ChatMessage message)
        {
            if (message.Role == ChatRole.Tool)
            {
                return OpenAiCompatibleChatMessage.Create(
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

            return OpenAiCompatibleChatMessage.Create(
                role: ToRole(message.Role),
                contentText: contentText,
                contentParts: contentParts,
                toolCalls: toolCalls,
                toolCallId: null,
                name: null);
        }

        private static bool CanUseStringContent(IReadOnlyList<ChatContent> content) =>
            content.Count == 1 && content[0].IsText;

        private static OpenAiCompatibleChatTool ToTool(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("OpenAI-compatible function tools require a function definition.");
            }

            return new OpenAiCompatibleChatTool(
                Type: tool.Type,
                Function: new OpenAiCompatibleChatFunctionDefinition(
                    Name: tool.Function.Name,
                    Description: tool.Function.Description,
                    Parameters: tool.Function.Parameters));
        }

        private static OpenAiCompatibleChatToolCall ToToolCall(ChatToolCall toolCall) =>
            new(
                Id: toolCall.Id,
                Type: toolCall.Type,
                Function: new OpenAiCompatibleChatToolCallFunction(
                    Name: toolCall.Function.Name,
                    Arguments: toolCall.Function.Arguments.ValueKind == JsonValueKind.String
                        ? toolCall.Function.Arguments.GetString() ?? "{}"
                        : toolCall.Function.Arguments.GetRawText()));

        private static OpenAiCompatibleChatContentPart ToContentPart(ChatContent content)
        {
            if (content.IsText)
            {
                return new OpenAiCompatibleChatContentPart("text", content.Text, null);
            }

            if (content.IsImage && content.ImageUrl != null)
            {
                return new OpenAiCompatibleChatContentPart(
                    "image_url",
                    null,
                    new OpenAiCompatibleChatImageUrl(content.ImageUrl.Url));
            }

            throw new InvalidOperationException("Unsupported OpenAI-compatible content item.");
        }

        private static string ToRole(ChatRole role) => role switch
        {
            ChatRole.System => "system",
            ChatRole.Developer => "developer",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new InvalidOperationException($"Unsupported OpenAI-compatible role '{role}'.")
        };
    }

    private static class OpenAiCompatibleResponseMapper
    {
        public static ChatCompletionResponse ToChatCompletionResponse(OpenAiCompatibleChatResponse response)
        {
            var choices = response.Choices?.Select(ToChoice).ToList()
                ?? throw new InvalidOperationException("OpenAI-compatible response did not contain choices.");
            return new ChatCompletionResponse(choices, ToUsage(response.Usage));
        }

        private static ChatChoice ToChoice(OpenAiCompatibleChatChoice choice)
        {
            var message = ToMessage(choice.Message);
            var finishReason = NormalizeFinishReason(choice.FinishReason, message.ToolCalls);
            return new ChatChoice(message, finishReason);
        }

        private static ChatMessage ToMessage(OpenAiCompatibleChatResponseMessage? message)
        {
            if (message == null)
            {
                throw new InvalidOperationException("OpenAI-compatible choice message is required.");
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

        private static ChatToolCall ToToolCall(OpenAiCompatibleChatToolCall toolCall)
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

        internal static ChatCompletionUsage? ToUsage(OpenAiCompatibleUsage? usage)
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

    private sealed class OpenAiCompatibleStreamingAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly Dictionary<int, MutableToolCall> _toolCalls = [];
        private string? _finishReason;
        private ChatCompletionUsage? _usage;

        public void Apply(OpenAiCompatibleChatStreamResponse response, Action<ChatCompletionChunk> onChunk)
        {
            _usage = OpenAiCompatibleResponseMapper.ToUsage(response.Usage) ?? _usage;
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
                    _finishReason = OpenAiCompatibleResponseMapper.NormalizeFinishReason(choice.FinishReason, null);
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
                _finishReason = OpenAiCompatibleResponseMapper.NormalizeFinishReason(
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
                        Arguments = OpenAiCompatibleResponseMapper.ParseArguments(pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString())
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

public sealed record OpenAiCompatibleChatConfig
{
    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
}

internal sealed record OpenAiCompatibleChatRequest(
    string Model,
    IReadOnlyList<OpenAiCompatibleChatMessage> Messages,
    IReadOnlyList<OpenAiCompatibleChatTool>? Tools,
    double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort,
    bool Stream,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class OpenAiCompatibleChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public object? Content => (object?)ContentParts ?? ContentText;

    [JsonIgnore]
    public string? ContentText { get; init; }

    [JsonIgnore]
    public IReadOnlyList<OpenAiCompatibleChatContentPart>? ContentParts { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAiCompatibleChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    public static OpenAiCompatibleChatMessage Create(
        string role,
        string? contentText,
        IReadOnlyList<OpenAiCompatibleChatContentPart>? contentParts,
        IReadOnlyList<OpenAiCompatibleChatToolCall>? toolCalls,
        string? toolCallId,
        string? name)
    {
        return new OpenAiCompatibleChatMessage
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

internal sealed record OpenAiCompatibleChatContentPart(
    string Type,
    string? Text,
    [property: JsonPropertyName("image_url")] OpenAiCompatibleChatImageUrl? ImageUrl);

internal sealed record OpenAiCompatibleChatImageUrl(string Url);

internal sealed record OpenAiCompatibleChatTool(
    string Type,
    OpenAiCompatibleChatFunctionDefinition Function);

internal sealed record OpenAiCompatibleChatFunctionDefinition(
    string Name,
    string? Description,
    JsonNode? Parameters);

internal sealed record OpenAiCompatibleChatToolCall(
    string? Id,
    string? Type,
    OpenAiCompatibleChatToolCallFunction? Function);

internal sealed record OpenAiCompatibleChatToolCallFunction(
    string? Name,
    string? Arguments);

internal sealed record OpenAiCompatibleChatResponse(
    IReadOnlyList<OpenAiCompatibleChatChoice>? Choices,
    OpenAiCompatibleUsage? Usage);

internal sealed record OpenAiCompatibleChatChoice(
    [property: JsonPropertyName("message")] OpenAiCompatibleChatResponseMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record OpenAiCompatibleChatResponseMessage(
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAiCompatibleChatToolCall>? ToolCalls);

internal sealed record OpenAiCompatibleUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens,
    [property: JsonPropertyName("prompt_tokens_details")] OpenAiCompatiblePromptTokensDetails? PromptTokensDetails);

internal sealed record OpenAiCompatiblePromptTokensDetails(
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens);

internal sealed record OpenAiCompatibleChatStreamResponse(
    IReadOnlyList<OpenAiCompatibleChatStreamChoice>? Choices,
    OpenAiCompatibleUsage? Usage);

internal sealed record OpenAiCompatibleChatStreamChoice(
    OpenAiCompatibleChatStreamDelta? Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record OpenAiCompatibleChatStreamDelta(
    string? Role,
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAiCompatibleChatStreamToolCall>? ToolCalls);

internal sealed record OpenAiCompatibleChatStreamToolCall(
    int? Index,
    string? Id,
    string? Type,
    OpenAiCompatibleChatToolCallFunction? Function);
