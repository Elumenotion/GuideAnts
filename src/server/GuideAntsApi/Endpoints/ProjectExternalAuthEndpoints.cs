using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.SystemGuide;

namespace GuideAntsApi.Endpoints;

public static class ProjectExternalAuthEndpoints
{
    public static void MapProjectExternalAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/external-auth")
            .WithTags("Projects")
            .RequireAuthorization("RequireApprovedUser")
            .WithSystemProjectAccessGuard();

        group.MapGet("", async (
            Guid projectId,
            [FromServices] ApplicationDbContext db) =>
        {

var list = await db.ProjectExternalAuths
                .AsNoTracking()
                .Where(e => e.ProjectId == projectId)
                .Select(e => new
                {
                    e.ProviderId,
                    e.AuthType,
                    e.ClientId,
                    e.Tenant,
                    e.HeaderName,
                    HasHeaderValue = e.HeaderValue != null
                })
                .ToListAsync();

            return Results.Ok(list);
        })
        .WithName("GetProjectExternalAuth")
        .Produces<object[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("{providerId}", async (
            Guid projectId,
            string providerId,
            [FromBody] ExternalAuthRequest req,
            [FromServices] ApplicationDbContext db) =>
        {

if (string.IsNullOrWhiteSpace(providerId))
                return Results.BadRequest(new { message = "providerId required" });

            // Validate based on auth type
            if (req.AuthType?.ToLower() == "oauth")
            {
                if (string.IsNullOrWhiteSpace(req.ClientId))
                    return Results.BadRequest(new { message = "clientId required for OAuth" });
                if (string.IsNullOrWhiteSpace(req.Tenant))
                    return Results.BadRequest(new { message = "tenant required for OAuth" });
                
                // Validate GUID format for clientId
                if (!Guid.TryParse(req.ClientId, out _))
                    return Results.BadRequest(new { message = "clientId must be a valid GUID" });
                
                // Validate tenant format
                var tenant = req.Tenant.Trim();
                if (tenant != "organizations" && tenant != "common" && !Guid.TryParse(tenant, out _))
                    return Results.BadRequest(new { message = "tenant must be 'organizations', 'common', or a valid GUID" });
            }
            else if (req.AuthType?.ToLower() == "service_http")
            {
                if (string.IsNullOrWhiteSpace(req.HeaderValue))
                    return Results.BadRequest(new { message = "headerValue required for service_http" });
                if (req.HeaderValue.Trim().Length < 8)
                    return Results.BadRequest(new { message = "headerValue appears too short" });
            }
            else
            {
                return Results.BadRequest(new { message = "authType must be 'oauth' or 'service_http'" });
            }

            var entity = await db.ProjectExternalAuths
                .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.ProviderId == providerId);

            if (entity == null)
            {
                entity = new ProjectExternalAuth
                {
                    ProjectId = projectId,
                    ProviderId = providerId,
                    AuthType = req.AuthType!.ToLower(),
                    Created = DateTime.UtcNow
                };
                db.ProjectExternalAuths.Add(entity);
            }

            if (req.AuthType?.ToLower() == "oauth")
            {
                entity.AuthType = "oauth";
                entity.ClientId = req.ClientId!.Trim();
                entity.Tenant = req.Tenant!.Trim();
                entity.HeaderName = null;
                entity.HeaderValue = null;
            }
            else if (req.AuthType?.ToLower() == "service_http")
            {
                entity.AuthType = "service_http";
                entity.ClientId = null;
                entity.Tenant = null;
                entity.HeaderName = string.IsNullOrWhiteSpace(req.HeaderName) ? "Authorization" : req.HeaderName.Trim();
                entity.HeaderValue = req.HeaderValue!.Trim();
            }

            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("UpsertProjectExternalAuth")
        .RequireAuthorization("RequireAdmin")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("{providerId}", async (
            Guid projectId,
            string providerId,
            [FromServices] ApplicationDbContext db) =>
        {

var entity = await db.ProjectExternalAuths
                .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.ProviderId == providerId);
            if (entity == null)
                return Results.NotFound();

            db.ProjectExternalAuths.Remove(entity);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteProjectExternalAuth")
        .RequireAuthorization("RequireAdmin")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("{providerId}/oauth/authorize-url", async (
            Guid projectId,
            string providerId,
            [FromBody] OAuthAuthorizeUrlRequest request,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IToolOAuthService toolOAuthService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await toolOAuthService.CreateAuthorizeUrlAsync(
                    projectId,
                    providerId,
                    currentUser.UserId,
                    new ToolOAuthAuthorizeUrlRequest(
                        request?.ClientId ?? string.Empty,
                        request?.Tenant ?? "organizations",
                        request?.Scopes ?? [],
                        request?.RedirectUri ?? string.Empty,
                        request?.ReturnUrl),
                    cancellationToken);

                return Results.Ok(new
                {
                    authorizeUrl = result.AuthorizeUrl,
                    state = result.State,
                    expiresAt = result.ExpiresAtUtc
                });
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
        .WithName("StartProjectExternalOAuth")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("{providerId}/oauth/callback", async (
            Guid projectId,
            string providerId,
            [FromBody] OAuthCallbackRequest request,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IToolOAuthService toolOAuthService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
            {
                return Results.BadRequest(new { message = "code and state are required." });
            }

            try
            {
                var status = await toolOAuthService.CompleteCallbackAsync(
                    projectId,
                    providerId,
                    currentUser.UserId,
                    request.Code,
                    request.State,
                    cancellationToken);

                return Results.Ok(new
                {
                    connected = status.Connected,
                    expiresAt = status.ExpiresAt,
                    scopes = status.Scopes
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
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
        .WithName("CompleteProjectExternalOAuthCallback")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("{providerId}/oauth/status", async (
            string providerId,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IToolOAuthService toolOAuthService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var status = await toolOAuthService.GetStatusAsync(
                currentUser.UserId,
                providerId,
                cancellationToken);

            return Results.Ok(new
            {
                connected = status.Connected,
                expiresAt = status.ExpiresAt,
                scopes = status.Scopes
            });
        })
        .WithName("GetProjectExternalOAuthStatus")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("{providerId}/oauth", async (
            string providerId,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IToolOAuthService toolOAuthService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            await toolOAuthService.DisconnectAsync(
                currentUser.UserId,
                providerId,
                cancellationToken);

            return Results.NoContent();
        })
        .WithName("DisconnectProjectExternalOAuth")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }

    public sealed class ExternalAuthRequest
    {
        public string? AuthType { get; set; }
        public string? ClientId { get; set; }
        public string? Tenant { get; set; }
        public string? HeaderName { get; set; }
        public string? HeaderValue { get; set; }
    }

    public sealed class OAuthAuthorizeUrlRequest
    {
        public string? ClientId { get; set; }
        public string? Tenant { get; set; }
        public List<string>? Scopes { get; set; }
        public string? RedirectUri { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public sealed class OAuthCallbackRequest
    {
        public string? Code { get; set; }
        public string? State { get; set; }
    }
}
