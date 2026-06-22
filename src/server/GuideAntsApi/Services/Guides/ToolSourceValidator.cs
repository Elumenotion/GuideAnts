using System.Text.Json;
using System.Text.Json.Nodes;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

/// <summary>
/// Shared backend validation and preview for Tool Source OpenAPI descriptors.
/// Uses OpenApiHelper for runtime-compatible tool definition generation.
/// </summary>
public static class ToolSourceValidator
{
    private static readonly HashSet<string> SupportedMcpTransports = new(StringComparer.Ordinal)
    {
        "streamable_http",
        "client_bridge",
    };

    private static readonly JsonSerializerOptions JsonIndentedOptions = new()
    {
        WriteIndented = true,
    };

    public static ToolDefinitionPreviewResultDto PreviewOperation(
        string schemaFragmentJson,
        string openApiSpecJson)
    {
        var messages = new List<ToolSourceValidationMessageDto>();

        JsonDocument fragment;
        try
        {
            fragment = JsonDocument.Parse(schemaFragmentJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to preview tool definition: invalid schema fragment JSON: {ex.Message}", ex);
        }

        var root = fragment.RootElement;
        if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Failed to preview tool definition: schema fragment must include a string 'path'.");
        }

        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Failed to preview tool definition: schema fragment must include a string 'method'.");
        }

        if (!root.TryGetProperty("operation", out var operationEl))
        {
            throw new InvalidOperationException("Failed to preview tool definition: schema fragment must include an 'operation' object.");
        }

        var path = pathEl.GetString() ?? "/";
        var method = methodEl.GetString()?.ToLowerInvariant() ?? "get";

        var previewSpecJson = BuildSingleOperationOpenApiSpec(openApiSpecJson, path, method, operationEl, messages);
        messages.AddRange(ValidateDescriptor(openApiSpecJson, publishChecks: false));

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(previewSpecJson);
        if (!validation.Status || validation.Spec is null)
        {
            throw new InvalidOperationException($"Failed to preview tool definition: {validation.Message}");
        }

        var sourceKind = ClassifySourceKind(openApiSpecJson);
        var actionType = ResolveActionType(sourceKind, openApiSpecJson);
        var hiddenParameters = OpenApiHelper.GetAutoInjectedRequestBodyParameterNames(operationEl);

