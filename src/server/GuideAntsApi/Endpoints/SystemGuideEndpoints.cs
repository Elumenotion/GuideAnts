using System.Net.Http;
using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.SystemGuide;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.SystemGuide;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        group.MapGet("/sandbox-admin/health", async (
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            return await proxy.ForwardAsync(
                HttpMethod.Get,
                "health",
                query: null,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminHealth")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/sandbox-admin/requirements", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            return await proxy.ForwardAsync(
                HttpMethod.Get,
                "requirements",
                query: scopeQuery,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminRequirements")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/sandbox-admin/requirements", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            HttpRequest request,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            var body = await ReadRawBodyAsync(request, cancellationToken);
            return await proxy.ForwardAsync(
                HttpMethod.Put,
                "requirements",
                query: scopeQuery,
                body: body,
                contentType: "text/plain",
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("PutSystemGuideSandboxAdminRequirements")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/sandbox-admin/apt-packages", async (
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            return await proxy.ForwardAsync(
                HttpMethod.Get,
                "apt-packages",
                query: null,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminAptPackages")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/sandbox-admin/apt-packages", async (
            HttpRequest request,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var body = await ReadRawBodyAsync(request, cancellationToken);
            return await proxy.ForwardAsync(
                HttpMethod.Put,
                "apt-packages",
                query: null,
                body: body,
                contentType: "text/plain",
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("PutSystemGuideSandboxAdminAptPackages")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/sandbox-admin/apply", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            return await proxy.ForwardAsync(
                HttpMethod.Post,
                "apply",
                query: scopeQuery,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("PostSystemGuideSandboxAdminApply")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status504GatewayTimeout);

        group.MapGet("/sandbox-admin/apply/jobs/{jobId}", async (
            string jobId,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Results.BadRequest(new { error = "jobId is required." });
            }

            return await proxy.ForwardAsync(
                HttpMethod.Get,
                $"apply/jobs/{Uri.EscapeDataString(jobId)}",
                query: null,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminApplyJob")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/sandbox-admin/setup-status", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            return await proxy.ForwardAsync(
                HttpMethod.Get,
                "setup-status",
                query: scopeQuery,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminSetupStatus")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/sandbox-admin/install-scripts", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            if (scopeQuery is null)
            {
                return Results.BadRequest(new { error = "projectId and guideId are required for install scripts." });
            }

            return await proxy.ForwardAsync(
                HttpMethod.Get,
                "install-scripts",
                query: scopeQuery,
                body: null,
                contentType: null,
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("GetSystemGuideSandboxAdminInstallScripts")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/sandbox-admin/install-scripts", async (
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? guideId,
            [FromQuery] Guid? notebookId,
            HttpRequest request,
            ApplicationDbContext db,
            ISystemGuideSandboxAdminProxy proxy,
            CancellationToken cancellationToken) =>
        {
            var (scopeError, scopeQuery) = await ResolveScopeQueryAsync(
                projectId,
                guideId,
                notebookId,
                db,
                cancellationToken);
            if (scopeError is not null)
            {
                return scopeError;
            }

            if (scopeQuery is null)
            {
                return Results.BadRequest(new { error = "projectId and guideId are required for install scripts." });
            }

            var body = await ReadRawBodyAsync(request, cancellationToken);
            return await proxy.ForwardAsync(
                HttpMethod.Put,
                "install-scripts",
                query: scopeQuery,
                body: body,
                contentType: "application/json",
                cancellationToken);
        })
        .RequireAuthorization("RequireAdmin")
        .WithName("PutSystemGuideSandboxAdminInstallScripts")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<(IResult? Error, IReadOnlyDictionary<string, string?>? Query)> ResolveScopeQueryAsync(
        Guid? projectId,
        Guid? guideId,
        Guid? notebookId,
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var hasProject = HasValue(projectId);
        var hasGuide = HasValue(guideId);
        var hasNotebook = HasValue(notebookId);

        if (!hasProject && !hasGuide && !hasNotebook)
        {
            return (null, null);
        }

        if (!hasProject)
        {
            return (Results.BadRequest(new { error = "projectId must be provided when guideId or notebookId is provided." }), null);
        }

        if (hasGuide && hasNotebook)
        {
            return (Results.BadRequest(new { error = "Provide either guideId or notebookId, not both." }), null);
        }

        if (!hasGuide && !hasNotebook)
        {
            return (Results.BadRequest(new { error = "Provide guideId or notebookId when projectId is provided." }), null);
        }

        if (hasNotebook)
        {
            var notebook = await db.Notebooks
                .AsNoTracking()
                .Where(n => n.Id == notebookId!.Value && n.ProjectId == projectId!.Value)
                .Select(n => new { n.GuideId })
                .SingleOrDefaultAsync(cancellationToken);

            if (notebook is null)
            {
                return (Results.BadRequest(new { error = "The specified notebook was not found in the specified project." }), null);
            }

            if (!notebook.GuideId.HasValue || notebook.GuideId.Value == Guid.Empty)
            {
                return (Results.BadRequest(new { error = "The specified notebook is not associated with a guide." }), null);
            }

            return (null, BuildScopeQuery(projectId!.Value, notebook.GuideId.Value));
        }

        return (null, BuildScopeQuery(projectId!.Value, guideId!.Value));
    }

    private static IReadOnlyDictionary<string, string?> BuildScopeQuery(Guid projectId, Guid guideId)
    {
        return new Dictionary<string, string?>
        {
            ["projectId"] = projectId.ToString("D"),
            ["guideId"] = guideId.ToString("D")
        };
    }

    private static bool HasValue(Guid? value) => value.HasValue && value.Value != Guid.Empty;

    private static async Task<string> ReadRawBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;
        return body;
    }
}
