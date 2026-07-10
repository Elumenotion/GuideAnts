using System.Data;
using System.Net.Mail;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .WithOpenApi();

        group.MapPost("/register", async (
            [FromBody] RegisterRequest request,
            HttpContext httpContext,
            [FromServices] ApplicationDbContext db,
            [FromServices] IUserPasswordHasher passwordHasher,
            [FromServices] IJwtTokenService jwtTokenService,
            [FromServices] IAuthCookieService authCookieService,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateRegisterRequest(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            var trimmedName = request.Name.Trim();
            var trimmedEmail = request.Email.Trim();
            var password = request.Password.Trim();

            var executionStrategy = db.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    "EXEC sp_getapplock @Resource = N'GuideAnts.Auth.Register', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;",
                    cancellationToken);

                var emailAlreadyExists = await db.Users
                    .AsNoTracking()
                    .AnyAsync(user => user.Email == trimmedEmail, cancellationToken);
                if (emailAlreadyExists)
                {
                    return Results.Conflict(new { message = "An account with this email already exists." });
                }

                var hasExistingUsers = await db.Users.AsNoTracking().AnyAsync(cancellationToken);
                var assignedRole = hasExistingUsers ? Role.Pending : Role.Admin;
                var nowUtc = DateTime.UtcNow;

                var user = new User
                {
                    Name = trimmedName,
                    Email = trimmedEmail,
                    SecurityStamp = Guid.NewGuid(),
                    LastLoginAt = nowUtc,
                    ApprovedAt = hasExistingUsers ? null : nowUtc,
                    MustChangePassword = false
                };
                user.PasswordHash = passwordHasher.HashPassword(user, password);

                db.Users.Add(user);
                db.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    Role = assignedRole,
                    AssignedAt = nowUtc,
                    AssignedByUserId = hasExistingUsers ? null : user.Id
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Results.Conflict(new { message = "Registration could not be completed." });
                }

                var issuedToken = jwtTokenService.IssueToken(user, assignedRole);
                authCookieService.AppendAuthCookie(httpContext.Response, httpContext.Request, issuedToken);
                return Results.Ok(new AuthResponse(
                    user.Id,
                    user.Name,
                    user.Email,
                    assignedRole,
                    user.MustChangePassword));
            });
        })
        .WithName("RegisterUser")
        .AllowAnonymous()
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (
            [FromBody] LoginRequest request,
            HttpContext httpContext,
            [FromServices] ApplicationDbContext db,
            [FromServices] IUserPasswordHasher passwordHasher,
            [FromServices] IJwtTokenService jwtTokenService,
            [FromServices] IAuthCookieService authCookieService,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateLoginRequest(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            var trimmedEmail = request.Email.Trim();
            var password = request.Password.Trim();

            var user = await db.Users
                .FirstOrDefaultAsync(
                    candidate => candidate.Email.ToLower() == trimmedEmail.ToLower(),
                    cancellationToken);
            if (user?.PasswordHash == null)
            {
                return Results.Json(
                    new { message = "Invalid email or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var passwordIsValid = passwordHasher.VerifyPassword(user, user.PasswordHash, password);
            if (!passwordIsValid)
            {
                return Results.Json(
                    new { message = "Invalid email or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var role = await db.UserRoles
                .AsNoTracking()
                .Where(ur => ur.UserId == user.Id)
                .Select(ur => (Role?)ur.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (role == null)
            {
                return Results.Json(
                    new { message = "Invalid email or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var isDeactivated = role.Value != Role.Pending && user.ApprovedAt == null;
            if (isDeactivated)
            {
                return Results.Json(
                    new { message = "This account has been deactivated. Contact an administrator." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var issuedToken = jwtTokenService.IssueToken(user, role.Value);
            authCookieService.AppendAuthCookie(httpContext.Response, httpContext.Request, issuedToken);

            return Results.Ok(new AuthResponse(
                user.Id,
                user.Name,
                user.Email,
                role.Value,
                user.MustChangePassword));
        })
        .WithName("LoginUser")
        .AllowAnonymous()
        .Produces<AuthResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", [Authorize] async (
            [FromServices] ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new AuthMeResponse(
                currentUser.UserId,
                currentUser.Name,
                currentUser.Email,
                currentUser.Role,
                currentUser.MustChangePassword,
                currentUser.LastLoginAt));
        })
        .WithName("GetAuthenticatedUser")
        .Produces<AuthMeResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", (
            HttpContext httpContext,
            [FromServices] IAuthCookieService authCookieService) =>
        {
            authCookieService.ClearAuthCookie(httpContext.Response, httpContext.Request);
            return Results.NoContent();
        })
        .WithName("LogoutUser")
        .AllowAnonymous()
        .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/change-password", [Authorize] async (
            [FromBody] ChangePasswordRequest request,
            HttpContext httpContext,
            [FromServices] ApplicationDbContext db,
            [FromServices] ICurrentUserService currentUserService,
            [FromServices] IUserPasswordHasher passwordHasher,
            [FromServices] IJwtTokenService jwtTokenService,
            [FromServices] IAuthCookieService authCookieService,
            CancellationToken cancellationToken) =>
        {
            var errors = ValidateChangePasswordRequest(request);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            var currentUser = await currentUserService.GetCurrentUserAsync(cancellationToken);
            if (currentUser == null)
            {
                return Results.Unauthorized();
            }

            var userRole = await db.UserRoles
                .Include(ur => ur.User)
                .SingleOrDefaultAsync(ur => ur.UserId == currentUser.UserId, cancellationToken);
            if (userRole?.User.PasswordHash == null)
            {
                return Results.BadRequest(new { message = "Current password is incorrect." });
            }

            var currentPassword = request.CurrentPassword.Trim();
            var newPassword = request.NewPassword.Trim();

            var passwordIsValid = passwordHasher.VerifyPassword(userRole.User, userRole.User.PasswordHash, currentPassword);
            if (!passwordIsValid)
            {
                return Results.BadRequest(new { message = "Current password is incorrect." });
            }

            userRole.User.PasswordHash = passwordHasher.HashPassword(userRole.User, newPassword);
            userRole.User.MustChangePassword = false;
            userRole.User.SecurityStamp = Guid.NewGuid();

            await db.SaveChangesAsync(cancellationToken);

            var issuedToken = jwtTokenService.IssueToken(userRole.User, userRole.Role);
            authCookieService.AppendAuthCookie(httpContext.Response, httpContext.Request, issuedToken);

            return Results.NoContent();
        })
        .WithName("ChangePassword")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }

    private static List<string> ValidateRegisterRequest(RegisterRequest request)
    {
        var errors = new List<string>();

        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Name is required.");
        }
        else if (name.Length > 255)
        {
            errors.Add("Name must be 255 characters or fewer.");
        }

        var email = request.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            errors.Add("Email is required.");
        }
        else if (!IsValidEmail(email))
        {
            errors.Add("Email must be a valid email address.");
        }

        var password = request.Password?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            errors.Add("Password is required.");
        }
        else if (password.Length < 8)
        {
            errors.Add("Password must be at least 8 characters.");
        }

        return errors;
    }

    private static List<string> ValidateLoginRequest(LoginRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Email?.Trim()))
        {
            errors.Add("Email is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password?.Trim()))
        {
            errors.Add("Password is required.");
        }

        return errors;
    }

    private static List<string> ValidateChangePasswordRequest(ChangePasswordRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.CurrentPassword?.Trim()))
        {
            errors.Add("Current password is required.");
        }

        var newPassword = request.NewPassword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newPassword))
        {
            errors.Add("New password is required.");
        }
        else if (newPassword.Length < 8)
        {
            errors.Add("New password must be at least 8 characters.");
        }

        return errors;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public sealed record RegisterRequest(string Name, string Email, string Password);

    public sealed record LoginRequest(string Email, string Password);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record AuthResponse(
        Guid UserId,
        string Name,
        string Email,
        Role Role,
        bool MustChangePassword);

    public sealed record AuthMeResponse(
        Guid UserId,
        string Name,
        string Email,
        Role Role,
        bool MustChangePassword,
        DateTime? LastLoginAt);
}
