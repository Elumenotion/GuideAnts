using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public class CreateLinkDto
{
    public string Url { get; set; } = string.Empty;
}

public class UpdateLinkDto
{
    public string Url { get; set; } = string.Empty;
}

public static class LinkEndpoints
{
    public static void MapLinkEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/links")
            .WithTags("Links")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Create a new link
        group.MapPost("/", async (Guid projectId, CreateLinkDto createLinkDto, ILinkService linkService) =>
        {
            var link = await linkService.CreateAsync(projectId, createLinkDto.Url);
            return Results.Created($"/api/projects/{projectId}/links/{link.Id}", link);
        })
        .WithName("CreateLink")
        .RequireAuthorization("RequireContributor")
        .Produces<LinkDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status402PaymentRequired)
        .Produces(StatusCodes.Status404NotFound);

        // Update a link
        group.MapPut("/{linkId}", async (Guid projectId, Guid linkId, UpdateLinkDto updateLinkDto, ILinkService linkService) =>
        {
            var link = await linkService.UpdateAsync(projectId, linkId, updateLinkDto.Url);
            if (link == null)
                return Results.NotFound();

            return Results.Ok(link);
        })
        .WithName("UpdateLink")
        .RequireAuthorization("RequireContributor")
        .Produces<LinkDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Delete a link
        group.MapDelete("/{linkId}", async (Guid projectId, Guid linkId, ILinkService linkService) =>
        {
            var result = await linkService.DeleteAsync(projectId, linkId);
            if (!result)
                return Results.NotFound();

            return Results.NoContent();
        })
        .WithName("DeleteLink")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get a specific link
        group.MapGet("/{linkId}", async (Guid projectId, Guid linkId, ILinkService linkService) =>
        {
            var link = await linkService.GetAsync(projectId, linkId);
            if (link == null)
                return Results.NotFound();

            return Results.Ok(link);
        })
        .WithName("GetLink")
        .Produces<LinkDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get all links for a project
        group.MapGet("/", async (Guid projectId, ILinkService linkService) =>
        {
            var links = await linkService.GetAllForProjectAsync(projectId);
            return Results.Ok(links);
        })
        .WithName("GetProjectLinks")
        .Produces<IEnumerable<LinkDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }
} 