using System.Text.Json;
using System.Text.Json.Nodes;
using GuideAntsApi.Models.Guides;
using ModelContextProtocol.Client;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpToolSourceDiscoveryService(
    IMcpSandboxStdioDiscoveryClient sandboxDiscoveryClient,
    ILogger<McpToolSourceDiscoveryService> logger) : IMcpToolSourceDiscoveryService
{
    public async Task<McpTestConnectionResponse> TestConnectionAsync(
        McpTestConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateConnection(request.Connection);
        if (validationError is not null)
        {
            return new McpTestConnectionResponse(false, validationError, null, null);
        }

        if (string.Equals(request.Connection.RuntimeExecution, McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal))
        {
            var scopeError = ValidateSandboxScope(request.ProjectId, request.GuideId);
            if (scopeError is not null)
            {
                return new McpTestConnectionResponse(false, scopeError, null, null);
            }

            var discoverResult = await sandboxDiscoveryClient.DiscoverAsync(
                request.ProjectId!.Value,
                request.GuideId!.Value,
                request.Connection,
                cancellationToken);

            if (!discoverResult.Success)
            {
                return new McpTestConnectionResponse(
                    false,
                    discoverResult.Error ?? "Failed to connect to MCP sandbox package.",
                    null,
                    null);
            }

            return new McpTestConnectionResponse(
                true,
                $"Connected to MCP sandbox package ({discoverResult.Tools.Count} tool(s) available).",
                discoverResult.ServerName,
                discoverResult.ServerVersion);
        }

        try
        {
            await using var client = await CreateMcpClientAsync(request.Connection, cancellationToken);
            var serverInfo = client.ServerInfo;
            return new McpTestConnectionResponse(
                true,
                "Connected to MCP server.",
                serverInfo?.Name,
                serverInfo?.Version);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "MCP connection test failed for runtime {RuntimeExecution}",
                LogValueSanitizer.Sanitize(request.Connection.RuntimeExecution));
            return new McpTestConnectionResponse(false, "Failed to connect to MCP server.", null, null);
        }
    }

    public async Task<McpDiscoverToolsResponse> DiscoverToolsAsync(
        McpDiscoverToolsRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateConnection(request.Connection);
        if (validationError is not null)
        {
            return EmptyDiscoverResponse(false, validationError);
        }

        List<DiscoveredMcpToolCandidate> discovered;
        if (string.Equals(request.Connection.RuntimeExecution, McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal))
        {
            var scopeError = ValidateSandboxScope(request.ProjectId, request.GuideId);
            if (scopeError is not null)
            {
                return EmptyDiscoverResponse(false, scopeError);
            }

            var discoverResult = await sandboxDiscoveryClient.DiscoverAsync(
                request.ProjectId!.Value,
                request.GuideId!.Value,
                request.Connection,
                cancellationToken);

            if (!discoverResult.Success)
            {
                return EmptyDiscoverResponse(
                    false,
                    discoverResult.Error ?? "MCP sandbox tool discovery failed.");
            }

            discovered = discoverResult.Tools
                .Select(tool => new DiscoveredMcpToolCandidate(
                    tool.Name,
                    tool.Title,
                    tool.Description,
                    tool.InputSchema))
                .ToList();
        }
        else
        {
            try
            {
                await using var client = await CreateMcpClientAsync(request.Connection, cancellationToken);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                discovered = tools
                    .Select(t => new DiscoveredMcpToolCandidate(
                        t.Name,
                        t.Title,
                        t.Description,
                        t.ProtocolTool.InputSchema))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "MCP tool discovery failed for runtime {RuntimeExecution}",
                    LogValueSanitizer.Sanitize(request.Connection.RuntimeExecution));
                return EmptyDiscoverResponse(false, "MCP tool discovery failed. Check connection settings and retry.");
            }
        }

        return BuildDiscoverResponse(request, discovered);
    }

    private static McpDiscoverToolsResponse BuildDiscoverResponse(
        McpDiscoverToolsRequest request,
        List<DiscoveredMcpToolCandidate> discovered)
    {
        var existingById = (request.ExistingTools ?? [])
            .GroupBy(t => t.BackingToolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var discoveredIds = new HashSet<string>(StringComparer.Ordinal);
        var prefix = request.Connection.ToolNamePrefix;
        var outputTools = new List<McpDiscoveredToolDto>();
        var diff = new McpDiscoverDiffSummaryDto(0, 0, 0, 0);

        foreach (var tool in discovered.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            discoveredIds.Add(tool.Name);
            var schemaHash = McpOpenApiDescriptorGenerator.ComputeSchemaHash(tool.InputSchema);
            var operationId = ResolveOperationId(tool.Name, prefix, existingById);
            var path = McpOpenApiDescriptorGenerator.BuildToolPath(tool.Name);
            const string method = "post";

            var diffState = "added";
            var selected = true;

            if (existingById.TryGetValue(tool.Name, out var existing))
            {
                selected = existing.Enabled;
                if (!string.Equals(existing.SchemaHash, schemaHash, StringComparison.Ordinal))
                {
                    diffState = "changed";
                    diff = diff with { Changed = diff.Changed + 1 };
                }
                else
                {
                    diffState = selected ? "unchanged" : "disabled";
                    if (!selected)
                    {
                        diff = diff with { Disabled = diff.Disabled + 1 };
                    }
                }

                if (!string.IsNullOrWhiteSpace(existing.OperationId))
                {
                    operationId = existing.OperationId!;
                }
            }
            else
            {
                diff = diff with { Added = diff.Added + 1 };
            }

            var operation = McpOpenApiDescriptorGenerator.BuildOperation(
                operationId,
                tool.Title,
                tool.Description,
                tool.Name,
                schemaHash,
                selected,
                tool.InputSchema);

            outputTools.Add(new McpDiscoveredToolDto(
                tool.Name,
                tool.Name,
                tool.Title,
                tool.Description,
                schemaHash,
                selected,
                diffState,
                operationId,
                path,
                method,
                McpOpenApiDescriptorGenerator.BuildSchemaFragment(path, method, operation)));
        }

        foreach (var existing in existingById.Values)
        {
            if (!discoveredIds.Contains(existing.BackingToolId))
            {
                diff = diff with { Removed = diff.Removed + 1 };
                var path = McpOpenApiDescriptorGenerator.BuildToolPath(existing.BackingToolId);
                const string method = "post";
                var operationId = existing.OperationId
                    ?? McpOpenApiDescriptorGenerator.SanitizeOperationId(existing.BackingToolId, prefix);
                var operation = McpOpenApiDescriptorGenerator.BuildOperation(
                    operationId,
                    existing.BackingToolId,
                    "Removed from MCP server",
                    existing.BackingToolId,
                    existing.SchemaHash ?? string.Empty,
                    false,
                    null);
                ((JsonObject)operation["x-guideants-mcp-tool"]!)["diffState"] = "removed";

                outputTools.Add(new McpDiscoveredToolDto(
                    existing.BackingToolId,
                    existing.BackingToolId,
                    existing.BackingToolId,
                    "Removed from MCP server",
                    existing.SchemaHash ?? string.Empty,
                    false,
                    "removed",
                    operationId,
                    path,
                    method,
                    McpOpenApiDescriptorGenerator.BuildSchemaFragment(path, method, operation)));
            }
        }

        return new McpDiscoverToolsResponse(
            true,
            $"Discovered {discovered.Count} tool(s).",
            outputTools,
            diff,
            null);
    }

    private static string? ValidateSandboxScope(Guid? projectId, Guid? guideId)
    {
        if (!projectId.HasValue || projectId.Value == Guid.Empty
            || !guideId.HasValue || guideId.Value == Guid.Empty)
        {
            return "projectId and guideId are required for sandbox_subprocess MCP connections.";
        }

        return null;
    }

    private static string? ValidateConnection(McpToolSourceConnectionDto connection)
    {
        if (string.IsNullOrWhiteSpace(connection.RuntimeExecution))
        {
            return "runtimeExecution is required.";
        }

        if (!McpRuntimeExecution.All.Contains(connection.RuntimeExecution))
        {
            return $"Unsupported runtimeExecution '{connection.RuntimeExecution}'. Supported values: api, sandbox_subprocess.";
        }

        if (string.IsNullOrWhiteSpace(connection.DiscoveryTransport))
        {
            return "discoveryTransport is required.";
        }

        if (!McpDiscoveryTransport.All.Contains(connection.DiscoveryTransport))
        {
            return $"Unsupported discoveryTransport '{connection.DiscoveryTransport}'. Supported values: streamable_http, stdio.";
        }

        var expectedTransport = string.Equals(connection.RuntimeExecution, McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal)
            ? McpDiscoveryTransport.Stdio
            : McpDiscoveryTransport.StreamableHttp;

        if (!string.Equals(connection.DiscoveryTransport, expectedTransport, StringComparison.Ordinal))
        {
            return $"runtimeExecution '{connection.RuntimeExecution}' requires discoveryTransport '{expectedTransport}'.";
        }

        if (string.IsNullOrWhiteSpace(connection.BridgeId))
        {
            return "bridgeId is required for MCP connections.";
        }

        if (string.Equals(connection.RuntimeExecution, McpRuntimeExecution.Api, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(connection.Url))
            {
                return "MCP server URL is required for api runtime execution.";
            }

            if (!Uri.TryCreate(connection.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "MCP server URL must be an absolute http or https URL.";
            }
        }

        if (string.Equals(connection.RuntimeExecution, McpRuntimeExecution.SandboxSubprocess, StringComparison.Ordinal)
            && connection.Package is null)
        {
            return "package is required for sandbox_subprocess runtime execution.";
        }

        return null;
    }

    private static async Task<McpClient> CreateMcpClientAsync(
        McpToolSourceConnectionDto connection,
        CancellationToken cancellationToken)
    {
        var headers = connection.Headers ?? new Dictionary<string, string>();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(connection.Url!),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AdditionalHeaders = headers,
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static string ResolveOperationId(
        string backingToolId,
        string? prefix,
        IReadOnlyDictionary<string, McpExistingToolStateDto> existingById)
    {
        if (existingById.TryGetValue(backingToolId, out var existing)
            && !string.IsNullOrWhiteSpace(existing.OperationId))
        {
            return existing.OperationId!;
        }

        return McpOpenApiDescriptorGenerator.SanitizeOperationId(backingToolId, prefix);
    }

    private static McpDiscoverToolsResponse EmptyDiscoverResponse(bool success, string message) =>
        new(success, message, [], new McpDiscoverDiffSummaryDto(0, 0, 0, 0), null);

    private sealed record DiscoveredMcpToolCandidate(
        string Name,
        string? Title,
        string? Description,
        JsonElement InputSchema);
}
