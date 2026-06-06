using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public sealed class IntegrationTestCurrentUserService : ICurrentUserService
{
    private readonly IUserContext _userContext;
    private readonly ApplicationDbContext _db;

    public IntegrationTestCurrentUserService(IUserContext userContext, ApplicationDbContext db)
    {
        _userContext = userContext;
        _db = db;
    }

    public async Task<CurrentUserContext?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var principal = _userContext.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            return null;
        }

        var roleValue = principal.FindFirstValue(ClaimTypes.Role)
            ?? principal.FindFirstValue("role");
        var role = Enum.TryParse<Role>(roleValue, ignoreCase: true, out var parsedRole)
            ? parsedRole
            : Role.Pending;

        var name = principal.FindFirstValue(ClaimTypes.Name) ?? "Integration Test User";
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? "integration.user@guideants.local";
        var mustChangePassword = bool.TryParse(principal.FindFirstValue("mustChangePassword"), out var parsedFlag) && parsedFlag;

        await EnsureUserRowAsync(userId, name, email, role, mustChangePassword, cancellationToken).ConfigureAwait(false);

        return new CurrentUserContext(
            userId,
            name,
            email,
            role,
            mustChangePassword,
            Guid.Empty,
            null);
    }

    private async Task EnsureUserRowAsync(
        Guid userId,
        string name,
        string email,
        Role role,
        bool mustChangePassword,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user == null)
        {
            user = new User
            {
                Id = userId,
                Name = name,
                Email = email,
                PasswordHash = "integration-test-hash",
                SecurityStamp = Guid.NewGuid(),
                LastLoginAt = DateTime.UtcNow,
                ApprovedAt = role == Role.Pending ? null : DateTime.UtcNow,
                MustChangePassword = mustChangePassword
            };
            _db.Users.Add(user);
        }

        var userRole = await _db.UserRoles
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (userRole == null)
        {
            _db.UserRoles.Add(new UserRole
            {
                UserId = userId,
                Role = role,
                AssignedAt = DateTime.UtcNow,
                AssignedByUserId = userId
            });
        }
        else
        {
            userRole.Role = role;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
