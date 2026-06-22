using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class NotebookFileMarkdownEndpoints
{
    public static void MapNotebookFileMarkdownEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/notebooks/{notebookId}/files")
            .WithTags("Notebook File Markdown")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Get markdown shadow info for a notebook file
        group.MapGet("/{fileId}/markdown", async (Guid projectId, Guid notebookId, Guid fileId, INotebookFileService notebookFileService, IMarkdownExtractionService markdownService) =>
        {
            // Verify access

// Verify file exists in this notebook
            var file = await ((NotebookFileService)notebookFileService).GetNotebookFile(fileId, notebookId);
            if (file == null)
                return Results.NotFound("Notebook file not found");

            var shadow = await markdownService.GetNotebookMarkdownShadowAsync(fileId);
            if (shadow == null)
                return Results.NotFound("No markdown shadow found for this file");

            var dto = new NotebookFileMarkdownShadowDto(
                shadow.Id,
                shadow.OriginalNotebookFileId,
                shadow.ContentHash,
                shadow.FileSize,
                shadow.Status,
                shadow.ErrorMessage,
                shadow.Created,
                shadow.ProcessedAt
            );

            return Results.Ok(dto);
        })
        .WithName("GetNotebookFileMarkdownShadow")
        .WithSummary("Get markdown shadow information for a notebook file");

        // Get markdown content for a notebook file
        group.MapGet("/{fileId}/markdown/content", async (Guid projectId, Guid notebookId, Guid fileId, INotebookFileService notebookFileService, IMarkdownExtractionService markdownService) =>
        {
            // Verify access

// Verify file exists in this notebook
            var file = await ((NotebookFileService)notebookFileService).GetNotebookFile(fileId, notebookId);
            if (file == null)
                return Results.NotFound("Notebook file not found");

            var contentResult = await markdownService.GetNotebookMarkdownContentAsync(fileId);
            if (!contentResult.HasValue)
                return Results.NotFound("No markdown content available for this file");

            var result = contentResult.Value;
            var content = result.Content;
            var contentType = result.ContentType;
            var fileName = result.FileName;
            if (content == null || contentType == null || fileName == null)
                return Results.NotFound("No markdown content available for this file");
                
            return Results.File(content, contentType, fileName);
        })
        .WithName("GetNotebookFileMarkdownContent")
        .WithSummary("Download the markdown content for a notebook file");

        // Retry markdown extraction for a notebook file
        group.MapPost("/{fileId}/markdown/retry", async (Guid projectId, Guid notebookId, Guid fileId, INotebookFileService notebookFileService, IMarkdownExtractionService markdownService) =>
        {
            // Verify access

// Verify file exists in this notebook
            var file = await ((NotebookFileService)notebookFileService).GetNotebookFile(fileId, notebookId);
            if (file == null)
                return Results.NotFound("Notebook file not found");

            try
            {
                var shadow = await markdownService.RetryNotebookExtractionAsync(fileId);
                
                var dto = new NotebookFileMarkdownShadowDto(
                    shadow.Id,
                    shadow.OriginalNotebookFileId,
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
                return Results.NotFound(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("RetryNotebookFileMarkdownExtraction")
        .RequireAuthorization("RequireContributor")
        .WithSummary("Retry markdown extraction for a notebook file");

        // Create markdown shadow for a notebook file (manual trigger)
        group.MapPost("/{fileId}/markdown", async (Guid projectId, Guid notebookId, Guid fileId, INotebookFileService notebookFileService, IMarkdownExtractionService markdownService) =>
        {
            // Verify access

// Verify file exists in this notebook
            var file = await ((NotebookFileService)notebookFileService).GetNotebookFile(fileId, notebookId);
            if (file == null)
                return Results.NotFound("Notebook file not found");

            try
            {
                var shadow = await markdownService.CreateNotebookMarkdownShadowAsync(fileId);
                
                var dto = new NotebookFileMarkdownShadowDto(
                    shadow.Id,
                    shadow.OriginalNotebookFileId,
                    shadow.ContentHash,
                    shadow.FileSize,
                    shadow.Status,
                    shadow.ErrorMessage,
                    shadow.Created,
                    shadow.ProcessedAt
                );

                return Results.Created($"/api/projects/{projectId}/notebooks/{notebookId}/files/{fileId}/markdown", dto);
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("CreateNotebookFileMarkdownShadow")
        .RequireAuthorization("RequireContributor")
        .WithSummary("Create/queue markdown extraction for a notebook file");

        // Health check endpoint removed: BackgroundJobs manages leases/cleanup
    }
} 