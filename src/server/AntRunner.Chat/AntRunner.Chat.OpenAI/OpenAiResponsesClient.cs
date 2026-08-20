using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Responses;
using ResponseImageContent = OpenAI.Responses.ImageContent;
using ResponseRefusalContent = OpenAI.Responses.RefusalContent;
using ResponseReasoningContent = OpenAI.Responses.ReasoningContent;
using ResponseReasoningSummary = OpenAI.Responses.ReasoningSummary;
using ResponseTextContent = OpenAI.Responses.TextContent;

namespace AntRunner.Chat.OpenAI;

public sealed class OpenAiResponsesClient : IChatCompletionClient
{
    private readonly OpenAiResponsesStreamingTransport _transport;
    private readonly ILogger<OpenAiResponsesClient> _logger;
    private readonly string? _defaultDeploymentId;

    public bool SupportsToolChoiceNone => false;

    public OpenAiResponsesClient(
        HttpClient httpClient,
        AzureOpenAiConfig config,
        ILogger<OpenAiResponsesClient>? logger = null)
    {
        _transport = new OpenAiResponsesStreamingTransport(httpClient, config);
        _logger = logger ?? NullLogger<OpenAiResponsesClient>.Instance;
        _defaultDeploymentId = config.DeploymentId;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var responseRequest = OpenAiResponsesMapper.ToResponseRequestWithDefault(
            request,
            _defaultDeploymentId);
        var response = await _transport.CreateAsync(
            responseRequest,
            eventHandler: null,
            cancellationToken);
        return OpenAiResponsesMapper.FromTerminalResponse(response, _logger);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var responseRequest = OpenAiResponsesMapper.ToResponseRequestWithDefault(
            request,
            _defaultDeploymentId);
        var streamHandler = new ResponsesStreamHandler(onChunk);
        var response = await _transport.CreateAsync(
            responseRequest,
            streamHandler.HandleEventAsync,
            cancellationToken);
        return OpenAiResponsesMapper.FromTerminalResponse(response, _logger);
    }

    private sealed class ResponsesStreamHandler
    {
        private readonly Action<ChatCompletionChunk>? _onChunk;
        private string? _lastContentType;
        private bool _hasEmittedContent;

        public ResponsesStreamHandler(Action<ChatCompletionChunk>? onChunk)
        {
            _onChunk = onChunk;
        }

        public Task HandleEventAsync(string @event, JsonElement eventData)
        {
            if (_onChunk == null)
            {
                return Task.CompletedTask;
            }

            switch (@event)
            {
                case "response.output_text.delta":
                    EmitDelta("text", ReadDelta(eventData));
                    break;
                case "response.refusal.delta":
                    EmitDelta("refusal", ReadDelta(eventData));
                    break;
                case "response.reasoning_summary_text.delta":
                    EmitDelta("reasoning_summary", ReadDelta(eventData), "thinking");
                    break;
                case "response.reasoning_text.delta":
                    EmitDelta("reasoning_content", ReadDelta(eventData), "thinking");
                    break;
            }

            return Task.CompletedTask;
        }

        private static string? ReadDelta(JsonElement eventData) =>
            eventData.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String
                ? delta.GetString()
                : null;

        private void EmitDelta(string contentType, string? delta, string? finishReason = null)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            // Emit paragraph break when switching between content types (e.g., reasoning_summary to reasoning_content)
            if (_hasEmittedContent && _lastContentType != contentType)
            {
                var separatorDelta = new ChatDelta(ChatRole.Assistant, "\n\n");
                _onChunk?.Invoke(new ChatCompletionChunk([new ChatChoiceDelta(separatorDelta, finishReason)]));
            }

            _lastContentType = contentType;
            _hasEmittedContent = true;

