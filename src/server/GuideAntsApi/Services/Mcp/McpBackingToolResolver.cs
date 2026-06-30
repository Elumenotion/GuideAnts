using System.Text.Json;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Maps prefixed OpenAPI operation ids back to backing MCP tool names.
/// </summary>
public static class McpBackingToolResolver
{
    public static string ResolveBackingToolName(
        string operationId,
        string? toolPath,
        JsonElement methodSchema,
        string? toolNamePrefix)
    {
        if (methodSchema.ValueKind == JsonValueKind.Object
            && methodSchema.TryGetProperty("x-guideants-mcp-tool", out var mcpToolEl)
            && mcpToolEl.TryGetProperty("backingToolId", out var backingEl)
            && backingEl.ValueKind == JsonValueKind.String)
        {
            var backingFromSchema = backingEl.GetString();
            if (!string.IsNullOrWhiteSpace(backingFromSchema))
            {
                return backingFromSchema;
            }
        }

        var backingFromPath = TryParseBackingToolFromPath(toolPath);
        if (!string.IsNullOrWhiteSpace(backingFromPath))
        {
            return backingFromPath;
        }

        if (!string.IsNullOrWhiteSpace(toolNamePrefix)
            && operationId.StartsWith(toolNamePrefix + "_", StringComparison.Ordinal))
        {
            return operationId[(toolNamePrefix.Length + 1)..];
        }

        throw new InvalidOperationException(
            $"Unable to resolve backing MCP tool name for operation '{operationId}'.");
    }

    private static string? TryParseBackingToolFromPath(string? toolPath)
    {
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return null;
        }

        const string prefix = "/tools/";
        if (!toolPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encoded = toolPath[prefix.Length..];
        return string.IsNullOrWhiteSpace(encoded) ? null : Uri.UnescapeDataString(encoded);
    }
}
