using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;

namespace GuideAntsApi.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/catalogs")
            .WithTags("Catalogs")
            .WithOpenApi();

        // Get all models
        group.MapGet("/models", async (ICatalogService catalogService) =>
        {
            var models = await catalogService.GetModelsAsync();
            return Results.Ok(models);
        })
        .WithName("GetModels")
        .Produces<IEnumerable<ModelDto>>(StatusCodes.Status200OK);

        // Get all tools
        group.MapGet("/tools", async (ICatalogService catalogService) =>
        {
            var tools = await catalogService.GetToolsAsync();
            return Results.Ok(tools);
        })
        .WithName("GetTools")
        .Produces<IEnumerable<ToolDto>>(StatusCodes.Status200OK);

        // Get all global assistants
        group.MapGet("/global-assistants", async (ICatalogService catalogService) =>
        {
            var assistants = await catalogService.GetGlobalAssistantsAsync();
            return Results.Ok(assistants);
        })
        .WithName("GetGlobalAssistants")
        .Produces<IEnumerable<GlobalAssistantDto>>(StatusCodes.Status200OK);

        // Get single global assistant with details
        group.MapGet("/global-assistants/{id}", async (Guid id, ICatalogService catalogService) =>
        {
            var assistant = await catalogService.GetGlobalAssistantAsync(id);
            if (assistant == null)
                return Results.NotFound();

            return Results.Ok(assistant);
        })
        .WithName("GetGlobalAssistant")
        .Produces<GlobalAssistantDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Get global assistant avatar
        group.MapGet("/global-assistants/{id}/avatar", async (Guid id, ICatalogService catalogService, HttpContext context) =>
        {
            var avatarBytes = await catalogService.GetGlobalAssistantAvatarBytesAsync(id);
            if (avatarBytes == null || avatarBytes.Length == 0)
                return Results.NotFound();

            var contentType = GuidesEndpoints.DetectImageContentType(avatarBytes);
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(avatarBytes, contentType);
        })
        .WithName("GetGlobalAssistantAvatar")
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound);
    }
}

