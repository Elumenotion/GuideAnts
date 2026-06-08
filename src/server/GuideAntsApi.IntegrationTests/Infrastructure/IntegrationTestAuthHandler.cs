using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public sealed class IntegrationTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IntegrationTestAuth";
    public const string JwtIssuer = "GuideAnts.IntegrationTests";
    public const string JwtAudience = "GuideAnts.IntegrationTests.Client";
    public const string JwtSigningKey = "GuideAnts.IntegrationTests.Signing.Key.2026.0001";
    public const int JwtLifetimeMinutes = 60;

    public IntegrationTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;

        if (Request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            var authorization = authorizationValues.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authorization["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token)
            && Request.Cookies.TryGetValue(AuthCookieConstants.CookieName, out var cookieToken)
            && !string.IsNullOrWhiteSpace(cookieToken))
        {
            token = cookieToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        ClaimsPrincipal principal;
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            principal = tokenHandler.ValidateToken(token, BuildValidationParameters(), out _);
        }
        catch
        {
            return AuthenticateResult.Fail("Bearer token could not be validated.");
        }

        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return AuthenticateResult.Fail("Bearer token principal is invalid.");
        }

        EnsureNameIdentifierClaim(identity);

        var authorityIsValid = await ApplyDbAuthorityAsync(identity).ConfigureAwait(false);
        if (!authorityIsValid)
        {
            return AuthenticateResult.Fail("Token security stamp mismatch.");
        }

        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    public static string CreateToken(IEnumerable<Claim> claims, DateTime? expiresAtUtc = null)
    {
        var nowUtc = DateTime.UtcNow;
        var expiry = expiresAtUtc ?? nowUtc.AddMinutes(JwtLifetimeMinutes);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            notBefore: nowUtc.AddMinutes(-1),
            expires: expiry,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static TokenValidationParameters BuildValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtIssuer,
            ValidateAudience = true,
            ValidAudience = JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    }

    private static void EnsureNameIdentifierClaim(ClaimsIdentity identity)
    {
        var hasNameIdentifier = identity.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier);
        if (hasNameIdentifier)
        {
            return;
        }

        var subClaim = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrWhiteSpace(subClaim))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subClaim));
        }
    }

    // Mirrors production OnTokenValidated: tokens that carry a security stamp represent a real
    // DB-backed identity, so the stamp is validated and the live role is applied (replacing the
    // possibly stale role claim minted at login). Synthetic fixture tokens (no stamp claim) skip
    // the DB and keep their declared role.
    private async Task<bool> ApplyDbAuthorityAsync(ClaimsIdentity identity)
    {
        var userIdValue = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var securityStampValue = identity.FindFirst(JwtClaimTypes.SecurityStamp)?.Value;

        if (!Guid.TryParse(userIdValue, out var userId) || !Guid.TryParse(securityStampValue, out var tokenSecurityStamp))
        {
            return true;
        }

        var db = Context.RequestServices.GetRequiredService<ApplicationDbContext>();
        var account = await db.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.UserId == userId)
            .Select(userRole => new { userRole.User.SecurityStamp, userRole.Role })
            .SingleOrDefaultAsync(Context.RequestAborted)
            .ConfigureAwait(false);

        if (account is null || account.SecurityStamp != tokenSecurityStamp)
        {
            return false;
        }

        AuthRoleClaims.ApplyLiveRole(identity, account.Role);
        return true;
    }
}
