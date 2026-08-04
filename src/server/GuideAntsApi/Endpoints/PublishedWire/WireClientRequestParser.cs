using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireClientRequestParser
{
internal sealed record ClientPromptParts(
        IReadOnlyList<ChatMessage> PrefixMessages,
        string UserPrompt,
        IReadOnlyList<string> UserImageUrls);

internal static List<ChatToolDefinition> ParseAnthropicClientToolDefinitions(JsonElement tools)
{
    var definitions = new List<ChatToolDefinition>();
    if (tools.ValueKind != JsonValueKind.Array)
    {
        return definitions;
    }

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in tools.EnumerateArray())
    {
        if (tool.ValueKind != JsonValueKind.Object ||
            !tool.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        var name = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
        {
            continue;
        }

        var description = tool.TryGetProperty("description", out var descriptionElement) &&
                          descriptionElement.ValueKind == JsonValueKind.String
            ? descriptionElement.GetString()
            : null;

        JsonNode? inputSchemaNode = null;
        if (tool.TryGetProperty("input_schema", out var inputSchemaElement) &&
            inputSchemaElement.ValueKind != JsonValueKind.Null &&
            inputSchemaElement.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                inputSchemaNode = JsonNode.Parse(inputSchemaElement.GetRawText());
            }
            catch
            {
                inputSchemaNode = null;
            }
        }

        inputSchemaNode ??= JsonNode.Parse("{}");
        var function = new ChatFunctionDefinition(name, description, inputSchemaNode);
        definitions.Add(new ChatToolDefinition(function));
    }

    return definitions;
}

