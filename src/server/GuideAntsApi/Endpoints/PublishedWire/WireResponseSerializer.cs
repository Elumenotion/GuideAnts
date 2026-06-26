using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireResponseSerializer
{
internal sealed record AnthropicContentBlock(
        string Type,
        string? Text = null,
        string? ToolUseId = null,
        string? ToolName = null,
        JsonElement? Input = null);

internal static List<AnthropicContentBlock> BuildAnthropicContentBlocks(
    string text,
    IReadOnlyList<ChatToolCall> externalToolCalls)
{
    var blocks = new List<AnthropicContentBlock>();
    if (!string.IsNullOrWhiteSpace(text))
    {
        blocks.Add(new AnthropicContentBlock("text", Text: text));
    }

    foreach (var toolCall in externalToolCalls)
    {
        if (string.IsNullOrWhiteSpace(toolCall.Function?.Name))
        {
            continue;
        }

        var toolUseId = string.IsNullOrWhiteSpace(toolCall.Id)
            ? $"toolu_{Guid.NewGuid():N}"
            : toolCall.Id;
        var input = WireJson.NormalizeAnthropicToolInput(toolCall.Function.Arguments);
        blocks.Add(new AnthropicContentBlock(
            "tool_use",
            ToolUseId: toolUseId,
            ToolName: toolCall.Function.Name,
            Input: input));
    }

    if (blocks.Count == 0 && externalToolCalls.Count == 0)
    {
        blocks.Add(new AnthropicContentBlock("text", Text: string.Empty));
    }

    return blocks;
}

internal static List<Dictionary<string, object?>> BuildOpenAiChatToolCallsForResponse(
    IReadOnlyList<ChatToolCall> externalToolCalls)
{
    var toolCalls = new List<Dictionary<string, object?>>();
    foreach (var toolCall in externalToolCalls)
    {
        if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Function?.Name))
        {
            continue;
        }

        var toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
            ? $"call_{Guid.NewGuid():N}"
            : toolCall.Id;
        toolCalls.Add(new Dictionary<string, object?>
        {
            ["id"] = toolCallId,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = toolCall.Function.Name,
                ["arguments"] = WireJson.NormalizeOpenAiFunctionArguments(toolCall.Function.Arguments)
            }
        });
    }

    return toolCalls;
}

