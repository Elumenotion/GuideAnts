using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsRuntimeProfilesEndpoints
{
    public static void MapSettingsRuntimeProfilesEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/runtime-profiles", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var profiles = await settingsService.GetRuntimeProfilesAsync(cancellationToken);
            return Results.Ok(profiles);
        })
        .WithName("GetSettingsRuntimeProfiles")
        .Produces<IReadOnlyList<SettingsRuntimeProfileDto>>(StatusCodes.Status200OK);

        group.MapGet("/runtime-profiles/{profileId}", async (
            string profileId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var profile = await settingsService.GetRuntimeProfileAsync(profileId, cancellationToken);
            return profile == null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/runtime-profiles", async (
            [FromBody] CreateRuntimeProfileRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await settingsService.CreateRuntimeProfileAsync(request, cancellationToken);
                return Results.Created($"/api/settings/runtime-profiles/{created.ProfileId}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapPut("/runtime-profiles/{profileId}", async (
            string profileId,
            [FromBody] UpdateRuntimeProfileRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.UpdateRuntimeProfileAsync(profileId, request, cancellationToken);
                return updated == null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/runtime-profiles/{profileId}", async (
            string profileId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var deleted = await settingsService.DeleteRuntimeProfileAsync(profileId, cancellationToken);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DbUpdateException ex)
            {
                return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
            }
        })
        .WithName("DeleteSettingsRuntimeProfile")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);
    }
}