internal static List<ChatToolDefinition> ParseOpenAiChatClientToolDefinitions(JsonElement tools)
{
    var definitions = new List<ChatToolDefinition>();
    if (tools.ValueKind != JsonValueKind.Array)
    {
        return definitions;
    }

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in tools.EnumerateArray())
    {
        if (tool.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        if (tool.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            !string.Equals(typeElement.GetString(), "function", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!TryReadOpenAiFunctionDescriptor(tool, out var name, out var description, out var parameters))
        {
            continue;
        }

        if (!names.Add(name!))
        {
            continue;
        }

        definitions.Add(new ChatToolDefinition(new ChatFunctionDefinition(name!, description, parameters)));
    }

    return definitions;
}

internal static List<ChatToolDefinition> ParseOpenAiResponsesClientToolDefinitions(JsonElement tools)
{
    var definitions = new List<ChatToolDefinition>();
    if (tools.ValueKind != JsonValueKind.Array)
    {
        return definitions;
    }

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var tool in tools.EnumerateArray())
    {
        if (tool.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        if (tool.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            !string.Equals(typeElement.GetString(), "function", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!TryReadOpenAiFunctionDescriptor(tool, out var name, out var description, out var parameters))
        {
            continue;
        }

        if (!names.Add(name!))
        {
            continue;
        }

        definitions.Add(new ChatToolDefinition(new ChatFunctionDefinition(name!, description, parameters)));
    }

    return definitions;
}

internal static ClientPromptParts BuildOpenAiChatClientPrompt(JsonElement messages)
{
    var parsedMessages = ParseOpenAiChatClientMessages(messages);
    var fallbackPrompt = BuildInstructionsFromChatMessages(messages);
    return SplitClientPrompt(parsedMessages, fallbackPrompt);
}

internal static ClientPromptParts BuildOpenAiResponsesClientPrompt(JsonElement input)
{
    var parsedMessages = ParseOpenAiResponsesClientMessages(input);
    var fallbackPrompt = BuildInstructionsFromResponsesInput(input);
    return SplitClientPrompt(parsedMessages, fallbackPrompt);
}

internal static ClientPromptParts BuildAnthropicClientPrompt(JsonElement system, JsonElement messages)
{
    var parsedMessages = ParseAnthropicClientMessages(system, messages);
    var fallbackPrompt = BuildInstructionsFromAnthropicMessages(system, messages);
    return SplitClientPrompt(parsedMessages, fallbackPrompt);
}

internal static ClientPromptParts SplitClientPrompt(IReadOnlyList<ChatMessage> parsedMessages, string fallbackPrompt)
{
    var normalizedMessages = parsedMessages
        .Where(IsMeaningfulClientMessage)
        .ToList();

    for (var i = normalizedMessages.Count - 1; i >= 0; i--)
    {
        var message = normalizedMessages[i];
        if (message.Role != ChatRole.User)
        {
            continue;
        }

        var text = message.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            continue;
        }

        var imageUrls = message.Content
            .Where(static c => c.IsImage && c.ImageUrl != null && !string.IsNullOrWhiteSpace(c.ImageUrl.Url))
            .Select(static c => c.ImageUrl!.Url)
            .ToList();

        var prefixMessages = new List<ChatMessage>(normalizedMessages.Count - 1);
        for (var index = 0; index < normalizedMessages.Count; index++)
        {
            if (index == i)
            {
                continue;
            }

            prefixMessages.Add(normalizedMessages[index]);
        }

        return new ClientPromptParts(prefixMessages, text.Trim(), imageUrls);
    }

    return new ClientPromptParts(normalizedMessages, fallbackPrompt.Trim(), []);
}

internal static bool IsMeaningfulClientMessage(ChatMessage message)
{
    if (!string.IsNullOrWhiteSpace(message.GetText()))
    {
        return true;
    }

    if (message.Content.Any(static c =>
            c.IsImage && c.ImageUrl != null && !string.IsNullOrWhiteSpace(c.ImageUrl.Url)))
    {
        return true;
    }

    if (message.ToolCalls != null && message.ToolCalls.Count > 0)
    {
        return true;
    }

    return message.Role == ChatRole.Tool && !string.IsNullOrWhiteSpace(message.ToolCallId);
}

internal static List<ChatMessage> ParseOpenAiChatClientMessages(JsonElement messages)
{
    var parsedMessages = new List<ChatMessage>();
    if (messages.ValueKind != JsonValueKind.Array)
    {
        return parsedMessages;
    }

    foreach (var item in messages.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        var roleText = item.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString()
            : "user";
        if (!TryMapClientRole(roleText, out var role))
        {
            continue;
        }

        if (role == ChatRole.Tool)
        {
            var toolCallId = item.TryGetProperty("tool_call_id", out var toolCallIdElement) &&
                             toolCallIdElement.ValueKind == JsonValueKind.String
                ? toolCallIdElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                continue;
            }

            var functionName = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : "external_tool";
            var text = item.TryGetProperty("content", out var toolContentElement)
                ? WireJson.ExtractTextContent(toolContentElement)
                : null;
            parsedMessages.Add(new ChatMessage(
                toolCallId.Trim(),
                string.IsNullOrWhiteSpace(functionName) ? "external_tool" : functionName.Trim(),
                CreateTextContents(text)));
            continue;
        }

        var contents = item.TryGetProperty("content", out var contentElement)
            ? CreateOpenAiChatContents(contentElement)
            : Array.Empty<ChatContent>();

        var toolCalls = role == ChatRole.Assistant &&
                        item.TryGetProperty("tool_calls", out var toolCallsElement)
            ? ParseOpenAiToolCalls(toolCallsElement)
            : [];

        if (toolCalls.Count > 0)
        {
            parsedMessages.Add(new ChatMessage(role, contents, toolCalls));
        }
        else if (contents.Count > 0)
        {
            parsedMessages.Add(new ChatMessage(role, contents));
        }
    }

    return parsedMessages;
}

internal static List<ChatMessage> ParseOpenAiResponsesClientMessages(JsonElement input)
{
    var parsedMessages = new List<ChatMessage>();
    if (input.ValueKind == JsonValueKind.String)
    {
        parsedMessages.Add(new ChatMessage(ChatRole.User, CreateTextContents(input.GetString())));
        return parsedMessages;
    }

    if (input.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in input.EnumerateArray())
        {
            TryAddOpenAiResponsesClientMessage(item, parsedMessages);
        }

        return parsedMessages;
    }

    TryAddOpenAiResponsesClientMessage(input, parsedMessages);
    return parsedMessages;
}

