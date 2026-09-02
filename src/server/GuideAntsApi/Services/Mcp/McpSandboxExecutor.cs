using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Mcp;

/// <summary>
/// Spawns registry MCP packages via ScriptExecutionAgent stdio child (E7, E15).
/// </summary>
public sealed class McpSandboxExecutor(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    IOptions<McpToolExecutorOptions> options,
    IOptions<SettingsSecretsOptions> settingsSecretsOptions,
    ILogger<McpSandboxExecutor> logger)
{
    public async Task<string> ExecuteSandboxToolAsync(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        IReadOnlyDictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            return "ERROR: InvocationContext is required for MCP sandbox tool execution.";
        }

        cancellationToken.ThrowIfCancellationRequested();

        var connection = await McpSandboxConnectionReader.ResolveConnectionAsync(
            assistantName,
            mcpServerUrl,
            cancellationToken);
        if (connection is null)
        {
            return $"ERROR: MCP sandbox connection metadata not found for server URL '{mcpServerUrl}'.";
        }

        var backingToolName = McpBackingToolResolver.ResolveBackingToolName(
            operationId,
            toolPath,
            methodSchema,
            connection.ToolNamePrefix);

        IReadOnlyDictionary<string, string> executionEnvironment;
        Guid guideScopeId;
        try
        {
            guideScopeId = await ResolveGuideScopeIdAsync(context, cancellationToken);
            executionEnvironment = await ResolveExecutionEnvironmentAsync(context, guideScopeId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve guide environment for MCP sandbox tool call. BridgeId={BridgeId}, OperationId={OperationId}",
                LogValueSanitizer.Sanitize(connection.BridgeId),
                LogValueSanitizer.Sanitize(operationId));
            return $"ERROR: Failed to resolve guide environment for MCP sandbox tool execution: {ex.Message}";
        }

        Dictionary<string, string> resolvedPackageEnvironment;
        try
        {
            resolvedPackageEnvironment = McpSecretTemplateResolver.ResolveEnvironmentVariables(
                connection.EnvironmentVariableRefs,
                executionEnvironment);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }

        var timeoutSeconds = Math.Max(1, options.Value.CallTimeoutSeconds);
        var workingDirectory = NotebookPathHelper.GetWorkingDirectory(context);
        Directory.CreateDirectory(workingDirectory);

        var scriptExecutionBaseUrl = ResolveScriptExecutionBaseUrl();
        var requestBody = new
        {
            ProjectId = context.ProjectId.ToString("D"),
            NotebookId = context.NotebookId.ToString("D"),
            GuideId = guideScopeId.ToString("D"),
            WorkingDirectory = workingDirectory,
            Command = connection.Package.Command,
            Arguments = connection.Package.Args ?? new List<string>(),
            ToolName = backingToolName,
            ToolArguments = ConvertArgumentsToJsonElement(arguments),
            Environment = resolvedPackageEnvironment.Count > 0 ? resolvedPackageEnvironment : null,
            TimeoutSeconds = timeoutSeconds,
        };

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds + 30);
            NotebookDockerScriptService.ApplyAgentAuthHeader(httpClient, configuration["ScriptExecution:AgentToken"]);

            var executeUri = NotebookDockerScriptService.BuildEndpointUri(scriptExecutionBaseUrl, "mcp-stdio");
            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            cancellationToken.ThrowIfCancellationRequested();
            var response = await httpClient.PostAsync(executeUri, content, cancellationToken);

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"ERROR: MCP sandbox stdio request failed with HTTP {(int)response.StatusCode}: {responseBody}";
            }

            using var resultDoc = JsonDocument.Parse(responseBody);
            var root = resultDoc.RootElement;
            if (root.TryGetProperty("success", out var successEl)
                && successEl.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.String)
            {
                return resultEl.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("error", out var errorEl)
                && errorEl.ValueKind == JsonValueKind.String)
            {
                var error = errorEl.GetString();
                return string.IsNullOrWhiteSpace(error)
                    ? "ERROR: MCP sandbox stdio tool call failed."
                    : $"ERROR: {error}";
            }

            return "ERROR: MCP sandbox stdio tool call returned an unexpected response.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"ERROR: MCP sandbox tool call timed out after {timeoutSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "MCP sandbox stdio request failed. BridgeId={BridgeId}, BackingTool={BackingTool}",
                LogValueSanitizer.Sanitize(connection.BridgeId),
                LogValueSanitizer.Sanitize(backingToolName));
            return $"ERROR: MCP sandbox tool call failed: {ex.Message}";
        }
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

    private static JsonElement? ConvertArgumentsToJsonElement(IReadOnlyDictionary<string, object>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        var converted = McpToolArgumentConverter.Convert(arguments);
        return JsonSerializer.SerializeToElement(converted);
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveExecutionEnvironmentAsync(
        InvocationContext context,
        Guid guideScopeId,
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
            .Where(member => member.GuideId == guideScopeId)
            .OrderBy(member => member.DisplayOrder ?? int.MaxValue)
            .ThenBy(member => member.Assistant.Name)
            .Select(member => member.AssistantId)
            .ToListAsync(cancellationToken);

        guideAndCrewIds.Insert(0, guideScopeId);

        var environmentManifests = await db.ProjectAssistantEnvironments
            .AsNoTracking()
            .Where(environment => environment.ProjectId == context.ProjectId
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

    private async Task<Guid> ResolveGuideScopeIdAsync(
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (context.NotebookId != Guid.Empty)
        {
            var guideId = await db.Notebooks
                .AsNoTracking()
                .Where(notebook => notebook.Id == context.NotebookId && notebook.ProjectId == context.ProjectId)
                .Select(notebook => notebook.GuideId ?? notebook.NotebookTemplateId)
                .FirstOrDefaultAsync(cancellationToken);

            if (guideId.HasValue && guideId.Value != Guid.Empty)
            {
                return guideId.Value;
            }
        }

        if (context.AssistantId.HasValue && context.AssistantId.Value != Guid.Empty)
        {
            return context.AssistantId.Value;
        }

        throw new InvalidOperationException(
            $"Unable to resolve guide scope for ProjectId={context.ProjectId} NotebookId={context.NotebookId}.");
    }
}
