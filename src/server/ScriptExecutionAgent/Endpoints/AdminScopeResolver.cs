using Microsoft.AspNetCore.Http;

namespace ScriptExecutionAgent;

/// <summary>
/// Resolves the optional projectId/guideId query pair into a scoped execution context.
/// </summary>
internal static class AdminScopeResolver
{
    internal static bool TryResolveScope(
        HttpContext context,
        ScriptExecutionScopeOptions scopeOptions,
        out ScriptExecutionScope scope,
        out string error)
    {
        scope = default!;
        error = string.Empty;

        var projectIdValue = context.Request.Query["projectId"].ToString();
        var guideIdValue = context.Request.Query["guideId"].ToString();
        if (string.IsNullOrWhiteSpace(projectIdValue) && string.IsNullOrWhiteSpace(guideIdValue))
        {
            return false;
        }

        if (!Guid.TryParse(projectIdValue, out var projectId) || projectId == Guid.Empty)
        {
            error = "projectId query parameter must be a non-empty GUID when scope is provided.";
            return false;
        }

        if (!Guid.TryParse(guideIdValue, out var guideId) || guideId == Guid.Empty)
        {
            error = "guideId query parameter must be a non-empty GUID when scope is provided.";
            return false;
        }

        scope = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideId, scopeOptions);
        ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);
        return true;
    }
}