            var chatDelta = new ChatDelta(ChatRole.Assistant, delta);
            _onChunk?.Invoke(new ChatCompletionChunk([new ChatChoiceDelta(chatDelta, finishReason)]));
        }

    }

    private static class OpenAiResponsesMapper
    {
        internal static JsonObject ToResponseRequest(ChatCompletionRequest request) =>
            ToResponseRequestCore(request, defaultDeploymentId: null);

        internal static JsonObject ToResponseRequestWithDefault(
            ChatCompletionRequest request,
            string? defaultDeploymentId) =>
            ToResponseRequestCore(request, defaultDeploymentId);

        private static JsonObject ToResponseRequestCore(
            ChatCompletionRequest request,
            string? defaultDeploymentId)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var inputItems = new JsonArray();
            foreach (var message in request.Messages)
            {
                if (message.Role == ChatRole.Tool)
                {
                    inputItems.Add(ToJsonToolCallOutput(message));
                    continue;
                }

                inputItems.Add(ToJsonMessage(message));

                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        inputItems.Add(ToJsonToolCall(toolCall));
                    }
                }
            }

            var model = request.Model ?? defaultDeploymentId;
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidOperationException(
                    "OpenAI Responses requests require a model deployment ID.");
            }

            var reasoning = MapReasoning(model, request.ReasoningEffort);
            var (temperature, topP) = ResolveOpenAiSampling(request);
            var responseRequest = new JsonObject
            {
                ["input"] = inputItems,
                ["model"] = model,
                ["store"] = false,
                ["truncation"] = "disabled"
            };

            if (temperature.HasValue)
            {
                responseRequest["temperature"] = temperature.Value;
            }

            if (topP.HasValue)
            {
                responseRequest["top_p"] = topP.Value;
            }

            if (reasoning != null)
            {
                responseRequest["reasoning"] = new JsonObject
                {
                    ["effort"] = reasoning.Effort.ToString().ToLowerInvariant(),
                    ["summary"] = "detailed"
                };
            }

            if (request.Tools is { Count: > 0 })
            {
                var tools = new JsonArray();
                foreach (var tool in request.Tools)
                {
                    tools.Add(ToJsonTool(tool));
                }

                responseRequest["tools"] = tools;
            }

            return responseRequest;
        }

        private static (double? Temperature, double? TopP) ResolveOpenAiSampling(ChatCompletionRequest request)
        {
            double? temperature = null;
            double? topP = null;
            if (request.SamplingParameters == null)
            {
                return (temperature, topP);
            }

            List<string>? unprojectedKeys = null;
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

                unprojectedKeys ??= [];
                unprojectedKeys.Add(key);
            }

            if (unprojectedKeys is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Unable to project sampling parameter(s) [{string.Join(", ", unprojectedKeys)}] " +
                    "to OpenAI Responses request fields.");
            }

            return (temperature, topP);
        }

        internal static ChatCompletionResponse FromResponse(Response response)
        {
            return FromResponseCore(response, NullLogger<OpenAiResponsesClient>.Instance);
        }

        internal static ChatCompletionResponse FromResponseWithLogger(Response response, ILogger logger)
        {
            return FromResponseCore(response, logger ?? NullLogger<OpenAiResponsesClient>.Instance);
        }

        internal static ChatCompletionResponse FromTerminalResponse(JsonElement response, ILogger logger)
        {
            if (response.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Responses API response.completed payload must contain a JSON object.");
            }

            var content = new List<ChatContent>();
            var toolCalls = new List<ChatToolCall>();
            var reasoningSummaries = new List<string>();

            if (!response.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Responses API response.completed payload did not contain an output array.");
            }

            foreach (var item in output.EnumerateArray())
            {
                var type = ReadOptionalString(item, "type");
                switch (type)
                {
                    case "message" when string.Equals(
                        ReadOptionalString(item, "role"),
                        "assistant",
                        StringComparison.Ordinal):
                        AppendJsonMessageContent(item, content, reasoningSummaries, logger);
                        break;
                    case "function_call":
                        toolCalls.Add(FromJsonToolCall(item));
                        break;
                    case "reasoning":
                        AppendJsonReasoningItem(item, reasoningSummaries);
                        break;
                    case "function_call_output":
                        logger.LogWarning("OpenAI Responses returned tool outputs in the output list.");
                        break;
                    default:
                        logger.LogWarning("Unsupported OpenAI response item type: {ResponseItemType}", type);
                        break;
                }
            }

            var thinkingBlocks = BuildThinkingBlocks(reasoningSummaries);
            var assistantMessage = BuildAssistantMessage(content, toolCalls, thinkingBlocks);
            var finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";

            return new ChatCompletionResponse(
                [new ChatChoice(assistantMessage, finishReason)],
                MapJsonUsage(response));
        }

        private static ChatCompletionResponse FromResponseCore(Response response, ILogger logger)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var content = new List<ChatContent>();
            var toolCalls = new List<ChatToolCall>();
            var reasoningSummaries = new List<string>();

            foreach (var item in response.Output)
            {
                switch (item)
                {
                    case Message message when message.Role == Role.Assistant:
                        AppendMessageContent(message.Content, content, reasoningSummaries, logger);
                        break;
                    case FunctionToolCall toolCall:
                        toolCalls.Add(FromToolCall(toolCall));
                        break;
                    case ReasoningItem reasoningItem:
                        AppendReasoningItem(reasoningItem, reasoningSummaries);
                        break;
                    case FunctionToolCallOutput:
                        logger.LogWarning("OpenAI Responses returned tool outputs in the output list.");
                        break;
                    default:
                        logger.LogWarning("Unsupported OpenAI response item: {ResponseItemType}", item.GetType().Name);
                        break;
                }
            }

            var thinkingBlocks = BuildThinkingBlocks(reasoningSummaries);
            var assistantMessage = BuildAssistantMessage(content, toolCalls, thinkingBlocks);
            var finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";

            return new ChatCompletionResponse(
                [new ChatChoice(assistantMessage, finishReason)],
                MapUsage(response.Usage));
        }

        private static void AppendJsonMessageContent(
            JsonElement message,
            List<ChatContent> output,
            List<string> reasoningSummaries,
            ILogger logger)
        {
            if (!message.TryGetProperty("content", out var contents) ||
                contents.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var content in contents.EnumerateArray())
            {
                var type = ReadOptionalString(content, "type");
                switch (type)
                {
                    case "output_text":
                        AppendTextContent(output, ReadOptionalString(content, "text"), null);
                        break;
                    case "refusal":
                        AppendTextContent(output, ReadOptionalString(content, "refusal"), null);
                        break;
                    case "reasoning_text":
                        AppendReasoningText(reasoningSummaries, ReadOptionalString(content, "text"));
                        break;
                    case "output_image":
                        AppendJsonImageContent(content, output, logger);
                        break;
                    default:
                        logger.LogWarning(
                            "Unsupported OpenAI response content type: {ResponseContentType}",
                            type);
                        break;
                }
            }
        }

        private static void AppendJsonImageContent(
            JsonElement content,
            List<ChatContent> output,
            ILogger logger)
        {
            var imageUrl = ReadOptionalString(content, "image_url");
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                output.Add(new ChatContent(new ChatImageUrl(imageUrl)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(ReadOptionalString(content, "file_id")))
            {
                logger.LogWarning("OpenAI Responses returned image content with FileId only.");
            }
        }

        private static ChatToolCall FromJsonToolCall(JsonElement item)
        {
            var id = ReadOptionalString(item, "call_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = ReadOptionalString(item, "id");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    "Responses API function_call output did not include call_id or id.");
            }

            var name = ReadOptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "Responses API function_call output did not include a function name.");
            }

            if (!item.TryGetProperty("arguments", out var arguments))
            {
                throw new InvalidOperationException(
                    "Responses API function_call output did not include arguments.");
            }

            var parsedArguments = ParseJsonToolArguments(arguments);
            return new ChatToolCall
            {
                Id = id,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = name,
                    Arguments = parsedArguments
                }
            };
        }

        private static JsonElement ParseJsonToolArguments(JsonElement arguments)
        {
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                return arguments.Clone();
            }

            if (arguments.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "Responses API function_call arguments must be a JSON string or object.");
            }

            var argumentsJson = arguments.GetString();
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                throw new InvalidOperationException(
                    "Responses API function_call arguments were empty.");
            }

            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Responses API function_call arguments must contain a JSON object.");
            }

            return document.RootElement.Clone();
        }

        private static void AppendJsonReasoningItem(
            JsonElement item,
            List<string> reasoningSummaries)
        {
            if (AppendJsonTextArray(item, "summary", reasoningSummaries))
            {
                return;
            }

            AppendJsonTextArray(item, "content", reasoningSummaries);
        }

        private static bool AppendJsonTextArray(
            JsonElement item,
            string propertyName,
            List<string> output)
        {
            if (!item.TryGetProperty(propertyName, out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var value in values.EnumerateArray())
            {
                AppendReasoningText(output, ReadOptionalString(value, "text"));
            }

            return values.GetArrayLength() > 0;
        }

        private static void AppendReasoningText(List<string> output, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }
        }

        private static ChatCompletionUsage? MapJsonUsage(JsonElement response)
        {
            if (!response.TryGetProperty("usage", out var usage) ||
                usage.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (usage.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Responses API usage value must be an object or null.");
            }

            return new ChatCompletionUsage
            {
                PromptTokens = ReadOptionalInt32(usage, "input_tokens"),
                CompletionTokens = ReadOptionalInt32(usage, "output_tokens"),
                TotalTokens = ReadOptionalInt32(usage, "total_tokens"),
                PromptTokensDetails = MapJsonInputTokenDetails(usage)
            };
        }

        private static ChatPromptTokensDetails? MapJsonInputTokenDetails(JsonElement usage)
        {
            JsonElement details;
            if (!usage.TryGetProperty("input_tokens_details", out details) &&
                !usage.TryGetProperty("input_token_details", out details))
            {
                return null;
            }

            if (details.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ChatPromptTokensDetails
            {
                CachedTokens = ReadOptionalInt32(details, "cached_tokens")
            };
        }

        private static int ReadOptionalInt32(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
                ? value
                : 0;

        private static string? ReadOptionalString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static JsonObject ToJsonMessage(ChatMessage message)
        {
            var contentType = message.Role == ChatRole.Assistant ? "output_text" : "input_text";
            var contents = new JsonArray();
            foreach (var content in message.Content)
            {
                if (content.ImageUrl != null)
                {
                    contents.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = content.ImageUrl.Url,
                        ["detail"] = "auto"
                    });
                    continue;
                }

                contents.Add(new JsonObject
                {
                    ["type"] = contentType,
                    ["text"] = content.Text ?? string.Empty
                });
            }

            return new JsonObject
            {
                ["role"] = ToJsonRole(message.Role),
                ["content"] = contents
            };
        }

        private static JsonObject ToJsonToolCall(ChatToolCall toolCall)
        {
            if (string.IsNullOrWhiteSpace(toolCall.Id))
            {
                throw new InvalidOperationException("Assistant tool calls must include an ID.");
            }

            if (string.IsNullOrWhiteSpace(toolCall.Function.Name))
            {
                throw new InvalidOperationException("Assistant tool calls must include a function name.");
            }

            var arguments = toolCall.Function.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : toolCall.Function.Arguments.GetRawText();

            return new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = toolCall.Id,
                ["name"] = toolCall.Function.Name,
                ["arguments"] = arguments
            };
        }

        private static JsonObject ToJsonToolCallOutput(ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new InvalidOperationException("Tool response messages must include ToolCallId.");
            }

            return new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = message.ToolCallId,
                ["output"] = message.GetText()
            };
        }

        private static JsonObject ToJsonTool(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("Function tool definition is required.");
            }

            var function = tool.Function;
            var json = new JsonObject
            {
                ["type"] = "function",
                ["name"] = function.Name
            };
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                json["description"] = function.Description;
            }

            if (function.Parameters != null)
            {
                json["parameters"] = JsonNode.Parse(function.Parameters.ToJsonString());
            }

            return json;
        }

        private static void AppendMessageContent(
            IReadOnlyList<IResponseContent> contents,
            List<ChatContent> output,
            List<string> reasoningSummaries,
            ILogger logger)
        {
            foreach (var content in contents)
            {
                switch (content)
                {
                    case ResponseTextContent textContent:
                        AppendTextContent(output, textContent.Text, textContent.Delta);
                        break;
                    case ResponseImageContent imageContent:
                        if (!string.IsNullOrWhiteSpace(imageContent.ImageUrl))
                        {
                            output.Add(new ChatContent(new ChatImageUrl(imageContent.ImageUrl)));
                        }
                        else if (!string.IsNullOrWhiteSpace(imageContent.FileId))
                        {
                            logger.LogWarning("OpenAI Responses returned image content with FileId only.");
                        }
                        break;
                    case ResponseRefusalContent refusalContent:
                        AppendTextContent(output, refusalContent.Refusal, refusalContent.Delta);
                        break;
                    case ResponseReasoningContent reasoningContent:
                        var reasoningText = !string.IsNullOrWhiteSpace(reasoningContent.Text)
                            ? reasoningContent.Text
                            : reasoningContent.Delta;
                        if (!string.IsNullOrWhiteSpace(reasoningText))
                        {
                            reasoningSummaries.Add(reasoningText);
                        }
                        break;
                    default:
                        logger.LogWarning(
                            "Unsupported OpenAI response content: {ResponseContentType}",
                            content.GetType().Name);
                        break;
                }
            }
        }

        private static void AppendReasoningItem(ReasoningItem reasoningItem, List<string> reasoningSummaries)
        {
            if (reasoningItem == null)
            {
                return;
            }

            if (reasoningItem.Summary is { Count: > 0 })
            {
                foreach (var summary in reasoningItem.Summary)
                {
                    var text = summary?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        reasoningSummaries.Add(text);
                    }
                }

                return;
            }

            if (reasoningItem.Content is { Count: > 0 })
            {
                foreach (var content in reasoningItem.Content)
                {
                    var text = content?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        reasoningSummaries.Add(text);
                    }
                }
            }
        }

        private static void AppendTextContent(List<ChatContent> output, string? text, string? delta)
        {
            var value = !string.IsNullOrWhiteSpace(text) ? text : delta;
            if (!string.IsNullOrWhiteSpace(value))
            {
                output.Add(new ChatContent(value));
            }
        }

        private static IReadOnlyList<ChatThinkingBlock>? BuildThinkingBlocks(List<string> reasoningSummaries)
        {
            if (reasoningSummaries.Count == 0)
            {
                return null;
            }

            var summaryText = string.Join("\n", reasoningSummaries.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                return null;
            }

            return [ChatThinkingBlock.ForThinking(summaryText, string.Empty)];
        }

        private static ChatMessage BuildAssistantMessage(
            IReadOnlyList<ChatContent> content,
            IReadOnlyList<ChatToolCall> toolCalls,
            IReadOnlyList<ChatThinkingBlock>? thinkingBlocks)
        {
            if (toolCalls.Count > 0 || thinkingBlocks != null)
            {
                return new ChatMessage(ChatRole.Assistant, content, toolCalls, thinkingBlocks);
            }

            return new ChatMessage(ChatRole.Assistant, content);
        }

        private static ChatToolCall FromToolCall(FunctionToolCall toolCall)
        {
            var argumentsNode = toolCall.Arguments;
            var argumentsJson = argumentsNode?.ToJsonString() ?? "{}";

            using var doc = JsonDocument.Parse(argumentsJson);
            var argumentsElement = doc.RootElement.Clone();

            return new ChatToolCall
            {
                Id = !string.IsNullOrWhiteSpace(toolCall.CallId) ? toolCall.CallId : toolCall.Id,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = toolCall.Name ?? string.Empty,
                    Arguments = argumentsElement
                }
            };
        }

        private static ChatCompletionUsage? MapUsage(TokenUsage? usage)
        {
            if (usage == null)
            {
                return null;
            }

            return new ChatCompletionUsage
            {
                PromptTokens = usage.InputTokens,
                CompletionTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens,
                PromptTokensDetails = usage.InputTokenDetails != null
                    ? new ChatPromptTokensDetails { CachedTokens = usage.InputTokenDetails.CachedTokens }
                    : null
            };
        }

        private static Reasoning? MapReasoning(string? model, string? effort)
        {
            var mapped = OpenAiReasoningSupport.MapReasoningEffort(model, effort);
            if (mapped == null)
            {
                return null;
            }

            return new Reasoning(
                mapped.Value,
                global::OpenAI.ReasoningSummary.Detailed);
        }

        private static string ToJsonRole(ChatRole role) => role switch
        {
            ChatRole.System => "system",
            ChatRole.Developer => "developer",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            ChatRole.Tool => "tool",
            _ => throw new InvalidOperationException($"Unsupported chat role '{role}'.")
        };
    }
}