internal static void TryAddOpenAiResponsesClientMessage(JsonElement item, ICollection<ChatMessage> destination)
{
    if (item.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
        ? typeElement.GetString()
        : null;

    if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
    {
        var toolCallId = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
            ? callIdElement.GetString()
            : item.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
                ? toolCallIdElement.GetString()
                : item.TryGetProperty("tool_use_id", out var toolUseIdElement) && toolUseIdElement.ValueKind == JsonValueKind.String
                    ? toolUseIdElement.GetString()
                    : null;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : "external_tool";
        var text = item.TryGetProperty("output", out var outputElement)
            ? WireJson.ExtractTextContent(outputElement)
            : item.TryGetProperty("content", out var contentElement)
                ? WireJson.ExtractTextContent(contentElement)
                : null;
        destination.Add(new ChatMessage(
            toolCallId.Trim(),
            string.IsNullOrWhiteSpace(name) ? "external_tool" : name.Trim(),
            CreateTextContents(text)));
        return;
    }

    if (string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase))
    {
        var text = item.TryGetProperty("text", out var textElement)
            ? WireJson.ExtractTextContent(textElement)
            : null;
        destination.Add(new ChatMessage(ChatRole.User, CreateTextContents(text)));
        return;
    }

    if (string.Equals(type, "input_image", StringComparison.OrdinalIgnoreCase))
    {
        var imageContents = CreateOpenAiChatContents(item);
        if (imageContents.Count > 0)
        {
            destination.Add(new ChatMessage(ChatRole.User, imageContents));
        }
        return;
    }

    if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
    {
        var text = item.TryGetProperty("text", out var textElement)
            ? WireJson.ExtractTextContent(textElement)
            : null;
        destination.Add(new ChatMessage(ChatRole.Assistant, CreateTextContents(text)));
        return;
    }

    JsonElement messagePayload = item;
    if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) &&
        item.TryGetProperty("message", out var nestedMessage) &&
        nestedMessage.ValueKind == JsonValueKind.Object)
    {
        messagePayload = nestedMessage;
    }

    var roleText = messagePayload.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
        ? roleElement.GetString()
        : "user";
    if (!TryMapClientRole(roleText, out var role))
    {
        return;
    }

    if (role == ChatRole.Tool)
    {
        var toolCallId = messagePayload.TryGetProperty("tool_call_id", out var toolCallIdElement) &&
                         toolCallIdElement.ValueKind == JsonValueKind.String
            ? toolCallIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var functionName = messagePayload.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : "external_tool";
        var text = messagePayload.TryGetProperty("content", out var contentElement)
            ? WireJson.ExtractTextContent(contentElement)
            : null;
        destination.Add(new ChatMessage(
            toolCallId.Trim(),
            string.IsNullOrWhiteSpace(functionName) ? "external_tool" : functionName.Trim(),
            CreateTextContents(text)));
        return;
    }

    var content = messagePayload.TryGetProperty("content", out var messageContentElement)
        ? messageContentElement
        : messagePayload.TryGetProperty("text", out var messageTextElement)
            ? messageTextElement
            : default;
    var contents = content.ValueKind == JsonValueKind.Undefined
        ? Array.Empty<ChatContent>()
        : CreateOpenAiChatContents(content);

    var toolCalls = role == ChatRole.Assistant &&
                    messagePayload.TryGetProperty("tool_calls", out var toolCallsElement)
        ? ParseOpenAiToolCalls(toolCallsElement)
        : [];

    if (toolCalls.Count > 0)
    {
        destination.Add(new ChatMessage(role, contents, toolCalls));
    }
    else if (contents.Count > 0)
    {
        destination.Add(new ChatMessage(role, contents));
    }
}

