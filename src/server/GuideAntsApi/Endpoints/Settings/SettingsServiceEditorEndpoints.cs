using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsServiceEditorEndpoints
{
    public static void MapSettingsServiceEditorEndpoints(this WebApplication app)
    {
        var serviceEditorsGroup = SettingsGroupFactory.MapServiceEditorsGroup(app);

        serviceEditorsGroup.MapGet("/{serviceId}", async (
            string serviceId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var state = await settingsService.GetServiceEditorStateAsync(serviceId, cancellationToken);
                return Results.Ok(state);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetServiceEditorState")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapGet("/{serviceId}/readiness", async (
            string serviceId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var readiness = await settingsService.GetServiceEditorReadinessAsync(serviceId, cancellationToken);
                return Results.Ok(readiness);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetServiceEditorReadiness")
        .Produces<ServiceEditorReadinessDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapPut("/{serviceId}/active-provider", async (
            string serviceId,
            [FromBody] SetActiveProviderRequest request,
            IApplicationSettingsService settingsService,
            ILocalServiceLoadService loadService,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                ServiceEditorStateDto? previousState = null;
                try
                {
                    previousState = await settingsService.GetServiceEditorStateAsync(serviceId, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // Let SetServiceActiveProviderAsync surface invalid service ids.
                }

                var updated = await settingsService.SetServiceActiveProviderAsync(serviceId, request.ProviderId, cancellationToken);
                var providerChanged = previousState == null
                    || !string.Equals(previousState.ActiveProviderId, request.ProviderId, StringComparison.Ordinal);

                // Config-change lifecycle, per service: if the previously active provider
                // was local, unload that service's model. If the newly active provider is
                // local, ensure that service's model is loaded. Independent per service.
                if (providerChanged && !request.DeferWarmup)
                {
                    var localSection = LocalServiceModeSelectionReader.ResolveLocalProviderSection(serviceId);
                    var oldWasLocal = localSection is not null
                        && previousState is not null
                        && string.Equals(previousState.ActiveProviderId, localSection, StringComparison.Ordinal);
                    var newIsLocal = localSection is not null
                        && string.Equals(request.ProviderId, localSection, StringComparison.Ordinal);

                    var logger = loggerFactory.CreateLogger("ServiceModesRuntimeReload");
                    if (oldWasLocal)
                    {
                        try
                        {
                            var unloadResult = await loadService.UnloadServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
                            if (!unloadResult.Success)
                            {
                                logger.LogWarning(
                                    "Failed to unload {ServiceId} after provider changed away from local: {Error}",
                                    serviceId, unloadResult.Error);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to unload {ServiceId} after provider changed away from local.", serviceId);
                        }
                    }

                    if (newIsLocal)
                    {
                        try
                        {
                            var ensureResult = await loadService.EnsureLoadedAsync(serviceId, cancellationToken).ConfigureAwait(false);
                            if (!ensureResult.Success)
                            {
                                logger.LogWarning(
                                    "Failed to ensure {ServiceId} loaded after provider changed to local: {Error}",
                                    serviceId, ensureResult.Error);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to ensure {ServiceId} loaded after provider changed to local.", serviceId);
                        }
                    }
                }

                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("SetServiceActiveProvider")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapPut("/{serviceId}/providers/{providerId}", async (
            string serviceId,
            string providerId,
            [FromBody] ProviderFieldsUpdateRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.UpdateServiceProviderFieldsAsync(
                    serviceId,
                    providerId,
                    request,
                    cancellationToken);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateServiceProviderFields")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
