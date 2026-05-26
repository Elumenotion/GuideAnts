using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.GoogleGemini;

public sealed class GoogleGeminiChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly GoogleGeminiChatConfig _config;
    private readonly string? _defaultModel;
    private readonly ILogger<GoogleGeminiChatClient> _logger;

    public GoogleGeminiChatClient(
        HttpClient httpClient,
        GoogleGeminiChatConfig config,
        string? defaultModel,
        ILogger<GoogleGeminiChatClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _defaultModel = defaultModel;
        _logger = logger ?? NullLogger<GoogleGeminiChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = GoogleGeminiRequestMapper.ToRequest(request, ResolveModel(request));
        using var message = CreateRequestMessage(payload, stream: false);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Google Gemini chat request failed ({(int)response.StatusCode}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<GoogleGeminiGenerateContentResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("Google Gemini chat response was empty.");
        return GoogleGeminiResponseMapper.ToChatCompletionResponse(parsed);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var payload = GoogleGeminiRequestMapper.ToRequest(request, ResolveModel(request));
        using var message = CreateRequestMessage(payload, stream: true);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Google Gemini chat stream failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulator = new GoogleGeminiStreamingAccumulator();
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

    private HttpRequestMessage CreateRequestMessage(
        GoogleGeminiGenerateContentRequest payload,
        bool stream)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
        }

        var method = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{payload.Model}:{method}";

        var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SerializerOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-goog-api-key", _config.ApiKey);
        return message;
    }

    private string ResolveModel(ChatCompletionRequest request)
    {
        var model = request.Model ?? _defaultModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Google Gemini chat requires a model identifier.");
        }

        return NormalizeModelName(model);
    }

    private static string NormalizeModelName(string model)
    {
        var trimmed = model.Trim();
        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"models/{trimmed}";
    }

    private static bool ProcessServerSentEvent(
        string payload,
        GoogleGeminiStreamingAccumulator accumulator,
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

        var parsed = JsonSerializer.Deserialize<GoogleGeminiGenerateContentResponse>(trimmed, SerializerOptions);
        if (parsed != null)
        {
            accumulator.Apply(parsed, onChunk);
        }

        return false;
    }

    private static class GoogleGeminiRequestMapper
    {
        public static GoogleGeminiGenerateContentRequest ToRequest(ChatCompletionRequest request, string model)
        {
            var systemParts = request.Messages
                .Where(message => message.Role is ChatRole.System or ChatRole.Developer)
                .SelectMany(ToSystemParts)
                .ToList();

            var contents = request.Messages
                .Where(message => message.Role is not ChatRole.System and not ChatRole.Developer)
                .Select(ToContent)
                .ToList();

            if (contents.Count == 0)
            {
                throw new InvalidOperationException("Google Gemini chat requires at least one non-system message.");
            }

            IReadOnlyList<GoogleGeminiTool>? tools = request.Tools?.Count > 0
                ? new List<GoogleGeminiTool> { new(request.Tools.Select(ToFunctionDeclaration).ToList()) }
                : null;
            var (temperature, topP, additionalGenerationConfig) = ResolveGeminiSampling(request);
            var generationConfig = new GoogleGeminiGenerationConfig(
                Temperature: temperature,
                TopP: topP,
                ThinkingConfig: ToThinkingConfig(request.ReasoningEffort, model))
            {
                AdditionalProperties = additionalGenerationConfig
            };

            return new GoogleGeminiGenerateContentRequest(
                Model: model,
                Contents: contents,
                Tools: tools,
                SystemInstruction: systemParts.Count > 0 ? new GoogleGeminiContent(null, systemParts) : null,
                GenerationConfig: generationConfig);
        }

        private static (double? Temperature, double? TopP, Dictionary<string, JsonElement>? AdditionalGenerationConfig)
            ResolveGeminiSampling(ChatCompletionRequest request)
        {
            double? temperature = null;
            double? topP = null;
            Dictionary<string, JsonElement>? additional = null;
            if (request.SamplingParameters == null)
            {
                return (temperature, topP, additional);
            }

            foreach (var (key, value) in request.SamplingParameters)
            {
                if (string.Equals(key, "temperature", StringComparison.Ordinal))
                {
                    temperature = value;
                    continue;
                }

                if (string.Equals(key, "top_p", StringComparison.Ordinal) || string.Equals(key, "topP", StringComparison.Ordinal))
                {
                    topP = value;
                    continue;
                }

                additional ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                additional[key] = JsonSerializer.SerializeToElement(value);
            }

            return (temperature, topP, additional);
        }

        // Gemini 2.5 series uses thinkingBudget (integer token count).
        // Gemini 3+ series uses thinkingLevel (string).
        private static GoogleGeminiThinkingConfig? ToThinkingConfig(string? reasoningEffort, string model)
        {
            if (string.IsNullOrWhiteSpace(reasoningEffort))
            {
                return null;
            }

            var m = model.ToLowerInvariant();
            bool isGemini25 = m.Contains("gemini-2.5") || m.Contains("gemini-2-5");

            if (isGemini25)
            {
                int budget = reasoningEffort.Trim().ToLowerInvariant() switch
                {
                    "none"   => 0,      // Flash only: disables thinking
                    "low"    => 2048,
                    "medium" => 8192,
                    "high"   => 24576,
                    _ => throw new InvalidOperationException(
                        $"Unsupported Google Gemini reasoning_effort '{reasoningEffort}'.")
                };
                return new GoogleGeminiThinkingConfig(ThinkingBudget: budget, ThinkingLevel: null);
            }

            // Gemini 3+ — thinkingLevel string
            string level = reasoningEffort.Trim().ToLowerInvariant() switch
            {
                "minimal" => "minimal",
                "low"     => "low",
                "medium"  => "medium",
                "high"    => "high",
                _ => throw new InvalidOperationException(
                    $"Unsupported Google Gemini reasoning_effort '{reasoningEffort}'.")
            };
            return new GoogleGeminiThinkingConfig(ThinkingBudget: null, ThinkingLevel: level);
        }

        private static IEnumerable<GoogleGeminiPart> ToSystemParts(ChatMessage message)
        {
            foreach (var content in message.Content)
            {
                if (!content.IsText)
                {
                    throw new InvalidOperationException("Google Gemini system/developer messages only support text content.");
                }

                yield return new GoogleGeminiPart(Text: content.Text);
            }
        }

        private static GoogleGeminiContent ToContent(ChatMessage message)
        {
            if (message.Role == ChatRole.Tool)
            {
                return new GoogleGeminiContent(
                    "user",
                    [CreateFunctionResponsePart(message)]);
            }

            var parts = new List<GoogleGeminiPart>();
            foreach (var content in message.Content)
            {
                parts.Add(ToPart(content));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                parts.AddRange(message.ToolCalls.Select(ToFunctionCallPart));
            }

            return new GoogleGeminiContent(ToRole(message.Role), parts);
        }

        private static GoogleGeminiPart ToPart(ChatContent content)
        {
            if (content.IsText)
            {
                return new GoogleGeminiPart(Text: content.Text);
            }

            if (content.IsImage && content.ImageUrl != null)
            {
                return ToImagePart(content.ImageUrl.Url);
            }

            throw new InvalidOperationException("Unsupported Google Gemini content item.");
        }

        private static GoogleGeminiPart ToImagePart(string url)
        {
            if (TryParseDataUrl(url, out var mimeType, out var bytes))
            {
                return new GoogleGeminiPart(InlineData: new GoogleGeminiBlob(mimeType, Convert.ToBase64String(bytes)));
            }

            return new GoogleGeminiPart(FileData: new GoogleGeminiFileData(GuessMimeType(url), url));
        }

        private static GoogleGeminiPart ToFunctionCallPart(ChatToolCall toolCall)
        {
            return new GoogleGeminiPart(
                FunctionCall: new GoogleGeminiFunctionCall(
                    toolCall.Function.Name,
                    ToJsonNode(toolCall.Function.Arguments)));
        }

        private static GoogleGeminiPart CreateFunctionResponsePart(ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.FunctionName))
            {
                throw new InvalidOperationException("Google Gemini tool messages require FunctionName.");
            }

            return new GoogleGeminiPart(
                FunctionResponse: new GoogleGeminiFunctionResponse(
                    message.FunctionName,
                    ParseToolResponse(message.GetText())));
        }

        private static GoogleGeminiFunctionDeclaration ToFunctionDeclaration(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("Google Gemini tools require a function definition.");
            }

            return new GoogleGeminiFunctionDeclaration(
                tool.Function.Name,
                tool.Function.Description,
                tool.Function.Parameters);
        }

        private static JsonNode ParseToolResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(content)?.DeepClone() ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject
                {
                    ["content"] = content
                };
            }
        }

        private static JsonNode? ToJsonNode(JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return null;
            }

            return JsonNode.Parse(element.GetRawText())?.DeepClone();
        }

        private static string ToRole(ChatRole role) => role switch
        {
            ChatRole.User => "user",
            ChatRole.Assistant => "model",
            _ => throw new InvalidOperationException($"Unsupported Google Gemini role '{role}'.")
        };

        private static bool TryParseDataUrl(string url, out string mimeType, out byte[] bytes)
        {
            mimeType = string.Empty;
            bytes = [];
            const string prefix = "data:";
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var commaIndex = url.IndexOf(',');
            if (commaIndex < 0)
            {
                return false;
            }

            var metadata = url[prefix.Length..commaIndex];
            var payload = url[(commaIndex + 1)..];
            if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            mimeType = metadata[..^";base64".Length];
            bytes = Convert.FromBase64String(payload);
            return true;
        }

        private static string GuessMimeType(string url)
        {
            if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return "image/png";
            }

            if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return "image/jpeg";
            }

            if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                return "image/webp";
            }

            if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                return "image/gif";
            }

            return "application/octet-stream";
        }
    }

    private static class GoogleGeminiResponseMapper
    {
        public static ChatCompletionResponse ToChatCompletionResponse(GoogleGeminiGenerateContentResponse response)
        {
            var choices = response.Candidates?.Select(ToChoice).ToList()
                ?? throw new InvalidOperationException("Google Gemini response did not contain candidates.");
            return new ChatCompletionResponse(choices, ToUsage(response.UsageMetadata));
        }

        public static ChatCompletionUsage? ToUsage(GoogleGeminiUsageMetadata? usage)
        {
            if (usage == null)
            {
                return null;
            }

            return new ChatCompletionUsage
            {
                PromptTokens = usage.PromptTokenCount,
                CompletionTokens = usage.CandidatesTokenCount,
                TotalTokens = usage.TotalTokenCount,
                PromptTokensDetails = usage.CachedContentTokenCount.HasValue
                    ? new ChatPromptTokensDetails
                    {
                        CachedTokens = usage.CachedContentTokenCount
                    }
                    : null
            };
        }

        public static string? NormalizeFinishReason(string? finishReason, IReadOnlyList<ChatToolCall>? toolCalls)
        {
            if (toolCalls is { Count: > 0 })
            {
                return "tool_calls";
            }

            return finishReason?.ToUpperInvariant() switch
            {
                null => null,
                "STOP" => "stop",
                "MAX_TOKENS" => "length",
                "FINISH_REASON_UNSPECIFIED" => null,
                "SAFETY" => "stop",
                "RECITATION" => "stop",
                "TOO_MANY_TOOL_CALLS" => "tool_calls",
                "UNEXPECTED_TOOL_CALL" => "tool_calls",
                _ => "stop"
            };
        }

        private static ChatChoice ToChoice(GoogleGeminiCandidate candidate)
        {
            var message = ToMessage(candidate.Content);
            return new ChatChoice(message, NormalizeFinishReason(candidate.FinishReason, message.ToolCalls));
        }

        private static ChatMessage ToMessage(GoogleGeminiContent? content)
        {
            if (content == null)
            {
                throw new InvalidOperationException("Google Gemini candidate content is required.");
            }

            var textParts = new List<ChatContent>();
            var toolCalls = new List<ChatToolCall>();

            foreach (var part in content.Parts ?? [])
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    textParts.Add(new ChatContent(part.Text));
                }

                if (part.FunctionCall != null)
                {
                    toolCalls.Add(new ChatToolCall
                    {
                        Id = $"call_{toolCalls.Count + 1}",
                        Type = "function",
                        Function = new ChatToolCallFunction
                        {
                            Name = part.FunctionCall.Name ?? string.Empty,
                            Arguments = ToJsonElement(part.FunctionCall.Args)
                        }
                    });
                }
            }

            return toolCalls.Count > 0
                ? new ChatMessage(ChatRole.Assistant, textParts, toolCalls)
                : new ChatMessage(ChatRole.Assistant, textParts);
        }

        private static JsonElement ToJsonElement(JsonNode? node)
        {
            var raw = node?.ToJsonString() ?? "{}";
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
    }

    private sealed class GoogleGeminiStreamingAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly List<MutableToolCall> _toolCalls = [];
        private string? _finishReason;
        private ChatCompletionUsage? _usage;

        public void Apply(GoogleGeminiGenerateContentResponse response, Action<ChatCompletionChunk> onChunk)
        {
            _usage = GoogleGeminiResponseMapper.ToUsage(response.UsageMetadata) ?? _usage;

            var candidate = response.Candidates?.FirstOrDefault();
            if (candidate == null)
            {
                return;
            }

            foreach (var part in candidate.Content?.Parts ?? [])
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    _content.Append(part.Text);
                    onChunk(new ChatCompletionChunk(
                    [
                        new ChatChoiceDelta(new ChatDelta(ChatRole.Assistant, part.Text), null)
                    ]));
                }

                if (part.FunctionCall != null)
                {
                    ApplyFunctionCall(part.FunctionCall);
                }
            }

            if (!string.IsNullOrWhiteSpace(candidate.FinishReason))
            {
                _finishReason = GoogleGeminiResponseMapper.NormalizeFinishReason(candidate.FinishReason, BuildToolCalls());
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

        private void ApplyFunctionCall(GoogleGeminiFunctionCall functionCall)
        {
            var index = _toolCalls.Count;
            if (index >= _toolCalls.Count)
            {
                _toolCalls.Add(new MutableToolCall());
            }

            var mutable = _toolCalls[index];
            mutable.Name = functionCall.Name ?? mutable.Name;
            if (functionCall.Args != null)
            {
                mutable.Arguments = functionCall.Args;
            }
        }

        private List<ChatToolCall> BuildToolCalls()
        {
            return _toolCalls
                .Select((toolCall, index) => new ChatToolCall
                {
                    Id = $"call_{index + 1}",
                    Type = "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = toolCall.Name ?? string.Empty,
                        Arguments = ToJsonElement(toolCall.Arguments)
                    }
                })
                .Where(toolCall => !string.IsNullOrWhiteSpace(toolCall.Function.Name))
                .ToList();
        }

        private static JsonElement ToJsonElement(JsonNode? node)
        {
            var raw = node?.ToJsonString() ?? "{}";
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }

        private sealed class MutableToolCall
        {
            public string? Name { get; set; }
            public JsonNode? Arguments { get; set; }
        }
    }
}

