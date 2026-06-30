using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Endpoints.PublishedWire;

/// <summary>
/// Maps <see cref="StreamingEvent"/> sequences from <c>SendMessageStreamAsync</c> to provider-shaped wire output.
/// Single engine: no conversation re-orchestration.
/// </summary>
internal static class WireStreamAdapter
{
    internal sealed class OpenAiChatStreamState
    {
        public bool RoleSent { get; set; }
        public bool ContentStreamed { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public bool UsageEmitted { get; set; }
        public bool PendingClientTool { get; set; }
        public string? ErrorPayload { get; set; }
        public List<ChatToolCall> ExternalToolCalls { get; } = [];
    }

    internal static async IAsyncEnumerable<string> WriteOpenAiChatCompletionsSseAsync(
        IAsyncEnumerable<StreamingEvent> source,
        string completionId,
        string modelAlias,
        long created,
        string? publicApiOrigin,
        OpenAiChatStreamState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in source.WithCancellation(ct))
        {
            if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
            {
                state.PendingClientTool = true;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.ExternalToolCall, StringComparison.Ordinal))
            {
                state.ExternalToolCalls.AddRange(WireConversationExecutor.ParseExternalToolCallsFromPayload(ev.Payload));
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Error, StringComparison.Ordinal))
            {
                state.ErrorPayload = ev.Payload;
                yield return FormatOpenAiChatErrorSseLine(ev.Payload);
                yield break;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Usage, StringComparison.Ordinal))
            {
                long promptTokens = state.PromptTokens;
                long completionTokens = state.CompletionTokens;
                WireStreamPayloadReader.ReadUsagePayload(ev.Payload, out promptTokens, out completionTokens);
                state.PromptTokens = promptTokens;
                state.CompletionTokens = completionTokens;
                yield return FormatOpenAiChatChunk(
                    completionId,
                    modelAlias,
                    created,
                    delta: new Dictionary<string, object?>(),
                    finishReason: null,
                    usage: WireStreamPayloadReader.BuildOpenAiUsage(state.PromptTokens, state.CompletionTokens));
                state.UsageEmitted = true;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Token, StringComparison.Ordinal))
            {
                var deltaText = WireStreamPayloadReader.ReadContentDeltaPayload(ev.Payload);
                if (string.IsNullOrEmpty(deltaText))
                {
                    continue;
                }

                deltaText = WireResponseSerializer.RewritePublishedContentUrls(deltaText, publicApiOrigin) ?? deltaText;
                state.ContentStreamed = true;

                var delta = BuildContentDelta(state, deltaText);
                yield return FormatOpenAiChatChunk(completionId, modelAlias, created, delta, finishReason: null);
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.AssistantMessage, StringComparison.Ordinal) ||
                string.Equals(ev.EventType, StreamingEventTypes.Message, StringComparison.Ordinal))
            {
                var content = WireStreamPayloadReader.ReadContentPayload(ev.Payload);
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (!state.RoleSent)
                    {
                        yield return FormatOpenAiChatChunk(
                            completionId,
                            modelAlias,
                            created,
                            new Dictionary<string, object?> { ["role"] = "assistant" },
                            finishReason: null);
                        state.RoleSent = true;
                    }

                    continue;
                }

                content = WireResponseSerializer.RewritePublishedContentUrls(content, publicApiOrigin) ?? content;
                if (!state.ContentStreamed)
                {
                    var delta = BuildContentDelta(state, content);
                    state.ContentStreamed = true;
                    yield return FormatOpenAiChatChunk(completionId, modelAlias, created, delta, finishReason: null);
                }
                else if (!state.RoleSent)
                {
                    yield return FormatOpenAiChatChunk(
                        completionId,
                        modelAlias,
                        created,
                        new Dictionary<string, object?> { ["role"] = "assistant" },
                        finishReason: null);
                    state.RoleSent = true;
                }
            }
        }

        var toolCalls = WireResponseSerializer.BuildOpenAiChatToolCallsForResponse(state.ExternalToolCalls);
        if (toolCalls.Count > 0)
        {
            var toolDelta = new Dictionary<string, object?> { ["role"] = "assistant", ["tool_calls"] = toolCalls };
            yield return FormatOpenAiChatChunk(completionId, modelAlias, created, toolDelta, finishReason: null);
            state.RoleSent = true;
        }

        var finishReason = state.PendingClientTool && toolCalls.Count == 0
            ? "tool_calls"
            : state.PendingClientTool || toolCalls.Count > 0
                ? "tool_calls"
                : "stop";

        yield return FormatOpenAiChatChunk(
            completionId,
            modelAlias,
            created,
            new Dictionary<string, object?>(),
            finishReason);

        if (!state.UsageEmitted && (state.PromptTokens > 0 || state.CompletionTokens > 0))
        {
            yield return FormatOpenAiChatChunk(
                completionId,
                modelAlias,
                created,
                new Dictionary<string, object?>(),
                finishReason: null,
                usage: WireStreamPayloadReader.BuildOpenAiUsage(state.PromptTokens, state.CompletionTokens));
        }

        yield return "data: [DONE]\n\n";
    }

    private static Dictionary<string, object?> BuildContentDelta(OpenAiChatStreamState state, string content)
    {
        if (!state.RoleSent)
        {
            state.RoleSent = true;
            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = content
            };
        }

        return new Dictionary<string, object?> { ["content"] = content };
    }

    private static string FormatOpenAiChatChunk(
        string completionId,
        string modelAlias,
        long created,
        Dictionary<string, object?> delta,
        string? finishReason,
        object? usage = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = completionId,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = modelAlias,
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason
                }
            }
        };

        if (usage != null)
        {
            payload["usage"] = usage;
        }

        return "data: " + JsonSerializer.Serialize(payload, WireJson.SerializationOptions) + "\n\n";
    }

    private static string FormatOpenAiChatErrorSseLine(string? errorPayload)
    {
        object errorObject;
        if (!string.IsNullOrWhiteSpace(errorPayload))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorPayload);
                errorObject = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                errorObject = new { message = errorPayload, type = "provider_error" };
            }
        }
        else
        {
            errorObject = new { message = "Provider execution failed.", type = "provider_error" };
        }

        var payload = new { error = errorObject };
        return "data: " + JsonSerializer.Serialize(payload, WireJson.SerializationOptions) + "\n\n";
    }

    internal sealed class OpenAiResponsesStreamState
    {
        public bool ResponseCreatedEmitted { get; set; }
        public bool MessageItemStarted { get; set; }
        public bool MessageItemCompleted { get; set; }
        public string MessageItemId { get; } = $"msg_{Guid.NewGuid():N}";
        public int MessageOutputIndex { get; set; }
        public int NextOutputIndex { get; set; }
        public string AccumulatedText { get; set; } = string.Empty;
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public bool PendingClientTool { get; set; }
        public string? ErrorPayload { get; set; }
        public List<ChatToolCall> ExternalToolCalls { get; } = [];
    }

    internal static async IAsyncEnumerable<string> WriteOpenAiResponsesSseAsync(
        IAsyncEnumerable<StreamingEvent> source,
        string responseId,
        string modelAlias,
        long created,
        string? publicApiOrigin,
        OpenAiResponsesStreamState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in source.WithCancellation(ct))
        {
            if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
            {
                state.PendingClientTool = true;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.ExternalToolCall, StringComparison.Ordinal))
            {
                state.ExternalToolCalls.AddRange(WireConversationExecutor.ParseExternalToolCallsFromPayload(ev.Payload));
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Error, StringComparison.Ordinal))
            {
                state.ErrorPayload = ev.Payload;
                yield return FormatOpenAiResponsesErrorEvent(ev.Payload);
                yield break;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Usage, StringComparison.Ordinal))
            {
                WireStreamPayloadReader.ReadUsagePayload(ev.Payload, out var promptTokens, out var completionTokens);
                state.PromptTokens = promptTokens;
                state.CompletionTokens = completionTokens;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Token, StringComparison.Ordinal))
            {
                var deltaText = WireStreamPayloadReader.ReadContentDeltaPayload(ev.Payload);
                if (string.IsNullOrEmpty(deltaText))
                {
                    continue;
                }

                deltaText = WireResponseSerializer.RewritePublishedContentUrls(deltaText, publicApiOrigin) ?? deltaText;
                foreach (var chunk in EmitOpenAiResponsesTextDelta(
                             responseId,
                             modelAlias,
                             created,
                             state,
                             deltaText))
                {
                    yield return chunk;
                }

                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.AssistantMessage, StringComparison.Ordinal) ||
                string.Equals(ev.EventType, StreamingEventTypes.Message, StringComparison.Ordinal))
            {
                var content = WireStreamPayloadReader.ReadContentPayload(ev.Payload);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    content = WireResponseSerializer.RewritePublishedContentUrls(content, publicApiOrigin) ?? content;
                    if (string.IsNullOrEmpty(state.AccumulatedText))
                    {
                        foreach (var chunk in EmitOpenAiResponsesTextDelta(
                                     responseId,
                                     modelAlias,
                                     created,
                                     state,
                                     content))
                        {
                            yield return chunk;
                        }
                    }
                }
            }
        }

        foreach (var chunk in CompleteOpenAiResponsesStream(responseId, modelAlias, created, state))
        {
            yield return chunk;
        }
    }

    internal sealed class AnthropicMessagesStreamState
    {
        public bool MessageStartEmitted { get; set; }
        public bool TextBlockStarted { get; set; }
        public bool TextBlockStopped { get; set; }
        public int TextBlockIndex { get; set; }
        public int NextBlockIndex { get; set; }
        public string AccumulatedText { get; set; } = string.Empty;
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public bool PendingClientTool { get; set; }
        public string? ErrorPayload { get; set; }
        public List<ChatToolCall> ExternalToolCalls { get; } = [];
    }

    internal static async IAsyncEnumerable<string> WriteAnthropicMessagesSseAsync(
        IAsyncEnumerable<StreamingEvent> source,
        string messageId,
        string modelAlias,
        string? publicApiOrigin,
        AnthropicMessagesStreamState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ev in source.WithCancellation(ct))
        {
            if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
            {
                state.PendingClientTool = true;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.ExternalToolCall, StringComparison.Ordinal))
            {
                state.ExternalToolCalls.AddRange(WireConversationExecutor.ParseExternalToolCallsFromPayload(ev.Payload));
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Error, StringComparison.Ordinal))
            {
                state.ErrorPayload = ev.Payload;
                yield return FormatAnthropicErrorEvent(ev.Payload);
                yield break;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Usage, StringComparison.Ordinal))
            {
                WireStreamPayloadReader.ReadUsagePayload(ev.Payload, out var promptTokens, out var completionTokens);
                state.PromptTokens = promptTokens;
                state.CompletionTokens = completionTokens;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Token, StringComparison.Ordinal))
            {
                var deltaText = WireStreamPayloadReader.ReadContentDeltaPayload(ev.Payload);
                if (string.IsNullOrEmpty(deltaText))
                {
                    continue;
                }

                deltaText = WireResponseSerializer.RewritePublishedContentUrls(deltaText, publicApiOrigin) ?? deltaText;
                foreach (var chunk in EmitAnthropicTextDelta(messageId, modelAlias, state, deltaText))
                {
                    yield return chunk;
                }

                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.AssistantMessage, StringComparison.Ordinal) ||
                string.Equals(ev.EventType, StreamingEventTypes.Message, StringComparison.Ordinal))
            {
                var content = WireStreamPayloadReader.ReadContentPayload(ev.Payload);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    content = WireResponseSerializer.RewritePublishedContentUrls(content, publicApiOrigin) ?? content;
                    if (string.IsNullOrEmpty(state.AccumulatedText))
                    {
                        foreach (var chunk in EmitAnthropicTextDelta(messageId, modelAlias, state, content))
                        {
                            yield return chunk;
                        }
                    }
                }
            }
        }

        foreach (var chunk in CompleteAnthropicMessagesStream(messageId, modelAlias, state))
        {
            yield return chunk;
        }
    }

    private static IEnumerable<string> EmitOpenAiResponsesTextDelta(
        string responseId,
        string modelAlias,
        long created,
        OpenAiResponsesStreamState state,
        string deltaText)
    {
        EnsureOpenAiResponsesMessageItemStarted(responseId, modelAlias, created, state, out var startedChunks);
        foreach (var chunk in startedChunks)
        {
            yield return chunk;
        }

        state.AccumulatedText += deltaText;
        yield return FormatOpenAiResponsesEvent("response.output_text.delta", new
        {
            type = "response.output_text.delta",
            item_id = state.MessageItemId,
            output_index = state.MessageOutputIndex,
            content_index = 0,
            delta = deltaText
        });
    }

    private static void EnsureOpenAiResponsesMessageItemStarted(
        string responseId,
        string modelAlias,
        long created,
        OpenAiResponsesStreamState state,
        out List<string> chunks)
    {
        chunks = [];
        if (!state.ResponseCreatedEmitted)
        {
            chunks.Add(FormatOpenAiResponsesEvent("response.created", new
            {
                type = "response.created",
                response = new
                {
                    id = responseId,
                    @object = "response",
                    created,
                    status = "in_progress",
                    model = modelAlias,
                    output = Array.Empty<object>()
                }
            }));
            state.ResponseCreatedEmitted = true;
            state.MessageOutputIndex = state.NextOutputIndex;
            state.NextOutputIndex++;
        }

        if (state.MessageItemStarted)
        {
            return;
        }

        chunks.Add(FormatOpenAiResponsesEvent("response.output_item.added", new
        {
            type = "response.output_item.added",
            output_index = state.MessageOutputIndex,
            item = new
            {
                type = "message",
                id = state.MessageItemId,
                status = "in_progress",
                role = "assistant",
                content = Array.Empty<object>()
            }
        }));

        chunks.Add(FormatOpenAiResponsesEvent("response.content_part.added", new
        {
            type = "response.content_part.added",
            item_id = state.MessageItemId,
            output_index = state.MessageOutputIndex,
            content_index = 0,
            part = new
            {
                type = "output_text",
                text = string.Empty,
                annotations = Array.Empty<object>()
            }
        }));

        state.MessageItemStarted = true;
    }

    private static IEnumerable<string> CompleteOpenAiResponsesStream(
        string responseId,
        string modelAlias,
        long created,
        OpenAiResponsesStreamState state)
    {
        if (state.MessageItemStarted && !state.MessageItemCompleted)
        {
            yield return FormatOpenAiResponsesEvent("response.output_text.done", new
            {
                type = "response.output_text.done",
                item_id = state.MessageItemId,
                output_index = state.MessageOutputIndex,
                content_index = 0,
                text = state.AccumulatedText
            });

            var messageItem = new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["id"] = state.MessageItemId,
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = state.AccumulatedText,
                        annotations = Array.Empty<object>()
                    }
                }
            };

            yield return FormatOpenAiResponsesEvent("response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = state.MessageOutputIndex,
                item = messageItem
            });
            state.MessageItemCompleted = true;
        }

        var outputItems = new List<Dictionary<string, object?>>();
        if (state.MessageItemStarted)
        {
            outputItems.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["id"] = state.MessageItemId,
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = state.AccumulatedText,
                        annotations = Array.Empty<object>()
                    }
                }
            });
        }

        foreach (var toolCall in state.ExternalToolCalls)
        {
            if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                continue;
            }

            var callId = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"call_{Guid.NewGuid():N}"
                : toolCall.Id;
            var functionCallItem = new Dictionary<string, object?>
            {
                ["type"] = "function_call",
                ["id"] = $"fc_{Guid.NewGuid():N}",
                ["call_id"] = callId,
                ["name"] = toolCall.Function.Name,
                ["arguments"] = WireJson.NormalizeOpenAiFunctionArguments(toolCall.Function.Arguments),
                ["status"] = "completed"
            };
            var outputIndex = state.NextOutputIndex;
            state.NextOutputIndex++;

            yield return FormatOpenAiResponsesEvent("response.output_item.added", new
            {
                type = "response.output_item.added",
                output_index = outputIndex,
                item = functionCallItem
            });

            yield return FormatOpenAiResponsesEvent("response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = outputIndex,
                item = functionCallItem
            });

            outputItems.Add(functionCallItem);
        }

        if (outputItems.Count == 0)
        {
            var emptyMessageId = $"msg_{Guid.NewGuid():N}";
            outputItems.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["id"] = emptyMessageId,
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = string.Empty,
                        annotations = Array.Empty<object>()
                    }
                }
            });
        }

        if (!state.ResponseCreatedEmitted)
        {
            yield return FormatOpenAiResponsesEvent("response.created", new
            {
                type = "response.created",
                response = new
                {
                    id = responseId,
                    @object = "response",
                    created,
                    status = "in_progress",
                    model = modelAlias,
                    output = Array.Empty<object>()
                }
            });
        }

        yield return FormatOpenAiResponsesEvent("response.completed", new
        {
            type = "response.completed",
            response = new
            {
                id = responseId,
                @object = "response",
                created,
                status = "completed",
                model = modelAlias,
                output = outputItems,
                usage = new
                {
                    input_tokens = state.PromptTokens,
                    output_tokens = state.CompletionTokens,
                    total_tokens = state.PromptTokens + state.CompletionTokens
                }
            }
        });
    }

    private static IEnumerable<string> EmitAnthropicTextDelta(
        string messageId,
        string modelAlias,
        AnthropicMessagesStreamState state,
        string deltaText)
    {
        EnsureAnthropicMessageStarted(messageId, modelAlias, state, out var startedChunks);
        foreach (var chunk in startedChunks)
        {
            yield return chunk;
        }

        if (!state.TextBlockStarted)
        {
            state.TextBlockIndex = state.NextBlockIndex;
            state.NextBlockIndex++;
            yield return FormatAnthropicEvent("content_block_start", new
            {
                type = "content_block_start",
                index = state.TextBlockIndex,
                content_block = new
                {
                    type = "text",
                    text = string.Empty
                }
            });
            state.TextBlockStarted = true;
        }

        state.AccumulatedText += deltaText;
        yield return FormatAnthropicEvent("content_block_delta", new
        {
            type = "content_block_delta",
            index = state.TextBlockIndex,
            delta = new
            {
                type = "text_delta",
                text = deltaText
            }
        });
    }

    private static void EnsureAnthropicMessageStarted(
        string messageId,
        string modelAlias,
        AnthropicMessagesStreamState state,
        out List<string> chunks)
    {
        chunks = [];
        if (state.MessageStartEmitted)
        {
            return;
        }

        chunks.Add(FormatAnthropicEvent("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                model = modelAlias,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = state.PromptTokens,
                    output_tokens = 0L
                }
            }
        }));
        state.MessageStartEmitted = true;
    }

    private static IEnumerable<string> CompleteAnthropicMessagesStream(
        string messageId,
        string modelAlias,
        AnthropicMessagesStreamState state)
    {
        if (state.TextBlockStarted && !state.TextBlockStopped)
        {
            yield return FormatAnthropicEvent("content_block_stop", new
            {
                type = "content_block_stop",
                index = state.TextBlockIndex
            });
            state.TextBlockStopped = true;
        }

        var toolCalls = WireResponseSerializer.BuildOpenAiChatToolCallsForResponse(state.ExternalToolCalls);
        foreach (var toolCall in toolCalls)
        {
            var toolUseId = toolCall.TryGetValue("id", out var idValue) ? idValue as string : null;
            var function = toolCall.TryGetValue("function", out var functionValue) &&
                           functionValue is Dictionary<string, object?> functionDict
                ? functionDict
                : null;
            var toolName = function?.TryGetValue("name", out var nameValue) == true ? nameValue as string : null;
            var arguments = function?.TryGetValue("arguments", out var argsValue) == true ? argsValue as string : "{}";
            if (string.IsNullOrWhiteSpace(toolName))
            {
                continue;
            }

            var blockIndex = state.NextBlockIndex;
            state.NextBlockIndex++;
            toolUseId ??= $"toolu_{Guid.NewGuid():N}";

            if (!state.MessageStartEmitted)
            {
                EnsureAnthropicMessageStarted(messageId, modelAlias, state, out var startChunks);
                foreach (var chunk in startChunks)
                {
                    yield return chunk;
                }
            }

            yield return FormatAnthropicEvent("content_block_start", new
            {
                type = "content_block_start",
                index = blockIndex,
                content_block = new
                {
                    type = "tool_use",
                    id = toolUseId,
                    name = toolName,
                    input = new { }
                }
            });

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                yield return FormatAnthropicEvent("content_block_delta", new
                {
                    type = "content_block_delta",
                    index = blockIndex,
                    delta = new
                    {
                        type = "input_json_delta",
                        partial_json = arguments
                    }
                });
            }

            yield return FormatAnthropicEvent("content_block_stop", new
            {
                type = "content_block_stop",
                index = blockIndex
            });
        }

        if (!state.MessageStartEmitted)
        {
            EnsureAnthropicMessageStarted(messageId, modelAlias, state, out var startChunks);
            foreach (var chunk in startChunks)
            {
                yield return chunk;
            }

            if (!state.TextBlockStarted)
            {
                state.TextBlockIndex = state.NextBlockIndex;
                state.NextBlockIndex++;
                yield return FormatAnthropicEvent("content_block_start", new
                {
                    type = "content_block_start",
                    index = state.TextBlockIndex,
                    content_block = new
                    {
                        type = "text",
                        text = string.Empty
                    }
                });
                yield return FormatAnthropicEvent("content_block_stop", new
                {
                    type = "content_block_stop",
                    index = state.TextBlockIndex
                });
                state.TextBlockStarted = true;
                state.TextBlockStopped = true;
            }
        }

        var stopReason = state.PendingClientTool || toolCalls.Count > 0 ? "tool_use" : "end_turn";
        yield return FormatAnthropicEvent("message_delta", new
        {
            type = "message_delta",
            delta = new
            {
                stop_reason = stopReason,
                stop_sequence = (string?)null
            },
            usage = new
            {
                output_tokens = state.CompletionTokens
            }
        });

        yield return FormatAnthropicEvent("message_stop", new
        {
            type = "message_stop"
        });
    }

    private static string FormatOpenAiResponsesEvent(string eventType, object payload) =>
        "event: " + eventType + "\n" +
        "data: " + JsonSerializer.Serialize(payload, WireJson.SerializationOptions) + "\n\n";

    private static string FormatAnthropicEvent(string eventType, object payload) =>
        "event: " + eventType + "\n" +
        "data: " + JsonSerializer.Serialize(payload, WireJson.SerializationOptions) + "\n\n";

    private static string FormatOpenAiResponsesErrorEvent(string? errorPayload)
    {
        object errorObject = ParseErrorObject(errorPayload);
        return FormatOpenAiResponsesEvent("error", new { type = "error", error = errorObject });
    }

    private static string FormatAnthropicErrorEvent(string? errorPayload)
    {
        object errorObject = ParseErrorObject(errorPayload);
        return FormatAnthropicEvent("error", new
        {
            type = "error",
            error = errorObject
        });
    }

    private static object ParseErrorObject(string? errorPayload)
    {
        if (!string.IsNullOrWhiteSpace(errorPayload))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorPayload);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return new { message = errorPayload, type = "api_error" };
            }
        }

        return new { message = "Provider execution failed.", type = "api_error" };
    }
}
