using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Chat;

namespace AntRunner.Chat.OpenAI;

public sealed class OpenAiChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions ToolCallJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly OpenAIClient _client;
    private readonly ILogger<OpenAiChatClient> _logger;

    public OpenAiChatClient(OpenAIClient client, ILogger<OpenAiChatClient>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<OpenAiChatClient>.Instance;
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        LogRequestSummary(request, stream: false);
        var chatRequest = OpenAiMapper.ToChatRequest(request);
        var response = await _client.ChatEndpoint.GetCompletionAsync(chatRequest, cancellationToken);
        return OpenAiMapper.FromChatResponse(response);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        LogRequestSummary(request, stream: true);
        var chatRequest = OpenAiMapper.ToChatRequest(request);
        var response = await _client.ChatEndpoint.StreamCompletionAsync(
            chatRequest,
            partialResponse =>
            {
                if (onChunk == null)
                {
                    return;
                }

                var chunk = OpenAiMapper.FromChatResponseDelta(partialResponse);
                if (chunk.Choices.Count > 0)
                {
                    onChunk(chunk);
                }
            },
            streamUsage: true,
            cancellationToken);

        return OpenAiMapper.FromChatResponse(response);
    }

    private void LogRequestSummary(ChatCompletionRequest request, bool stream)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var sampling = request.SamplingParameters != null
                ? string.Join(", ", request.SamplingParameters.Select(kv => $"{kv.Key}={kv.Value}"))
                : null;

            _logger.LogInformation(
                "OpenAI request. Model={Model}, Stream={Stream}, Messages={MessageCount}, Tools={ToolCount}, "
                + "Temperature={Temperature}, TopP={TopP}, ReasoningEffort={ReasoningEffort}, "
                + "SamplingOverrides=[{SamplingOverrides}]",
                request.Model, stream, request.Messages.Count, request.Tools?.Count ?? 0,
                request.Temperature, request.TopP, request.ReasoningEffort, sampling ?? "none");
        }
    }

    private static class OpenAiMapper
    {
        internal static ChatRequest ToChatRequest(ChatCompletionRequest request)
        {
            var messages = request.Messages.Select(ToOpenAiMessage).ToList();
            var tools = request.Tools?.Select(ToOpenAiTool).ToList();
            var reasoningEffort = OpenAiReasoningSupport.MapReasoningEffort(request.Model, request.ReasoningEffort);

            return new ChatRequest(
                messages,
                tools: tools,
                model: request.Model,
                temperature: request.Temperature,
                topP: request.TopP,
                reasoningEffort: reasoningEffort);
        }

        internal static ChatCompletionResponse FromChatResponse(ChatResponse response)
        {
            var choices = response.Choices.Select(choice =>
            {
                var message = FromOpenAiMessage(choice.Message);
                return new ChatChoice(message, choice.FinishReason);
            }).ToList();

            ChatCompletionUsage? usage = null;
            if (response.Usage != null)
            {
                usage = new ChatCompletionUsage
                {
                    PromptTokens = response.Usage.PromptTokens,
                    CompletionTokens = response.Usage.CompletionTokens,
                    TotalTokens = response.Usage.TotalTokens,
                    PromptTokensDetails = response.Usage.PromptTokensDetails != null
                        ? new ChatPromptTokensDetails { CachedTokens = response.Usage.PromptTokensDetails.CachedTokens }
                        : null
                };
            }

            return new ChatCompletionResponse(choices, usage);
        }

        internal static ChatCompletionChunk FromChatResponseDelta(ChatResponse response)
        {
            var delta = response.FirstChoice?.Delta;
            if (delta == null)
            {
                return new ChatCompletionChunk([]);
            }

            ChatRole? role = delta.Role != 0 ? MapRole(delta.Role) : null;
            var chatDelta = new ChatDelta(role, delta.Content);
            var choiceDelta = new ChatChoiceDelta(chatDelta, response.FirstChoice?.FinishReason);
            return new ChatCompletionChunk([choiceDelta]);
        }

        private static Message ToOpenAiMessage(ChatMessage message)
        {
            if (message.Role == ChatRole.Tool && !string.IsNullOrEmpty(message.ToolCallId) && !string.IsNullOrEmpty(message.FunctionName))
            {
                var content = ToOpenAiContent(message.Content);
                return new Message(message.ToolCallId!, message.FunctionName!, content);
            }

            var role = MapRole(message.Role);
            var contentList = ToOpenAiContent(message.Content);
            var openAiMessage = new Message(role, contentList);

            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                // Normalize tool calls: OpenAI expects function.arguments to be a JSON string,
                // but Anthropic stores them as objects. Convert objects to strings.
                var normalizedToolCalls = NormalizeToolCallsForOpenAi(message.ToolCalls);
                var toolCalls = JsonSerializer.Deserialize<List<global::OpenAI.ToolCall>>(
                    JsonSerializer.Serialize(normalizedToolCalls),
                    ToolCallJsonOptions);

                if (toolCalls != null)
                {
                    var toolCallsProperty = typeof(Message).GetProperty("ToolCalls");
                    toolCallsProperty?.SetValue(openAiMessage, toolCalls);
                }
            }

            return openAiMessage;
        }

        private static ChatMessage FromOpenAiMessage(Message message)
        {
            var role = MapRole(message.Role);
            var contentItems = ToChatContent(message.Content);

            string? toolCallId = null;
            string? functionName = null;
            try
            {
                toolCallId = message.ToolCallId;
                functionName = TryGetToolFunctionName(message);
            }
            catch
            {
                // Some OpenAI message types may not expose tool identifiers.
            }

            IReadOnlyList<ChatToolCall>? toolCalls = null;
            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(
                    JsonSerializer.Serialize(message.ToolCalls),
                    ToolCallJsonOptions);
            }

            if (role == ChatRole.Tool)
            {
                return new ChatMessage(role, contentItems, toolCallId, functionName);
            }

            return toolCalls != null && toolCalls.Count > 0
                ? new ChatMessage(role, contentItems, toolCalls)
                : new ChatMessage(role, contentItems);
        }

        private static List<Content> ToOpenAiContent(IReadOnlyList<ChatContent> contents)
        {
            var list = new List<Content>();
            foreach (var content in contents)
            {
                if (content.ImageUrl != null)
                {
                    list.Add(new Content(new ImageUrl(content.ImageUrl.Url)));
                }
                else
                {
                    list.Add(new Content(content.Text ?? string.Empty));
                }
            }
            return list;
        }

        private static List<ChatContent> ToChatContent(object? content)
        {
            var items = new List<ChatContent>();
            if (content == null)
            {
                return items;
            }

            if (content is string text)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    items.Add(new ChatContent(text));
                }
                return items;
            }

            if (content is JsonElement element)
            {
                AddChatContentFromJsonElement(element, items);
                return items;
            }

            if (content is JsonNode jsonNode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonNode.ToJsonString());
                    AddChatContentFromJsonElement(doc.RootElement, items);
                    return items;
                }
                catch
                {
                    var raw = jsonNode.ToJsonString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        items.Add(new ChatContent(raw));
                    }
                    return items;
                }
            }

            if (content is IEnumerable<Content> contentList)
            {
                foreach (var item in contentList)
                {
                    var itemText = item.Text?.ToString();
                    if (!string.IsNullOrEmpty(itemText))
                    {
                        items.Add(new ChatContent(itemText));
                    }
                    else if (item.ImageUrl != null)
                    {
                        items.Add(new ChatContent(new ChatImageUrl(item.ImageUrl.Url)));
                    }
                }
            }

            return items;
        }

        private static void AddChatContentFromJsonElement(JsonElement element, List<ChatContent> items)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        items.Add(new ChatContent(text));
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var entry in element.EnumerateArray())
                    {
                        AddChatContentFromJsonElement(entry, items);
                    }
                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    {
                        var textValue = textProp.GetString();
                        if (!string.IsNullOrEmpty(textValue))
                        {
                            items.Add(new ChatContent(textValue));
                        }
                        break;
                    }
                    if (element.TryGetProperty("image_url", out var imageProp))
                    {
                        if (imageProp.ValueKind == JsonValueKind.Object && imageProp.TryGetProperty("url", out var urlProp))
                        {
                            var url = urlProp.GetString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                items.Add(new ChatContent(new ChatImageUrl(url)));
                            }
                            break;
                        }
                        if (imageProp.ValueKind == JsonValueKind.String)
                        {
                            var url = imageProp.GetString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                items.Add(new ChatContent(new ChatImageUrl(url)));
                            }
                            break;
                        }
                    }
                    break;
            }
        }

        private static List<object> NormalizeToolCallsForOpenAi(IReadOnlyList<ChatToolCall> toolCalls)
        {
            // OpenAI expects function.arguments to be a JSON string, not an object.
            // When tool calls originate from Anthropic, Arguments is stored as a JsonElement object.
            // We need to convert it to a string representation for OpenAI compatibility.
            var normalized = new List<object>();
            foreach (var tc in toolCalls)
            {
                var arguments = tc.Function.Arguments;
                string argumentsString;
                
                if (arguments.ValueKind == JsonValueKind.String)
                {
                    // Already a string - use as-is
                    argumentsString = arguments.GetString() ?? "{}";
                }
                else if (arguments.ValueKind == JsonValueKind.Object)
                {
                    // Object - serialize to JSON string
                    argumentsString = arguments.GetRawText();
                }
                else if (arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null)
                {
                    argumentsString = "{}";
                }
                else
                {
                    argumentsString = arguments.GetRawText();
                }

                normalized.Add(new
                {
                    id = tc.Id,
                    type = tc.Type,
                    function = new
                    {
                        name = tc.Function.Name,
                        arguments = argumentsString
                    }
                });
            }
            return normalized;
        }

        private static string? TryGetToolFunctionName(Message message)
        {
            var functionNameProperty = message.GetType().GetProperty("FunctionName")
                ?? message.GetType().GetProperty("Name");
            return functionNameProperty?.GetValue(message) as string;
        }

        private static Tool ToOpenAiTool(ChatToolDefinition tool)
        {
            if (tool.Function == null)
            {
                throw new InvalidOperationException("Function tool definition is required.");
            }

            var function = tool.Function;
            JsonNode? parameters = function.Parameters;
            var openAiFunction = new Function(function.Name, function.Description, parameters, null);
            return new Tool(openAiFunction);
        }

        private static Role MapRole(ChatRole role) => role switch
        {
            ChatRole.System => Role.System,
            ChatRole.Developer => Role.Developer,
            ChatRole.User => Role.User,
            ChatRole.Assistant => Role.Assistant,
            ChatRole.Tool => Role.Tool,
            _ => Role.User
        };

        private static ChatRole MapRole(Role role) => role switch
        {
            Role.System => ChatRole.System,
            Role.Developer => ChatRole.Developer,
            Role.User => ChatRole.User,
            Role.Assistant => ChatRole.Assistant,
            Role.Tool => ChatRole.Tool,
            _ => ChatRole.User
        };

    }
}
