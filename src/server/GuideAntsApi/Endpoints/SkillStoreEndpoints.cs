using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Skills;

namespace GuideAntsApi.Endpoints;

public static class SkillStoreEndpoints
{
    public static void MapSkillStoreEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/skillstore")
            .WithTags("SkillStore")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        group.MapGet("/", async (string? search, ISkillCatalogService catalog, CancellationToken ct) =>
        {
            var entries = await catalog.ListAsync(search, ct);
            return Results.Ok(entries);
        })
        .WithName("ListSkillStore")
        .Produces<IReadOnlyList<SkillCatalogEntryDto>>(StatusCodes.Status200OK);

        group.MapGet("/{name}", async (string name, int? version, ISkillCatalogService catalog, CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(name, version, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        })
        .WithName("GetSkillStoreEntry")
        .Produces<SkillCatalogDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{name}/package", async (string name, int? version, ISkillCatalogService catalog, CancellationToken ct) =>
        {
            var package = await catalog.GetPackageAsync(name, version, ct);
            return package is null ? Results.NotFound() : Results.Ok(package);
        })
        .WithName("GetSkillStorePackage")
        .Produces<SkillCatalogPackageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/publish", async (SkillPublishRequest request, ISkillCatalogService catalog, CancellationToken ct) =>
        {
            try
            {
                var result = await catalog.PublishFromAssistantAsync(request.AssistantId, request.SkillName, ct);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("PublishSkillToStore")
        .Produces<SkillPublishResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/{name}", async (string name, ISkillCatalogService catalog, CancellationToken ct) =>
        {
            var orphaned = await catalog.DeleteAsync(name, ct);
            return orphaned is null
                ? Results.NotFound()
                : Results.Ok(new { name, orphanedInstalls = orphaned.Value });
        })
        .WithName("DeleteSkillStoreEntry")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}
