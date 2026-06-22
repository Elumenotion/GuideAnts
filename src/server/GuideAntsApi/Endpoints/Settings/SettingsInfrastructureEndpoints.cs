using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsInfrastructureEndpoints
{
    public static void MapSettingsInfrastructureEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/connections/{section}/usage", async (
            string section,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var usage = await settingsService.GetConnectionUsageAsync(section, cancellationToken);
                return Results.Ok(usage);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetConnectionUsage")
        .Produces<ConnectionUsageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/infrastructure/dependencies", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var dependencies = await settingsService.GetRuntimeDependenciesAsync(cancellationToken);
            return Results.Ok(dependencies);
        })
        .WithName("GetInfrastructureDependencies")
        .Produces<IReadOnlyList<SettingsRuntimeDependencyDto>>(StatusCodes.Status200OK);

        group.MapPut("/infrastructure/dependencies/{key}", async (
            string key,
            [FromBody] InfrastructureDependencyOverrideRequestDto request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.SetRuntimeDependencyOverrideAsync(
                    key,
                    request?.Value,
                    cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("PutInfrastructureDependencyOverride")
        .Accepts<InfrastructureDependencyOverrideRequestDto>("application/json")
        .Produces<SettingsRuntimeDependencyDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/infrastructure/probes", async (
            [FromBody] InfrastructureProbeRequestDto request,
            GuideAntsApi.Services.Infrastructure.IInfrastructureProbeService probeService,
            CancellationToken cancellationToken) =>
        {
            if (request?.Items is null)
            {
                return Results.BadRequest(new { error = "items is required" });
            }

            var batch = await probeService.ProbeAsync(request.Items, cancellationToken);
            return Results.Ok(batch);
        })
        .WithName("PostInfrastructureProbes")
        .Accepts<InfrastructureProbeRequestDto>("application/json")
        .Produces<InfrastructureProbeBatchDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
