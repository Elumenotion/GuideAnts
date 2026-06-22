using GuideAnts.Logging;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class HostFolderMountEndpoints
{
    public static void MapHostFolderMountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/host-folder-mounts")
            .WithTags("Host Folder Mounts")
            .RequireAuthorization("RequireAdmin")
            .WithSystemProjectAccessGuard();

        group.MapGet("/", async (
            Guid projectId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            var mounts = await mountService.ListMountsAsync(projectId, cancellationToken);
            return Results.Ok(mounts.Select(HostFolderMountDtoMapper.ToSummaryDto));
        })
        .WithName("ListHostFolderMounts")
        .Produces<IEnumerable<HostFolderMountSummaryDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
            Guid projectId,
            [FromBody] HostFolderMountCreateEndpointRequest request,
            IHostFolderMountService mountService,
            ICurrentUserService currentUserService,
            ILogger<Program> logger,
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

            if (!TryParseScope(request.Scope, out var scope))
            {
                return Results.BadRequest(new { message = "Scope must be 'Notebook' or 'Project'." });
            }

            try
            {
                var serviceRequest = new HostFolderMountCreateRequest(
                    scope,
                    request.NotebookId,
                    request.HostPath ?? string.Empty,
                    request.LeafName,
                    SourceKind.LocalPath);

                var result = await mountService.CreateMountAsync(
                    projectId,
                    serviceRequest,
                    currentUser.UserId,
                    cancellationToken);

                return Results.Created(
                    $"/api/projects/{projectId}/host-folder-mounts/{result.MountId}",
                    new HostFolderMountCreateEndpointResponse(
                        result.MountId,
                        result.Status,
                        result.LeafName,
                        result.ContainerSourcePath,
                        result.ApplyCommand));
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
                logger.LogWarning(
                    "Host folder mount create validation failed for project {ProjectId}: {Reason}",
                    projectId,
                    LogValueSanitizer.Sanitize(ex.Message));
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("CreateHostFolderMount")
        .Produces<HostFolderMountCreateEndpointResponse>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{mountId:guid}", async (
            Guid projectId,
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            var mount = await mountService.GetMountAsync(projectId, mountId, cancellationToken);
            if (mount == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(HostFolderMountDtoMapper.ToDetailDto(mount, includeAdminSensitiveFields: true));
        })
        .WithName("GetHostFolderMount")
        .Produces<HostFolderMountDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{mountId:guid}/commands/apply", async (
            Guid projectId,
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await mountService.GetApplyCommandAsync(projectId, mountId, cancellationToken);
                return Results.Ok(new HostFolderMountApplyCommandEndpointResponse(
                    result.MountId,
                    result.Status,
                    result.LeafName,
                    result.ContainerSourcePath,
                    result.ApplyCommand));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("GetHostFolderMountApplyCommand")
        .Produces<HostFolderMountApplyCommandEndpointResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{mountId:guid}/commands/remove", async (
            Guid projectId,
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await mountService.BeginRemoveMountAsync(projectId, mountId, cancellationToken);
                return Results.Ok(new HostFolderMountRemoveEndpointResponse(
                    result.MountId,
                    result.Status,
                    result.RemoveCommand));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message, code = "HostFolderMount.RemoveFailed" });
            }
        })
        .WithName("GetHostFolderMountRemoveCommand")
        .Produces<HostFolderMountRemoveEndpointResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{mountId:guid}/reconcile", async (
            Guid projectId,
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await mountService.ReconcileMountAsync(projectId, mountId, cancellationToken);
                return Results.Ok(new HostFolderMountReconcileEndpointResponse(
                    result.MountId,
                    result.Status,
                    result.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        })
        .WithName("ReconcileHostFolderMount")
        .Produces<HostFolderMountReconcileEndpointResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{mountId:guid}", async (
            Guid projectId,
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            var deleted = await mountService.DeleteMountAsync(projectId, mountId, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteHostFolderMount")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }

    public static void MapHostFolderMountInternalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/host-folder-mounts/{mountId:guid}/compose-override-plan", async (
            Guid mountId,
            IHostFolderMountService mountService,
            CancellationToken cancellationToken) =>
        {
            var plan = await mountService.GetComposeOverridePlanForScriptAsync(mountId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new HostFolderMountComposeOverridePlanEndpointResponse(
                plan.MountId,
                plan.ProjectId,
                plan.MountKey,
                plan.SourceKind.ToString(),
                plan.HostPath));
        })
        .WithName("GetHostFolderMountComposeOverridePlan")
        .RequireAuthorization("RequireAdmin")
        .WithTags("Host Folder Mounts")
        .Produces<HostFolderMountComposeOverridePlanEndpointResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }

    private static bool TryParseScope(string? scopeValue, out HostFolderMountScope scope)
    {
        if (string.IsNullOrWhiteSpace(scopeValue))
        {
            scope = default;
            return false;
        }

        return Enum.TryParse(scopeValue, ignoreCase: true, out scope);
    }
}
