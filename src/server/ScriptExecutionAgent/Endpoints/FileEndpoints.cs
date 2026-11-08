using Microsoft.Extensions.Logging;
using GuideAnts.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// GET /files - lists files in an authorized notebook directory.
/// </summary>
internal static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AgentRuntimeOptions>();

        app.MapGet("/files", async (HttpContext context, ILogger<Program> logger) =>
        {
            try
            {
                var directory = context.Request.Query["directory"].ToString();
                var projectIdValue = context.Request.Query["projectId"].ToString();
                var notebookIdValue = context.Request.Query["notebookId"].ToString();

                if (string.IsNullOrWhiteSpace(directory))
                {
                    return AgentResponses.Text("directory parameter is required", StatusCodes.Status400BadRequest);
                }

                if (!Guid.TryParse(projectIdValue, out var projectId) || projectId == Guid.Empty)
                {
                    return AgentResponses.Text("projectId parameter must be a non-empty GUID", StatusCodes.Status400BadRequest);
                }

                if (!Guid.TryParse(notebookIdValue, out var notebookId) || notebookId == Guid.Empty)
                {
                    return AgentResponses.Text("notebookId parameter must be a non-empty GUID", StatusCodes.Status400BadRequest);
                }

                if (!PathGuard.TryResolveAndAuthorizePath(
                        options.FileStorageRoot,
                        directory,
                        projectId,
                        notebookId,
                        PathAccessMode.Read,
                        out var authorizedDirectory,
                        out var notebookRoot,
                        out var rejectionReason))
                {
                    logger.LogWarning(
                        "SECURITY: /files rejected due to path authorization failure. projectId={ProjectId} notebookId={NotebookId} reason={Reason}",
                        projectId,
                        notebookId,
                        LogValueSanitizer.Sanitize(rejectionReason));
                    return AgentResponses.Text($"directory rejected: {rejectionReason}", StatusCodes.Status400BadRequest);
                }

                if (!Directory.Exists(authorizedDirectory))
                {
                    return Results.Json(Array.Empty<string>(), AgentJson.Default);
                }

                var executionIdentity = await NotebookExecutionIdentityProvider.PrepareAsync(
                    projectId,
                    notebookId,
                    notebookRoot,
                    authorizedDirectory,
                    options.Security,
                    logger,
                    context.RequestAborted);

                var files = await FileListingRuntime.ListFilesAsync(
                    authorizedDirectory, executionIdentity, options.Security, logger, context.RequestAborted);
                return Results.Json(files, AgentJson.Default);
            }
            catch (Exception ex)
            {
                return AgentErrorResult.FromFileListing(ex, logger);
            }
        })
        .RequireAuthorization(AgentAuthorizationPolicies.Agent);
    }
}
