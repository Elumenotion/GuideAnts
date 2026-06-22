using GuideAntsApi.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects")
            .WithTags("Projects")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        // Create a new project
        group.MapPost("/", async (CreateProjectDto createProjectDto, IProjectService projectService) =>
        {
            var project = await projectService.CreateProjectAsync(createProjectDto);
            return Results.Created($"/api/projects/{project.Id}", project);
        })
        .WithName("CreateProject")
        .RequireAuthorization("RequireContributor")
        .Produces<ProjectDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get all projects for the current user
        group.MapGet("/", async (IProjectService projectService) =>
        {
            var projects = await projectService.GetProjectsAsync();
            return Results.Ok(projects);
        })
        .WithName("GetUserProjects")
        .Produces<IEnumerable<ProjectDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get a specific project
        group.MapGet("/{projectId}", async (Guid projectId, IProjectService projectService) =>
        {
            var project = await projectService.GetProjectAsync(projectId);
            if (project == null)
                return Results.NotFound();

            return Results.Ok(project);
        })
        .WithName("GetProject")
        .Produces<ProjectDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Get project details
        group.MapGet("/{projectId}/details", async (Guid projectId, IProjectService projectService) =>
        {
            var project = await projectService.GetProjectDetailsAsync(projectId);
            if (project == null)
                return Results.NotFound();

            return Results.Ok(project);
        })
        .WithName("GetProjectDetails")
        .Produces<ProjectDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Update a project
        group.MapPut("/{projectId}", async (Guid projectId, UpdateProjectDto updateProjectDto, IProjectService projectService) =>
        {
            var project = await projectService.UpdateProjectAsync(projectId, updateProjectDto);
            if (project == null)
                return Results.NotFound();

            return Results.Ok(project);
        })
        .WithName("UpdateProject")
        .RequireAuthorization("RequireContributor")
        .Produces<ProjectDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Delete a project
        group.MapDelete("/{projectId}", async (
            Guid projectId,
            IProjectService projectService,
            ISystemProjectAccessGuard systemProjectAccessGuard) =>
        {
            var deleteBlocked = await systemProjectAccessGuard.EnsureDeleteAllowedAsync(projectId);
            if (deleteBlocked != null)
            {
                return deleteBlocked;
            }

            var result = await projectService.DeleteProjectAsync(projectId);
            if (!result)
                return Results.NotFound();

            return Results.NoContent();
        })
        .WithName("DeleteProject")
        .RequireAuthorization("RequireAdmin")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

                // Set project home page
        group.MapPost("/{projectId}/homepage/{fileId}", async (Guid projectId, Guid fileId, IProjectService projectService) =>
        {
            var ok = await projectService.SetHomePageAsync(projectId, fileId);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("SetProjectHomePage")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Clear project home page
        group.MapDelete("/{projectId}/homepage", async (Guid projectId, IProjectService projectService) =>
        {
            var ok = await projectService.ClearHomePageAsync(projectId);
            return ok ? Results.NoContent() : Results.NotFound();
        })
        .WithName("ClearProjectHomePage")
        .RequireAuthorization("RequireContributor")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);

        // Copy a project with all its child collections (for template initialization)
        group.MapPost("/{projectId}/copy", async (Guid projectId, IProjectService projectService, ILogger<Program> logger) =>
        {
            try
            {
                var project = await projectService.CopyProjectAsync(projectId);
                if (project == null)
                    return Results.NotFound();

                return Results.Created($"/api/projects/{project.Id}", project);
            }
            catch (Exception ex)
            {
                // Log the full exception for debugging
                logger.LogError(ex, "Error copying project {ProjectId}", projectId);
                
                // Return a safe error message to the client
                return Results.Problem(
                    title: "Failed to copy project",
                    detail: "An error occurred while copying the project. Please try again.",
                    statusCode: 500
                );
            }
        })
        .WithName("CopyProject")
        .RequireAuthorization("RequireContributor")
        .Produces<ProjectDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized);
    }
} 
