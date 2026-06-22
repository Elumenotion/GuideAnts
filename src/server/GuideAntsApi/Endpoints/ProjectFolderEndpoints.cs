using GuideAntsApi.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class ProjectFolderEndpoints
{
    public static void MapProjectFolderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/folders")
            .WithTags("Project Folders")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Get folder tree
        group.MapGet("/tree", async (Guid projectId, IProjectFolderService folderService) =>
        {
            try
            {
                var tree = await folderService.GetFolderTreeAsync(projectId);
                return Results.Ok(tree);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("GetProjectFolderTree")
        .Produces<FolderTreeDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Get all folders
        group.MapGet("/", async (Guid projectId, IProjectFolderService folderService) =>
        {
            var folders = await folderService.GetFoldersAsync(projectId);
            return Results.Ok(folders);
        })
        .WithName("GetProjectFolders")
        .Produces<IEnumerable<ProjectFolderDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get specific folder
        group.MapGet("/{folderId}", async (Guid projectId, Guid folderId, IProjectFolderService folderService) =>
        {
            var folder = await folderService.GetFolderAsync(projectId, folderId);
            if (folder == null)
                return Results.NotFound();

            return Results.Ok(folder);
        })
        .WithName("GetProjectFolder")
        .Produces<ProjectFolderDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Create folder
        group.MapPost("/", async (Guid projectId, CreateFolderDto dto, IProjectFolderService folderService) =>
        {
            try
            {
                var folder = await folderService.CreateFolderAsync(projectId, dto);
                return Results.Created($"/api/projects/{projectId}/folders/{folder.Id}", folder);
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
        .WithName("CreateProjectFolder")
        .RequireAuthorization("RequireContributor")
        .Produces<ProjectFolderDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status402PaymentRequired)
        .Produces(StatusCodes.Status403Forbidden);

        // Update folder
        group.MapPut("/{folderId}", async (Guid projectId, Guid folderId, UpdateFolderDto dto, IProjectFolderService folderService) =>
        {
            try
            {
                var folder = await folderService.UpdateFolderAsync(projectId, folderId, dto);
                if (folder == null)
                    return Results.NotFound();

                return Results.Ok(folder);
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
        .WithName("UpdateProjectFolder")
        .RequireAuthorization("RequireContributor")
        .Produces<ProjectFolderDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Delete folder
        group.MapDelete("/{folderId}", async (Guid projectId, Guid folderId, IProjectFolderService folderService) =>
        {
            try
            {
                var result = await folderService.DeleteFolderAsync(projectId, folderId);
                if (!result)
                    return Results.NotFound();

                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("DeleteProjectFolder")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        // Move folder
        group.MapPatch("/{folderId}/move", async (Guid projectId, Guid folderId, MoveFolderDto dto, IProjectFolderService folderService) =>
        {
            var result = await folderService.MoveFolderAsync(projectId, folderId, dto.NewParentFolderId);
            if (!result)
                return Results.BadRequest(new { error = "Failed to move folder" });

            return Results.NoContent();
        })
        .WithName("MoveProjectFolder")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }
} 