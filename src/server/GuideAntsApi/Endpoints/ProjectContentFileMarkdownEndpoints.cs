using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class ProjectContentFileMarkdownEndpoints
{
    public static void MapProjectContentFileMarkdownEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/files")
            .WithTags("Project Content File Markdown")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Get markdown shadow for latest version of a file
        group.MapGet("/{fileId}/markdown", async (Guid projectId, Guid fileId, IContentFileService contentFileService, IMarkdownExtractionService markdownService) =>
        {
            // Get the file first to determine the latest version
            var file = await contentFileService.GetAsync(projectId, fileId);
            if (file == null)
                return Results.NotFound();

            // Get the latest version to find its version record
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            var latestVersion = versions.FirstOrDefault();
            if (latestVersion == null)
                return Results.NotFound();

            var shadow = await markdownService.GetMarkdownShadowAsync(latestVersion.Id);
            if (shadow == null)
                return Results.NotFound("No markdown version available for this file");

            var shadowDto = new ContentFileMarkdownShadowDto(
                shadow.Id,
                shadow.OriginalContentFileVersionId,
                shadow.ContentHash,
                shadow.FileSize,
                shadow.Status,
                shadow.ErrorMessage,
                shadow.Created,
                shadow.ProcessedAt
            );

            return Results.Ok(shadowDto);
        })
        .WithName("GetProjectContentFileMarkdown")
        .Produces<ContentFileMarkdownShadowDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get markdown shadow for specific version of a file
        group.MapGet("/{fileId}/versions/{versionNumber}/markdown", async (Guid projectId, Guid fileId, int versionNumber, IContentFileService contentFileService, IMarkdownExtractionService markdownService) =>
        {
            // Get the specific version
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            var version = versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
            if (version == null)
                return Results.NotFound();

            var shadow = await markdownService.GetMarkdownShadowAsync(version.Id);
            if (shadow == null)
                return Results.NotFound("No markdown version available for this file version");

            var shadowDto = new ContentFileMarkdownShadowDto(
                shadow.Id,
                shadow.OriginalContentFileVersionId,
                shadow.ContentHash,
                shadow.FileSize,
                shadow.Status,
                shadow.ErrorMessage,
                shadow.Created,
                shadow.ProcessedAt
            );

            return Results.Ok(shadowDto);
        })
        .WithName("GetProjectContentFileVersionMarkdown")
        .Produces<ContentFileMarkdownShadowDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get markdown content for latest version of a file
        group.MapGet("/{fileId}/markdown/content", async (Guid projectId, Guid fileId, IContentFileService contentFileService, IMarkdownExtractionService markdownService) =>
        {
            // Get the file first to determine the latest version
            var file = await contentFileService.GetAsync(projectId, fileId);
            if (file == null)
                return Results.NotFound();

            // Get the latest version to find its version record
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            var latestVersion = versions.FirstOrDefault();
            if (latestVersion == null)
                return Results.NotFound();

            var contentResult = await markdownService.GetMarkdownContentAsync(latestVersion.Id);
            if (!contentResult.HasValue)
                return Results.NotFound("No markdown content available for this file");

            var result = contentResult.Value;
            var content = result.Content;
            var contentType = result.ContentType;
            var fileName = result.FileName;
            if (content == null || contentType == null || fileName == null)
                return Results.NotFound("No markdown content available for this file");
                
            // After null checks above, we know these are not null
            return Results.File(content!, contentType!, fileName!);
        })
        .WithName("GetProjectContentFileMarkdownContent")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get markdown content for specific version of a file
        group.MapGet("/{fileId}/versions/{versionNumber}/markdown/content", async (Guid projectId, Guid fileId, int versionNumber, IContentFileService contentFileService, IMarkdownExtractionService markdownService) =>
        {
            // Get the specific version
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            var version = versions.FirstOrDefault(v => v.VersionNumber == versionNumber);
            if (version == null)
                return Results.NotFound();

            var contentResult = await markdownService.GetMarkdownContentAsync(version.Id);
            if (!contentResult.HasValue)
                return Results.NotFound("No markdown content available for this file version");

            var result = contentResult.Value;
            var content = result.Content;
            var contentType = result.ContentType;
            var fileName = result.FileName;
            if (content == null || contentType == null || fileName == null)
                return Results.NotFound("No markdown content available for this file version");
                
            // After null checks above, we know these are not null
            return Results.File(content!, contentType!, fileName!);
        })
        .WithName("GetProjectContentFileVersionMarkdownContent")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Retry failed markdown extraction for latest version
        group.MapPost("/{fileId}/markdown/retry", async (Guid projectId, Guid fileId, IContentFileService contentFileService, IMarkdownExtractionService markdownService) =>
        {
            // Get the file first to determine the latest version
            var file = await contentFileService.GetAsync(projectId, fileId);
            if (file == null)
                return Results.NotFound();

            // Get the latest version to find its version record
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            var latestVersion = versions.FirstOrDefault();
            if (latestVersion == null)
                return Results.NotFound();

            try
            {
                var shadow = await markdownService.RetryExtractionAsync(latestVersion.Id);
                var shadowDto = new ContentFileMarkdownShadowDto(
                    shadow.Id,
                    shadow.OriginalContentFileVersionId,
                    shadow.ContentHash,
                    shadow.FileSize,
                    shadow.Status,
                    shadow.ErrorMessage,
                    shadow.Created,
                    shadow.ProcessedAt
                );

                return Results.Ok(shadowDto);
            }
            catch (ArgumentException)
            {
                return Results.NotFound("No markdown shadow found for this file");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("RetryProjectContentFileMarkdownExtraction")
        .RequireAuthorization("RequireContributor")
        .Produces<ContentFileMarkdownShadowDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);
    }
} 