using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.Anthropic;

public sealed class AnthropicChatClient : IChatCompletionClient
{
    private readonly IAnthropicClient _client;
    private readonly string? _defaultModel;
    private readonly int _defaultMaxTokens;
    private readonly AnthropicThinkingBudgets _thinkingBudgets;
    private readonly ILogger<AnthropicChatClient> _logger;

    public AnthropicChatClient(
        IAnthropicClient client,
        string? defaultModel,
        int defaultMaxTokens,
        AnthropicThinkingBudgets thinkingBudgets,
        ILogger<AnthropicChatClient>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultModel = defaultModel;
        _defaultMaxTokens = defaultMaxTokens;
        _thinkingBudgets = thinkingBudgets ?? new AnthropicThinkingBudgets();
        _logger = logger ?? NullLogger<AnthropicChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        LogRequestSummary(request, stream: false);
        var messageParams = AnthropicMapper.ToMessageCreateParams(
            request,
            _defaultModel,
            _defaultMaxTokens,
            _thinkingBudgets);

        var response = await _client.Messages.Create(messageParams, cancellationToken);
        return AnthropicMapper.FromMessage(response);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        LogRequestSummary(request, stream: true);
        var messageParams = AnthropicMapper.ToMessageCreateParams(
            request,
            _defaultModel,
            _defaultMaxTokens,
            _thinkingBudgets);

        var stream = _client.Messages.CreateStreaming(messageParams, cancellationToken);
        var accumulator = new AnthropicStreamingAccumulator();

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
        {
            accumulator.HandleEvent(streamEvent, onChunk);
        }

        return accumulator.ToResponse();
    }

    private void LogRequestSummary(ChatCompletionRequest request, bool stream)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var sampling = request.SamplingParameters != null
                ? string.Join(", ", request.SamplingParameters.Select(kv => $"{kv.Key}={kv.Value}"))
                : null;

