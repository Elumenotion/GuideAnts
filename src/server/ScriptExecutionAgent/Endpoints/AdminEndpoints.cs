using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScriptExecutionAgent;

/// <summary>
/// /admin/* - sandbox provisioning API (requirements, apt packages, install scripts, apply jobs).
/// Registered only when SCRIPT_EXECUTION_ADMIN_API_ENABLED=true; every route requires the admin policy.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AgentRuntimeOptions>();
        var adminOptions = options.AdminOptions;
        var scopeOptions = options.ScopeOptions;
        var adminApiJsonOptions = AgentJson.Web;

        var group = app.MapGroup("/admin")
            .RequireAuthorization(AgentAuthorizationPolicies.Admin);

        group.MapGet("/health", (HttpContext context, ILogger<Program> logger) =>
            Results.Json(new
            {
                status = "OK",
                adminStateDir = adminOptions.StateDirectoryPath,
                scopeStateRoot = scopeOptions.StateRootPath
            }, adminApiJsonOptions));

        group.MapGet("/requirements", async (HttpContext context, ILogger<Program> logger) =>
        {
            var hasScope = AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error);
            if (!string.IsNullOrEmpty(error))
            {
                return AgentResponses.Text(error, StatusCodes.Status400BadRequest);
            }

            var requirementsPath = hasScope
                ? AdminStateRuntime.GetRequirementsPath(adminOptions, scope)
                : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
            return AgentResponses.Plain(File.Exists(requirementsPath)
                ? await File.ReadAllTextAsync(requirementsPath, context.RequestAborted)
                : string.Empty);
        });

        group.MapPut("/requirements", async (HttpContext context, ILogger<Program> logger) =>
        {
            string body;
            try
            {
                body = await AgentRequestBody.ReadTextAsync(context, AgentRequestBody.RequirementsBodyMaxBytes);
            }
            catch (RequestBodyTooLargeException ex)
            {
                return AgentErrorResult.FromRequestBodyTooLarge(ex, "/admin/requirements", logger);
            }

            var validation = ScriptExecutionScopeRuntime.ValidateRequirements(body);
            if (!validation.IsValid)
            {
                return AgentResponses.Text(validation.ErrorMessage, StatusCodes.Status400BadRequest);
            }

            var hasScope = AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error);
            if (!string.IsNullOrEmpty(error))
            {
                return AgentResponses.Text(error, StatusCodes.Status400BadRequest);
            }

            var requirementsPath = hasScope
                ? AdminStateRuntime.GetRequirementsPath(adminOptions, scope)
                : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
            await AtomicFile.WriteAllTextAsync(requirementsPath, body, context.RequestAborted);
            return Results.NoContent();
        });

        group.MapGet("/apt-packages", async (HttpContext context, ILogger<Program> logger) =>
        {
            var aptPackagesPath = AdminStateRuntime.GetAptPackagesPath(adminOptions);
            return AgentResponses.Plain(File.Exists(aptPackagesPath)
                ? await File.ReadAllTextAsync(aptPackagesPath, context.RequestAborted)
                : string.Empty);
        });

        group.MapPut("/apt-packages", async (HttpContext context, ILogger<Program> logger) =>
        {
            string body;
            try
            {
                body = await AgentRequestBody.ReadTextAsync(context, AgentRequestBody.AptPackagesBodyMaxBytes);
            }
            catch (RequestBodyTooLargeException ex)
            {
                return AgentErrorResult.FromRequestBodyTooLarge(ex, "/admin/apt-packages", logger);
            }

            var validation = AdminStateRuntime.ValidateAptPackages(body);
            if (!validation.IsValid)
            {
                return AgentResponses.Text(validation.ErrorMessage, StatusCodes.Status400BadRequest);
            }

            await AtomicFile.WriteAllTextAsync(AdminStateRuntime.GetAptPackagesPath(adminOptions), body, context.RequestAborted);
            return Results.NoContent();
        });

        group.MapGet("/setup-status", async (HttpContext context, ILogger<Program> logger) =>
        {
            var hasScope = AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error);
            if (!string.IsNullOrEmpty(error))
            {
                return AgentResponses.Text(error, StatusCodes.Status400BadRequest);
            }

            var status = await AdminSetupStatusRuntime.BuildAsync(
                hasScope,
                hasScope ? scope : null,
                scopeOptions,
                adminOptions,
                context.RequestAborted);
            return Results.Json(status, adminApiJsonOptions);
        });

        group.MapGet("/install-scripts", (HttpContext context, ILogger<Program> logger) =>
        {
            if (!AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error))
            {
                return AgentResponses.Text(string.IsNullOrEmpty(error)
                    ? "projectId and guideId are required for install scripts."
                    : error, StatusCodes.Status400BadRequest);
            }

            var document = AdminInstallScriptsRuntime.ReadDocument(scope);
            return Results.Json(document, adminApiJsonOptions);
        });

        group.MapPut("/install-scripts", async (HttpContext context, ILogger<Program> logger) =>
        {
            if (!AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error))
            {
                return AgentResponses.Text(string.IsNullOrEmpty(error)
                    ? "projectId and guideId are required for install scripts."
                    : error, StatusCodes.Status400BadRequest);
            }

            string body;
            try
            {
                body = await AgentRequestBody.ReadTextAsync(context, AgentRequestBody.InstallScriptsBodyMaxBytes);
            }
            catch (RequestBodyTooLargeException ex)
            {
                return AgentErrorResult.FromRequestBodyTooLarge(ex, "/admin/install-scripts", logger);
            }

            try
            {
                var document = await AdminInstallScriptsRuntime.ParseAndValidateSubmitAsync(body, context.RequestAborted);
                await AdminInstallScriptsRuntime.PersistDocumentAsync(scope, document, context.RequestAborted);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return AgentResponses.Text(ex.Message, StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/apply", async (HttpContext context, ILogger<Program> logger) =>
        {
            var hasScope = AdminScopeResolver.TryResolveScope(context, scopeOptions, out var scope, out var error);
            if (!string.IsNullOrEmpty(error))
            {
                return AgentResponses.Text(error, StatusCodes.Status400BadRequest);
            }

            try
            {
                var body = await AgentRequestBody.ReadTextAsync(context, AgentRequestBody.ApplyBodyMaxBytes);
                AdminApplyRequest? applyRequest = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    applyRequest = JsonSerializer.Deserialize<AdminApplyRequest>(body, adminApiJsonOptions);
                }

                var targets = AdminApplyTargets.Resolve(hasScope, applyRequest?.Targets);
                var accepted = await AdminApplyJobRuntime.StartApplyAsync(
                    hasScope,
                    hasScope ? scope : null,
                    targets,
                    scopeOptions,
                    adminOptions,
                    logger,
                    context.RequestAborted);

                return Results.Json(accepted, adminApiJsonOptions, statusCode: StatusCodes.Status202Accepted);
            }
            catch (RequestBodyTooLargeException ex)
            {
                return AgentErrorResult.FromRequestBodyTooLarge(ex, "/admin/apply", logger);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Admin apply preflight rejected.");
                return AgentResponses.Text(ex.Message, StatusCodes.Status400BadRequest);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return AgentResponses.Text("Apply preflight was canceled.", StatusCodes.Status499ClientClosedRequest);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Admin apply preflight timed out.");
                return AgentResponses.Text(
                    $"Apply preflight timed out after {AdminApplyJobRuntime.PreflightTimeout.TotalSeconds:0} seconds.",
                    StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin apply preflight failed.");
                return AgentResponses.Text("Failed to start sandbox admin apply.", StatusCodes.Status500InternalServerError);
            }
        });

        group.MapGet("/apply/jobs/{jobId}", (HttpContext context, string jobId, ILogger<Program> logger) =>
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return AgentResponses.Text("jobId is required.", StatusCodes.Status400BadRequest);
            }

            if (!AdminApplyJobRuntime.TryGetStatus(jobId, adminOptions, out var status) || status is null)
            {
                return AgentResponses.Text("Apply job was not found.", StatusCodes.Status404NotFound);
            }

            return Results.Json(status, adminApiJsonOptions);
        });
    }
}
