using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Auth;

public sealed record CurrentUserContext(
    Guid UserId,
    string Name,
    string Email,
    Role Role,
    bool MustChangePassword,
    Guid SecurityStamp,
    DateTime? LastLoginAt);

public interface IUserContext
{
    ClaimsPrincipal? Principal { get; }
}

public sealed class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;
}

public interface ICurrentUserService
{
    Task<CurrentUserContext?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

public sealed class CurrentUserService : ICurrentUserService
{
    private readonly ApplicationDbContext _db;
    private readonly IUserContext _userContext;

    public CurrentUserService(ApplicationDbContext db, IUserContext userContext)
    {
        _db = db;
        _userContext = userContext;
    }

    public async Task<CurrentUserContext?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var principal = _userContext.Principal;
        var userId = ResolveUserId(principal);
        if (userId == null)
        {
            return null;
        }

        return await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId.Value)
            .Select(ur => new CurrentUserContext(
                ur.UserId,
                ur.User.Name,
                ur.User.Email,
                ur.Role,
                ur.User.MustChangePassword,
                ur.User.SecurityStamp,
                ur.User.LastLoginAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Guid? ResolveUserId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        return Guid.TryParse(userIdValue, out var userId) ? userId : null;
    }
}
