using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Rewrites legacy <c>client://mcp-bridge-*</c> MCP descriptors to API-only schemes (E4).
/// </summary>
public static class McpDescriptorMigrator
{
    private const string LegacyBridgeHostPrefix = "mcp-bridge-";
    private const string LegacyTransportClientBridge = "client_bridge";
    private const string LegacyTransportStreamableHttp = "streamable_http";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static bool NeedsMigration(string openApiSpecJson)
    {
        if (string.IsNullOrWhiteSpace(openApiSpecJson) || openApiSpecJson.Trim() == "{}")
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(openApiSpecJson);
            return NeedsMigration(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Migrate(string openApiSpecJson)
    {
        if (!NeedsMigration(openApiSpecJson))
        {
            return openApiSpecJson;
        }

        var root = JsonNode.Parse(openApiSpecJson)
            ?? throw new InvalidOperationException("MCP descriptor migration failed: invalid JSON root.");

        var meta = root["x-guideants-tool-source"] as JsonObject
            ?? throw new InvalidOperationException(
                "MCP descriptor migration failed: missing x-guideants-tool-source.");

        var bridgeId = ResolveBridgeId(root, meta);
        if (string.IsNullOrWhiteSpace(bridgeId))
        {
            throw new InvalidOperationException(
                "MCP descriptor migration failed: bridgeId is required for legacy MCP descriptors.");
        }

        var runtimeExecution = ResolveRuntimeExecution(meta);
        var discoveryTransport = ResolveDiscoveryTransport(runtimeExecution, meta);

        meta["runtimeExecution"] = runtimeExecution;
        meta["discoveryTransport"] = discoveryTransport;
        meta["bridgeId"] = bridgeId;
        meta.Remove("transport");

        var servers = root["servers"] as JsonArray ?? new JsonArray();
        if (servers.Count == 0)
        {
            servers.Add(new JsonObject());
        }

        if (servers[0] is JsonObject firstServer)
        {
            firstServer["url"] = McpOpenApiDescriptorGenerator.BuildMcpServerUrl(bridgeId, runtimeExecution);
            firstServer["description"] = runtimeExecution == McpRuntimeExecution.SandboxSubprocess
                ? "MCP sandbox subprocess"
                : "MCP API execution";
        }

        return root.ToJsonString(JsonOptions);
    }

    public static async Task<int> BackfillGuideSchemasAsync(
        ApplicationDbContext db,
        Guid guideId,
        CancellationToken cancellationToken = default)
    {
        var schemas = await db.AssistantOpenApiSchemas
            .Where(s => s.AssistantId == guideId)
            .ToListAsync(cancellationToken);

        var updated = 0;
        foreach (var schema in schemas)
        {
            if (!NeedsMigration(schema.SpecificationJson))
            {
                continue;
            }

            schema.SpecificationJson = Migrate(schema.SpecificationJson);
            updated++;
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }

    private static bool NeedsMigration(JsonElement root)
    {
        if (!root.TryGetProperty("x-guideants-tool-source", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("kind", out var kindEl)
            || kindEl.ValueKind != JsonValueKind.String
            || !string.Equals(kindEl.GetString(), "mcp", StringComparison.Ordinal))
        {
            return false;
        }

        if (meta.TryGetProperty("transport", out _))
        {
            return true;
        }

        if (!meta.TryGetProperty("runtimeExecution", out var runtimeEl)
            || runtimeEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(runtimeEl.GetString()))
        {
            return true;
        }

        var serverUrl = TryGetServerUrl(root);
        if (IsLegacyClientBridgeUrl(serverUrl))
        {
            return true;
        }

        if (TryGetUriScheme(serverUrl) == "mcp")
        {
            return true;
        }

        return false;
    }

    private static string ResolveRuntimeExecution(JsonObject meta)
    {
        if (meta.TryGetPropertyValue("runtimeExecution", out var runtimeNode)
            && runtimeNode is JsonValue runtimeValue
            && runtimeValue.TryGetValue<string>(out var runtime)
            && McpRuntimeExecution.All.Contains(runtime))
        {
            return runtime;
        }

        if (meta["package"] is JsonObject)
        {
            return McpRuntimeExecution.SandboxSubprocess;
        }

        return McpRuntimeExecution.Api;
    }

    private static string ResolveDiscoveryTransport(string runtimeExecution, JsonObject meta)
    {
        if (meta.TryGetPropertyValue("discoveryTransport", out var transportNode)
            && transportNode is JsonValue transportValue
            && transportValue.TryGetValue<string>(out var discoveryTransport)
            && McpDiscoveryTransport.All.Contains(discoveryTransport))
        {
            return discoveryTransport;
        }

        if (string.Equals(runtimeExecution, McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal))
        {
            return McpDiscoveryTransport.Stdio;
        }

        var legacyTransport = meta.TryGetPropertyValue("transport", out var legacyNode)
            && legacyNode is JsonValue legacyValue
            && legacyValue.TryGetValue<string>(out var legacy)
            ? legacy
            : null;

        if (string.Equals(legacyTransport, LegacyTransportStreamableHttp, StringComparison.Ordinal))
        {
            return McpDiscoveryTransport.StreamableHttp;
        }

        if (string.Equals(legacyTransport, LegacyTransportClientBridge, StringComparison.Ordinal))
        {
            return McpDiscoveryTransport.StreamableHttp;
        }

        return McpDiscoveryTransport.StreamableHttp;
    }

    private static string? ResolveBridgeId(JsonNode root, JsonObject meta)
    {
        if (meta.TryGetPropertyValue("bridgeId", out var bridgeNode)
            && bridgeNode is JsonValue bridgeValue
            && bridgeValue.TryGetValue<string>(out var bridgeId)
            && !string.IsNullOrWhiteSpace(bridgeId))
        {
            return bridgeId;
        }

        var serverUrl = root["servers"]?[0]?["url"]?.GetValue<string>();
        return ExtractBridgeIdFromLegacyUrl(serverUrl);
    }

    private static string? ExtractBridgeIdFromLegacyUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, "client", StringComparison.Ordinal))
        {
            return uri.Host;
        }

        var host = uri.Host;
        if (!host.StartsWith(LegacyBridgeHostPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return host[LegacyBridgeHostPrefix.Length..];
    }

    private static bool IsLegacyClientBridgeUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, "client", StringComparison.Ordinal)
            && uri.Host.StartsWith(LegacyBridgeHostPrefix, StringComparison.Ordinal);
    }

    private static string? TryGetServerUrl(JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array
            || servers.GetArrayLength() == 0)
        {
            return null;
        }

        var first = servers[0];
        return first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : null;
    }

    private static string? TryGetUriScheme(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme;
    }
}
