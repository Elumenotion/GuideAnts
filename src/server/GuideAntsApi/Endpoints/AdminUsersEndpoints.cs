using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class AdminUsersEndpoints
{
    public static void MapAdminUsersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/users")
            .WithTags("AdminUsers")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        group.MapGet("/", async (
            [FromQuery] string? role,
            [FromQuery] string? status,
            [FromServices] IAdminUserService adminUserService,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseRoleFilter(role, out var roleFilter))
            {
                return Results.BadRequest(new { message = "Role filter must be one of: Pending, Reader, Contributor, Admin." });
            }

            if (!TryParseStatusFilter(status, out var statusFilter))
            {
                return Results.BadRequest(new { message = "Status filter must be one of: all, pending, active, inactive." });
            }

            var users = await adminUserService.ListUsersAsync(roleFilter, statusFilter, cancellationToken);
            return Results.Ok(users);
        })
        .WithName("ListAdminUsers")
        .Produces<IReadOnlyList<AdminUserSummary>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            [FromBody] AssignRoleRequest request,
            [FromServices] IAdminUserService adminUserService,
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var result = await adminUserService
                .ApproveUserAsync(id, request.Role, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);

            return MapOperationResult(result);
        })
        .WithName("ApproveAdminUser")
        .Produces<AdminUserSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/role", async (
            Guid id,
            [FromBody] AssignRoleRequest request,
            [FromServices] IAdminUserService adminUserService,
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var result = await adminUserService
                .ChangeRoleAsync(id, request.Role, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);

            return MapOperationResult(result);
        })
        .WithName("ChangeAdminUserRole")
        .Produces<AdminUserSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/deactivate", async (
            Guid id,
            [FromServices] IAdminUserService adminUserService,
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var result = await adminUserService
                .DeactivateUserAsync(id, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);

            return MapOperationResult(result);
        })
        .WithName("DeactivateAdminUser")
        .Produces<AdminUserSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/reactivate", async (
            Guid id,
            [FromServices] IAdminUserService adminUserService,
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var result = await adminUserService
                .ReactivateUserAsync(id, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);

            return MapOperationResult(result);
        })
        .WithName("ReactivateAdminUser")
        .Produces<AdminUserSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/set-password", async (
            Guid id,
            [FromBody] SetPasswordRequest request,
            [FromServices] IAdminUserService adminUserService,
            CancellationToken cancellationToken) =>
        {
            var result = await adminUserService
                .SetPasswordAsync(id, request.Password, cancellationToken)
                .ConfigureAwait(false);

            return MapOperationResult(result);
        })
        .WithName("SetAdminUserPassword")
        .Produces<AdminUserSummary>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);
    }

    private static bool TryParseRoleFilter(string? role, out Role? roleFilter)
    {
        roleFilter = null;
        if (string.IsNullOrWhiteSpace(role))
        {
            return true;
        }

        if (!Enum.TryParse<Role>(role.Trim(), ignoreCase: true, out var parsedRole))
        {
            return false;
        }

        roleFilter = parsedRole;
        return true;
    }

    private static bool TryParseStatusFilter(string? status, out AdminUserListStatusFilter statusFilter)
    {
        statusFilter = AdminUserListStatusFilter.All;
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        return Enum.TryParse<AdminUserListStatusFilter>(status.Trim(), ignoreCase: true, out statusFilter);
    }

    private static IResult MapOperationResult(AdminUserOperationResult<AdminUserSummary> result)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(result.Value);
        }

        var failure = result.Failure!;
        return failure.Error switch
        {
            AdminUserOperationError.NotFound => Results.NotFound(new { message = failure.Message }),
            AdminUserOperationError.InvalidInput => Results.BadRequest(new { message = failure.Message }),
            AdminUserOperationError.InvalidState => Results.Conflict(new { message = failure.Message }),
            AdminUserOperationError.Guarded => Results.Conflict(new { message = failure.Message }),
            _ => Results.Problem(title: "Admin user operation failed.", detail: failure.Message, statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    public sealed record AssignRoleRequest(string Role);

    public sealed record SetPasswordRequest(string Password);
}
