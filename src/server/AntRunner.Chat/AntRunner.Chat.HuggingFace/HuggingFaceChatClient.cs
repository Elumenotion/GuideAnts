using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.HuggingFace;

public sealed class HuggingFaceChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly HuggingFaceChatConfig _config;
    private readonly string? _defaultModel;
    private readonly ILogger<HuggingFaceChatClient> _logger;

    public HuggingFaceChatClient(
        HttpClient httpClient,
        HuggingFaceChatConfig config,
        string? defaultModel,
        ILogger<HuggingFaceChatClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _defaultModel = defaultModel;
        _logger = logger ?? NullLogger<HuggingFaceChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePayloadOrThrow(request, stream: false);
        using var message = CreateRequestMessage(payload);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Hugging Face chat request failed. status={StatusCode} body={Body}",
                (int)response.StatusCode,
                body);
            throw new InvalidOperationException(
                $"Hugging Face chat request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<HuggingFaceChatResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("Hugging Face chat response was empty.");
        return HuggingFaceResponseMapper.ToChatCompletionResponse(parsed);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePayloadOrThrow(request, stream: true);
        using var message = CreateRequestMessage(payload);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "Hugging Face chat stream failed. status={StatusCode} body={Body}",
                (int)response.StatusCode,
                errorBody);
            throw new InvalidOperationException(
                $"Hugging Face chat stream failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulator = new HuggingFaceStreamingAccumulator();
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

    private HttpRequestMessage CreateRequestMessage(HuggingFaceChatRequest payload)
    {
        if (string.IsNullOrWhiteSpace(_config.Token))
        {
            throw new InvalidOperationException("HuggingFace:Token is required.");
        }

        var endpoint = $"{(_config.RouterBaseUrl ?? "https://router.huggingface.co/v1").TrimEnd('/')}/chat/completions";
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        return message;
    }

    private string ResolveModel(ChatCompletionRequest request)
    {
        var model = request.Model ?? _defaultModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Hugging Face chat requires a model identifier.");
        }

        return model;
    }

    private HuggingFaceChatRequest CreatePayloadOrThrow(ChatCompletionRequest request, bool stream)
    {
        var model = ResolveModel(request);
        try
        {
            return HuggingFaceRequestMapper.ToRequest(request, model, stream);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogError(
                ex,
                "Failed to map Hugging Face request payload. model={Model} stream={Stream} messageCount={MessageCount} messageShapes={MessageShapes}",
                model,
                stream,
                request.Messages.Count,
                HuggingFaceRequestMapper.DescribeMessageShapes(request.Messages));
            throw;
        }
    }

    private static bool ProcessServerSentEvent(
        string payload,
        HuggingFaceStreamingAccumulator accumulator,
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

        var parsed = JsonSerializer.Deserialize<HuggingFaceChatStreamResponse>(trimmed, SerializerOptions);
        if (parsed != null)
        {
            accumulator.Apply(parsed, onChunk);
        }

        return false;
    }

    private static class HuggingFaceRequestMapper
    {
        public static HuggingFaceChatRequest ToRequest(ChatCompletionRequest request, string model, bool stream)
        {
            var messages = new List<HuggingFaceChatMessage>(request.Messages.Count);
            for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
            {
                messages.Add(ToMessage(request.Messages[messageIndex], messageIndex));
            }

            return new HuggingFaceChatRequest(
                Model: model,
                Messages: messages,
                Tools: request.Tools?.Select(ToTool).ToList(),
                Temperature: request.Temperature,
                TopP: request.TopP,
                ReasoningEffort: request.ReasoningEffort,
                Stream: stream);
        }

        public static string DescribeMessageShapes(IReadOnlyList<ChatMessage> messages)
        {
            if (messages.Count == 0)
            {
                return "[]";
            }

            var parts = new List<string>(messages.Count);
            for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
            {
                var message = messages[messageIndex];
                var contentShape = message.Content.Count == 0
                    ? "content=[]"
                    : $"content=[{string.Join(",", message.Content.Select(DescribeContentShape))}]";
                parts.Add($"#{messageIndex} role={message.Role} {contentShape}");
            }

            return string.Join("; ", parts);
        }

        private static HuggingFaceChatMessage ToMessage(ChatMessage message, int messageIndex)
        {
            if (message.Role == ChatRole.Tool)
            {
                return HuggingFaceChatMessage.Create(
                    "tool",
                    message.GetText(),
                    null,
                    null,
                    message.ToolCallId,
                    message.FunctionName);
            }

            var mappableContent = GetMappableContent(message.Content);
            if (mappableContent.Count == 0
                && message.Content.Count > 0
                && (message.ToolCalls == null || message.ToolCalls.Count == 0))
            {
                ToContentPart(message.Content[0], message.Role, messageIndex, 0);
            }

            string? contentText = null;
            IReadOnlyList<HuggingFaceChatContentPart>? contentParts = null;

            if (mappableContent.Count == 1 && mappableContent[0].IsText)
            {
                contentText = mappableContent[0].Text;
            }
            else if (mappableContent.Count > 0)
            {
                contentParts = mappableContent
                    .Select((content, contentIndex) => ToContentPart(content, message.Role, messageIndex, contentIndex))
                    .ToList();
            }

            var toolCalls = message.ToolCalls?.Count > 0
                ? message.ToolCalls.Select(ToToolCall).ToList()
                : null;

            return HuggingFaceChatMessage.Create(
                ToRole(message.Role),
                contentText,
                contentParts,
                toolCalls,
                null,
                null);
        }

        /// <summary>
        /// Drops empty text parts (e.g. <c>new ChatContent("")</c>) that are not <see cref="ChatContent.IsText"/>
        /// but still appear in <see cref="ChatMessage.Content"/> after tool-call turns.
        /// </summary>
        private static IReadOnlyList<ChatContent> GetMappableContent(IReadOnlyList<ChatContent> content)
        {
            if (content.Count == 0)
            {
                return content;
            }

            var filtered = new List<ChatContent>(content.Count);
            foreach (var part in content)
            {
                if (part.IsImage)
                {
                    filtered.Add(part);
                    continue;
                }

                if (part.IsText)
                {
                    filtered.Add(part);
                }
            }

            return filtered;
        }

        private static HuggingFaceChatTool ToTool(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("Hugging Face function tools require a function definition.");
            }

            return new HuggingFaceChatTool(
                tool.Type,
                new HuggingFaceChatFunctionDefinition(
                    tool.Function.Name,
                    tool.Function.Description,
                    tool.Function.Parameters));
        }

        private static HuggingFaceChatToolCall ToToolCall(ChatToolCall toolCall) =>
            new(
                toolCall.Id,
                toolCall.Type,
                new HuggingFaceChatToolCallFunction(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments.ValueKind == JsonValueKind.String
                        ? toolCall.Function.Arguments.GetString() ?? "{}"
                        : toolCall.Function.Arguments.GetRawText()));

        private static HuggingFaceChatContentPart ToContentPart(ChatContent content, ChatRole role, int messageIndex, int contentIndex)
        {
            if (content.IsText)
            {
                return new HuggingFaceChatContentPart("text", content.Text, null);
            }

            if (content.IsImage && content.ImageUrl != null)
            {
                return new HuggingFaceChatContentPart(
                    "image_url",
                    null,
                    new HuggingFaceChatImageUrl(content.ImageUrl.Url));
            }

            var shape = DescribeContentShape(content);
            var userFacing = content.IsImage
                ? "The chat model routed through Hugging Face does not support image inputs. "
                  + "Either switch to a vision-capable model or remove the image attachment."
                : $"A message content part could not be mapped for the Hugging Face chat provider "
                  + $"(message[{messageIndex}] role='{ToRole(role)}' part[{contentIndex}]: {shape}).";

            throw new InvalidOperationException(userFacing);
        }

        private static string DescribeContentShape(ChatContent content)
        {
            var textState = content.Text switch
            {
                null => "null",
                "" => "empty",
                _ => "present"
            };
            var imageState = content.ImageUrl?.Url switch
            {
                null => "null",
                "" => "empty",
                _ => "present"
            };

            return $"isText={content.IsText},isImage={content.IsImage},text={textState},imageUrl={imageState}";
        }

        private static string ToRole(ChatRole role) => role switch
        {
            ChatRole.System => "system",
            ChatRole.Developer => "developer",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new InvalidOperationException($"Unsupported Hugging Face role '{role}'.")
        };
    }

    private static class HuggingFaceResponseMapper
    {
        public static ChatCompletionResponse ToChatCompletionResponse(HuggingFaceChatResponse response)
        {
            var choices = response.Choices?.Select(ToChoice).ToList()
                ?? throw new InvalidOperationException("Hugging Face response did not contain choices.");
            return new ChatCompletionResponse(choices, ToUsage(response.Usage));
        }

        private static ChatChoice ToChoice(HuggingFaceChatChoice choice)
        {
            var message = ToMessage(choice.Message);
            return new ChatChoice(message, NormalizeFinishReason(choice.FinishReason, message.ToolCalls));
        }

        private static ChatMessage ToMessage(HuggingFaceChatResponseMessage? message)
        {
            if (message == null)
            {
                throw new InvalidOperationException("Hugging Face choice message is required.");
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
            if (contentElement == null || contentElement.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return [];
            }

            var content = contentElement.Value;
            if (content.ValueKind == JsonValueKind.String)
            {
                return [new ChatContent(content.GetString() ?? string.Empty)];
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var result = new List<ChatContent>();
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
                        result.Add(new ChatContent(textProp.GetString() ?? string.Empty));
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
                            result.Add(new ChatContent(new ChatImageUrl(url)));
                        }
                    }
                }

                return result;
            }

            if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var textOnly))
            {
                return [new ChatContent(textOnly.GetString() ?? string.Empty)];
            }

            return [];
        }

        private static ChatToolCall ToToolCall(HuggingFaceChatToolCall toolCall)
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

        internal static ChatCompletionUsage? ToUsage(HuggingFaceUsage? usage)
        {
            if (usage == null)
            {
                return null;
            }

            return new ChatCompletionUsage
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens
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

    private sealed class HuggingFaceStreamingAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly Dictionary<int, MutableToolCall> _toolCalls = [];
        private string? _finishReason;
        private ChatCompletionUsage? _usage;

        public void Apply(HuggingFaceChatStreamResponse response, Action<ChatCompletionChunk> onChunk)
        {
            _usage = HuggingFaceResponseMapper.ToUsage(response.Usage) ?? _usage;
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
                    _finishReason = HuggingFaceResponseMapper.NormalizeFinishReason(choice.FinishReason, null);
                }

                return;
            }

            var text = ExtractDeltaText(delta.Content);
            if (!string.IsNullOrEmpty(text))
            {
                _content.Append(text);
                onChunk(new ChatCompletionChunk(
                [
                    new ChatChoiceDelta(new ChatDelta(MapRole(delta.Role), text), null)
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

                    mutable.Id ??= toolCall.Id;
                    mutable.Type ??= toolCall.Type;
                    mutable.Name ??= toolCall.Function?.Name;
                    if (!string.IsNullOrWhiteSpace(toolCall.Function?.Arguments))
                    {
                        mutable.Arguments.Append(toolCall.Function.Arguments);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(choice.FinishReason))
            {
                _finishReason = HuggingFaceResponseMapper.NormalizeFinishReason(
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
            return new ChatCompletionResponse(
                [new ChatChoice(message, _finishReason ?? (toolCalls.Count > 0 ? "tool_calls" : "stop"))],
                _usage);
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
                        Arguments = HuggingFaceResponseMapper.ParseArguments(pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString())
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
                    .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out _))
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

public sealed record HuggingFaceChatConfig
{
    public string Token { get; init; } = string.Empty;
    public string RouterBaseUrl { get; init; } = "https://router.huggingface.co/v1";
}

internal sealed record HuggingFaceChatRequest(
    string Model,
    IReadOnlyList<HuggingFaceChatMessage> Messages,
    IReadOnlyList<HuggingFaceChatTool>? Tools,
    double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP,
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort,
    bool Stream);

internal sealed class HuggingFaceChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public object? Content => (object?)ContentParts ?? ContentText;

    [JsonIgnore]
    public string? ContentText { get; init; }

    [JsonIgnore]
    public IReadOnlyList<HuggingFaceChatContentPart>? ContentParts { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<HuggingFaceChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    public static HuggingFaceChatMessage Create(
        string role,
        string? contentText,
        IReadOnlyList<HuggingFaceChatContentPart>? contentParts,
        IReadOnlyList<HuggingFaceChatToolCall>? toolCalls,
        string? toolCallId,
        string? name)
    {
        return new HuggingFaceChatMessage
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

internal sealed record HuggingFaceChatContentPart(
    string Type,
    string? Text,
    [property: JsonPropertyName("image_url")] HuggingFaceChatImageUrl? ImageUrl);

internal sealed record HuggingFaceChatImageUrl(string Url);

internal sealed record HuggingFaceChatTool(string Type, HuggingFaceChatFunctionDefinition Function);

internal sealed record HuggingFaceChatFunctionDefinition(
    string Name,
    string? Description,
    JsonNode? Parameters);

internal sealed record HuggingFaceChatToolCall(
    string? Id,
    string? Type,
    HuggingFaceChatToolCallFunction? Function);

internal sealed record HuggingFaceChatToolCallFunction(string? Name, string? Arguments);

internal sealed record HuggingFaceChatResponse(
    IReadOnlyList<HuggingFaceChatChoice>? Choices,
    HuggingFaceUsage? Usage);

internal sealed record HuggingFaceChatChoice(
    HuggingFaceChatResponseMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record HuggingFaceChatResponseMessage(
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<HuggingFaceChatToolCall>? ToolCalls);

internal sealed record HuggingFaceUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens);

internal sealed record HuggingFaceChatStreamResponse(
    IReadOnlyList<HuggingFaceChatStreamChoice>? Choices,
    HuggingFaceUsage? Usage);

internal sealed record HuggingFaceChatStreamChoice(
    HuggingFaceChatStreamDelta? Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record HuggingFaceChatStreamDelta(
    string? Role,
    JsonElement? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<HuggingFaceChatStreamToolCall>? ToolCalls);

internal sealed record HuggingFaceChatStreamToolCall(
    int? Index,
    string? Id,
    string? Type,
    HuggingFaceChatToolCallFunction? Function);
