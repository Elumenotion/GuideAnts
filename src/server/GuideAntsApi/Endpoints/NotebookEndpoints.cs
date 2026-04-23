using System.Security.Claims;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using GuideAntsApi.Services;
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
            .WithOpenApi();

        // Create a new notebook
        group.MapPost("/", async ([FromRoute] Guid projectId, CreateNotebookDto createNotebookDto, INotebookService notebookService) =>
        {
            var notebook = await notebookService.CreateAsync(projectId, createNotebookDto);
            return Results.Created($"/api/projects/{projectId}/notebooks/{notebook.Id}", notebook);
        })
        .WithName("CreateNotebook")
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
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/copy", async ([FromRoute] Guid projectId, CopyNotebookDto copyDto, INotebookCopyService svc) =>
        {
            var nb = await svc.CopyAsync(projectId, copyDto);
            return Results.Created($"/api/projects/{projectId}/notebooks/{nb.Id}", nb);
        })
        .WithName("CopyNotebook")
        .Produces<NotebookDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // --- Notebook Files (Snapshots) ---
        var fileGroup = group.MapGroup("/{notebookId}/files");

        fileGroup.MapGet("/", async (
            ClaimsPrincipal user,
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
            ClaimsPrincipal user,
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
            ClaimsPrincipal user,
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
            ClaimsPrincipal user,
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
        .Produces<NotebookFileDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPost("/upload", async (
            ClaimsPrincipal user,
            Guid projectId,
            Guid notebookId,
            [FromForm] IFormFileCollection files,
            [FromForm] string targetRelativePath,
            [FromForm] bool index,
            INotebookFileService fsService) =>
        {
            if (files == null || files.Count == 0)
            {
                return Results.BadRequest("No files were provided for upload.");
            }
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
        .DisableAntiforgery()
        .Produces<IEnumerable<NotebookFileDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Accepts<IFormFileCollection>("multipart/form-data");



        fileGroup.MapPost("/create-folder", async (
            ClaimsPrincipal user,
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
        .Produces<NotebookFolderTreeDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        // By-ID file operations (no tree lookup required on client)
        fileGroup.MapDelete("/{fileId:guid}", async (
            ClaimsPrincipal user,
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
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        fileGroup.MapPatch("/{fileId:guid}/rename", async (
            ClaimsPrincipal user,
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
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapPatch("/{fileId:guid}/move", async (
            ClaimsPrincipal user,
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
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        fileGroup.MapGet("/origin-info", async (
            ClaimsPrincipal user,
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
            ClaimsPrincipal user,
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
        .Produces<CreateNotebookFromFileResultDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // --- Notebook Templates ---
        var templatesGroup = app.MapGroup("/api/notebook-templates")
            .WithTags("Notebooks") // Keep it tagged with Notebooks
            .WithOpenApi();

        templatesGroup.MapGet("/", async (
            INotebookTemplateService service,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            logger.LogInformation("GetNotebookTemplates called");

            // Return lightweight summaries - no home page content, auth providers, etc.
            var templates = await service.GetTemplateSummariesAsync();
            logger.LogInformation("Returning {Count} template summaries", templates.Count());
            return Results.Ok(templates);
        })
        .WithName("GetNotebookTemplates")
        .Produces<List<NotebookTemplateSummaryDto>>(StatusCodes.Status200OK);

        templatesGroup.MapGet("/{templateId:guid}", async (
            Guid templateId,
            INotebookTemplateService service,
            CancellationToken ct) =>
        {
            var template = await service.GetTemplateByIdAsync(templateId);
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
            var list = await svc.GetAssistantsAsync(templateId);
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
        .Produces<List<string>>(StatusCodes.Status200OK);
    }
} 
