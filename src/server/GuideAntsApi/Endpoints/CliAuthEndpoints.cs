using GuideAntsApi.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class CliAuthEndpoints
{
    public static void MapCliAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cli/sessions")
            .WithTags("CLI Auth")
            .WithOpenApi();

        group.MapPost("/", async (
            [FromServices] ICliAuthService cliAuthService,
            CancellationToken cancellationToken) =>
        {
            var result = await cliAuthService.CreateSessionAsync(cancellationToken);
            return Results.Ok(new CreateCliSessionResponse(
                result.SessionId,
                result.DeviceSecret,
                result.ExpiresAt));
        })
        .WithName("CreateCliSession")
        .AllowAnonymous()
        .Produces<CreateCliSessionResponse>(StatusCodes.Status200OK);

        group.MapPost("/{sessionId}/approve", async (
            string sessionId,
            [FromServices] ICliAuthService cliAuthService,
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var outcome = await cliAuthService.ApproveAsync(sessionId, currentUser.UserId, cancellationToken);
            return outcome switch
            {
                CliAuthApproveOutcome.Approved => Results.NoContent(),
                CliAuthApproveOutcome.NotFound => Results.NotFound(new { message = "CLI session not found." }),
                CliAuthApproveOutcome.Expired => Results.Json(new { message = "CLI session has expired." }, statusCode: 410),
                _ => Results.NotFound(new { message = "CLI session not found." })
            };
        })
        .WithName("ApproveCliSession")
        .RequireAuthorization("RequireApprovedUser")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{sessionId}/token", async (
            string sessionId,
            HttpContext httpContext,
            [FromServices] ICliAuthService cliAuthService,
            CancellationToken cancellationToken) =>
        {
            var deviceSecret = httpContext.Request.Headers["X-Device-Secret"].ToString();
            if (string.IsNullOrEmpty(deviceSecret))
            {
                return Results.Json(new { message = "Missing X-Device-Secret header." }, statusCode: 401);
            }

            var result = await cliAuthService.IssueTokenAsync(sessionId, deviceSecret, cancellationToken);
            return result.Status switch
            {
                CliAuthTokenStatus.Pending => Results.Json(new { status = "pending" }, statusCode: 202),
                CliAuthTokenStatus.Issued => Results.Ok(new CliTokenResponse(result.Token!, result.ExpiresAt!.Value)),
                CliAuthTokenStatus.InvalidSecret => Results.Json(new { message = "Invalid device secret." }, statusCode: 401),
                CliAuthTokenStatus.Consumed => Results.Json(new { message = "CLI session already used." }, statusCode: 410),
                CliAuthTokenStatus.Expired => Results.Json(new { message = "CLI session has expired." }, statusCode: 410),
                CliAuthTokenStatus.NotFound => Results.NotFound(new { message = "CLI session not found." }),
                _ => Results.NotFound(new { message = "CLI session not found." })
            };
        })
        .WithName("GetCliSessionToken")
        .AllowAnonymous()
        .Produces<CliTokenResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status410Gone);
    }

    public sealed record CreateCliSessionResponse(string SessionId, string DeviceSecret, DateTime ExpiresAt);

    public sealed record CliTokenResponse(string Token, DateTime ExpiresAt);
}
