using System.Text.Json;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireIdCodec
{
internal static string? ParseResponsesConversationWireId(JsonElement conversation)
{
    if (conversation.ValueKind == JsonValueKind.Null ||
        conversation.ValueKind == JsonValueKind.Undefined)
    {
        return null;
    }

    if (conversation.ValueKind == JsonValueKind.String)
    {
        return conversation.GetString();
    }

    if (conversation.ValueKind == JsonValueKind.Object &&
        conversation.TryGetProperty("id", out var idElement) &&
        idElement.ValueKind == JsonValueKind.String)
    {
        return idElement.GetString();
    }

    return null;
}

internal static string FormatResponsesId(Guid messageId) => $"resp_{messageId:N}";

internal static string FormatConversationId(Guid notebookConversationId) => $"conv_{notebookConversationId:N}";

internal static string FormatAnthropicMessageId(Guid messageId) => $"msg_{messageId:N}";

internal static bool TryParseAnthropicMessageId(string? messageId, out Guid parsedMessageId)
{
    parsedMessageId = Guid.Empty;

    if (string.IsNullOrWhiteSpace(messageId))
    {
        return false;
    }

    var trimmed = messageId.Trim();
    const string prefix = "msg_";
    if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var rawId = trimmed[prefix.Length..];
    return Guid.TryParse(rawId, out parsedMessageId);
}

internal static bool TryParseResponsesMessageId(string responseId, out Guid messageId)
{
    messageId = Guid.Empty;

    if (string.IsNullOrWhiteSpace(responseId))
    {
        return false;
    }

    var trimmed = responseId.Trim();
    const string prefix = "resp_";
    if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var rawId = trimmed[prefix.Length..];
    return Guid.TryParse(rawId, out messageId);
}

internal static bool TryParseConversationId(string? conversationId, out Guid notebookConversationId)
{
    notebookConversationId = Guid.Empty;

    if (string.IsNullOrWhiteSpace(conversationId))
    {
        return false;
    }

    var trimmed = conversationId.Trim();
    const string prefix = "conv_";
    if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var rawId = trimmed[prefix.Length..];
    return Guid.TryParse(rawId, out notebookConversationId);
}
}