public sealed record GoogleGeminiChatConfig
{
    public string? ApiKey { get; init; }
}

internal sealed record GoogleGeminiGenerateContentRequest(
    [property: JsonIgnore] string Model,
    IReadOnlyList<GoogleGeminiContent> Contents,
    IReadOnlyList<GoogleGeminiTool>? Tools,
    GoogleGeminiContent? SystemInstruction,
    GoogleGeminiGenerationConfig? GenerationConfig);

internal sealed record GoogleGeminiGenerationConfig(
    double? Temperature,
    [property: JsonPropertyName("topP")] double? TopP,
    GoogleGeminiThinkingConfig? ThinkingConfig)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed record GoogleGeminiThinkingConfig(
    int? ThinkingBudget,
    string? ThinkingLevel);

internal sealed record GoogleGeminiTool(
    IReadOnlyList<GoogleGeminiFunctionDeclaration> FunctionDeclarations);

internal sealed record GoogleGeminiFunctionDeclaration(
    string Name,
    string? Description,
    JsonNode? Parameters);

internal sealed record GoogleGeminiContent(
    string? Role,
    IReadOnlyList<GoogleGeminiPart> Parts);

internal sealed record GoogleGeminiPart(
    string? Text = null,
    GoogleGeminiBlob? InlineData = null,
    GoogleGeminiFileData? FileData = null,
    GoogleGeminiFunctionCall? FunctionCall = null,
    GoogleGeminiFunctionResponse? FunctionResponse = null);

internal sealed record GoogleGeminiBlob(string MimeType, string Data);

internal sealed record GoogleGeminiFileData(string MimeType, string FileUri);

internal sealed record GoogleGeminiFunctionCall(string? Name, JsonNode? Args);

internal sealed record GoogleGeminiFunctionResponse(string Name, JsonNode Response);

internal sealed record GoogleGeminiGenerateContentResponse(
    IReadOnlyList<GoogleGeminiCandidate>? Candidates,
    GoogleGeminiUsageMetadata? UsageMetadata);

internal sealed record GoogleGeminiCandidate(
    GoogleGeminiContent? Content,
    string? FinishReason);

internal sealed record GoogleGeminiUsageMetadata(
    int? PromptTokenCount,
    int? CandidatesTokenCount,
    int? TotalTokenCount,
    int? CachedContentTokenCount);
