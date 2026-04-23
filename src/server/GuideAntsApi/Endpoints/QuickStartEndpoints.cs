using GuideAntsApi.Models;
using GuideAntsApi.Services;

namespace GuideAntsApi.Endpoints;

public static class QuickStartEndpoints
{
    public static void MapQuickStartEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/quick-start")
            .WithTags("Quick Start")
            .WithOpenApi();

        group.MapPost("/", async (IQuickStartService service) =>
        {
            var result = await service.CreateQuickStartProjectAsync();
            return Results.Ok(result);
        })
        .WithName("CreateQuickStartProject")
        .Produces<QuickStartResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
