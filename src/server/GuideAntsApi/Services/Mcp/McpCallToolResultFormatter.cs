using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace GuideAntsApi.Services.Mcp;

internal static class McpCallToolResultFormatter
{
    public static string Format(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return structured.GetRawText();
        }

        if (result.Content is { Count: > 0 })
        {
            var textParts = result.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            if (textParts.Count > 0)
            {
                var combined = string.Join("\n", textParts);
                return result.IsError == true ? $"ERROR: {combined}" : combined;
            }

            return JsonSerializer.Serialize(result.Content);
        }

        return result.IsError == true ? "ERROR: MCP tool call returned an error with no content." : string.Empty;
    }
}
