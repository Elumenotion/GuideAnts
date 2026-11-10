using System.Text.Json;
using Microsoft.Extensions.Logging;
using GuideAnts.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// POST /execute - runs a Python, Bash, or PowerShell script inside the caller's notebook scope.
/// </summary>
internal static class ExecutionEndpoints
{
    public static void MapExecutionEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AgentRuntimeOptions>();

        app.MapPost("/execute", async (HttpContext context, ILogger<Program> logger) =>
        {
            try
            {
                var request = await AgentRequestBody.ReadJsonAsync<ScriptExecutionRequest>(context);

                if (request is null)
                {
                    logger.LogWarning("SECURITY: /execute rejected because request JSON was missing or invalid.");
                    return AgentResponses.Text("Invalid request body", StatusCodes.Status400BadRequest);
                }

                var validationResult = ExecutionRequestValidator.ValidateExecutionRequest(request, options.ScriptConfig);
                if (!validationResult.IsValid)
                {
                    logger.LogWarning("SECURITY: /execute rejected due to invalid request. reason={Reason}", LogValueSanitizer.Sanitize(validationResult.ErrorMessage));
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
                        out var notebookRoot,
                        out var rejectionReason))
                {
                    logger.LogWarning(
                        "SECURITY: /execute rejected due to path authorization failure. projectId={ProjectId} notebookId={NotebookId} reason={Reason}",
                        projectId,
                        notebookId,
                        LogValueSanitizer.Sanitize(rejectionReason));
                    return AgentResponses.Text($"WorkingDirectory rejected: {rejectionReason}", StatusCodes.Status400BadRequest);
                }

                var guideId = ExecutionRequestValidator.ResolveGuideScopeId(request);
                var executionStopwatch = System.Diagnostics.Stopwatch.StartNew();
                AgentLogEvent.Emit("script_execution_start", new Dictionary<string, object?>
                {
                    ["projectId"] = projectId.ToString("D"),
                    ["notebookId"] = notebookId.ToString("D"),
                    ["guideId"] = guideId.ToString("D"),
                    ["scriptType"] = request.ScriptType.ToString(),
                    ["workingDirectory"] = LogValueSanitizer.Sanitize(authorizedWorkingDirectory),
                });

                var executionIdentity = await NotebookExecutionIdentityProvider.PrepareAsync(
                    projectId,
                    notebookId,
                    notebookRoot,
                    authorizedWorkingDirectory,
                    options.Security,
                    logger,
                    context.RequestAborted);

                var normalizedRequest = request with { WorkingDirectory = authorizedWorkingDirectory };
                var result = await ScriptProcessRuntime.ExecuteScriptAsync(
                    normalizedRequest,
                    options.ScriptConfig,
                    logger,
                    executionIdentity,
                    options.ScopeOptions,
                    options.AdminOptions,
                    executionStopwatch,
                    context.RequestAborted);

                return Results.Json(result, AgentJson.Default);
            }
            catch (Exception ex)
            {
                return AgentErrorResult.FromExecutionRoute("/execute", ex, logger);
            }
        })
        .RequireAuthorization(AgentAuthorizationPolicies.Agent);
    }
}
