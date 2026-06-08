using System.Security.Claims;
using GuideAntsApi.DataModel.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace GuideAntsApi.Services.Auth;

/// <summary>
/// Implements sliding-session behavior for the cookie-backed JWT. While a user keeps making
/// authenticated requests, their auth cookie is re-issued before it can expire, so active
/// sessions never get logged out mid-use. Only genuinely idle sessions lapse.
/// </summary>
public static class SlidingSessionRenewal
{
    /// <summary>
    /// Re-issue the cookie once the token has passed this fraction of its own lifetime.
    /// Using a fraction of the actual lifetime (rather than a fixed interval) guarantees
    /// renewal always fires well before expiry for ANY configured lifetime — a fixed
    /// interval larger than the lifetime would silently never renew and hard-expire the
    /// session. Renewing at the halfway point keeps an active session's expiry near the
    /// full configured idle window while bounding Set-Cookie churn.
    /// </summary>
    public const double RenewAfterLifetimeFraction = 0.5;

    /// <summary>
    /// True once the token is past <see cref="RenewAfterLifetimeFraction"/> of its lifetime,
    /// so an active session's cookie is refreshed before it can expire. An unset issuance
    /// time or a non-positive lifetime never triggers renewal.
    /// </summary>
    public static bool ShouldRenew(DateTime issuedAtUtc, DateTime expiresAtUtc, DateTime nowUtc)
    {
        if (issuedAtUtc == default || expiresAtUtc <= issuedAtUtc)
        {
            return false;
        }

        var lifetime = expiresAtUtc - issuedAtUtc;
        var renewAfter = TimeSpan.FromTicks((long)(lifetime.Ticks * RenewAfterLifetimeFraction));
        return nowUtc - issuedAtUtc >= renewAfter;
    }

    public static void RenewIfNeeded(
        TokenValidatedContext context,
        ClaimsPrincipal principal,
        Guid userId,
        Guid securityStamp)
    {
        if (context.SecurityToken is null)
        {
            return;
        }

        var issuedAtUtc = DateTime.SpecifyKind(context.SecurityToken.ValidFrom, DateTimeKind.Utc);
        var expiresAtUtc = DateTime.SpecifyKind(context.SecurityToken.ValidTo, DateTimeKind.Utc);
        if (!ShouldRenew(issuedAtUtc, expiresAtUtc, DateTime.UtcNow))
        {
            return;
        }

        var roleValue = principal.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<Role>(roleValue, ignoreCase: true, out var role))
        {
            return;
        }

        var name = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

        var services = context.HttpContext.RequestServices;
        var jwtTokenService = services.GetRequiredService<IJwtTokenService>();
        var authCookieService = services.GetRequiredService<IAuthCookieService>();

        var user = new User
        {
            Id = userId,
            Name = name,
            Email = email,
            SecurityStamp = securityStamp
        };

        var issuedToken = jwtTokenService.IssueToken(user, role);
        authCookieService.AppendAuthCookie(context.HttpContext.Response, context.HttpContext.Request, issuedToken);
    }
}