        var toolDefinitions = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec);
        var operationId = operationEl.TryGetProperty("operationId", out var opId) && opId.ValueKind == JsonValueKind.String
            ? opId.GetString()
            : null;

        var toolDef = toolDefinitions.FirstOrDefault(td =>
            string.Equals(td.Function?.AsObject?.Name, operationId, StringComparison.Ordinal))
            ?? toolDefinitions.FirstOrDefault()
            ?? throw new InvalidOperationException("Failed to generate tool definition");

        var responseSchemas = ExtractResponseSchemaView(toolDef);

        return new ToolDefinitionPreviewResultDto(
            SourceKind: sourceKind,
            ActionType: actionType,
            ToolDefinition: JsonSerializer.Serialize(toolDef, JsonIndentedOptions),
            HiddenParameters: hiddenParameters,
            ResponseSchemas: responseSchemas,
            ValidationMessages: messages);
    }

    public static List<ToolSourceValidationMessageDto> ValidateDescriptor(
        string openApiSpecJson,
        bool publishChecks = true)
    {
        var messages = new List<ToolSourceValidationMessageDto>();

        if (string.IsNullOrWhiteSpace(openApiSpecJson))
        {
            messages.Add(Error("missing_spec", "OpenAPI specification is required.", "openApiSpec"));
            return messages;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(openApiSpecJson);
        }
        catch (Exception ex)
        {
            messages.Add(Error("invalid_json", $"OpenAPI specification is not valid JSON: {ex.Message}", "openApiSpec"));
            return messages;
        }

        var root = parsed.RootElement;

        if (!root.TryGetProperty("openapi", out _) && !root.TryGetProperty("swagger", out _))
        {
            messages.Add(Error("missing_openapi_version", "Descriptor must include an 'openapi' or 'swagger' version field.", "openApiSpec.openapi"));
        }

        if (!root.TryGetProperty("info", out var info)
            || !info.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(title.GetString()))
        {
            messages.Add(Error("missing_info_title", "Descriptor must include info.title.", "openApiSpec.info.title"));
        }

        string? serverUrl = null;
        if (!root.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array
            || servers.GetArrayLength() == 0)
        {
            messages.Add(Error("missing_servers", "Descriptor must include at least one server URL in servers[0].url.", "openApiSpec.servers"));
        }
        else
        {
            var firstServer = servers[0];
            if (!firstServer.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            {
                messages.Add(Error("missing_server_url", "servers[0].url must be a string.", "openApiSpec.servers[0].url"));
            }
            else
            {
                serverUrl = urlEl.GetString();
            }
        }

        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object || paths.EnumerateObject().Count() == 0)
        {
            messages.Add(Error("missing_paths", "Descriptor must include at least one path operation.", "openApiSpec.paths"));
        }
        else
        {
            ValidateUniqueOperationIds(paths, messages);
        }

        var declaredKind = TryGetDeclaredSourceKind(root);
        var schemeKind = ClassifySourceKindFromServerUrl(serverUrl);

        if (declaredKind is not null && schemeKind != "unknown" && !KindsMatch(declaredKind, schemeKind))
        {
            messages.Add(Error(
                "source_kind_scheme_mismatch",
                $"x-guideants-tool-source kind '{declaredKind}' does not match server URL scheme (expected '{schemeKind}').",
                "openApiSpec.servers[0].url"));
        }

        ValidateSchemeSpecificRequirements(root, serverUrl, declaredKind ?? schemeKind, messages);

        if (publishChecks)
        {
            ValidateMcpPublishConstraints(root, serverUrl, declaredKind ?? schemeKind, messages);
        }

        return messages;
    }

    public static void EnsurePublishableOrThrow(string openApiSpecJson, string toolName)
    {
        var messages = ValidateDescriptor(openApiSpecJson, publishChecks: true);
        var blocking = messages.Where(m => string.Equals(m.Severity, "error", StringComparison.OrdinalIgnoreCase)).ToList();
        if (blocking.Count == 0)
        {
            return;
        }

        var summary = string.Join("; ", blocking.Select(m => m.Message));
        throw new InvalidOperationException($"Tool source '{toolName}' failed validation: {summary}");
    }

    public static string BuildSingleOperationOpenApiSpec(
        string openApiSpecJson,
        string path,
        string method,
        JsonElement operation,
        List<ToolSourceValidationMessageDto>? warnings = null)
    {
        var spec = new JsonObject
        {
            ["openapi"] = "3.0.0",
            ["info"] = JsonNode.Parse("""{"title":"Preview","version":"1.0.0"}"""),
            ["servers"] = JsonSerializer.SerializeToNode(new[] { new { url = "https://localhost" } }),
            ["paths"] = new JsonObject
            {
                [path] = new JsonObject
                {
                    [method.ToLowerInvariant()] = JsonNode.Parse(operation.GetRawText()),
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(openApiSpecJson) && openApiSpecJson.Trim() != "{}")
        {
            var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(openApiSpecJson);
            if (validation.Status && validation.Spec is not null)
            {
                var root = validation.Spec.RootElement;

                if (root.TryGetProperty("openapi", out var openapiEl) && openapiEl.ValueKind == JsonValueKind.String)
                {
                    spec["openapi"] = openapiEl.GetString();
                }
                else if (root.TryGetProperty("swagger", out var swaggerEl) && swaggerEl.ValueKind == JsonValueKind.String)
                {
                    spec["swagger"] = swaggerEl.GetString();
                }

                if (root.TryGetProperty("info", out var infoEl))
                {
                    spec["info"] = JsonNode.Parse(infoEl.GetRawText());
                }

                if (root.TryGetProperty("servers", out var serversEl) && serversEl.GetArrayLength() > 0)
                {
                    spec["servers"] = JsonNode.Parse(serversEl.GetRawText());
                }
                else
                {
                    warnings?.Add(Warning(
                        "preview_missing_servers",
                        "Parent OpenAPI spec has no servers; using https://localhost for preview.",
                        "openApiSpec.servers"));
                }

                if (root.TryGetProperty("components", out var componentsEl))
                {
                    spec["components"] = JsonNode.Parse(componentsEl.GetRawText());
                }
            }
            else
            {
                warnings?.Add(Warning(
                    "preview_invalid_parent_spec",
                    $"Parent OpenAPI spec could not be parsed for preview context: {validation.Message}",
                    "openApiSpec"));
            }
        }

        return spec.ToJsonString();
    }

    private static void ValidateUniqueOperationIds(JsonElement paths, List<ToolSourceValidationMessageDto> messages)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pathProperty in paths.EnumerateObject())
        {
            foreach (var methodProperty in pathProperty.Value.EnumerateObject())
            {
                if (!methodProperty.Value.TryGetProperty("operationId", out var opId)
                    || opId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var operationId = opId.GetString()!;
                var location = $"{pathProperty.Name} {methodProperty.Name.ToUpperInvariant()}";
                if (seen.TryGetValue(operationId, out var firstLocation))
                {
                    messages.Add(Error(
                        "duplicate_operation_id",
                        $"Duplicate operationId '{operationId}' at {location} (first seen at {firstLocation}).",
                        "openApiSpec.paths"));
                }
                else
                {
                    seen[operationId] = location;
                }
            }
        }
    }

    private static void ValidateSchemeSpecificRequirements(
        JsonElement root,
        string? serverUrl,
        string sourceKind,
        List<ToolSourceValidationMessageDto> messages)
    {
        switch (sourceKind)
        {
            case "web-api":
                if (!HasWebAuthority(serverUrl))
                {
                    messages.Add(Error(
                        "missing_api_host_authority",
                        "Web API server URL must include an http or https authority.",
                        "openApiSpec.servers[0].url"));
                }

                break;

            case "client-actions":
                if (!HasClientBridge(serverUrl))
                {
                    messages.Add(Error(
                        "missing_client_bridge",
                        "Client Actions server URL must use client:// with a bridge identifier.",
                        "openApiSpec.servers[0].url"));
                }

                break;

            case "sandbox-module":
                if (!HasSandboxModule(serverUrl))
                {
                    messages.Add(Error(
                        "missing_sandbox_module",
                        "Sandbox Module server URL must use sandbox:// with an init module identifier.",
                        "openApiSpec.servers[0].url"));
                }

                break;

            case "local-function":
                if (!HasToolHost(serverUrl))
                {
                    messages.Add(Error(
                        "missing_tool_target",
                        "Local Function server URL must use tool://.",
                        "openApiSpec.servers[0].url"));
                }

                break;

            case "mcp-connection":
                ValidateMcpMetadata(root, serverUrl, messages, publishChecks: false);
                break;
        }
    }

    private static void ValidateMcpPublishConstraints(
        JsonElement root,
        string? serverUrl,
        string sourceKind,
        List<ToolSourceValidationMessageDto> messages)
    {
        if (sourceKind != "mcp-connection")
        {
            return;
        }

        ValidateMcpMetadata(root, serverUrl, messages, publishChecks: true);

        if (TryGetUriScheme(serverUrl) == "mcp")
        {
            messages.Add(Error(
                "unsupported_mcp_scheme",
                "MCP tool sources must route through client://mcp-bridge-{id} (client-bridge-first). mcp:// is not supported.",
                "openApiSpec.servers[0].url"));
        }
    }

    private static void ValidateMcpMetadata(
        JsonElement root,
        string? serverUrl,
        List<ToolSourceValidationMessageDto> messages,
        bool publishChecks)
    {
        if (!root.TryGetProperty("x-guideants-tool-source", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            messages.Add(Error(
                "missing_mcp_metadata",
                "MCP connection descriptors must include x-guideants-tool-source metadata.",
                "openApiSpec.x-guideants-tool-source"));
            return;
        }

        if (!meta.TryGetProperty("kind", out var kindEl)
            || kindEl.ValueKind != JsonValueKind.String
            || !string.Equals(kindEl.GetString(), "mcp", StringComparison.Ordinal))
        {
            messages.Add(Error(
                "missing_mcp_metadata",
                "x-guideants-tool-source.kind must be 'mcp' for MCP connections.",
                "openApiSpec.x-guideants-tool-source.kind"));
        }

        if (!meta.TryGetProperty("transport", out var transportEl) || transportEl.ValueKind != JsonValueKind.String)
        {
            messages.Add(Error(
                "missing_mcp_transport",
                "x-guideants-tool-source.transport is required for MCP connections.",
                "openApiSpec.x-guideants-tool-source.transport"));
            return;
        }

        var transport = transportEl.GetString()!;
        if (!SupportedMcpTransports.Contains(transport))
        {
            messages.Add(Error(
                "unsupported_mcp_transport",
                $"MCP transport '{transport}' is not supported. Allowed transports: streamable_http, client_bridge.",
                "openApiSpec.x-guideants-tool-source.transport"));
            return;
        }

        if (publishChecks && string.Equals(transport, "streamable_http", StringComparison.Ordinal))
        {
            if (!meta.TryGetProperty("url", out var urlEl)
                || urlEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(urlEl.GetString()))
            {
                messages.Add(Error(
                    "missing_mcp_server_url",
                    "Streamable HTTP MCP connections require x-guideants-tool-source.url for publish.",
                    "openApiSpec.x-guideants-tool-source.url"));
            }
            else if (!Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                messages.Add(Error(
                    "invalid_mcp_server_url",
                    "MCP server URL must be an absolute http or https URL.",
                    "openApiSpec.x-guideants-tool-source.url"));
            }
        }

        if (string.Equals(transport, "client_bridge", StringComparison.Ordinal))
        {
            var bridgeFromMeta = meta.TryGetProperty("bridgeId", out var bridgeEl) && bridgeEl.ValueKind == JsonValueKind.String
                ? bridgeEl.GetString()
                : null;
            var bridgeFromUrl = ExtractMcpBridgeId(serverUrl);

            if (string.IsNullOrWhiteSpace(bridgeFromMeta) && string.IsNullOrWhiteSpace(bridgeFromUrl))
            {
                messages.Add(Error(
                    "missing_mcp_bridge_id",
                    "Client bridge MCP connections require x-guideants-tool-source.bridgeId or client://mcp-bridge-{id} server URL.",
                    "openApiSpec.x-guideants-tool-source.bridgeId"));
            }
        }

        if (publishChecks
            && string.Equals(transport, "client_bridge", StringComparison.Ordinal)
            && TryGetUriScheme(serverUrl) == "client"
            && serverUrl is not null
            && !serverUrl.Contains("mcp-bridge-", StringComparison.Ordinal))
        {
            messages.Add(Error(
                "mcp_client_bridge_url_mismatch",
                "Client bridge MCP descriptors must use client://mcp-bridge-{id} as servers[0].url.",
                "openApiSpec.servers[0].url"));
        }
    }

    public static string ClassifySourceKind(string openApiSpecJson)
    {
        if (string.IsNullOrWhiteSpace(openApiSpecJson) || openApiSpecJson.Trim() == "{}")
        {
            return "unknown";
        }

        try
        {
            using var doc = JsonDocument.Parse(openApiSpecJson);
            var declared = TryGetDeclaredSourceKind(doc.RootElement);
            if (declared == "mcp-connection")
            {
                return declared;
            }

            var serverUrl = doc.RootElement.TryGetProperty("servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array
                && servers.GetArrayLength() > 0
                && servers[0].TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

            return ClassifySourceKindFromServerUrl(serverUrl);
        }
        catch
        {
            return "unknown";
        }
    }

    public static string ResolveActionType(string sourceKind, string openApiSpecJson)
    {
        if (sourceKind == "mcp-connection")
        {
            return "ClientHandled";
        }

        var serverUrl = TryExtractServerUrl(openApiSpecJson);
        var scheme = TryGetUriScheme(serverUrl);
        return scheme switch
        {
            "client" => "ClientHandled",
            "sandbox" => "SandboxHandled",
            "tool" => "LocalFunction",
            _ => "WebApi",
        };
    }

    private static string? TryGetDeclaredSourceKind(JsonElement root)
    {
        if (!root.TryGetProperty("x-guideants-tool-source", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (meta.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
        {
            return kindEl.GetString() switch
            {
                "mcp" => "mcp-connection",
                "web-api" => "web-api",
                "client-actions" or "client" => "client-actions",
                "sandbox-module" or "sandbox" => "sandbox-module",
                "local-function" or "tool" => "local-function",
                _ => null,
            };
        }

        return null;
    }

    private static string ClassifySourceKindFromServerUrl(string? serverUrl)
    {
        var scheme = TryGetUriScheme(serverUrl);
        return scheme switch
        {
            "http" or "https" => "web-api",
            "client" when serverUrl?.Contains("mcp-bridge-", StringComparison.Ordinal) == true => "mcp-connection",
            "client" => "client-actions",
            "sandbox" => "sandbox-module",
            "tool" => "local-function",
            "mcp" => "mcp-connection",
            _ => "unknown",
        };
    }

    private static bool KindsMatch(string declaredKind, string schemeKind) =>
        string.Equals(declaredKind, schemeKind, StringComparison.Ordinal)
        || (declaredKind == "mcp-connection" && schemeKind == "mcp-connection");

    private static string? TryExtractServerUrl(string openApiSpecJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(openApiSpecJson);
            if (doc.RootElement.TryGetProperty("servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array
                && servers.GetArrayLength() > 0
                && servers[0].TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String)
            {
                return urlEl.GetString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static string? TryGetUriScheme(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme;
    }

    private static bool HasWebAuthority(string? serverUrl)
    {
        var scheme = TryGetUriScheme(serverUrl);
        if (scheme is not ("http" or "https"))
        {
            return false;
        }

        return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static bool HasClientBridge(string? serverUrl) =>
        TryGetUriScheme(serverUrl) == "client" && !string.IsNullOrWhiteSpace(ExtractUriHost(serverUrl));

    private static bool HasSandboxModule(string? serverUrl) =>
        TryGetUriScheme(serverUrl) == "sandbox" && !string.IsNullOrWhiteSpace(ExtractUriHost(serverUrl));

    private static bool HasToolHost(string? serverUrl) => TryGetUriScheme(serverUrl) == "tool";

    private static string? ExtractUriHost(string? serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string? ExtractMcpBridgeId(string? serverUrl)
    {
        var host = ExtractUriHost(serverUrl);
        if (host is null || !host.StartsWith("mcp-bridge-", StringComparison.Ordinal))
        {
            return null;
        }

        return host["mcp-bridge-".Length..];
    }

    private static Dictionary<string, object>? ExtractResponseSchemaView(ToolDefinition toolDef)
    {
        var schemas = toolDef.Function?.AsObject?.ResponseSchemas;
        if (schemas is null || schemas.Count == 0)
        {
            return null;
        }

        var view = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (statusCode, schema) in schemas)
        {
            view[statusCode] = JsonSerializer.Deserialize<object>(schema.GetRawText()) ?? new object();
        }

        return view;
    }

    private static ToolSourceValidationMessageDto Error(string code, string message, string? field) =>
        new(code, message, field, "error");

    private static ToolSourceValidationMessageDto Warning(string code, string message, string? field) =>
        new(code, message, field, "warning");
}
