using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.SystemGuide;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class SystemGuideEndpoints
{
    public static void MapSystemGuideEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/system-guide")
            .WithTags("System Guide")
            .WithOpenApi();

        group.MapGet("/session", async (
            ICurrentUserService currentUserService,
            ISystemGuideSessionService sessionService,
            CancellationToken cancellationToken) =>
        {
            var user = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (user == null)
            {
                return Results.Unauthorized();
            }

            if (user.Role == Role.Pending)
            {
                return Results.Forbid();
            }

            var session = await sessionService.GetSessionAsync(user, cancellationToken);
            if (session == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(session);
        })
        .RequireAuthorization("RequireApprovedUser")
        .WithName("GetSystemGuideSession")
        .Produces<SystemGuideSessionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/workspace", async (
            ICurrentUserService currentUserService,
            ISystemGuideSessionService sessionService,
            CancellationToken cancellationToken) =>
        {
            var user = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (user == null)
            {
                return Results.Unauthorized();
            }

            var workspace = await sessionService.GetWorkspaceAsync(user, cancellationToken);
            if (workspace == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(workspace);
        })
        .RequireAuthorization("RequireApprovedUser")
        .WithName("GetSystemGuideWorkspace")
        .Produces<SystemGuideWorkspaceDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);
    }
}
