using GuideAnts.Logging;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using GuideAntsApi.Services;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Endpoints;

public class CreateNotebookDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? GuideId { get; set; }
}

public class UpdateNotebookDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CopyFileIntoNotebookDto
{
    public Guid ContentFileId { get; set; }
    public int? VersionNumber { get; set; }
    public string? TargetRelativePath { get; set; }
}

public class PublishNotebookFileDto
{
    [Required]
    public Guid NotebookFileId { get; set; }
    public Guid? DestinationFolderId { get; set; }
}

public class PublishNotebookFileResultDto
{
    public string ContentFileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public bool IsNewFile { get; set; }
    public string RelativePath { get; set; } = string.Empty;
}

public class OriginFileInfoDto
{
    public string FileName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public Guid ContentFileId { get; set; }
    public int VersionNumber { get; set; }
}

public class RenameByIdDto
{
    public string NewName { get; set; } = string.Empty;
}

public class MoveByIdDto
{
    public string? DestinationPath { get; set; }
}

public static class NotebookEndpoints
{
    public static void MapNotebookEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/notebooks")
            .WithTags("Notebooks")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Create a new notebook
        group.MapPost("/", async ([FromRoute] Guid projectId, CreateNotebookDto createNotebookDto, INotebookService notebookService) =>
        {
            var notebook = await notebookService.CreateAsync(projectId, createNotebookDto);
            return Results.Created($"/api/projects/{projectId}/notebooks/{notebook.Id}", notebook);
        })
        .WithName("CreateNotebook")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status402PaymentRequired)
        .Produces(StatusCodes.Status404NotFound);

        // Update a notebook
        group.MapPut("/{notebookId}", async ([FromRoute] Guid projectId, [FromRoute] Guid notebookId, UpdateNotebookDto updateNotebookDto, INotebookService notebookService) =>
        {
            var notebook = await notebookService.UpdateAsync(projectId, notebookId, updateNotebookDto);
            if (notebook == null)
                return Results.NotFound();

            return Results.Ok(notebook);
        })
        .WithName("UpdateNotebook")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Delete a notebook
        group.MapDelete("/{notebookId}", async ([FromRoute] Guid projectId, [FromRoute] Guid notebookId, INotebookService notebookService) =>
        {
            var result = await notebookService.DeleteAsync(projectId, notebookId);
            if (!result)
                return Results.NotFound();

            return Results.NoContent();
        })
        .WithName("DeleteNotebook")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get a specific notebook
        group.MapGet("/{notebookId}", async ([FromRoute] Guid projectId, [FromRoute] Guid notebookId, INotebookService notebookService) =>
        {
            var notebook = await notebookService.GetAsync(projectId, notebookId);
            if (notebook == null)
                return Results.NotFound();

            return Results.Ok(notebook);
        })
        .WithName("GetNotebook")
        .Produces<NotebookDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get all notebooks for a project
        group.MapGet("/", async ([FromRoute] Guid projectId, INotebookService notebookService) =>
        {
            var notebooks = await notebookService.GetAllForProjectAsync(projectId);
            return Results.Ok(notebooks);
        })
        .WithName("GetProjectNotebooks")
        .Produces<IEnumerable<NotebookDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Set notebook home page file
        group.MapPost("/{notebookId}/homepage/{fileId}", async ([FromRoute] Guid projectId, [FromRoute] Guid notebookId, [FromRoute] Guid fileId, INotebookService notebookService) =>
        {
            var ok = await notebookService.SetHomePageFileAsync(projectId, notebookId, fileId);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("SetNotebookHomePageFile")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Clear notebook home page
        group.MapDelete("/{notebookId}/homepage", async ([FromRoute] Guid projectId, [FromRoute] Guid notebookId, INotebookService notebookService) =>
        {
            var ok = await notebookService.ClearHomePageAsync(projectId, notebookId);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("ClearNotebookHomePage")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/copy", async ([FromRoute] Guid projectId, CopyNotebookDto copyDto, INotebookCopyService svc) =>
        {
            var nb = await svc.CopyAsync(projectId, copyDto);
            return Results.Created($"/api/projects/{projectId}/notebooks/{nb.Id}", nb);
        })
        .WithName("CopyNotebook")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // --- Notebook Files (Snapshots) ---
        var fileGroup = group.MapGroup("/{notebookId}/files")
            .RequireAuthorization("RequireApprovedUser");

        fileGroup.MapGet("/", async (
            Guid projectId,
            Guid notebookId,
            INotebookFileService fsService) =>
        {
            var files = await fsService.ListFilesAsync(projectId, notebookId);
            return Results.Ok(files);
        })
        .WithName("ListNotebookFiles")
        .Produces<IEnumerable<NotebookFileDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        fileGroup.MapGet("/tree", async (
            Guid projectId,
            Guid notebookId,
            INotebookFileService fsService) =>
        {
            var tree = await fsService.GetFolderTreeAsync(projectId, notebookId);
            return Results.Ok(tree);
        })
        .WithName("GetNotebookFolderTree")
        .Produces<NotebookFolderTreeDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        fileGroup.MapPost("/sync", async (
            Guid projectId,
            Guid notebookId,
            INotebookFileSyncService syncService,
            GuideAntsApi.BackgroundJobs.IJobQueueService jobQueue) =>
        {

await jobQueue.EnqueueAsync(
                jobType: nameof(GuideAntsApi.BackgroundJobs.Jobs.SyncNotebookJob).Replace("Job", string.Empty),
                payload: new GuideAntsApi.BackgroundJobs.Jobs.SyncNotebookJob(notebookId));
            return Results.NoContent();
        })
        .WithName("SyncNotebookFiles")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        fileGroup.MapGet("/content", async (Guid projectId, Guid notebookId, string path, INotebookFileService service) =>
        {

try
            {
                var result = await service.GetFileContentStreamAsync(projectId, notebookId, path);
                return Results.Stream(result.stream, result.contentType);
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetNotebookFileContent")
        .Produces<IResult>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPost("/copy-from-project", async (
            Guid projectId,
            Guid notebookId,
            CopyFileIntoNotebookDto dto,
            INotebookFileService fsService) =>
        {
            try
            {
                var result = await fsService.CopyFromProjectAsync(projectId, notebookId, dto.ContentFileId, dto.VersionNumber, dto.TargetRelativePath);
                if (result == null) return Results.NotFound(new { message = "File not found or access denied." });
                return Results.Created($"/api/projects/{projectId}/notebooks/{notebookId}/files/content?path={Uri.EscapeDataString(result.RelativePath)}", result);
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (ArgumentException ex) { return Results.NotFound(new { message = ex.Message }); }
        })
        .WithName("CopyFileFromProject")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookFileDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPost("/upload", async (
            Guid projectId,
            Guid notebookId,
            HttpContext ctx,
            INotebookFileService fsService) =>
        {
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
            {
                return Results.BadRequest("No files were provided for upload.");
            }

            var targetRelativePath = ctx.Request.Form["targetRelativePath"].ToString();
            var index = ctx.Request.Form.ContainsKey("index") && bool.TryParse(ctx.Request.Form["index"], out var indexValue) && indexValue;
            var files = ctx.Request.Form.Files;

            try
            {
                var result = await fsService.UploadFilesAsync(projectId, notebookId, files, targetRelativePath, index);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (ArgumentException ex) { return Results.NotFound(new { message = ex.Message }); }
            catch (DbUpdateException dbEx)
            {
                // Most common cause is unique constraint violation (duplicate file name)
                return Results.Conflict(new { message = "A file with the same name already exists in this location.", detail = dbEx.Message });
            }
        })
        .WithName("UploadNotebookFiles")
        .RequireAuthorization("RequireContributor")
        .DisableAntiforgery()
        .Produces<IEnumerable<NotebookFileDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Accepts<IFormFileCollection>("multipart/form-data");



        fileGroup.MapPost("/create-folder", async (
            Guid projectId,
            Guid notebookId,
            NotebookCreateFolderDto dto,
            INotebookFileService fsService) =>
        {
            try
            {
                var folderTree = await fsService.CreateFolderAsync(projectId, notebookId, dto.Path);
                if (folderTree == null) return Results.Conflict(new { message = "A folder or file with that name already exists." });
                return Results.Created($"/api/projects/{projectId}/notebooks/{notebookId}/files/tree", folderTree);
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (ArgumentException ex) { return Results.NotFound(new { message = ex.Message }); }
        })
        .WithName("CreateNotebookFolder")
        .RequireAuthorization("RequireContributor")
        .Produces<NotebookFolderTreeDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        // By-ID file operations (no tree lookup required on client)
        fileGroup.MapDelete("/{fileId:guid}", async (
            Guid projectId,
            Guid notebookId,
            Guid fileId,
            INotebookFileService fsService) =>
        {
            try
            {
                var success = await fsService.DeleteByIdAsync(projectId, notebookId, fileId);
                if (!success) return Results.NotFound(new { message = "The specified file was not found." });
                return Results.NoContent();
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message?.Contains("FK_ContentFileVersions_NotebookFiles_OriginNotebookFileId") == true)
            {
                return Results.Conflict(new
                {
                    message = "This notebook file cannot be deleted because it is referenced by one or more project file versions.",
                    code = "NotebookFile.DeleteReferenced"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message, code = "NotebookFile.DeleteFailed" });
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        })
        .WithName("DeleteNotebookFileById")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        fileGroup.MapPatch("/{fileId:guid}/rename", async (
            Guid projectId,
            Guid notebookId,
            Guid fileId,
            RenameByIdDto dto,
            INotebookFileService fsService) =>
        {
            try
            {
                var success = await fsService.RenameByIdAsync(projectId, notebookId, fileId, dto.NewName);
                if (!success) return Results.NotFound(new { message = "The file was not found or the new name is invalid/conflicts." });
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        })
        .WithName("RenameNotebookFileById")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPatch("/{fileId:guid}/move", async (
            Guid projectId,
            Guid notebookId,
            Guid fileId,
            MoveByIdDto dto,
            INotebookFileService fsService) =>
        {
            try
            {
                var success = await fsService.MoveByIdAsync(projectId, notebookId, fileId, dto.DestinationPath);
                if (!success) return Results.NotFound(new { message = "The file was not found or the move is invalid." });
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        })
        .WithName("MoveNotebookFileById")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapGet("/origin-info", async (
            Guid projectId,
            Guid notebookId,
            [FromQuery] Guid contentFileVersionId,
            INotebookFileService fsService) =>
        {
            try
            {
                var originInfo = await fsService.GetOriginFileInfoAsync(projectId, contentFileVersionId);
                if (originInfo == null) return Results.NotFound(new { message = "Origin file not found or access denied." });
                return Results.Ok(originInfo);
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        })
        .WithName("GetOriginFileInfo")
        .Produces<OriginFileInfoDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPost("/publish-to-project", async (
            Guid projectId,
            Guid notebookId,
            PublishNotebookFileDto dto,
            INotebookFileService fsService) =>
        {
            try
            {
                var result = await fsService.PublishToProjectAsync(projectId, notebookId, dto.NotebookFileId, dto.DestinationFolderId, false);
                if (result == null) return Results.NotFound(new { message = "File not found or access denied." });

                return Results.Created($"/api/projects/{projectId}/files/{result.Id}",
                    new PublishNotebookFileResultDto
                    {
                        ContentFileId = result.Id.ToString(),
                        FileName = result.FileName,
                        VersionNumber = result.LatestVersion,
                        IsNewFile = result.LatestVersion == 1,
                        RelativePath = result.RelativePath ?? string.Empty
                    });
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { message = ex.Message }); }
        })
        .WithName("PublishNotebookFileToProject")
        .RequireAuthorization("RequireContributor")
        .Produces<PublishNotebookFileResultDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // Create notebook from project file (atomic operation)
        group.MapPost("/create-from-file", async (
            Guid projectId,
            CreateNotebookFromFileDto dto,
            INotebookService notebookService) =>
        {

var result = await notebookService.CreateNotebookFromFileAsync(projectId, dto);
            return Results.Created($"/api/projects/{projectId}/notebooks/{result.NotebookId}", result);
        })
        .WithName("CreateNotebookFromFile")
        .RequireAuthorization("RequireContributor")
        .Produces<CreateNotebookFromFileResultDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // --- Notebook Templates ---
        var templatesGroup = app.MapGroup("/api/notebook-templates")
            .WithTags("Notebooks") // Keep it tagged with Notebooks
            .RequireAuthorization("RequireApprovedUser")
            .WithOpenApi();

        templatesGroup.MapGet("/", async (
            Guid? projectId,
            INotebookTemplateService service,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("GetNotebookTemplates called for projectId {ProjectId}", LogValueSanitizer.Sanitize(projectId));

            // Return lightweight summaries - no home page content, auth providers, etc.
            var templates = await service.GetTemplateSummariesAsync(projectId);
            logger.LogInformation("Returning {Count} template summaries", templates.Count());
            return Results.Ok(templates);
        })
        .WithName("GetNotebookTemplates")
        .Produces<List<NotebookTemplateSummaryDto>>(StatusCodes.Status200OK);

        templatesGroup.MapGet("/{templateId:guid}", async (
            Guid templateId,
            Guid? projectId,
            INotebookTemplateService service,
            CancellationToken ct) =>
        {
            var template = await service.GetTemplateByIdAsync(templateId, projectId);
            return template is null ? Results.NotFound() : Results.Ok(template);
        })
        .WithName("GetNotebookTemplateById")
        .Produces<NotebookTemplateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        templatesGroup.MapGet("/avatar/{templateName}", async (
            string templateName,
            HttpContext context,
            CancellationToken ct) =>
        {
            var dbAvatar = await AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles.GetAssistantAvatarAsync(
                templateName, ct);
            
            if (dbAvatar.HasValue)
            {
                context.Response.Headers.CacheControl = "public, max-age=86400";
                context.Response.Headers.Expires = DateTime.UtcNow.AddDays(1).ToString("R");
                return Results.File(dbAvatar.Value.Bytes, dbAvatar.Value.ContentType);
            }

            return Results.NotFound();
        })
        .WithName("GetNotebookTemplateAvatar")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .AllowAnonymous();

        templatesGroup.MapGet("/{templateId:guid}/assistants", async (
            Guid templateId,
            Guid projectId,
            INotebookTemplateService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetAssistantsAsync(templateId, projectId);
            return Results.Ok(list);
        })
        .WithName("GetNotebookTemplateAssistants")
        .Produces<List<AssistantDefinitionDto>>(StatusCodes.Status200OK);

        // Serve assistant avatar images
        app.MapGet("/api/assistants/avatar/{assistantName}", async (
            string assistantName,
            HttpContext context,
            CancellationToken ct) =>
        {
            var dbAvatar = await AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles.GetAssistantAvatarAsync(
                assistantName, ct);
            
            if (dbAvatar.HasValue)
            {
                context.Response.Headers.CacheControl = "public, max-age=86400";
                context.Response.Headers.Expires = DateTime.UtcNow.AddDays(1).ToString("R");
                return Results.File(dbAvatar.Value.Bytes, dbAvatar.Value.ContentType);
            }

            return Results.NotFound();
        })
        .WithName("GetAssistantAvatarByName")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .AllowAnonymous();

        // Serve assistant-specific conversation starters if present
        app.MapGet("/api/assistants/conversation-starters/{assistantName}", async (
            string assistantName,
            CancellationToken ct) =>
        {
            var dbStarters = await AntRunner.ToolCalling.AssistantDefinitions.Storage.AssistantDefinitionFiles.GetAssistantConversationStartersAsync(
                assistantName, ct);
            
            if (dbStarters != null)
            {
                return Results.Ok(dbStarters);
            }

            return Results.Ok(Array.Empty<string>());
        })
        .WithName("GetAssistantConversationStarters")
        .RequireAuthorization("RequireApprovedUser")
        .Produces<List<string>>(StatusCodes.Status200OK);
    }
} 
