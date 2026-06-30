using System.Text;
using System.Text.Json;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpSandboxStdioDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    IOptions<McpToolExecutorOptions> options,
    IOptions<SettingsSecretsOptions> settingsSecretsOptions,
    ILogger<McpSandboxStdioDiscoveryClient> logger)
    : IMcpSandboxStdioDiscoveryClient
{
    public async Task<McpStdioDiscoverResponse> DiscoverAsync(
        Guid projectId,
        Guid guideId,
        McpToolSourceConnectionDto connection,
        CancellationToken cancellationToken = default)
    {
        var package = connection.Package
            ?? throw new InvalidOperationException("package is required for sandbox_subprocess discovery.");

        IReadOnlyDictionary<string, string> executionEnvironment;
        try
        {
            executionEnvironment = await ResolveExecutionEnvironmentAsync(projectId, guideId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve guide environment for MCP sandbox discovery. GuideId={GuideId}",
                guideId);
            return McpStdioDiscoverResponse.Failed(
                $"Failed to resolve guide environment for MCP sandbox discovery: {ex.Message}");
        }

        Dictionary<string, string> resolvedPackageEnvironment;
        try
        {
            resolvedPackageEnvironment = McpSecretTemplateResolver.ResolveEnvironmentVariables(
                connection.EnvironmentVariables,
                executionEnvironment);
        }
        catch (Exception ex)
        {
            return McpStdioDiscoverResponse.Failed(ex.Message);
        }

        return await PostDiscoverAsync(
            projectId,
            guideId,
            package.Command,
            package.Args ?? [],
            resolvedPackageEnvironment,
            cancellationToken);
    }

    private async Task<McpStdioDiscoverResponse> PostDiscoverAsync(
        Guid projectId,
        Guid guideId,
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, options.Value.CallTimeoutSeconds);
        var requestBody = new
        {
            ProjectId = projectId.ToString("D"),
            GuideId = guideId.ToString("D"),
            Command = command,
            Arguments = arguments,
            Environment = environment.Count > 0 ? environment : null,
            TimeoutSeconds = timeoutSeconds,
        };

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 30);
            NotebookDockerScriptService.ApplyAgentAuthHeader(httpClient, configuration["ScriptExecution:AgentToken"]);

            var discoverUri = NotebookDockerScriptService.BuildEndpointUri(
                ResolveScriptExecutionBaseUrl(),
                "mcp-stdio/discover");
            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(discoverUri, content, cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return McpStdioDiscoverResponse.Failed(
                    $"MCP sandbox stdio discovery failed with HTTP {(int)response.StatusCode}: {responseBody}");
            }

            using var resultDoc = JsonDocument.Parse(responseBody);
            var root = resultDoc.RootElement;
            if (root.TryGetProperty("success", out var successEl)
                && successEl.ValueKind == JsonValueKind.True)
            {
                string? serverName = null;
                string? serverVersion = null;
                if (root.TryGetProperty("serverName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    serverName = nameEl.GetString();
                }

                if (root.TryGetProperty("serverVersion", out var versionEl) && versionEl.ValueKind == JsonValueKind.String)
                {
                    serverVersion = versionEl.GetString();
                }

                var tools = new List<McpStdioDiscoveredToolResponse>();
                if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolEl in toolsEl.EnumerateArray())
                    {
                        if (!toolEl.TryGetProperty("name", out var toolNameEl)
                            || toolNameEl.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(toolNameEl.GetString()))
                        {
                            continue;
                        }

                        string? title = null;
                        if (toolEl.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                        {
                            title = titleEl.GetString();
                        }

                        string? description = null;
                        if (toolEl.TryGetProperty("description", out var descriptionEl)
                            && descriptionEl.ValueKind == JsonValueKind.String)
                        {
                            description = descriptionEl.GetString();
                        }

                        JsonElement inputSchema = default;
                        if (toolEl.TryGetProperty("inputSchema", out var schemaEl))
                        {
                            inputSchema = schemaEl.Clone();
                        }

                        tools.Add(new McpStdioDiscoveredToolResponse(
                            toolNameEl.GetString()!,
                            title,
                            description,
                            inputSchema));
                    }
                }

                return McpStdioDiscoverResponse.Succeeded(serverName, serverVersion, tools);
            }

            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            {
                var error = errorEl.GetString();
                return McpStdioDiscoverResponse.Failed(
                    string.IsNullOrWhiteSpace(error)
                        ? "MCP sandbox stdio discovery failed."
                        : error);
            }

            return McpStdioDiscoverResponse.Failed("MCP sandbox stdio discovery returned an unexpected response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return McpStdioDiscoverResponse.Failed(
                $"MCP sandbox discovery timed out after {timeoutSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP sandbox stdio discovery request failed for guide {GuideId}", guideId);
            return McpStdioDiscoverResponse.Failed($"MCP sandbox discovery failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveExecutionEnvironmentAsync(
        Guid projectId,
        Guid guideId,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
        if (db is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var guideAndCrewIds = await db.GuideMembers
            .AsNoTracking()
            .Where(member => member.GuideId == guideId)
            .OrderBy(member => member.DisplayOrder ?? int.MaxValue)
            .ThenBy(member => member.Assistant.Name)
            .Select(member => member.AssistantId)
            .ToListAsync(cancellationToken);

        guideAndCrewIds.Insert(0, guideId);

        var environmentManifests = await db.ProjectAssistantEnvironments
            .AsNoTracking()
            .Where(environment => environment.ProjectId == projectId
                && guideAndCrewIds.Contains(environment.AssistantId))
            .Select(environment => new
            {
                environment.AssistantId,
                environment.EnvironmentConfigJson,
            })
            .ToListAsync(cancellationToken);

        var manifestByAssistantId = environmentManifests
            .ToDictionary(environment => environment.AssistantId, environment => environment.EnvironmentConfigJson);
        var orderedManifests = guideAndCrewIds
            .Select(assistantId => manifestByAssistantId.TryGetValue(assistantId, out var manifest) ? manifest : null)
            .Where(manifest => !string.IsNullOrWhiteSpace(manifest))
            .ToArray();

        return EnvironmentVariableConfigSerializer.DeserializeForExecution(
            settingsSecretsOptions.Value,
            orderedManifests);
    }

    private string ResolveScriptExecutionBaseUrl()
    {
        var containerName = ServiceRoutingContracts.GuideantsAiContainerName;
        var configKey = ServiceRoutingContracts.ContainerBaseUrlKey(containerName);
        var configuredContainerUrl = configuration[configKey];
        var envSuffix = Environment.GetEnvironmentVariable("CONTAINER_APP_ENV_DNS_SUFFIX");
        return NotebookDockerScriptService.ResolveScriptExecutionBaseUrl(
            containerName,
            configuredContainerUrl,
            envSuffix,
            configKey);
    }
}

public sealed record McpStdioDiscoveredToolResponse(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema);

public sealed class McpStdioDiscoverResponse
{
    public bool Success { get; init; }
    public string? ServerName { get; init; }
    public string? ServerVersion { get; init; }
    public IReadOnlyList<McpStdioDiscoveredToolResponse> Tools { get; init; } = [];
    public string? Error { get; init; }

    public static McpStdioDiscoverResponse Succeeded(
        string? serverName,
        string? serverVersion,
        IReadOnlyList<McpStdioDiscoveredToolResponse> tools) =>
        new()
        {
            Success = true,
            ServerName = serverName,
            ServerVersion = serverVersion,
            Tools = tools,
        };

    public static McpStdioDiscoverResponse Failed(string error) =>
        new() { Success = false, Error = error };
}
