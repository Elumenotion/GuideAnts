using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

internal static class McpOpenApiDescriptorGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string ComputeSchemaHash(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ComputeSchemaHashFromString("{}");
        }

        return ComputeSchemaHashFromString(schema.GetRawText());
    }

    public static string ComputeSchemaHash(JsonElement? schema)
    {
        if (schema is null)
        {
            return ComputeSchemaHashFromString("{}");
        }

        return ComputeSchemaHash(schema.Value);
    }

    public static string ComputeSchemaHashFromString(string schemaJson)
    {
        var normalized = NormalizeJson(schemaJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string BuildBridgeServerUrl(string bridgeId) => $"client://mcp-bridge-{bridgeId}";

    public static string SanitizeOperationId(string toolName, string? prefix)
    {
        var baseName = string.IsNullOrWhiteSpace(prefix) ? toolName : $"{prefix}_{toolName}";
        var sanitized = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sanitized.Append(ch);
            }
            else if (ch is '-' or '.' or '/')
            {
                sanitized.Append('_');
            }
        }

        var result = sanitized.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "mcp_tool" : result;
    }

    public static string BuildToolPath(string backingToolId) => $"/tools/{Uri.EscapeDataString(backingToolId)}";

    public static JsonObject BuildOperation(
        string operationId,
        string? title,
        string? description,
        string backingToolId,
        string schemaHash,
        bool enabled,
        JsonElement? inputSchema)
    {
        var operation = new JsonObject
        {
            ["operationId"] = operationId,
            ["summary"] = title ?? operationId,
            ["description"] = description,
            ["x-guideants-mcp-tool"] = new JsonObject
            {
                ["backingToolId"] = backingToolId,
                ["schemaHash"] = schemaHash,
                ["enabled"] = enabled,
            },
        };

        var requestSchema = BuildRequestBodySchema(inputSchema);
        operation["requestBody"] = new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = requestSchema,
                },
            },
        };

        operation["responses"] = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "MCP tool result",
            },
        };

        return operation;
    }

    public static string BuildSchemaFragment(
        string path,
        string method,
        JsonObject operation)
    {
        var fragment = new JsonObject
        {
            ["path"] = path,
            ["method"] = method,
            ["operation"] = operation,
        };
        return fragment.ToJsonString(JsonOptions);
    }

    public static string BuildSourceMetadataExtension(
        McpToolSourceConnectionDto connection,
        Dictionary<string, string>? redactedHeaders)
    {
        var metadata = new JsonObject
        {
            ["kind"] = "mcp",
            ["transport"] = connection.Transport,
        };

        if (!string.IsNullOrWhiteSpace(connection.Url))
        {
            metadata["url"] = connection.Url;
        }

        if (!string.IsNullOrWhiteSpace(connection.BridgeId))
        {
            metadata["bridgeId"] = connection.BridgeId;
        }

        if (!string.IsNullOrWhiteSpace(connection.ToolNamePrefix))
        {
            metadata["toolNamePrefix"] = connection.ToolNamePrefix;
        }

        if (redactedHeaders is { Count: > 0 })
        {
            metadata["headers"] = JsonSerializer.SerializeToNode(redactedHeaders);
        }

        return metadata.ToJsonString(JsonOptions);
    }

    public static Dictionary<string, string> RedactHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            redacted[key] = string.IsNullOrWhiteSpace(value) ? value : "***";
        }

        return redacted;
    }

    private static JsonNode BuildRequestBodySchema(JsonElement? inputSchema)
    {
        if (inputSchema is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            return JsonNode.Parse(inputSchema.Value.GetRawText()) ?? new JsonObject { ["type"] = "object" };
        }

        return new JsonObject { ["type"] = "object" };
    }

    private static string NormalizeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, JsonOptions);
        }
        catch
        {
            return json;
        }
    }
}
