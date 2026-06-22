using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
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
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.SetServiceActiveProviderAsync(serviceId, request.ProviderId, cancellationToken);
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