internal static List<Dictionary<string, object?>> BuildOpenAiResponsesOutputItems(
    string text,
    IReadOnlyList<ChatToolCall> externalToolCalls)
{
    var outputItems = new List<Dictionary<string, object?>>();
    if (!string.IsNullOrWhiteSpace(text))
    {
        outputItems.Add(new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new[]
            {
                new
                {
                    type = "output_text",
                    text,
                    annotations = Array.Empty<object>()
                }
            }
        });
    }

    foreach (var toolCall in externalToolCalls)
    {
        if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Function?.Name))
        {
            continue;
        }

        var callId = string.IsNullOrWhiteSpace(toolCall.Id)
            ? $"call_{Guid.NewGuid():N}"
            : toolCall.Id;
        outputItems.Add(new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["id"] = $"fc_{Guid.NewGuid():N}",
            ["call_id"] = callId,
            ["name"] = toolCall.Function.Name,
            ["arguments"] = WireJson.NormalizeOpenAiFunctionArguments(toolCall.Function.Arguments),
            ["status"] = "completed"
        });
    }

    if (outputItems.Count == 0)
    {
        outputItems.Add(new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["id"] = $"msg_{Guid.NewGuid():N}",
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

    return outputItems;
}

internal static bool ContainsFunctionCallItem(IEnumerable<Dictionary<string, object?>> outputItems)
{
    return outputItems.Any(item =>
        item.TryGetValue("type", out var type) &&
        type is string typeString &&
        string.Equals(typeString, "function_call", StringComparison.OrdinalIgnoreCase));
}

internal static object BuildAnthropicResponseContentBlock(AnthropicContentBlock block)
{
    if (string.Equals(block.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
    {
        var input = block.Input ?? JsonSerializer.SerializeToElement(new { });
        return new
        {
            type = "tool_use",
            id = block.ToolUseId,
            name = block.ToolName,
            input
        };
    }

    return new
    {
        type = "text",
        text = block.Text ?? string.Empty
    };
}

internal static string ExtractTextFromResponseMessageItem(Dictionary<string, object?> item)
{
    if (!item.TryGetValue("content", out var contentValue) || contentValue == null)
    {
        return string.Empty;
    }

    try
    {
        var raw = JsonSerializer.Serialize(contentValue, WireJson.SerializationOptions);
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var contentItem in doc.RootElement.EnumerateArray())
        {
            if (contentItem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = contentItem.TryGetProperty("type", out var typeElement) &&
                       typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (!string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (contentItem.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString() ?? string.Empty;
            }
        }
    }
    catch
    {
        // best effort
    }

    return string.Empty;
}

internal static string BuildOpenAiChatCompletionsSsePayload(
    string completionId,
    string modelAlias,
    long created,
    string? text,
    IReadOnlyList<Dictionary<string, object?>> toolCalls,
    string finishReason,
    long promptTokens,
    long completionTokens)
{
    var builder = new StringBuilder();

    void AppendData(object payload)
    {
        builder.Append("data: ")
            .Append(JsonSerializer.Serialize(payload, WireJson.SerializationOptions))
            .Append("\n\n");
    }

    object delta;
    if (!string.IsNullOrWhiteSpace(text) && toolCalls.Count > 0)
    {
        delta = new
        {
            role = "assistant",
            content = text,
            tool_calls = toolCalls
        };
    }
    else if (!string.IsNullOrWhiteSpace(text))
    {
        delta = new
        {
            role = "assistant",
            content = text
        };
    }
    else if (toolCalls.Count > 0)
    {
        delta = new
        {
            role = "assistant",
            tool_calls = toolCalls
        };
    }
    else
    {
        delta = new { role = "assistant" };
    }

    AppendData(new
    {
        id = completionId,
        @object = "chat.completion.chunk",
        created,
        model = modelAlias,
        choices = new[]
        {
            new
            {
                index = 0,
                delta,
                finish_reason = (string?)null
            }
        }
    });

    AppendData(new
    {
        id = completionId,
        @object = "chat.completion.chunk",
        created,
        model = modelAlias,
        choices = new[]
        {
            new
            {
                index = 0,
                delta = new Dictionary<string, object?>(),
                finish_reason = finishReason
            }
        }
    });

    AppendData(new
    {
        id = completionId,
        @object = "chat.completion.chunk",
        created,
        model = modelAlias,
        choices = Array.Empty<object>(),
        usage = WireStreamPayloadReader.BuildOpenAiUsage(promptTokens, completionTokens)
    });

    builder.Append("data: [DONE]\n\n");
    return builder.ToString();
}

internal static string BuildOpenAiResponsesSsePayload(
    string responseId,
    string modelAlias,
    long created,
    IReadOnlyList<Dictionary<string, object?>> outputItems,
    long promptTokens,
    long completionTokens)
{
    var builder = new StringBuilder();

    void AppendEvent(string eventType, object payload)
    {
        builder.Append("event: ")
            .Append(eventType)
            .Append('\n')
            .Append("data: ")
            .Append(JsonSerializer.Serialize(payload, WireJson.SerializationOptions))
            .Append("\n\n");
    }

    AppendEvent("response.created", new
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

    for (var index = 0; index < outputItems.Count; index++)
    {
        var item = outputItems[index];
        var itemType = item.TryGetValue("type", out var typeValue) ? typeValue as string : null;

        if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
        {
            var messageId = item.TryGetValue("id", out var idValue) && idValue is string id
                ? id
                : $"msg_{Guid.NewGuid():N}";
            var text = ExtractTextFromResponseMessageItem(item);

            AppendEvent("response.output_item.added", new
            {
                type = "response.output_item.added",
                output_index = index,
                item = new
                {
                    type = "message",
                    id = messageId,
                    status = "in_progress",
                    role = "assistant",
                    content = Array.Empty<object>()
                }
            });

            AppendEvent("response.content_part.added", new
            {
                type = "response.content_part.added",
                item_id = messageId,
                output_index = index,
                content_index = 0,
                part = new
                {
                    type = "output_text",
                    text = string.Empty,
                    annotations = Array.Empty<object>()
                }
            });

            if (!string.IsNullOrEmpty(text))
            {
                AppendEvent("response.output_text.delta", new
                {
                    type = "response.output_text.delta",
                    item_id = messageId,
                    output_index = index,
                    content_index = 0,
                    delta = text
                });
            }

            AppendEvent("response.output_text.done", new
            {
                type = "response.output_text.done",
                item_id = messageId,
                output_index = index,
                content_index = 0,
                text
            });

            AppendEvent("response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = index,
                item
            });
        }
        else
        {
            AppendEvent("response.output_item.added", new
            {
                type = "response.output_item.added",
                output_index = index,
                item
            });

            AppendEvent("response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = index,
                item
            });
        }
    }

    AppendEvent("response.completed", new
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
                input_tokens = promptTokens,
                output_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        }
    });

    return builder.ToString();
}

internal static IResult CreateAnthropicError(
    int statusCode,
    string errorType,
    string message)
{
    return Results.Json(
        new
        {
            type = "error",
            error = new
            {
                type = errorType,
                message
            },
            request_id = Guid.NewGuid().ToString("N")
        },
        WireJson.SerializationOptions,
        statusCode: statusCode);
}

internal static string BuildAnthropicMessageSsePayload(
    string messageId,
    string modelAlias,
    IReadOnlyList<AnthropicContentBlock> contentBlocks,
    string stopReason,
    long promptTokens,
    long completionTokens)
{
    var builder = new StringBuilder();

    void AppendEvent(string eventType, object payload)
    {
        builder.Append("event: ")
            .Append(eventType)
            .Append('\n')
            .Append("data: ")
            .Append(JsonSerializer.Serialize(payload, WireJson.SerializationOptions))
            .Append("\n\n");
    }

    AppendEvent("message_start", new
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
                input_tokens = promptTokens,
                output_tokens = 0L
            }
        }
    });

    for (var index = 0; index < contentBlocks.Count; index++)
    {
        var block = contentBlocks[index];
        if (string.Equals(block.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
        {
            var toolInput = block.Input ?? JsonSerializer.SerializeToElement(new { });
            AppendEvent("content_block_start", new
            {
                type = "content_block_start",
                index,
                content_block = new
                {
                    type = "tool_use",
                    id = block.ToolUseId,
                    name = block.ToolName,
                    input = new { }
                }
            });

            var partialJson = toolInput.GetRawText();
            if (!string.IsNullOrWhiteSpace(partialJson))
            {
                AppendEvent("content_block_delta", new
                {
                    type = "content_block_delta",
                    index,
                    delta = new
                    {
                        type = "input_json_delta",
                        partial_json = partialJson
                    }
                });
            }

            AppendEvent("content_block_stop", new
            {
                type = "content_block_stop",
                index
            });
            continue;
        }

        AppendEvent("content_block_start", new
        {
            type = "content_block_start",
            index,
            content_block = new
            {
                type = "text",
                text = string.Empty
            }
        });

        if (!string.IsNullOrEmpty(block.Text))
        {
            AppendEvent("content_block_delta", new
            {
                type = "content_block_delta",
                index,
                delta = new
                {
                    type = "text_delta",
                    text = block.Text
                }
            });
        }

        AppendEvent("content_block_stop", new
        {
            type = "content_block_stop",
            index
        });
    }

    AppendEvent("message_delta", new
    {
        type = "message_delta",
        delta = new
        {
            stop_reason = stopReason,
            stop_sequence = (string?)null
        },
        usage = new
        {
            output_tokens = completionTokens
        }
    });

    AppendEvent("message_stop", new
    {
        type = "message_stop"
    });

    return builder.ToString();
}

internal static string? ResolvePublicApiOrigin(HttpContext context)
{
    var scheme = context.Request.Scheme?.Trim();
    if (string.IsNullOrWhiteSpace(scheme) || !context.Request.Host.HasValue)
    {
        return null;
    }

    return $"{scheme}://{context.Request.Host.Value}".TrimEnd('/');
}

internal static string RewritePublishedContentUrls(string? content, string? publicApiOrigin) =>
    McpPublishedContentUrlRewriter.Rewrite(content, publicApiOrigin);
}
