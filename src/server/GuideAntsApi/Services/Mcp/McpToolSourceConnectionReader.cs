using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Reads MCP API connection metadata from assistant OpenAPI descriptors.
/// </summary>
public static class McpToolSourceConnectionReader
{
    public static McpApiConnection? TryReadConnection(string openApiSpecJson)
    {
        using var doc = JsonDocument.Parse(openApiSpecJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("x-guideants-tool-source", out var meta)
            || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!meta.TryGetProperty("runtimeExecution", out var runtimeEl)
            || runtimeEl.ValueKind != JsonValueKind.String
            || !string.Equals(runtimeEl.GetString(), McpRuntimeExecution.Api, StringComparison.Ordinal))
        {
            return null;
        }

        if (!meta.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlEl.GetString()))
        {
            return null;
        }

        var bridgeId = meta.TryGetProperty("bridgeId", out var bridgeEl) && bridgeEl.ValueKind == JsonValueKind.String
            ? bridgeEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(bridgeId))
        {
            bridgeId = TryReadBridgeIdFromServerUrl(root);
        }

        if (string.IsNullOrWhiteSpace(bridgeId))
        {
            return null;
        }

        var toolNamePrefix = meta.TryGetProperty("toolNamePrefix", out var prefixEl) && prefixEl.ValueKind == JsonValueKind.String
            ? prefixEl.GetString()
            : null;

        var headerTemplates = ReadHeaderTemplates(meta);

        return new McpApiConnection(
            bridgeId,
            urlEl.GetString()!,
            headerTemplates,
            toolNamePrefix);
    }

    public static async Task<McpApiConnection?> ResolveConnectionAsync(
        string assistantName,
        string mcpServerUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storageMetadata = await AssistantDefinitionFiles.GetAssistantComplete(assistantName);
        if (storageMetadata?.OpenApiSchemas is not { Count: > 0 })
        {
            return null;
        }

        foreach (var schemaJson in storageMetadata.OpenApiSchemas.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var doc = JsonDocument.Parse(schemaJson);
            var serverUrl = doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString();
            if (!string.Equals(serverUrl, mcpServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return TryReadConnection(schemaJson);
        }

        return null;
    }

    private static string? TryReadBridgeIdFromServerUrl(JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array
            || servers.GetArrayLength() == 0)
        {
            return null;
        }

        var serverUrl = servers[0].TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(serverUrl)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "mcp+api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
    }

    private static Dictionary<string, string> ReadHeaderTemplates(JsonElement meta)
    {
        if (!meta.TryGetProperty("headers", out var headersEl))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (headersEl.ValueKind == JsonValueKind.Object)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headersEl.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    headers[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return headers;
        }

        if (headersEl.ValueKind == JsonValueKind.String)
        {
            var raw = headersEl.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
