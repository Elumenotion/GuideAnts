using GuideAntsApi.Models.Scheduling;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Scheduling;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class ProjectScheduledJobEndpoints
{
    public static void MapProjectScheduledJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/scheduled-jobs")
            .WithTags("Project Scheduled Jobs")
            .RequireAuthorization("RequireAdmin")
            .WithSystemProjectAccessGuard()
            .WithOpenApi();

        group.MapGet("/", async (
            Guid projectId,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            var jobs = await service.ListAsync(projectId, cancellationToken);
            return Results.Ok(jobs);
        })
        .WithName("ListProjectScheduledJobs")
        .Produces<IReadOnlyList<ProjectScheduledJobSummaryDto>>(StatusCodes.Status200OK);

        group.MapGet("/{jobId:guid}", async (
            Guid projectId,
            Guid jobId,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            var job = await service.GetAsync(projectId, jobId, cancellationToken);
            return job == null ? Results.NotFound() : Results.Ok(job);
        })
        .WithName("GetProjectScheduledJob")
        .Produces<ProjectScheduledJobDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
            Guid projectId,
            [FromBody] CreateProjectScheduledJobRequest request,
            IProjectScheduledJobService service,
            ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            if (request == null)
            {
                return Results.BadRequest(new { message = "Request body is required." });
            }

            try
            {
                var created = await service.CreateAsync(projectId, request, currentUser.UserId, cancellationToken);
                return Results.Created($"/api/projects/{projectId}/scheduled-jobs/{created.Id}", created);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("CreateProjectScheduledJob")
        .Produces<ProjectScheduledJobDetailDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapPut("/{jobId:guid}", async (
            Guid projectId,
            Guid jobId,
            [FromBody] UpdateProjectScheduledJobRequest request,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new { message = "Request body is required." });
            }

            try
            {
                var updated = await service.UpdateAsync(projectId, jobId, request, cancellationToken);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("UpdateProjectScheduledJob")
        .Produces<ProjectScheduledJobDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{jobId:guid}", async (
            Guid projectId,
            Guid jobId,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.DeleteAsync(projectId, jobId, cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        })
        .WithName("DeleteProjectScheduledJob")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{jobId:guid}/run", async (
            Guid projectId,
            Guid jobId,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.EnqueueManualRunAsync(projectId, jobId, cancellationToken);
                return Results.Accepted($"/api/projects/{projectId}/scheduled-jobs/{jobId}/runs", null);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        })
        .WithName("RunProjectScheduledJobNow")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/{jobId:guid}/runs", async (
            Guid projectId,
            Guid jobId,
            IProjectScheduledJobService service,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default) =>
        {
            try
            {
                var runs = await service.ListRunsAsync(projectId, jobId, page, pageSize, cancellationToken);
                return Results.Ok(runs);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        })
        .WithName("ListProjectScheduledJobRuns")
        .Produces<PagedProjectScheduledJobRunsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{jobId:guid}/runs/{runId:guid}", async (
            Guid projectId,
            Guid jobId,
            Guid runId,
            IProjectScheduledJobService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var run = await service.GetRunAsync(projectId, jobId, runId, cancellationToken);
                return run == null ? Results.NotFound() : Results.Ok(run);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        })
        .WithName("GetProjectScheduledJobRun")
        .Produces<ProjectScheduledJobRunDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}
