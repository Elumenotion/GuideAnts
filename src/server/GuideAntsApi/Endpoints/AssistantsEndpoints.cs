using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;

namespace GuideAntsApi.Endpoints;

public static class AssistantsEndpoints
{
    public static void MapAssistantsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/assistants")
            .WithTags("Assistants")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        // List assistants
        group.MapGet("/", async (IGuidesService guidesService) =>
        {
            var assistants = await guidesService.GetAssistantsAsync();
            return Results.Ok(assistants);
        })
        .WithName("GetAssistants")
        .Produces<IEnumerable<AssistantDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Get single assistant
        group.MapGet("/{assistantId}", async (Guid assistantId, Guid? projectId, IGuidesService guidesService) =>
        {
            var assistant = await guidesService.GetAssistantAsync(assistantId, projectId);
            if (assistant == null)
                return Results.NotFound();

            return Results.Ok(assistant);
        })
        .WithName("GetAssistant")
        .Produces<AssistantDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Create assistant
        group.MapPost("/", async (CreateAssistantDto dto, IGuidesService guidesService) =>
        {
            try
            {
                var assistant = await guidesService.CreateAssistantAsync(dto);
                return Results.Created($"/api/assistants/{assistant.Id}", assistant);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateAssistant")
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue))
        .Produces<AssistantDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Update assistant
        group.MapPut("/{assistantId}", async (Guid assistantId, UpdateAssistantDto dto, IGuidesService guidesService) =>
        {
            try
            {
                var assistant = await guidesService.UpdateAssistantAsync(assistantId, dto);
                return Results.Ok(assistant);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (NotImplementedException)
            {
                return Results.BadRequest(new { error = "Update assistant functionality is not yet fully implemented" });
            }
        })
        .WithName("UpdateAssistant")
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue))
        .Produces<AssistantDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Delete assistant
        group.MapDelete("/{assistantId}", async (Guid assistantId, IGuidesService guidesService) =>
        {
            var result = await guidesService.DeleteAssistantAsync(assistantId);
            if (!result)
                return Results.NotFound();

            return Results.NoContent();
        })
        .WithName("DeleteAssistant")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Duplicate assistant
        group.MapPost("/{assistantId}/duplicate", async (Guid assistantId, IGuidesService guidesService) =>
        {
            try
            {
                var assistant = await guidesService.DuplicateAssistantAsync(assistantId);
                return Results.Created($"/api/assistants/{assistant.Id}", assistant);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (NotImplementedException)
            {
                return Results.BadRequest(new { error = "Duplicate assistant functionality is not yet fully implemented" });
            }
        })
        .WithName("DuplicateAssistant")
        .Produces<AssistantDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Get assistant avatar
        group.MapGet("/{assistantId}/avatar", async (Guid assistantId, IGuidesService guidesService, HttpContext context) =>
        {
            var avatarBytes = await guidesService.GetAssistantAvatarBytesAsync(assistantId);
            if (avatarBytes == null || avatarBytes.Length == 0)
                return Results.NotFound();

            // Detect content type from bytes or default to PNG
            var contentType = GuidesEndpoints.DetectImageContentType(avatarBytes);
            
            // Add caching headers
            context.Response.Headers.CacheControl = "public, max-age=3600";
            
            return Results.File(avatarBytes, contentType);
        })
        .WithName("GetAssistantAvatar")
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Export assistant
        group.MapGet("/{assistantId}/export", async (Guid assistantId, IGuideExportImportService exportImportService) =>
        {
            try
            {
                var zipBytes = await exportImportService.ExportAssistantAsync(assistantId);
                var fileName = $"assistant-{assistantId}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
                return Results.File(zipBytes, "application/zip", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ExportAssistant")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Import assistant
        group.MapPost("/import", async (IFormFile file, IGuideExportImportService exportImportService) =>
        {
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "File must be a .zip archive" });

            try
            {
                using var stream = file.OpenReadStream();
                var result = await exportImportService.ImportAssistantAsync(stream);
                return Results.Ok(result);
            }
            catch (NotImplementedException)
            {
                return Results.BadRequest(new { error = "Import functionality is not yet implemented" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ImportAssistant")
        .Produces<ImportGuideResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .DisableAntiforgery();  // Required for file uploads
    }
}
