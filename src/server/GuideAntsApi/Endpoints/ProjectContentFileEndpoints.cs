using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class ProjectContentFileEndpoints
{
    public static void MapProjectContentFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/files")
            .WithTags("Project Content Files")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Get all content files for a project
        group.MapGet("/", async (Guid projectId, IContentFileService contentFileService) =>
        {
            var files = await contentFileService.GetAllForProjectAsync(projectId);
            return Results.Ok(files);
        })
        .WithName("GetProjectContentFiles")
        .Produces<IEnumerable<ContentFileDetailsDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get a specific content file
        group.MapGet("/{fileId}", async (Guid projectId, Guid fileId, IContentFileService contentFileService) =>
        {
            var file = await contentFileService.GetAsync(projectId, fileId);
            if (file == null)
                return Results.NotFound();

            return Results.Ok(file);
        })
        .WithName("GetProjectContentFile")
        .Produces<ContentFileDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get content file data
        group.MapGet("/{fileId}/content", async (Guid projectId, Guid fileId, IContentFileService contentFileService) =>
        {
            var content = await contentFileService.GetContentAsync(projectId, fileId);
            if (content == null)
                return Results.NotFound();

            return Results.File(content.Content, content.ContentType, content.FileName);
        })
        .WithName("GetProjectContentFileContent")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Upload a file
        group.MapPost("/", async (
            Guid projectId,
            HttpContext context,
            IContentFileService contentFileService) =>
        {
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            
            if (file == null || file.Length == 0)
                return Results.BadRequest("No file was uploaded.");

            // Extract optional parameters from form data
            bool index = form.ContainsKey("index") && bool.TryParse(form["index"], out var indexValue) && indexValue;
            Guid? folderId = form.ContainsKey("folderId") && Guid.TryParse(form["folderId"], out var folderIdValue) ? folderIdValue : null;

            try
            {
                var uploadedFile = await contentFileService.UploadFileAsync(projectId, file, index, folderId);
                return Results.Created($"/api/projects/{projectId}/files/{uploadedFile.Id}", uploadedFile);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UploadProjectFile")
        .RequireAuthorization("RequireContributor")
        .Accepts<UploadFileForm>("multipart/form-data")
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue), new DisableRequestSizeLimitAttribute())
        .Produces<ContentFileDetailsDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status402PaymentRequired);

        // Update a content file
        group.MapPatch("/{fileId}", async (
            Guid projectId,
            Guid fileId,
            UpdateContentFileDto updates,
            IContentFileService contentFileService) =>
        {
            try
            {
                var file = await contentFileService.UpdateAsync(projectId, fileId, updates);
                if (file == null)
                    return Results.NotFound();

                return Results.Ok(file);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("UpdateProjectContentFile")
        .RequireAuthorization("RequireContributor")
        .Produces<ContentFileDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Delete a content file
        group.MapDelete("/{fileId}", async (Guid projectId, Guid fileId, IContentFileService contentFileService) =>
        {
            try
            {
                var result = await contentFileService.DeleteAsync(projectId, fileId);
                if (!result)
                    return Results.NotFound();

                return Results.NoContent();
            }
            catch (FileInUseException ex)
            {
                var errorResponse = new FileInUseErrorDto(ex.Message, ex.NotebooksUsingFile);
                return Results.BadRequest(errorResponse);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("DeleteProjectContentFile")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<FileInUseErrorDto>(StatusCodes.Status400BadRequest);

        // Move a content file
        group.MapPatch("/{fileId}/move", async (
            Guid projectId,
            Guid fileId,
            MoveFileDto dto,
            IContentFileService contentFileService) =>
        {
            try
            {
                var result = await contentFileService.MoveFileAsync(projectId, fileId, dto.DestinationFolderId);
                if (result == null)
                    return Results.NotFound();

                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("MoveProjectContentFile")
        .RequireAuthorization("RequireContributor")
        .Produces<ContentFileDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Rename a content file
        group.MapPatch("/{fileId}/rename", async (
            Guid projectId,
            Guid fileId,
            RenameFileDto dto,
            IContentFileService contentFileService) =>
        {
            try
            {
                var updates = new UpdateContentFileDto { FileName = dto.NewName };
                var result = await contentFileService.UpdateAsync(projectId, fileId, updates);
                if (result == null)
                    return Results.NotFound();

                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("RenameProjectContentFile")
        .RequireAuthorization("RequireContributor")
        .Produces<ContentFileDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // --- Versioning Endpoints ---
        var versionGroup = group.MapGroup("/{fileId}/versions");

        versionGroup.MapGet("/", async (Guid projectId, Guid fileId, IContentFileService contentFileService) =>
        {
            var versions = await contentFileService.GetVersionsAsync(projectId, fileId);
            return Results.Ok(versions);
        })
        .WithName("GetProjectContentFileVersions")
        .Produces<IEnumerable<ContentFileVersionDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        versionGroup.MapGet("/{versionNumber}/content", async (Guid projectId, Guid fileId, int versionNumber, IContentFileService contentFileService) =>
        {
            var content = await contentFileService.GetVersionContentAsync(projectId, fileId, versionNumber);
            if (content == null)
                return Results.NotFound();

            return Results.File(content.Content, content.ContentType, content.FileName);
        })
        .WithName("GetProjectContentFileVersionContent")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // ===== File History =====
        group.MapGet("/{fileId}/history", async (
            Guid projectId,
            Guid fileId,
            ApplicationDbContext db) =>
        {

var events = await db.FileLineageEvents
                .Where(ev => ev.ProjectId == projectId && ev.FileId == fileId)
                .OrderByDescending(ev => ev.Timestamp)
                .ToListAsync();

            var dtoList = new List<FileLineageEventDto>();
            foreach (var ev in events)
            {
                dtoList.Add(await ToDto(db, ev));
            }

            return Results.Ok(dtoList);
        })
        .WithName("GetProjectFileHistory")
        .Produces<IEnumerable<FileLineageEventDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Local helper for DTO conversion (reuse same logic)
        static async Task<FileLineageEventDto> ToDto(ApplicationDbContext db, FileLineageEvent ev)
        {
            string fileName = ev.FileKind == FileKind.Project ?
                (await db.ContentFiles.Where(cf => cf.Id == ev.FileId).Select(cf => cf.FileName).FirstOrDefaultAsync()) ?? string.Empty :
                (await db.NotebookFiles.Where(nf => nf.Id == ev.FileId).Select(nf => nf.RelativePath).FirstOrDefaultAsync()) ?? string.Empty;

            var userDisplayName = await db.Users
                .Where(u => u.Email == ev.UserId || u.Id.ToString() == ev.UserId)
                .Select(u => u.Name)
                .FirstOrDefaultAsync() ?? ev.UserId;

            // Resolve notebook name when NotebookId is present
            string? notebookName = null;
            if (ev.NotebookId.HasValue)
            {
                notebookName = await db.Notebooks
                    .Where(n => n.Id == ev.NotebookId.Value)
                    .Select(n => n.Title)
                    .FirstOrDefaultAsync();
            }

            var versionLabel = ev.VersionNumber.HasValue ? $"v{ev.VersionNumber}" : "–";

            return new FileLineageEventDto(
                ev.Id,
                ev.Action,
                ev.FileKind,
                ev.FileId,
                ev.VersionNumber,
                ev.ProjectId,
                ev.NotebookId,
                ev.StoragePath,
                ev.Timestamp,
                ev.UserId,
                userDisplayName,
                fileName,
                versionLabel,
                notebookName);
        }
    }

    // Helper type used only to enrich the OpenAPI metadata so Swagger UI can render a file-picker.
    // This does NOT change the runtime contract because the handler still manually reads the file from HttpContext.
    private sealed record UploadFileForm(IFormFile File, bool? Index, Guid? FolderId);
} 