internal static List<ChatMessage> ParseAnthropicClientMessages(JsonElement system, JsonElement messages)
{
    var parsedMessages = new List<ChatMessage>();

    var systemText = WireJson.ExtractTextContent(system);
    if (!string.IsNullOrWhiteSpace(systemText))
    {
        parsedMessages.Add(new ChatMessage(ChatRole.System, CreateTextContents(systemText)));
    }

    if (messages.ValueKind != JsonValueKind.Array)
    {
        return parsedMessages;
    }

    foreach (var message in messages.EnumerateArray())
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        var roleText = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString()
            : "user";
        if (!TryMapClientRole(roleText, out var role))
        {
            continue;
        }

        if (!message.TryGetProperty("content", out var content))
        {
            continue;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            var messageText = WireJson.ExtractTextContent(content);
            var messageContents = CreateTextContents(messageText);
            if (messageContents.Count > 0)
            {
                parsedMessages.Add(new ChatMessage(role, messageContents));
            }
            continue;
        }

        var textBlocks = new List<string>();
        var imageContents = new List<ChatContent>();
        var toolCalls = new List<ChatToolCall>();
        var toolResults = new List<ChatMessage>();

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blockType = block.TryGetProperty("type", out var blockTypeElement) && blockTypeElement.ValueKind == JsonValueKind.String
                ? blockTypeElement.GetString()
                : null;
            if (string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
            {
                var blockText = WireJson.ExtractTextContent(block);
                if (!string.IsNullOrWhiteSpace(blockText))
                {
                    textBlocks.Add(blockText.Trim());
                }

                continue;
            }

            if (string.Equals(blockType, "image", StringComparison.OrdinalIgnoreCase) &&
                TryCreateAnthropicImageContent(block, out var imageContent))
            {
                imageContents.Add(imageContent!);
                continue;
            }

            if (role == ChatRole.Assistant &&
                string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var toolId = block.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : $"call_{Guid.NewGuid():N}";
                var toolName = block.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : "external_tool";
                var arguments = block.TryGetProperty("input", out var inputElement)
                    ? WireJson.NormalizeAnthropicToolInput(inputElement)
                    : JsonSerializer.SerializeToElement(new { });

                toolCalls.Add(new ChatToolCall
                {
                    Id = string.IsNullOrWhiteSpace(toolId) ? $"call_{Guid.NewGuid():N}" : toolId.Trim(),
                    Type = "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = string.IsNullOrWhiteSpace(toolName) ? "external_tool" : toolName.Trim(),
                        Arguments = arguments
                    }
                });

                continue;
            }

            if (role == ChatRole.User &&
                string.Equals(blockType, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                var toolId = block.TryGetProperty("tool_use_id", out var toolUseIdElement) &&
                             toolUseIdElement.ValueKind == JsonValueKind.String
                    ? toolUseIdElement.GetString()
                    : block.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
                        ? toolCallIdElement.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    continue;
                }

                var toolName = block.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : "external_tool";
                var outputText = block.TryGetProperty("content", out var outputElement)
                    ? WireJson.ExtractTextContent(outputElement)
                    : null;
                toolResults.Add(new ChatMessage(
                    toolId.Trim(),
                    string.IsNullOrWhiteSpace(toolName) ? "external_tool" : toolName.Trim(),
                    CreateTextContents(outputText)));
            }
        }

        var contents = new List<ChatContent>();
        var text = textBlocks.Count == 0 ? null : string.Join("\n", textBlocks);
        contents.AddRange(CreateTextContents(text));
        contents.AddRange(imageContents);

        if (role == ChatRole.Assistant && toolCalls.Count > 0)
        {
            parsedMessages.Add(new ChatMessage(role, contents, toolCalls));
        }
        else if (contents.Count > 0)
        {
            parsedMessages.Add(new ChatMessage(role, contents));
        }

        if (toolResults.Count > 0)
        {
            parsedMessages.AddRange(toolResults);
        }
    }

    return parsedMessages;
}

