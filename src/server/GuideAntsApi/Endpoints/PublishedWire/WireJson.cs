using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Endpoints.PublishedWire;

public static class WireJson
{
public static readonly JsonSerializerOptions SerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static readonly JsonSerializerOptions DiagnosticsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

internal static string NormalizeOpenAiFunctionArguments(JsonElement arguments)
{
    if (arguments.ValueKind == JsonValueKind.Object || arguments.ValueKind == JsonValueKind.Array)
    {
        return arguments.GetRawText();
    }

    if (arguments.ValueKind == JsonValueKind.String)
    {
        var raw = arguments.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "{}";
        }

        try
        {
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object ||
                parsed.RootElement.ValueKind == JsonValueKind.Array)
            {
                return parsed.RootElement.GetRawText();
            }
        }
        catch
        {
            // fall back to wrapping invalid JSON strings
        }

        return JsonSerializer.Serialize(new { value = raw });
    }

    if (arguments.ValueKind == JsonValueKind.Null || arguments.ValueKind == JsonValueKind.Undefined)
    {
        return "{}";
    }

    return JsonSerializer.Serialize(new { value = arguments.GetRawText() });
}

internal static JsonElement NormalizeAnthropicToolInput(JsonElement arguments)
{
    if (arguments.ValueKind == JsonValueKind.Object || arguments.ValueKind == JsonValueKind.Array)
    {
        return arguments.Clone();
    }

    if (arguments.ValueKind == JsonValueKind.String)
    {
        var raw = arguments.GetString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object ||
                    parsed.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return parsed.RootElement.Clone();
                }
            }
            catch
            {
                return JsonSerializer.SerializeToElement(new { value = raw });
            }

            return JsonSerializer.SerializeToElement(new { value = raw });
        }
    }

    if (arguments.ValueKind == JsonValueKind.Null || arguments.ValueKind == JsonValueKind.Undefined)
    {
        return JsonSerializer.SerializeToElement(new { });
    }

    return JsonSerializer.SerializeToElement(new { value = arguments.GetRawText() });
}

internal static string? ExtractTextContent(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.String)
    {
        return element.GetString();
    }

    if (element.ValueKind == JsonValueKind.Array)
    {
        var builder = new StringBuilder();
        foreach (var item in element.EnumerateArray())
        {
            var text = ExtractTextContent(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.Append(text.Trim());
        }

        return builder.ToString();
    }

    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString();
        }

        if (element.TryGetProperty("content", out var contentElement))
        {
            return ExtractTextContent(contentElement);
        }
    }

    return null;
}
}