            _logger.LogInformation(
                "Anthropic request. Model={Model}, Stream={Stream}, Messages={MessageCount}, Tools={ToolCount}, "
                + "ReasoningEffort={ReasoningEffort}, SamplingOverrides={SamplingOverrides}",
                request.Model ?? _defaultModel, stream, request.Messages.Count, request.Tools?.Count ?? 0,
                request.ReasoningEffort, sampling);
        }
    }

    private static class AnthropicMapper
    {
        public static MessageCreateParams ToMessageCreateParams(
            ChatCompletionRequest request,
            string? defaultModel,
            int defaultMaxTokens,
            AnthropicThinkingBudgets thinkingBudgets)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var model = request.Model ?? defaultModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidOperationException("Anthropic requires a model identifier.");
            }

            var thinkingEnabled = !string.IsNullOrWhiteSpace(request.ReasoningEffort);
            var messageParams = MapMessages(request.Messages, thinkingEnabled, out var systemMessages);

            var toolDefinitions = request.Tools?.Count > 0
                ? request.Tools.Select(MapToolDefinition).ToList()
                : null;
            var (temperature, topP) = ResolveAnthropicSampling(request, thinkingEnabled);

            var messageRequest = new MessageCreateParams
            {
                Model = model,
                MaxTokens = defaultMaxTokens,
                Messages = messageParams,
                Temperature = temperature,
                TopP = topP,
                System = systemMessages.Count > 0 ? new MessageCreateParamsSystem(systemMessages) : null,
                Tools = toolDefinitions
            };

            if (thinkingEnabled)
            {
                var budget = thinkingBudgets.ForEffort(request.ReasoningEffort);
                if (!budget.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Thinking budget not configured for {request.ReasoningEffort}.");
                }

                if (budget.Value < 1024)
                {
                    throw new InvalidOperationException(
                        "Thinking budget must be at least 1024 tokens.");
                }

                if (budget.Value >= defaultMaxTokens)
                {
                    throw new InvalidOperationException(
                        "Thinking budget must be less than max tokens.");
                }

                messageRequest = messageRequest with
                {
                    Thinking = new ThinkingConfigEnabled(budget.Value)
                };
            }

            return messageRequest;
        }

        private static (double? Temperature, double? TopP) ResolveAnthropicSampling(ChatCompletionRequest request, bool thinkingEnabled)
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
                    if (thinkingEnabled)
                    {
                        // Anthropic extended thinking rejects non-1 temperature values.
                        // Preserve historical behavior by omitting temperature when thinking is enabled.
                        continue;
                    }

                    temperature = value;
                    continue;
                }

                if (string.Equals(key, "top_p", StringComparison.Ordinal))
                {
                    if (thinkingEnabled)
                    {
                        // Anthropic extended thinking constrains top_p (>= 0.95 or unset).
                        // Preserve historical behavior by omitting top_p when thinking is enabled.
                        continue;
                    }

                    topP = value;
                    continue;
                }

                unprojectedKeys ??= [];
                unprojectedKeys.Add(key);
            }

            if (unprojectedKeys is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Unable to project sampling parameter(s) [{string.Join(", ", unprojectedKeys)}] to Anthropic request fields.");
            }

            return (temperature, topP);
        }

        public static ChatCompletionResponse FromMessage(Message message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var content = new List<ChatContent>();
            var toolCalls = new List<ChatToolCall>();
            var thinkingBlocks = new List<ChatThinkingBlock>();

            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var textBlock))
                {
                    if (!string.IsNullOrEmpty(textBlock.Text))
                    {
                        content.Add(new ChatContent(textBlock.Text));
                    }

                    continue;
                }

                if (block.TryPickToolUse(out var toolUse))
                {
                    toolCalls.Add(MapToolUse(toolUse));
                    continue;
                }

                if (block.TryPickThinking(out var thinkingBlock))
                {
                    thinkingBlocks.Add(ChatThinkingBlock.ForThinking(thinkingBlock.Thinking, thinkingBlock.Signature));
                    continue;
                }

                if (block.TryPickRedactedThinking(out var redactedThinking))
                {
                    thinkingBlocks.Add(ChatThinkingBlock.ForRedacted(redactedThinking.Data));
                    continue;
                }

                if (block.TryPickServerToolUse(out _) || block.TryPickWebSearchToolResult(out _))
                {
                    throw new InvalidOperationException("Server tool blocks are not supported.");
                }

                throw new InvalidOperationException("Unsupported content block received from Anthropic.");
            }

            var role = MapMessageRole(message.Role);

            ChatMessage chatMessage = toolCalls.Count > 0
                ? new ChatMessage(role, content, toolCalls, thinkingBlocks)
                : new ChatMessage(role, content, null, thinkingBlocks);

            var finishReason = MapStopReason(message.StopReason);
            var usage = MapUsage(message.Usage);

            return new ChatCompletionResponse(
                [new ChatChoice(chatMessage, finishReason)],
                usage);
        }

        private static List<MessageParam> MapMessages(
            IReadOnlyList<ChatMessage> messages,
            bool thinkingEnabled,
            out List<TextBlockParam> systemMessages)
        {
            systemMessages = [];
            var mapped = new List<MessageParam>();

            foreach (var message in messages)
            {
                if (message.Role is ChatRole.System or ChatRole.Developer)
                {
                    AppendSystemMessage(systemMessages, message);
                    continue;
                }

                if (message.Role == ChatRole.Tool)
                {
                    mapped.Add(MapToolResultMessage(message));
                    continue;
                }

                mapped.Add(MapUserOrAssistantMessage(message, thinkingEnabled));
            }

            if (mapped.Count == 0)
            {
                throw new InvalidOperationException("Anthropic requires at least one message.");
            }

            return mapped;
        }

        private static void AppendSystemMessage(List<TextBlockParam> systemMessages, ChatMessage message)
        {
            foreach (var content in message.Content)
            {
                if (content.IsText)
                {
                    systemMessages.Add(new TextBlockParam(content.Text ?? string.Empty));
                    continue;
                }

                if (content.IsImage && content.ImageUrl != null)
                {
                    systemMessages.Add(new TextBlockParam(content.ImageUrl.Url));
                }
            }
        }

        private static MessageParam MapUserOrAssistantMessage(ChatMessage message, bool thinkingEnabled)
        {
            var contentBlocks = new List<ContentBlockParam>();

            if (message.Role == ChatRole.Assistant && thinkingEnabled)
            {
                AppendThinkingBlocks(contentBlocks, message.ThinkingBlocks);
            }

            foreach (var content in message.Content)
            {
                contentBlocks.Add(MapContentBlock(content));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    contentBlocks.Add(MapToolUseBlock(toolCall));
                }
            }

            var role = message.Role switch
            {
                ChatRole.User => Role.User,
                ChatRole.Assistant => Role.Assistant,
                _ => throw new InvalidOperationException($"Unsupported role {message.Role}.")
            };

            return new MessageParam
            {
                Role = role,
                Content = new MessageParamContent(contentBlocks)
            };
        }

        private static void AppendThinkingBlocks(
            List<ContentBlockParam> contentBlocks,
            IReadOnlyList<ChatThinkingBlock>? thinkingBlocks)
        {
            if (thinkingBlocks is not { Count: > 0 })
            {
                return;
            }

            foreach (var block in thinkingBlocks)
            {
                contentBlocks.Add(MapThinkingBlock(block));
            }
        }

        private static ContentBlockParam MapThinkingBlock(ChatThinkingBlock block)
        {
            if (block.IsThinking)
            {
                if (string.IsNullOrWhiteSpace(block.Thinking) || string.IsNullOrWhiteSpace(block.Signature))
                {
                    throw new InvalidOperationException("Thinking block requires thinking text and signature.");
                }

                return new ContentBlockParam(new ThinkingBlockParam
                {
                    Thinking = block.Thinking,
                    Signature = block.Signature
                });
            }

            if (block.IsRedactedThinking)
            {
                var data = string.IsNullOrWhiteSpace(block.Data) ? "redacted" : block.Data;
                return new ContentBlockParam(new RedactedThinkingBlockParam(data));
            }

            throw new InvalidOperationException($"Unsupported thinking block type '{block.Type}'.");
        }

        private static MessageParam MapToolResultMessage(ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new InvalidOperationException("Tool message requires a tool_call_id.");
            }

            var resultBlock = new ToolResultBlockParam(message.ToolCallId)
            {
                Content = MapToolResultContent(message.Content)
            };

            return new MessageParam
            {
                Role = Role.User,
                Content = new MessageParamContent([new ContentBlockParam(resultBlock)])
            };
        }

        private static ToolResultBlockParamContent? MapToolResultContent(IReadOnlyList<ChatContent> contents)
        {
            if (contents.Count == 0)
            {
                return null;
            }

            var blocks = new List<Block>();

            foreach (var content in contents)
            {
                if (content.IsText)
                {
                    blocks.Add(new Block(new TextBlockParam(content.Text ?? string.Empty)));
                    continue;
                }

                if (content.IsImage && content.ImageUrl != null)
                {
                    var imageBlock = MapImageBlock(content.ImageUrl);
                    blocks.Add(new Block(imageBlock));
                }
            }

            return blocks.Count > 0 ? new ToolResultBlockParamContent(blocks) : null;
        }

        private static ContentBlockParam MapContentBlock(ChatContent content)
        {
            if (content.IsText)
            {
                return new ContentBlockParam(new TextBlockParam(content.Text ?? string.Empty));
            }

            if (content.IsImage && content.ImageUrl != null)
            {
                var image = MapImageBlock(content.ImageUrl);
                return new ContentBlockParam(image);
            }

            throw new InvalidOperationException("Unsupported content type in message.");
        }

        private static ImageBlockParam MapImageBlock(ChatImageUrl imageUrl)
        {
            if (imageUrl == null || string.IsNullOrWhiteSpace(imageUrl.Url))
            {
                throw new InvalidOperationException("Image URL is required for Anthropic image blocks.");
            }

            if (TryParseDataUrl(imageUrl.Url, out var base64Source))
            {
                return new ImageBlockParam(base64Source);
            }

            return new ImageBlockParam(new UrlImageSource(imageUrl.Url));
        }

        private static bool TryParseDataUrl(string url, out Base64ImageSource base64Source)
        {
            base64Source = null!;

            if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var commaIndex = url.IndexOf(',');
            if (commaIndex <= "data:".Length)
            {
                throw new InvalidOperationException("Invalid data URL for image attachment.");
            }

            var metadata = url.Substring("data:".Length, commaIndex - "data:".Length);
            var data = url[(commaIndex + 1)..];

            if (string.IsNullOrWhiteSpace(metadata) || string.IsNullOrWhiteSpace(data))
            {
                throw new InvalidOperationException("Invalid data URL for image attachment.");
            }

            var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mediaType = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
            var isBase64 = false;
            foreach (var part in parts)
            {
                if (string.Equals(part, "base64", StringComparison.OrdinalIgnoreCase))
                {
                    isBase64 = true;
                    break;
                }
            }

            if (!isBase64)
            {
                throw new InvalidOperationException("Anthropic image attachments require base64 data URLs.");
            }

            if (!TryMapMediaType(mediaType, out var mappedMediaType))
            {
                throw new InvalidOperationException(
                    $"Unsupported image media type '{mediaType}' for Anthropic image attachments.");
            }

            base64Source = new Base64ImageSource
            {
                Data = data,
                MediaType = mappedMediaType
            };

            return true;
        }

        private static bool TryMapMediaType(string mediaType, out MediaType mapped)
        {
            switch (mediaType)
            {
                case "image/jpeg":
                case "image/jpg":
                    mapped = MediaType.ImageJpeg;
                    return true;
                case "image/png":
                    mapped = MediaType.ImagePng;
                    return true;
                case "image/gif":
                    mapped = MediaType.ImageGif;
                    return true;
                case "image/webp":
                    mapped = MediaType.ImageWebP;
                    return true;
                default:
                    mapped = default;
                    return false;
            }
        }

        private static ContentBlockParam MapToolUseBlock(ChatToolCall toolCall)
        {
            if (string.IsNullOrWhiteSpace(toolCall.Id))
            {
                throw new InvalidOperationException("Tool call id is required.");
            }

            if (string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                throw new InvalidOperationException("Tool call function name is required.");
            }

            var input = BuildToolInput(toolCall.Function.Arguments);
            var toolUse = new ToolUseBlockParam
            {
                ID = toolCall.Id,
                Name = toolCall.Function.Name,
                Input = input
            };
            return new ContentBlockParam(toolUse);
        }

        private static ToolUnion MapToolDefinition(ChatToolDefinition definition)
        {
            if (definition.Type != "function" || definition.Function == null)
            {
                throw new InvalidOperationException("Only function tools are supported.");
            }

            var function = definition.Function;
            if (string.IsNullOrWhiteSpace(function.Name))
            {
                throw new InvalidOperationException("Tool definition name is required.");
            }

            return new Tool
            {
                Name = function.Name,
                Description = function.Description,
                InputSchema = BuildInputSchema(function.Parameters)
            };
        }

        private static InputSchema BuildInputSchema(JsonNode? parameters)
        {
            if (parameters == null)
            {
                throw new InvalidOperationException("Tool parameters are required.");
            }

            using var document = JsonDocument.Parse(parameters.ToJsonString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Tool parameters must be a JSON object schema.");
            }

            if (root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && !string.Equals(typeElement.GetString(), "object", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Tool parameters schema must have type \"object\".");
            }

            Dictionary<string, JsonElement>? properties = null;
            if (root.TryGetProperty("properties", out var propertiesElement)
                && propertiesElement.ValueKind == JsonValueKind.Object)
            {
                properties = [];
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    properties[property.Name] = JsonSerializer.SerializeToElement(property.Value);
                }
            }

            List<string>? required = null;
            if (root.TryGetProperty("required", out var requiredElement)
                && requiredElement.ValueKind == JsonValueKind.Array)
            {
                required = [];
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            required.Add(name);
                        }
                    }
                }
            }

            return new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),
                Properties = properties,
                Required = required
            };
        }

        private static IReadOnlyDictionary<string, JsonElement> BuildToolInput(JsonElement arguments)
        {
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                return ToElementDictionary(arguments);
            }

            if (arguments.ValueKind == JsonValueKind.String)
            {
                var json = arguments.GetString();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<string, JsonElement>();
                }

                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Tool arguments must be a JSON object.");
                }

                return ToElementDictionary(document.RootElement);
            }

            if (arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new Dictionary<string, JsonElement>();
            }

            throw new InvalidOperationException("Tool arguments must be a JSON object.");
        }

        private static Dictionary<string, JsonElement> ToElementDictionary(JsonElement element)
        {
            var dictionary = new Dictionary<string, JsonElement>();
            foreach (var property in element.EnumerateObject())
            {
                dictionary[property.Name] = JsonSerializer.SerializeToElement(property.Value);
            }

            return dictionary;
        }

        private static ChatToolCall MapToolUse(ToolUseBlock toolUse)
        {
            var arguments = JsonSerializer.SerializeToElement(toolUse.Input);
            return new ChatToolCall
            {
                Id = toolUse.ID,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = toolUse.Name,
                    Arguments = arguments
                }
            };
        }

        internal static string MapStopReason(ApiEnum<string, StopReason>? stopReason)
        {
            if (stopReason == null)
            {
                throw new InvalidOperationException("Missing Anthropic stop reason.");
            }

            var reason = stopReason.Value();
            if (!Enum.IsDefined(reason))
            {
                throw new InvalidOperationException("Unknown Anthropic stop reason.");
            }

            return reason switch
            {
                StopReason.ToolUse => "tool_calls",
                StopReason.MaxTokens => "length",
                StopReason.EndTurn => "stop",
                StopReason.StopSequence => "stop",
                StopReason.PauseTurn => "stop",
                StopReason.Refusal => "stop",
                _ => throw new InvalidOperationException("Unhandled Anthropic stop reason.")
            };
        }

        internal static ChatCompletionUsage? MapUsage(Usage? usage)
        {
            if (usage == null)
            {
                return null;
            }

            var cacheRead = usage.CacheReadInputTokens ?? 0;
            var cacheCreate = usage.CacheCreationInputTokens ?? 0;
            var inputTokens = usage.InputTokens + cacheRead + cacheCreate;
            var outputTokens = usage.OutputTokens;
            var totalTokens = inputTokens + outputTokens;

            return new ChatCompletionUsage
            {
                PromptTokens = (int)inputTokens,
                CompletionTokens = (int)outputTokens,
                TotalTokens = (int)totalTokens,
                PromptTokensDetails = new ChatPromptTokensDetails
                {
                    CachedTokens = (int)cacheRead
                }
            };
        }

        private static ChatRole MapMessageRole(JsonElement roleElement)
        {
            if (roleElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Unexpected Anthropic message role shape.");
            }

            var role = roleElement.GetString();
            return role?.ToLowerInvariant() switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => throw new InvalidOperationException("Unexpected Anthropic message role.")
            };
        }
    }

    private sealed class AnthropicStreamingAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly Dictionary<long, ToolCallState> _toolCalls = [];
        private readonly Dictionary<long, ThinkingBlockState> _thinkingStates = [];
        private readonly List<ChatThinkingBlock> _thinkingBlocks = [];
        private MessageDeltaUsage? _deltaUsage;
        private Usage? _startUsage;
        private ApiEnum<string, StopReason>? _stopReason;

        public void HandleEvent(RawMessageStreamEvent streamEvent, Action<ChatCompletionChunk> onChunk)
        {
            if (streamEvent.TryPickStart(out var startEvent))
            {
                _startUsage = startEvent.Message.Usage;
                return;
            }

            if (streamEvent.TryPickDelta(out var messageDelta))
            {
                _deltaUsage = messageDelta.Usage;
                _stopReason = messageDelta.Delta.StopReason;
                return;
            }

            if (streamEvent.TryPickContentBlockStart(out var contentStart))
            {
                HandleContentBlockStart(contentStart, onChunk);
                return;
            }

            if (streamEvent.TryPickContentBlockDelta(out var contentDelta))
            {
                HandleContentBlockDelta(contentDelta, onChunk);
                return;
            }

            if (streamEvent.TryPickContentBlockStop(out var contentStop))
            {
                HandleContentBlockStop(contentStop, onChunk);
            }
        }

        public ChatCompletionResponse ToResponse()
        {
            var content = new List<ChatContent>();
            if (_content.Length > 0)
            {
                content.Add(new ChatContent(_content.ToString()));
            }

            var toolCalls = _toolCalls
                .OrderBy(entry => entry.Key)
                .Select(entry => entry.Value.ToToolCall())
                .ToList();

            var message = toolCalls.Count > 0
                ? new ChatMessage(ChatRole.Assistant, content, toolCalls, _thinkingBlocks)
                : new ChatMessage(ChatRole.Assistant, content, null, _thinkingBlocks);

            var finishReason = AnthropicMapper.MapStopReason(_stopReason);
            var usage = MapUsage(_deltaUsage, _startUsage);

            return new ChatCompletionResponse(
                [new ChatChoice(message, finishReason)],
                usage);
        }

        private void HandleContentBlockStart(
            RawContentBlockStartEvent contentStart,
            Action<ChatCompletionChunk> onChunk)
        {
            var index = contentStart.Index;

            if (contentStart.ContentBlock.TryPickText(out var textBlock))
            {
                if (!string.IsNullOrEmpty(textBlock.Text))
                {
                    _content.Append(textBlock.Text);
                }

                return;
            }

            if (contentStart.ContentBlock.TryPickToolUse(out var toolUse))
            {
                _toolCalls[index] = new ToolCallState(toolUse.ID, toolUse.Name, toolUse.Input);
                return;
            }

            if (contentStart.ContentBlock.TryPickThinking(out var thinkingBlock))
            {
                _thinkingStates[index] = new ThinkingBlockState(
                    thinkingBlock.Thinking,
                    thinkingBlock.Signature);
                if (!string.IsNullOrWhiteSpace(thinkingBlock.Thinking))
                {
                    EmitTextDelta(thinkingBlock.Thinking, onChunk);
                }
                return;
            }

            if (contentStart.ContentBlock.TryPickRedactedThinking(out var redactedThinking))
            {
                _thinkingStates[index] = ThinkingBlockState.ForRedacted(redactedThinking.Data);
                return;
            }

            if (contentStart.ContentBlock.TryPickServerToolUse(out _)
                || contentStart.ContentBlock.TryPickWebSearchToolResult(out _))
            {
                throw new InvalidOperationException("Server tool blocks are not supported.");
            }

            throw new InvalidOperationException("Unsupported content block start received.");
        }

        private void HandleContentBlockDelta(
            RawContentBlockDeltaEvent contentDelta,
            Action<ChatCompletionChunk> onChunk)
        {
            if (contentDelta.Delta.TryPickText(out var textDelta))
            {
                if (!string.IsNullOrEmpty(textDelta.Text))
                {
                    _content.Append(textDelta.Text);
                    EmitTextDelta(textDelta.Text, onChunk);
                }

                return;
            }

            if (contentDelta.Delta.TryPickInputJson(out var inputJson))
            {
                if (_toolCalls.TryGetValue(contentDelta.Index, out var toolCall))
                {
                    toolCall.AppendInputJson(inputJson.PartialJson);
                    return;
                }

                throw new InvalidOperationException("Tool input delta received without tool state.");
            }

            if (contentDelta.Delta.TryPickThinking(out var thinkingDelta))
            {
                if (_thinkingStates.TryGetValue(contentDelta.Index, out var thinkingState))
                {
                    thinkingState.AppendThinking(thinkingDelta.Thinking);
                }
                if (!string.IsNullOrWhiteSpace(thinkingDelta.Thinking))
                {
                    EmitTextDelta(thinkingDelta.Thinking, onChunk);
                }

                return;
            }

            if (contentDelta.Delta.TryPickSignature(out var signatureDelta))
            {
                if (_thinkingStates.TryGetValue(contentDelta.Index, out var thinkingState))
                {
                    thinkingState.SetSignature(signatureDelta.Signature);
                }
            }
        }

        private void HandleContentBlockStop(
            RawContentBlockStopEvent contentStop,
            Action<ChatCompletionChunk> onChunk)
        {
            if (_thinkingStates.TryGetValue(contentStop.Index, out var state))
            {
                var block = state.ToThinkingBlock();
                if (block != null)
                {
                    _thinkingBlocks.Add(block);
                    // Don't re-emit the full thinking content here - it was already
                    // streamed via EmitTextDelta in HandleContentBlockDelta.
                    // Emit paragraph separator for next content block instead.
                    EmitTextDelta("\n\n", onChunk);
                }

                _thinkingStates.Remove(contentStop.Index);
            }
        }

        private static void EmitTextDelta(string text, Action<ChatCompletionChunk> onChunk)
        {
            var delta = new ChatDelta(ChatRole.Assistant, text);
            onChunk(new ChatCompletionChunk([new ChatChoiceDelta(delta, null)]));
        }

        private static ChatCompletionUsage? MapUsage(MessageDeltaUsage? deltaUsage, Usage? startUsage)
        {
            if (deltaUsage != null)
            {
                var cacheRead = deltaUsage.CacheReadInputTokens ?? 0;
                var cacheCreate = deltaUsage.CacheCreationInputTokens ?? 0;
                var inputTokens = (deltaUsage.InputTokens ?? 0) + cacheRead + cacheCreate;
                var outputTokens = deltaUsage.OutputTokens;
                var totalTokens = inputTokens + outputTokens;

                return new ChatCompletionUsage
                {
                    PromptTokens = (int)inputTokens,
                    CompletionTokens = (int)outputTokens,
                    TotalTokens = (int)totalTokens,
                    PromptTokensDetails = new ChatPromptTokensDetails
                    {
                        CachedTokens = (int)cacheRead
                    }
                };
            }

            return AnthropicMapper.MapUsage(startUsage);
        }

        private sealed class ToolCallState
        {
            private readonly StringBuilder _inputJson = new();
            private readonly IReadOnlyDictionary<string, JsonElement> _startInput;
            private bool _hasInputDeltas;

            public ToolCallState(
                string id,
                string name,
                IReadOnlyDictionary<string, JsonElement> startInput)
            {
                Id = id;
                Name = name;
                _startInput = startInput;
            }

            public string Id { get; }
            public string Name { get; }

            public void AppendInputJson(string partialJson)
            {
                _hasInputDeltas = true;
                _inputJson.Append(partialJson);
            }

            public ChatToolCall ToToolCall()
            {
                var arguments = BuildArguments();
                return new ChatToolCall
                {
                    Id = Id,
                    Type = "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = Name,
                        Arguments = arguments
                    }
                };
            }

            private JsonElement BuildArguments()
            {
                if (_hasInputDeltas)
                {
                    using var document = JsonDocument.Parse(_inputJson.ToString());
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException("Tool input must be a JSON object.");
                    }

                    return JsonSerializer.SerializeToElement(document.RootElement);
                }

                if (_startInput.Count > 0)
                {
                    return JsonSerializer.SerializeToElement(_startInput);
                }

                return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());
            }
        }

        private sealed class ThinkingBlockState
        {
            private readonly StringBuilder _thinking = new();
            private string? _signature;
            private readonly string? _redactedData;

            private ThinkingBlockState(string? redactedData)
            {
                _redactedData = redactedData;
            }

            public ThinkingBlockState(string? thinking, string? signature)
                : this(null)
            {
                if (!string.IsNullOrEmpty(thinking))
                {
                    _thinking.Append(thinking);
                }

                _signature = signature;
            }

            public static ThinkingBlockState ForRedacted(string? data) => new(data);

            public void AppendThinking(string thinking)
            {
                if (!string.IsNullOrEmpty(thinking))
                {
                    _thinking.Append(thinking);
                }
            }

            public void SetSignature(string signature)
            {
                if (!forceNullOrEmpty(signature))
                {
                    _signature = signature;
                }
            }

            public ChatThinkingBlock? ToThinkingBlock()
            {
                if (_redactedData != null)
                {
                    return ChatThinkingBlock.ForRedacted(_redactedData);
                }

                var thinking = _thinking.ToString();
                if (string.IsNullOrWhiteSpace(thinking) || string.IsNullOrWhiteSpace(_signature))
                {
                    return null;
                }

                return ChatThinkingBlock.ForThinking(thinking, _signature);
            }

            private static bool forceNullOrEmpty(string? value) => string.IsNullOrWhiteSpace(value);
        }
    }
}
