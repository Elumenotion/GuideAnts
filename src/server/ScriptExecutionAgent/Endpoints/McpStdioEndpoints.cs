using Microsoft.Extensions.Logging;
using GuideAnts.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// POST /mcp-stdio and POST /mcp-stdio/discover - run and discover MCP stdio child servers.
/// </summary>
internal static class McpStdioEndpoints
{
    public static void MapMcpStdioEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AgentRuntimeOptions>();

        app.MapPost("/mcp-stdio", async (HttpContext context, ILogger<Program> logger) =>
        {
            try
            {
                var request = await AgentRequestBody.ReadJsonAsync<McpStdioExecutionRequest>(context);

                if (request is null)
                {
                    logger.LogWarning("SECURITY: /mcp-stdio rejected because request JSON was missing or invalid.");
                    return AgentResponses.Text("Invalid request body", StatusCodes.Status400BadRequest);
                }

                var validationResult = McpStdioChildRuntime.ValidateMcpStdioRequest(request);
                if (!validationResult.IsValid)
                {
                    logger.LogWarning(
                        "SECURITY: /mcp-stdio rejected due to invalid request. reason={Reason}",
                        LogValueSanitizer.Sanitize(validationResult.ErrorMessage));
                    return AgentResponses.Text($"Validation failed: {validationResult.ErrorMessage}", StatusCodes.Status400BadRequest);
                }

                var projectId = Guid.Parse(request.ProjectId);
                var notebookId = Guid.Parse(request.NotebookId);

                if (!PathGuard.TryResolveAndAuthorizePath(
                        options.FileStorageRoot,
                        request.WorkingDirectory,
                        projectId,
                        notebookId,
                        PathAccessMode.Write,
                        out var authorizedWorkingDirectory,
                        out _,
                        out var rejectionReason))
                {
                    logger.LogWarning(
                        "SECURITY: /mcp-stdio rejected due to path authorization failure. projectId={ProjectId} notebookId={NotebookId} reason={Reason}",
                        projectId,
                        notebookId,
                        LogValueSanitizer.Sanitize(rejectionReason));
                    return AgentResponses.Text($"WorkingDirectory rejected: {rejectionReason}", StatusCodes.Status400BadRequest);
                }

                logger.LogInformation(
                    "Executing MCP stdio child. projectId={ProjectId} notebookId={NotebookId} guideId={GuideId} tool={ToolName}",
                    projectId,
                    notebookId,
                    request.GuideId,
                    LogValueSanitizer.Sanitize(request.ToolName));

                var result = await McpStdioChildRuntime.ExecuteToolCallAsync(
                    request,
                    authorizedWorkingDirectory,
                    options.ScopeOptions,
                    logger,
                    context.RequestAborted);

                return Results.Json(result, AgentJson.CamelCase);
            }
            catch (Exception ex)
            {
                return AgentErrorResult.FromExecutionRoute("/mcp-stdio", ex, logger);
            }
        })
        .RequireAuthorization(AgentAuthorizationPolicies.Agent);

        app.MapPost("/mcp-stdio/discover", async (HttpContext context, ILogger<Program> logger) =>
        {
            try
            {
                var request = await AgentRequestBody.ReadJsonAsync<McpStdioDiscoverRequest>(context);

                if (request is null)
                {
                    logger.LogWarning("SECURITY: /mcp-stdio/discover rejected because request JSON was missing or invalid.");
                    return AgentResponses.Text("Invalid request body", StatusCodes.Status400BadRequest);
                }

                var validationResult = McpStdioChildRuntime.ValidateMcpStdioDiscoverRequest(request);
                if (!validationResult.IsValid)
                {
                    logger.LogWarning(
                        "SECURITY: /mcp-stdio/discover rejected due to invalid request. reason={Reason}",
                        LogValueSanitizer.Sanitize(validationResult.ErrorMessage));
                    return AgentResponses.Text($"Validation failed: {validationResult.ErrorMessage}", StatusCodes.Status400BadRequest);
                }

                var projectId = Guid.Parse(request.ProjectId);
                var guideScopeId = Guid.Parse(request.GuideId);
                var scope = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideScopeId, options.ScopeOptions);
                ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);
                var authorizedWorkingDirectory = scope.ScopeRootPath;

                logger.LogInformation(
                    "Discovering MCP stdio tools. projectId={ProjectId} guideId={GuideId}",
                    projectId,
                    guideScopeId);

                var result = await McpStdioChildRuntime.DiscoverToolsAsync(
                    request,
                    authorizedWorkingDirectory,
                    options.ScopeOptions,
                    logger,
                    context.RequestAborted);

                return Results.Json(result, AgentJson.CamelCase);
            }
            catch (Exception ex)
            {
                return AgentErrorResult.FromExecutionRoute("/mcp-stdio/discover", ex, logger);
            }
        })
        .RequireAuthorization(AgentAuthorizationPolicies.Agent);
    }
}
