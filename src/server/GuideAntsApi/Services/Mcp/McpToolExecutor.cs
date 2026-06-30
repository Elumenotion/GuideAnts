using System.Text.Json;
using AntRunner.ToolCalling;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.EnvironmentVariables;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpToolExecutor(
    IServiceProvider serviceProvider,
    McpSandboxExecutor sandboxExecutor,
    IOptions<McpToolExecutorOptions> options,
    IOptions<SettingsSecretsOptions> settingsSecretsOptions,
    ILogger<McpToolExecutor> logger) : IMcpToolExecutor
{
    public Task<string> ExecuteSandboxToolAsync(
        string assistantName,
        string operationId,
        string mcpServerUrl,
        string toolPath,
        JsonElement methodSchema,
        IReadOnlyDictionary<string, object>? arguments,
        InvocationContext? context,
        CancellationToken cancellationToken = default) =>
        sandboxExecutor.ExecuteSandboxToolAsync(
            assistantName,
            operationId,
            mcpServerUrl,
            toolPath,
            methodSchema,
            arguments,
            context,
            cancellationToken);

    public async Task<string> ExecuteApiToolAsync(
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
            return "ERROR: InvocationContext is required for MCP tool execution.";
        }

        var connection = await McpToolSourceConnectionReader.ResolveConnectionAsync(
            assistantName,
            mcpServerUrl,
            cancellationToken);
        if (connection is null)
        {
            return $"ERROR: MCP connection metadata not found for server URL '{mcpServerUrl}'.";
        }

        if (!Uri.TryCreate(connection.Url, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            return "ERROR: MCP server URL must be an absolute http or https URL.";
        }

        var backingToolName = McpBackingToolResolver.ResolveBackingToolName(
            operationId,
            toolPath,
            methodSchema,
            connection.ToolNamePrefix);

        IReadOnlyDictionary<string, string> executionEnvironment;
        try
        {
            executionEnvironment = await ResolveExecutionEnvironmentAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve guide environment for MCP tool call. BridgeId={BridgeId}, OperationId={OperationId}",
                LogValueSanitizer.Sanitize(connection.BridgeId),
                LogValueSanitizer.Sanitize(operationId));
            return $"ERROR: Failed to resolve guide environment for MCP tool execution: {ex.Message}";
        }

        Dictionary<string, string> resolvedHeaders;
        try
        {
            resolvedHeaders = McpSecretTemplateResolver.ResolveHeaders(
                connection.HeaderTemplates,
                executionEnvironment);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.CallTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var result = await McpStreamableHttpToolClient.CallToolAsync(
                endpointUri,
                resolvedHeaders,
                backingToolName,
                arguments,
                timeout,
                timeoutCts.Token);
            return McpCallToolResultFormatter.Format(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"ERROR: MCP tool call timed out after {timeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "MCP tools/call failed. BridgeId={BridgeId}, BackingTool={BackingTool}",
                LogValueSanitizer.Sanitize(connection.BridgeId),
                LogValueSanitizer.Sanitize(backingToolName));
            return $"ERROR: MCP tool call failed: {ex.Message}";
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveExecutionEnvironmentAsync(
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
        if (db is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var guideScopeId = await ResolveGuideScopeIdAsync(db, context, cancellationToken);

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

    private static async Task<Guid> ResolveGuideScopeIdAsync(
        ApplicationDbContext db,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
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
