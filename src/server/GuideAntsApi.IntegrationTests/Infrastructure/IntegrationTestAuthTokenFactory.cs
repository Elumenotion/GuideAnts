using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public static class IntegrationTestAuthTokenFactory
{
    private static readonly Guid DefaultUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static string CreateAdminToken(
        Guid? userId = null,
        string email = "integration.user@guideants.local",
        string name = "Integration Test User")
    {
        return CreateToken(Role.Admin, userId, email, name);
    }

    public static string CreateToken(
        Role role,
        Guid? userId = null,
        string email = "integration.user@guideants.local",
        string name = "Integration Test User",
        bool mustChangePassword = false)
    {
        var resolvedUserId = userId ?? DefaultUserId;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, resolvedUserId.ToString()),
            new(ClaimTypes.NameIdentifier, resolvedUserId.ToString()),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role.ToString())
        };

        if (mustChangePassword)
        {
            claims.Add(new Claim("mustChangePassword", bool.TrueString));
        }

        return IntegrationTestAuthHandler.CreateToken(claims);
    }
}