internal static IReadOnlyList<ChatToolCall> ParseOpenAiToolCalls(JsonElement toolCallsElement)
{
    var toolCalls = new List<ChatToolCall>();
    if (toolCallsElement.ValueKind != JsonValueKind.Array)
    {
        return toolCalls;
    }

    foreach (var toolCallElement in toolCallsElement.EnumerateArray())
    {
        if (toolCallElement.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        var type = toolCallElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : "function";
        if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var id = toolCallElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : $"call_{Guid.NewGuid():N}";
        if (!toolCallElement.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        var name = functionElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        JsonElement arguments = JsonSerializer.SerializeToElement(new { });
        if (functionElement.TryGetProperty("arguments", out var argumentsElement))
        {
            arguments = WireJson.NormalizeAnthropicToolInput(argumentsElement);
        }

        toolCalls.Add(new ChatToolCall
        {
            Id = string.IsNullOrWhiteSpace(id) ? $"call_{Guid.NewGuid():N}" : id.Trim(),
            Type = "function",
            Function = new ChatToolCallFunction
            {
                Name = name.Trim(),
                Arguments = arguments
            }
        });
    }

    return toolCalls;
}

internal static IReadOnlyList<ChatContent> CreateTextContents(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return Array.Empty<ChatContent>();
    }

    return [new ChatContent(text.Trim())];
}

internal static IReadOnlyList<ChatContent> CreateOpenAiChatContents(JsonElement content)
{
    if (content.ValueKind == JsonValueKind.String)
    {
        return CreateTextContents(content.GetString());
    }

    if (content.ValueKind == JsonValueKind.Array)
    {
        var items = new List<ChatContent>();
        foreach (var part in content.EnumerateArray())
        {
            AddOpenAiChatContentPart(part, items);
        }

        return items;
    }

    if (content.ValueKind == JsonValueKind.Object)
    {
        var items = new List<ChatContent>();
        AddOpenAiChatContentPart(content, items);
        return items;
    }

    return Array.Empty<ChatContent>();
}

internal static void AddOpenAiChatContentPart(JsonElement part, List<ChatContent> items)
{
    if (part.ValueKind == JsonValueKind.String)
    {
        var text = part.GetString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(new ChatContent(text.Trim()));
        }

        return;
    }

    if (part.ValueKind != JsonValueKind.Object)
    {
        return;
    }

    var type = part.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
        ? typeElement.GetString()
        : null;

    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
    {
        var text = WireJson.ExtractTextContent(part);
        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(new ChatContent(text.Trim()));
        }

        return;
    }

    if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "input_image", StringComparison.OrdinalIgnoreCase) ||
        part.TryGetProperty("image_url", out _))
    {
        if (TryReadOpenAiImageUrl(part, out var imageUrl))
        {
            items.Add(new ChatContent(new ChatImageUrl(imageUrl!)));
        }

        return;
    }

    if (part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
    {
        var textValue = textElement.GetString();
        if (!string.IsNullOrWhiteSpace(textValue))
        {
            items.Add(new ChatContent(textValue.Trim()));
        }
    }
}

internal static bool TryReadOpenAiImageUrl(JsonElement part, out string? imageUrl)
{
    imageUrl = null;

    if (part.TryGetProperty("image_url", out var imageUrlElement))
    {
        if (imageUrlElement.ValueKind == JsonValueKind.String)
        {
            imageUrl = imageUrlElement.GetString();
        }
        else if (imageUrlElement.ValueKind == JsonValueKind.Object &&
                 imageUrlElement.TryGetProperty("url", out var urlElement) &&
                 urlElement.ValueKind == JsonValueKind.String)
        {
            imageUrl = urlElement.GetString();
        }
    }
    else if (part.TryGetProperty("url", out var directUrlElement) &&
             directUrlElement.ValueKind == JsonValueKind.String)
    {
        imageUrl = directUrlElement.GetString();
    }

    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        imageUrl = null;
        return false;
    }

    imageUrl = imageUrl.Trim();
    return true;
}

internal static bool TryCreateAnthropicImageContent(JsonElement block, out ChatContent? content)
{
    content = null;
    if (!block.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    var sourceType = source.TryGetProperty("type", out var sourceTypeElement) &&
                     sourceTypeElement.ValueKind == JsonValueKind.String
        ? sourceTypeElement.GetString()
        : null;

    if (string.Equals(sourceType, "url", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sourceType, "image_url", StringComparison.OrdinalIgnoreCase))
    {
        var url = source.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        content = new ChatContent(new ChatImageUrl(url.Trim()));
        return true;
    }

    if (string.Equals(sourceType, "base64", StringComparison.OrdinalIgnoreCase))
    {
        var data = source.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.String
            ? dataElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        var mediaType = source.TryGetProperty("media_type", out var mediaTypeElement) &&
                        mediaTypeElement.ValueKind == JsonValueKind.String
            ? mediaTypeElement.GetString()
            : "image/png";
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = "image/png";
        }

        content = new ChatContent(new ChatImageUrl($"data:{mediaType.Trim()};base64,{data.Trim()}"));
        return true;
    }

    return false;
}

