using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GuideAntsApi.Services.Auth;

public sealed record IssuedJwtToken(string Token, DateTime ExpiresAtUtc);

public interface IJwtTokenService
{
    IssuedJwtToken IssueToken(User user, Role role);
}

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        ValidateOptions(_options);
    }

    public IssuedJwtToken IssueToken(User user, Role role)
    {
        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.AddMinutes(_options.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, role.ToString()),
            new(JwtClaimTypes.SecurityStamp, user.SecurityStamp.ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresUtc,
            signingCredentials: credentials);

        return new IssuedJwtToken(new JwtSecurityTokenHandler().WriteToken(token), expiresUtc);
    }

    private static void ValidateOptions(JwtOptions options) => JwtTokenValidation.ValidateOptions(options);
}
