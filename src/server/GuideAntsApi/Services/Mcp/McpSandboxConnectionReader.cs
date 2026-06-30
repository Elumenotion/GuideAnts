using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Reads MCP sandbox subprocess connection metadata from assistant OpenAPI descriptors.
/// </summary>
public static class McpSandboxConnectionReader
{
    public static McpSandboxConnection? TryReadConnection(string openApiSpecJson)
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
            || !string.Equals(runtimeEl.GetString(), McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal))
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

        if (!meta.TryGetProperty("package", out var packageEl) || packageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var package = ReadPackage(packageEl);
        if (package is null)
        {
            return null;
        }

        var toolNamePrefix = meta.TryGetProperty("toolNamePrefix", out var prefixEl) && prefixEl.ValueKind == JsonValueKind.String
            ? prefixEl.GetString()
            : null;

        var environmentVariables = ReadEnvironmentVariableRefs(meta);

        return new McpSandboxConnection(
            bridgeId,
            package,
            environmentVariables,
            toolNamePrefix);
    }

    public static async Task<McpSandboxConnection?> ResolveConnectionAsync(
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
            || !string.Equals(uri.Scheme, "mcp+sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
    }

    private static McpPackageDescriptorDto? ReadPackage(JsonElement packageEl)
    {
        if (!packageEl.TryGetProperty("command", out var commandEl)
            || commandEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(commandEl.GetString()))
        {
            return null;
        }

        var registryType = packageEl.TryGetProperty("registryType", out var registryEl) && registryEl.ValueKind == JsonValueKind.String
            ? registryEl.GetString() ?? string.Empty
            : string.Empty;
        var identifier = packageEl.TryGetProperty("identifier", out var identifierEl) && identifierEl.ValueKind == JsonValueKind.String
            ? identifierEl.GetString() ?? string.Empty
            : string.Empty;

        List<string>? args = null;
        if (packageEl.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = argsEl.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
        }

        return new McpPackageDescriptorDto(
            registryType,
            identifier,
            commandEl.GetString()!,
            args);
    }

    private static IReadOnlyList<McpEnvironmentVariableRefDto> ReadEnvironmentVariableRefs(JsonElement meta)
    {
        if (!meta.TryGetProperty("environmentVariables", out var envEl) || envEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<McpEnvironmentVariableRefDto>();
        }

        var refs = new List<McpEnvironmentVariableRefDto>();
        foreach (var item in envEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            var secretRef = item.TryGetProperty("secretRef", out var secretEl) && secretEl.ValueKind == JsonValueKind.String
                ? secretEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(secretRef))
            {
                refs.Add(new McpEnvironmentVariableRefDto(name, secretRef));
            }
        }

        return refs;
    }
}