internal static bool TryMapClientRole(string? role, out ChatRole mappedRole)
{
    mappedRole = ChatRole.User;
    if (string.IsNullOrWhiteSpace(role))
    {
        return true;
    }

    switch (role.Trim().ToLowerInvariant())
    {
        case "system":
            mappedRole = ChatRole.System;
            return true;
        case "developer":
            mappedRole = ChatRole.Developer;
            return true;
        case "user":
            mappedRole = ChatRole.User;
            return true;
        case "assistant":
            mappedRole = ChatRole.Assistant;
            return true;
        case "tool":
            mappedRole = ChatRole.Tool;
            return true;
        default:
            return false;
    }
}

internal static bool TryReadOpenAiFunctionDescriptor(
    JsonElement tool,
    out string? name,
    out string? description,
    out JsonNode? parameters)
{
    name = null;
    description = null;
    parameters = JsonNode.Parse("{}");

    JsonElement descriptor;
    if (tool.TryGetProperty("function", out var functionElement) &&
        functionElement.ValueKind == JsonValueKind.Object)
    {
        descriptor = functionElement;
    }
    else
    {
        descriptor = tool;
    }

    if (!descriptor.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    name = nameElement.GetString()?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return false;
    }

    if (descriptor.TryGetProperty("description", out var descriptionElement) &&
        descriptionElement.ValueKind == JsonValueKind.String)
    {
        description = descriptionElement.GetString();
    }

    if (descriptor.TryGetProperty("parameters", out var parametersElement) &&
        parametersElement.ValueKind != JsonValueKind.Null &&
        parametersElement.ValueKind != JsonValueKind.Undefined)
    {
        try
        {
            parameters = JsonNode.Parse(parametersElement.GetRawText());
        }
        catch
        {
            parameters = JsonNode.Parse("{}");
        }
    }
    else if (descriptor.TryGetProperty("input_schema", out var inputSchemaElement) &&
             inputSchemaElement.ValueKind != JsonValueKind.Null &&
             inputSchemaElement.ValueKind != JsonValueKind.Undefined)
    {
        try
        {
            parameters = JsonNode.Parse(inputSchemaElement.GetRawText());
        }
        catch
        {
            parameters = JsonNode.Parse("{}");
        }
    }

    return true;
}

internal static string BuildInstructionsFromAnthropicMessages(JsonElement system, JsonElement messages)
{
    var builder = new StringBuilder();

    if (system.ValueKind != JsonValueKind.Undefined &&
        system.ValueKind != JsonValueKind.Null)
    {
        var systemText = WireJson.ExtractTextContent(system);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            builder.Append("system: ").AppendLine(systemText.Trim());
        }
    }

    var messageText = BuildInstructionsFromChatMessages(messages);
    if (!string.IsNullOrWhiteSpace(messageText))
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(messageText.Trim());
    }

    return builder.ToString().Trim();
}

internal static string BuildInstructionsFromChatMessages(JsonElement messages)
{
    if (messages.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    var builder = new StringBuilder();
    foreach (var message in messages.EnumerateArray())
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString()
            : "user";
        var content = message.TryGetProperty("content", out var contentElement)
            ? WireJson.ExtractTextContent(contentElement)
            : null;

        if (string.IsNullOrWhiteSpace(content))
        {
            continue;
        }

        builder.Append(role).Append(": ").AppendLine(content.Trim());
    }

    return builder.ToString().Trim();
}

internal static string BuildInstructionsFromResponsesInput(JsonElement input)
{
    return WireJson.ExtractTextContent(input)?.Trim() ?? string.Empty;
}

internal static List<string>? ParseEmbeddingsInput(JsonElement input)
{
    if (input.ValueKind == JsonValueKind.String)
    {
        var value = input.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
    }

    if (input.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var values = new List<string>();
    foreach (var item in input.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = item.GetString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            values.Add(text);
        }
    }

    return values;
}
}
