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
    /// Re-issue the cookie once the current token has been alive longer than this interval.
    /// Keeping it small relative to the token lifetime means an active session's expiry is
    /// always kept near the full configured idle window, while limiting Set-Cookie churn to
    /// roughly one re-issue per interval per active session.
    /// </summary>
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// True once enough time has elapsed since the token was issued that the cookie should be
    /// refreshed. A <c>default</c> (unset) issuance time never triggers renewal.
    /// </summary>
    public static bool ShouldRenew(DateTime issuedAtUtc, DateTime nowUtc)
    {
        if (issuedAtUtc == default)
        {
            return false;
        }

        return nowUtc - issuedAtUtc >= RenewalInterval;
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
        if (!ShouldRenew(issuedAtUtc, DateTime.UtcNow))
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
