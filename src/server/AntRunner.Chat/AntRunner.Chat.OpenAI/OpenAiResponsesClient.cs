using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
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
    private readonly OpenAIClient _client;

    public OpenAiResponsesClient(OpenAIClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var responseRequest = OpenAiResponsesMapper.ToResponseRequest(request);
        var response = await _client.ResponsesEndpoint.CreateModelResponseAsync(
            responseRequest,
            _ => Task.CompletedTask,
            cancellationToken);
        return OpenAiResponsesMapper.FromResponse(response);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var responseRequest = OpenAiResponsesMapper.ToResponseRequest(request);
        var streamHandler = new ResponsesStreamHandler(onChunk);
        var response = await _client.ResponsesEndpoint.CreateModelResponseAsync(
            responseRequest,
            streamHandler.HandleEventAsync,
            cancellationToken);
        return OpenAiResponsesMapper.FromResponse(response);
    }

    private sealed class ResponsesStreamHandler
    {
        private readonly Action<ChatCompletionChunk>? _onChunk;
        private int _lastTextLength;
        private int _lastRefusalLength;
        private int _lastReasoningSummaryLength;
        private int _lastReasoningContentLength;
        private string? _lastContentType;
        private bool _hasEmittedContent;

        public ResponsesStreamHandler(Action<ChatCompletionChunk>? onChunk)
        {
            _onChunk = onChunk;
        }

        public Task HandleEventAsync(string @event, IServerSentEvent sseEvent)
        {
            if (_onChunk == null)
            {
                return Task.CompletedTask;
            }

            // During streaming, the library sends individual content items (TextContent, ReasoningContent, etc.)
            // as the IServerSentEvent, not the entire Message. The Delta property contains accumulated text.
            switch (sseEvent)
            {
                case ResponseTextContent textContent:
                    EmitDelta("text", textContent.Delta, ref _lastTextLength);
                    break;
                case ResponseRefusalContent refusalContent:
                    EmitDelta("refusal", refusalContent.Delta, ref _lastRefusalLength);
                    break;
                case ResponseReasoningSummary reasoningSummary:
                    EmitDelta("reasoning_summary", reasoningSummary.Delta ?? reasoningSummary.Text, ref _lastReasoningSummaryLength, "thinking");
                    break;
                case ResponseReasoningContent reasoningContent:
                    EmitDelta("reasoning_content", reasoningContent.Delta ?? reasoningContent.Text, ref _lastReasoningContentLength, "thinking");
                    break;
                // FunctionToolCall deltas contain argument chunks, not emitted during streaming
            }

            return Task.CompletedTask;
        }

        private void EmitDelta(string contentType, string? accumulatedDelta, ref int lastLength, string? finishReason = null)
        {
            // The library accumulates deltas by appending each new piece to the Delta property.
            // We need to emit only the new portion since the last emission.
            if (string.IsNullOrEmpty(accumulatedDelta))
            {
                return;
            }

            // Emit paragraph break when switching between content types (e.g., reasoning_summary to reasoning_content)
            if (_hasEmittedContent && _lastContentType != contentType)
            {
                var separatorDelta = new ChatDelta(ChatRole.Assistant, "\n\n");
                _onChunk?.Invoke(new ChatCompletionChunk([new ChatChoiceDelta(separatorDelta, finishReason)]));
            }

            // Calculate what's new since last time
            if (accumulatedDelta.Length < lastLength)
            {
                // New content item of same type started; emit paragraph break, then reset.
                if (lastLength > 0)
                {
                    var separatorDelta = new ChatDelta(ChatRole.Assistant, "\n\n");
                    _onChunk?.Invoke(new ChatCompletionChunk([new ChatChoiceDelta(separatorDelta, finishReason)]));
                }
                lastLength = 0;
            }
            if (accumulatedDelta.Length <= lastLength)
            {
                return;
            }

            var newContent = accumulatedDelta[lastLength..];
            lastLength = accumulatedDelta.Length;

            _lastContentType = contentType;
            _hasEmittedContent = true;

            var chatDelta = new ChatDelta(ChatRole.Assistant, newContent);
            _onChunk?.Invoke(new ChatCompletionChunk([new ChatChoiceDelta(chatDelta, finishReason)]));
        }

    }

    private static class OpenAiResponsesMapper
    {
        internal static CreateResponseRequest ToResponseRequest(ChatCompletionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var inputItems = new List<IResponseItem>();
            foreach (var message in request.Messages)
            {
                if (message.Role == ChatRole.Tool)
                {
                    inputItems.Add(ToToolCallOutput(message));
                    continue;
                }

                inputItems.Add(ToResponseMessage(message));

                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        inputItems.Add(ToToolCall(toolCall));
                    }
                }
            }

            var tools = request.Tools?.Select(ToOpenAiTool).ToList();
            var reasoning = MapReasoning(request.Model, request.ReasoningEffort);

            var responseRequest = new CreateResponseRequest(
                input: inputItems,
                model: request.Model,
                temperature: request.Temperature,
                topP: request.TopP,
                reasoning: reasoning,
                tools: tools,
                truncation: Truncation.Disabled);

            return responseRequest;
        }

        internal static ChatCompletionResponse FromResponse(Response response)
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
                        AppendMessageContent(message.Content, content, reasoningSummaries);
                        break;
                    case FunctionToolCall toolCall:
                        toolCalls.Add(FromToolCall(toolCall));
                        break;
                    case ReasoningItem reasoningItem:
                        AppendReasoningItem(reasoningItem, reasoningSummaries);
                        break;
                    case FunctionToolCallOutput:
                        Trace.TraceWarning("OpenAI Responses returned tool outputs in the output list.");
                        break;
                    default:
                        Trace.TraceWarning($"Unsupported OpenAI response item: {item.GetType().Name}");
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

        private static Message ToResponseMessage(ChatMessage message)
        {
            var role = MapRole(message.Role);
            var content = ToResponseContent(message.Content, message.Role);
            return new Message(role, content);
        }

        private static IResponseItem ToToolCall(ChatToolCall toolCall)
        {
            var arguments = ParseArguments(toolCall.Function.Arguments);
            return new FunctionToolCall(toolCall.Id, toolCall.Function.Name, arguments);
        }

        private static IResponseItem ToToolCallOutput(ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new InvalidOperationException("Tool response messages must include ToolCallId.");
            }

            var output = message.GetText();
            return new FunctionToolCallOutput(message.ToolCallId, output);
        }

        private static IReadOnlyList<IResponseContent> ToResponseContent(IReadOnlyList<ChatContent> contents, ChatRole role)
        {
            var list = new List<IResponseContent>();
            var contentType = role == ChatRole.Assistant
                ? ResponseContentType.OutputText
                : ResponseContentType.InputText;

            foreach (var content in contents)
            {
                if (content.ImageUrl != null)
                {
                    list.Add(new ResponseImageContent(content.ImageUrl.Url, null, ImageDetail.Auto));
                    continue;
                }

                list.Add(new ResponseTextContent(content.Text ?? string.Empty, contentType));
            }

            return list;
        }

        private static void AppendMessageContent(
            IReadOnlyList<IResponseContent> contents,
            List<ChatContent> output,
            List<string> reasoningSummaries)
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
                            Trace.TraceWarning("OpenAI Responses returned image content with FileId only.");
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
                        Trace.TraceWarning($"Unsupported OpenAI response content: {content.GetType().Name}");
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

        private static JsonNode ParseArguments(JsonElement arguments)
        {
            if (arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null)
            {
                return JsonNode.Parse("{}")!;
            }

            var json = arguments.GetRawText();
            return JsonNode.Parse(json) ?? JsonNode.Parse("{}")!;
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

        private static Role MapRole(ChatRole role) => role switch
        {
            ChatRole.System => Role.System,
            ChatRole.Developer => Role.Developer,
            ChatRole.User => Role.User,
            ChatRole.Assistant => Role.Assistant,
            ChatRole.Tool => Role.Tool,
            _ => Role.User
        };
    }
}
