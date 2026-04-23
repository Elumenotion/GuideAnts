using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using IMarkdownExtractionService = GuideAntsApi.Services.Components.MarkdownExtractionService;

namespace GuideAntsApi.Endpoints;

public static class GuidesMarkdownEndpoints
{
    public static void MapGuidesMarkdownEndpoints(this IEndpointRouteBuilder app)
    {
        // File download endpoint
        var fileGroup = app.MapGroup("/api/assistants/{assistantId}/files/{fileId}")
            .WithTags("Guides");

        fileGroup.MapGet("/download", async (
            Guid assistantId,
            Guid fileId,
            [FromServices] ApplicationDbContext dbContext) =>
        {
            var file = await dbContext.AssistantFiles
                .Include(f => f.Assistant)
                .Where(f => f.Id == fileId && f.AssistantId == assistantId)
                .FirstOrDefaultAsync();

            if (file == null || file.ContentBytes == null)
            {
                return Results.NotFound(new { message = "File not found" });
            }

            var stream = new MemoryStream(file.ContentBytes);
            var contentType = file.ContentType ?? "application/octet-stream";
            var fileName = file.RelativePath;

            return Results.File(stream, contentType, fileName);
        })
        .WithName("DownloadAssistantFile")
        .Produces(200)
        .Produces(404);

        var group = app.MapGroup("/api/assistants/{assistantId}/files/{fileId}/markdown")
            .WithTags("Guides");

        // Get markdown shadow status
        group.MapGet("", async (
            Guid assistantId,
            Guid fileId,
            [FromServices] IMarkdownExtractionService markdownService) =>
        {
            var shadow = await markdownService.GetAssistantFileMarkdownShadowAsync(fileId);
            if (shadow == null)
            {
                return Results.NotFound(new { message = "Markdown shadow not found" });
            }

            var dto = new AssistantFileMarkdownShadowDto(
                shadow.Id,
                shadow.OriginalAssistantFileId,
                shadow.ContentHash,
                shadow.FileSize,
                shadow.Status,
                shadow.ErrorMessage,
                shadow.Created,
                shadow.ProcessedAt
            );

            return Results.Ok(dto);
        })
        .WithName("GetAssistantFileMarkdownShadow")
        .Produces<AssistantFileMarkdownShadowDto>()
        .Produces(404);

        // Get markdown content
        group.MapGet("/content", async (
            Guid assistantId,
            Guid fileId,
            [FromServices] IMarkdownExtractionService markdownService) =>
        {
            var result = await markdownService.GetAssistantFileMarkdownContentAsync(fileId);
            if (result == null)
            {
                return Results.NotFound(new { message = "Markdown content not available" });
            }

            var (stream, contentType, fileName) = result.Value;
            if (stream == null)
            {
                return Results.NotFound(new { message = "Markdown content stream not available" });
            }
            
            return Results.File(stream, contentType, fileName);
        })
        .WithName("GetAssistantFileMarkdownContent")
        .Produces(200, contentType: "text/markdown")
        .Produces(404);

        // Retry failed extraction
        group.MapPost("/retry", async (
            Guid assistantId,
            Guid fileId,
            [FromServices] IMarkdownExtractionService markdownService) =>
        {
            try
            {
                var shadow = await markdownService.RetryAssistantFileExtractionAsync(fileId);

                var dto = new AssistantFileMarkdownShadowDto(
                    shadow.Id,
                    shadow.OriginalAssistantFileId,
                    shadow.ContentHash,
                    shadow.FileSize,
                    shadow.Status,
                    shadow.ErrorMessage,
                    shadow.Created,
                    shadow.ProcessedAt
                );

                return Results.Ok(dto);
            }
            catch (ArgumentException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("RetryAssistantFileMarkdownExtraction")
        .Produces<AssistantFileMarkdownShadowDto>()
        .Produces(400)
        .Produces(404);
    }
}
