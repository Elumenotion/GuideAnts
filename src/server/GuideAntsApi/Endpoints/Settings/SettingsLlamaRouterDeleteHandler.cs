using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Configuration;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsLlamaRouterDeleteHandler
{
    public static bool TryGetLlamaRouterModelId(SettingsModelDto model, out string routerModelId)
    {
        routerModelId = string.Empty;
        if (!string.Equals(model.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(model.RuntimeConfigJson))
        {
            return false;
        }

        try
        {
            routerModelId = LocalRuntimeConfigurationParser.Parse(model.ModelId, model.RuntimeConfigJson)
                .RouterModelId
                .Trim();
            return routerModelId.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IResult> DeleteLlamaRouterEntryAsync(
        string routerModelId,
        ILlamaRuntimeInventoryService inventoryService,
        ILlamaServerRuntimeClient llamaClient,
        ILlamaRuntimeCoordinator coordinator,
        ILlamaRuntimeAdminClient adminClient,
        IApplicationSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var trimmed = (routerModelId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Results.BadRequest(new { error = "routerModelId is required" });
        }

        var inventory = await inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var row = inventory.FirstOrDefault(i =>
            string.Equals(i.RouterModelId, trimmed, StringComparison.Ordinal));

        if (row is null)
        {
            return Results.NotFound(new { error = $"No inventory entry for router alias '{trimmed}'." });
        }

        if (row.NotebookReferenceCount > 0)
        {
            return Results.Json(
                new
                {
                    error = "Cannot delete this router alias while notebooks still reference one or more catalog rows that target it.",
                    catalogModelIds = row.CatalogModelIds,
                    notebookReferenceCount = row.NotebookReferenceCount,
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var handle = coordinator.TryAcquireAliasLock(trimmed);
        if (handle is null)
        {
            var problem = new ProblemDetails
            {
                Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}runtime-not-ready",
                Title = "Local runtime busy",
                Status = StatusCodes.Status409Conflict,
                Detail = $"A load or unload operation is already in progress for alias '{trimmed}'.",
            };
            problem.Extensions["code"] = RoutingErrorCodes.RuntimeNotReady;
            problem.Extensions["action"] =
                "Wait for the in-flight operation on this alias to complete, then retry.";
            problem.Extensions["modelId"] = trimmed;
            return Results.Problem(problem);
        }

        await using var _ = handle;

        if (string.Equals(row.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.RuntimeState, "loading", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await llamaClient.UnloadModelAsync(trimmed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to unload model before delete: {ex.Message}");
            }
        }

        try
        {
            var deleted = await adminClient.DeleteRouterEntryAsync(trimmed, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                return Results.NotFound(new { error = $"Router alias '{trimmed}' is not registered in llama-admin." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            foreach (var modelId in row.CatalogModelIds)
            {
                await settingsService.DeleteModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Router alias '{trimmed}' was deleted, but one or more catalog rows could not be removed: {ex.Message}",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.NoContent();
    }
}
